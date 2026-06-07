import Foundation
import SwiftUI

/// 扫描 ~/.claude/projects/*/<uuid>.jsonl，解析活跃会话的花费与上下文占用。
/// 同步实现，约定只在后台串行队列调用；按 (路径, mtime) 缓存，避免重复解析大文件。
final class SessionScanner {
    private let projectsDir: URL
    private var cache: [String: (mtime: Date, info: SessionInfo?)] = [:]
    private let codex = CodexSessionScanner()

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
        if AgentContext.current == .codex { return codex.scan(maxAge: maxAge) }
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

    /// 把同目录下的活进程配对到「它当前正在写的」transcript。
    ///
    /// **关键：以 mtime 最新优先，而不是「启动时间↔创建时间」。**
    /// 一个长寿 `claude` 进程经 `/clear`、`--resume` 会先后创建多个会话文件，它的启动时间只对应**最早**那个，
    /// 但它当前写的是 **mtime 最新**那个。若按创建↔启动配，会把进程钉死在很久以前的旧会话上
    /// （花费/上下文永远停在旧值、永不更新）——这是实测踩到的坑。
    /// 因此：取 mtime 最新的 k 个文件（k = 该目录活进程数）= 当前活跃会话；
    /// 再把它们配到具体进程（按创建↔启动最近，仅为跳转/终端归属准确）。
    ///
    /// 取舍：刚写过又立刻关闭的并发同目录会话，可能在它仍是 mtime 最新、而另一条在跑的会话尚未再写入的
    /// 短暂窗口里被误显示一次，下次扫描（或对方一写入）即自愈——远小于「钉死旧会话」的持续错误。
    private static func assign(procs: [LiveClaude], candidates: [Candidate]) -> [(url: URL, proc: LiveClaude)] {
        guard !procs.isEmpty, !candidates.isEmpty else { return [] }

        // 活跃会话 = 进程正在写的 = mtime 最新的 k 个
        let active = Array(candidates.sorted { $0.mtime > $1.mtime }.prefix(procs.count))

        // 把活跃文件配到具体进程：按「创建时间↔启动时间」最近做唯一贪心（只为跳转/终端归属准确）。
        // 文件数 ≤ 进程数，故每个活跃文件都会拿到一个进程。
        var pairs: [(diff: TimeInterval, fi: Int, pi: Int)] = []
        for (fi, f) in active.enumerated() {
            for (pi, p) in procs.enumerated() {
                pairs.append((abs(f.creation.timeIntervalSince(p.startTime)), fi, pi))
            }
        }
        pairs.sort { $0.diff < $1.diff }

        var usedFile = Set<Int>(), usedProc = Set<Int>()
        var chosen: [(url: URL, proc: LiveClaude)] = []
        for pair in pairs {
            if usedFile.contains(pair.fi) || usedProc.contains(pair.pi) { continue }
            usedFile.insert(pair.fi); usedProc.insert(pair.pi)
            chosen.append((active[pair.fi].url, procs[pair.pi]))
        }
        // 兜底（理论上不会触发，因 active.count ≤ procs.count）
        for fi in active.indices where !usedFile.contains(fi) {
            chosen.append((active[fi].url, procs[0]))
        }
        return chosen
    }

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
            guard let p = parseAssistantUsageLine(line) else { return }
            // 上下文用最后一条（重复行 usage 相同，无影响），与去重无关
            if !p.model.isEmpty { r.lastModel = p.model }
            if !p.sessionId.isEmpty { r.sid = p.sessionId }
            if !p.cwd.isEmpty { r.cwd = p.cwd }
            if let b = p.gitBranch { r.branch = b }
            r.lastCtx = p.contextTokens
            r.peakCtx = max(r.peakCtx, p.contextTokens)

            // 成本去重：同一 messageId 只计一次
            guard seen.insert(p.messageId).inserted else { return }
            r.sawAssistant = true
            r.cost += p.tokens.cost(model: p.model)
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
            contextTokens: main.lastCtx, peakContextTokens: main.peakCtx,
            contextWindow: window, lastActivity: mtime
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

    // 上下文告警：某会话上下文 ≥阈值 提醒一次（建议 /compact）；回落后可再次提醒。
    // 阈值与是否出声由设置驱动（AppDelegate 在设置变化时回写）。
    var contextThreshold: Int = 90
    var criticalSoundEnabled: Bool = true
    private var notifiedContext: Set<String> = []

    private func checkContextNotifications(_ sessions: [SessionInfo]) {
        let threshold = contextThreshold
        for s in sessions where s.contextPercent >= threshold && !notifiedContext.contains(s.id) {
            notifiedContext.insert(s.id)
            NotificationManager.shared.notify(
                id: "ctx-\(s.id)",
                title: tr("上下文将满", "Context Almost Full"),
                body: tr("\(s.projectName) 上下文已用 \(s.contextPercent)%，建议 /compact 或新开会话", "\(s.projectName) context at \(s.contextPercent)% used, consider /compact or a new session"),
                sound: criticalSoundEnabled)
        }
        // 只在"本次扫描确实出现、且已回落到 <阈值"时解除标记。
        // 会话短暂从扫描中消失（非真正结束）不解除，避免重现时重复轰炸。
        for s in sessions where s.contextPercent < threshold {
            notifiedContext.remove(s.id)
        }
    }
}
