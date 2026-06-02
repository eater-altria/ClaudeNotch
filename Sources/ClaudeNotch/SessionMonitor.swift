import Foundation
import SwiftUI

/// 扫描 ~/.claude/projects/*/<uuid>.jsonl，解析活跃会话的花费与上下文占用。
/// 同步实现，约定只在后台串行队列调用；按 (路径, mtime) 缓存，避免重复解析大文件。
final class SessionScanner {
    private let projectsDir: URL
    private var cache: [String: (mtime: Date, info: SessionInfo?)] = [:]

    init() {
        projectsDir = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(".claude/projects", isDirectory: true)
    }

    private struct Candidate {
        let url: URL
        let creation: Date
        let mtime: Date
    }

    /// 返回当前“确有 claude 进程在跑”的会话，按最近活动倒序。
    /// 关键：把每个活进程**按启动时间配对到创建时间最接近的 transcript**，而不是按 mtime 猜。
    /// 这样关闭某个会话后，只有它对应的文件失配被丢弃，其余在跑的会话保持不变。
    /// `maxAge` 仅作过老文件的性能护栏。
    func scan(maxAge: TimeInterval) -> [SessionInfo] {
        let liveProcs = ProcessProbe.liveClaudeProcesses()
        guard !liveProcs.isEmpty else { return [] }   // 没有运行中的 claude

        // 按项目目录分组活进程
        var procsByDir: [String: [LiveClaude]] = [:]
        for p in liveProcs { procsByDir[p.dirName, default: []].append(p) }

        let fm = FileManager.default
        let cutoff = Date().addingTimeInterval(-maxAge)
        guard let dirs = try? fm.contentsOfDirectory(at: projectsDir,
                                                     includingPropertiesForKeys: [.isDirectoryKey],
                                                     options: [.skipsHiddenFiles]) else { return [] }

        var result: [SessionInfo] = []
        for dir in dirs {
            guard (try? dir.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true else { continue }
            guard let procs = procsByDir[dir.lastPathComponent], !procs.isEmpty else { continue }

            // 该目录顶层 *.jsonl（排除 subagents 子目录），带创建时间与 mtime
            guard let files = try? fm.contentsOfDirectory(at: dir,
                                                          includingPropertiesForKeys: [.creationDateKey, .contentModificationDateKey],
                                                          options: [.skipsHiddenFiles]) else { continue }
            let candidates: [Candidate] = files
                .filter { $0.pathExtension == "jsonl" }
                .compactMap { f in
                    let v = try? f.resourceValues(forKeys: [.creationDateKey, .contentModificationDateKey])
                    let mt = v?.contentModificationDate ?? .distantPast
                    if mt < cutoff { return nil }
                    return Candidate(url: f, creation: v?.creationDate ?? .distantPast, mtime: mt)
                }

            for (url, proc) in Self.assign(procs: procs, candidates: candidates) {
                let mtime = candidates.first { $0.url == url }?.mtime ?? .distantPast
                var info: SessionInfo?
                if let cached = cache[url.path], cached.mtime == mtime {
                    info = cached.info
                } else {
                    info = Self.parse(file: url, mtime: mtime)
                    cache[url.path] = (mtime, info)
                }
                if var i = info {
                    i.jump = proc.jump   // 附加该进程的终端跳转信息
                    result.append(i)
                }
            }
        }
        return result.sorted { $0.lastActivity > $1.lastActivity }
    }

    /// 把同目录下的活进程配对到各自的 transcript：
    /// ① 先按“启动时间 ↔ 创建时间”最近、容差内做唯一贪心匹配（新会话精确对应）；
    /// ② 仍未配上的进程（如 --resume：进程新、文件旧），从剩余文件里按 mtime 最近补齐。
    private static func assign(procs: [LiveClaude], candidates: [Candidate]) -> [(url: URL, proc: LiveClaude)] {
        let tolerance: TimeInterval = 300   // 启动与创建相差 5 分钟内视为同一会话

        // ① 生成所有容差内的 (进程,文件) 配对，按时间差升序贪心唯一分配
        var pairs: [(diff: TimeInterval, pi: Int, ci: Int)] = []
        for (pi, p) in procs.enumerated() {
            for (ci, c) in candidates.enumerated() {
                let diff = abs(c.creation.timeIntervalSince(p.startTime))
                if diff <= tolerance { pairs.append((diff, pi, ci)) }
            }
        }
        pairs.sort { $0.diff < $1.diff }

        var usedProc = Set<Int>(), usedCand = Set<Int>()
        var chosen: [(url: URL, proc: LiveClaude)] = []
        for pair in pairs {
            if usedProc.contains(pair.pi) || usedCand.contains(pair.ci) { continue }
            usedProc.insert(pair.pi); usedCand.insert(pair.ci)
            chosen.append((candidates[pair.ci].url, procs[pair.pi]))
        }

        // ② 兜底：未配上的进程（如 --resume）→ 剩余文件按 mtime 最近补齐
        let leftoverProcs = procs.indices.filter { !usedProc.contains($0) }
        if !leftoverProcs.isEmpty {
            let remaining = candidates.indices
                .filter { !usedCand.contains($0) }
                .sorted { candidates[$0].mtime > candidates[$1].mtime }
            for (k, ci) in remaining.prefix(leftoverProcs.count).enumerated() {
                usedCand.insert(ci)
                chosen.append((candidates[ci].url, procs[leftoverProcs[k]]))
            }
        }
        return chosen
    }

    private static func iv(_ a: Any?) -> Int { (a as? NSNumber)?.intValue ?? 0 }

    /// 单个 transcript 文件的解析结果。
    private struct FileParse {
        var cost: Double = 0          // 已按 messageId 去重、按各消息自身模型计价
        var lastModel = ""
        var lastCtx = 0
        var peakCtx = 0
        var sid = ""
        var cwd = ""
        var branch: String? = nil
        var sawAssistant = false
    }

    /// 解析一个 transcript 文件：按 messageId 去重（同一响应会被写多行，usage 重复），
    /// 每条按其自身模型单价累加成本。同时记录最后/峰值上下文占用。
    private static func parseFile(_ url: URL) -> FileParse? {
        guard let content = try? String(contentsOf: url, encoding: .utf8) else { return nil }
        var r = FileParse()
        var seen = Set<String>()

        content.enumerateLines { line, _ in
            guard let d = line.data(using: .utf8),
                  let o = try? JSONSerialization.jsonObject(with: d) as? [String: Any],
                  (o["type"] as? String) == "assistant",
                  let msg = o["message"] as? [String: Any],
                  let usage = msg["usage"] as? [String: Any] else { return }

            let inp = iv(usage["input_tokens"])
            let out = iv(usage["output_tokens"])
            let cr = iv(usage["cache_read_input_tokens"])
            var cw5 = 0, cw1h = 0
            if let cc = usage["cache_creation"] as? [String: Any] {
                cw5 = iv(cc["ephemeral_5m_input_tokens"])
                cw1h = iv(cc["ephemeral_1h_input_tokens"])
            } else {
                cw5 = iv(usage["cache_creation_input_tokens"])
            }
            let model = (msg["model"] as? String) ?? ""

            // 上下文用最后一条（重复行 usage 相同，无影响），与去重无关
            if !model.isEmpty { r.lastModel = model }
            if let s = o["sessionId"] as? String { r.sid = s }
            if let c = o["cwd"] as? String { r.cwd = c }
            if let b = o["gitBranch"] as? String { r.branch = b }
            let ctx = inp + cr + cw5 + cw1h
            r.lastCtx = ctx
            r.peakCtx = max(r.peakCtx, ctx)

            // 成本去重：同一 messageId 只计一次
            let mid = (msg["id"] as? String) ?? (o["uuid"] as? String) ?? ""
            guard seen.insert(mid).inserted else { return }
            r.sawAssistant = true

            let p = ModelPricing.lookup(model)
            r.cost += (Double(inp) * p.input
                       + Double(out) * p.output
                       + Double(cr) * p.cacheRead
                       + Double(cw5) * p.cacheWrite5m
                       + Double(cw1h) * p.cacheWrite1h) / 1_000_000.0
        }
        return r.sawAssistant ? r : nil
    }

    private static func parse(file: URL, mtime: Date) -> SessionInfo? {
        guard let main = parseFile(file), !main.cwd.isEmpty || !main.sid.isEmpty else { return nil }

        // 子代理花费（Task/workflow 子代理写在 <sessionId>/subagents/** 下）也算进本会话，
        // 与 Claude Code 的 /cost 口径一致。
        var subCost = 0.0
        let subDir = file.deletingPathExtension().appendingPathComponent("subagents", isDirectory: true)
        if let en = FileManager.default.enumerator(at: subDir, includingPropertiesForKeys: nil,
                                                   options: [.skipsHiddenFiles]) {
            for case let f as URL in en where f.pathExtension == "jsonl" {
                if let pf = parseFile(f) { subCost += pf.cost }
            }
        }

        let p = ModelPricing.lookup(main.lastModel)
        let window = max(p.window, main.peakCtx > 200_000 ? 1_000_000 : 200_000)
        let name = main.cwd.isEmpty ? "(unknown)" : (main.cwd as NSString).lastPathComponent
        let cleanBranch = (main.branch == "HEAD" || main.branch?.isEmpty == true) ? nil : main.branch

        return SessionInfo(
            id: main.sid.isEmpty ? file.lastPathComponent : main.sid,
            projectName: name, cwd: main.cwd, gitBranch: cleanBranch,
            model: main.lastModel, costUSD: main.cost + subCost,
            contextTokens: main.lastCtx, contextWindow: window, lastActivity: mtime
        )
    }
}

/// 活跃会话的可观察存储，自带轮询定时器（后台解析，主线程发布）。
@MainActor
final class SessionStore: ObservableObject {
    @Published private(set) var sessions: [SessionInfo] = []

    private let scanner = SessionScanner()
    private let queue = DispatchQueue(label: "com.claudenotch.sessionscan", qos: .utility)
    private var timer: Timer?
    let maxAge: TimeInterval = 12 * 3600   // 仅作过老文件护栏；活跃与否由进程决定
    let interval: TimeInterval = 30

    func start() {
        refresh()
        timer = Timer.scheduledTimer(withTimeInterval: interval, repeats: true) { [weak self] _ in
            self?.refresh()
        }
    }

    func refresh() {
        let age = maxAge
        queue.async { [weak self] in
            guard let self else { return }
            let result = self.scanner.scan(maxAge: age)
            DispatchQueue.main.async {
                self.sessions = result
                self.checkContextNotifications(result)
            }
        }
    }

    // 上下文告警：某会话上下文 ≥90% 提醒一次（建议 /compact）；回落后可再次提醒。
    private var notifiedContext: Set<String> = []

    private func checkContextNotifications(_ sessions: [SessionInfo]) {
        for s in sessions where s.contextPercent >= 90 && !notifiedContext.contains(s.id) {
            notifiedContext.insert(s.id)
            NotificationManager.shared.notify(
                id: "ctx-\(s.id)",
                title: "上下文将满",
                body: "\(s.projectName) 上下文已用 \(s.contextPercent)%，建议 /compact 或新开会话")
        }
        // 只在"本次扫描确实出现、且已回落到 <90"时解除标记。
        // 会话短暂从扫描中消失（非真正结束）不解除，避免重现时重复轰炸。
        for s in sessions where s.contextPercent < 90 {
            notifiedContext.remove(s.id)
        }
    }
}
