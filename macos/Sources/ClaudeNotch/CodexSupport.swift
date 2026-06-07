import Foundation

/// 受支持的 CLI 代理：Claude Code 或 OpenAI Codex。
/// 两者数据来路不同（见 `CodexSupport`），但落到同一套额度/会话/历史模型上，UI 层无需区分。
enum AgentKind: String, CaseIterable, Identifiable, Codable {
    case claudeCode
    case codex

    var id: String { rawValue }

    /// 展示名（产品名，不翻译）。
    var displayName: String {
        switch self {
        case .claudeCode: return "Claude Code"
        case .codex: return "Codex"
        }
    }

    /// CLI 可执行名（进程探测/提示文案用）。
    var cliName: String {
        switch self {
        case .claudeCode: return "claude"
        case .codex: return "codex"
        }
    }
}

/// 当前选中的代理。数据扫描在后台队列读取它、设置变更时在主线程写入；
/// 用锁包一层即可（值类型、改动极少）。默认 Claude Code。
enum AgentContext {
    private static let lock = NSLock()
    private static var _current: AgentKind = .claudeCode

    static var current: AgentKind {
        get { lock.lock(); defer { lock.unlock() }; return _current }
        set { lock.lock(); _current = newValue; lock.unlock() }
    }
}

// MARK: - Codex 路径

/// OpenAI Codex 的数据目录（`CODEX_HOME` 优先，否则 `~/.codex`）与会话目录。
enum CodexPaths {
    static var home: URL {
        if let env = ProcessInfo.processInfo.environment["CODEX_HOME"], !env.isEmpty {
            return URL(fileURLWithPath: (env as NSString).expandingTildeInPath, isDirectory: true)
        }
        return FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".codex", isDirectory: true)
    }
    /// `~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl`
    static var sessionsDir: URL { home.appendingPathComponent("sessions", isDirectory: true) }
}

// MARK: - Codex JSONL 行解析

@inline(__always) private func cint(_ a: Any?) -> Int {
    if let n = a as? NSNumber { return n.intValue }
    return 0
}
@inline(__always) private func cdouble(_ a: Any?) -> Double? {
    if let n = a as? NSNumber { return n.doubleValue }
    return nil
}

/// Codex 的 `TokenUsage` → 复用 Claude 侧的 `TokenBuckets`。
/// 约定：OpenAI 的 `input_tokens` 含缓存命中部分，故非缓存输入 = input - cached，缓存读 = cached。
/// 这样 `TokenBuckets.cost`（input×in + output×out + cacheRead×cr）就和 OpenAI 计费口径一致。
private func codexBuckets(_ usage: [String: Any]) -> TokenBuckets {
    let input = cint(usage["input_tokens"])
    let cached = cint(usage["cached_input_tokens"])
    let output = cint(usage["output_tokens"])
    return TokenBuckets(input: max(0, input - cached), output: output,
                        cacheRead: cached, cacheWrite5m: 0, cacheWrite1h: 0)
}

/// 一条 Codex rollout 行的归一结果（只取我们关心的几类）。
struct CodexLine {
    enum Kind { case meta, turnContext, tokenCount, other }
    let kind: Kind
    let timestampRaw: String?
    // session_meta
    var cwd: String? = nil
    var sessionId: String? = nil
    // turn_context
    var model: String? = nil
    // token_count
    var lastUsage: [String: Any]? = nil
    var totalUsage: [String: Any]? = nil
    var contextWindow: Int? = nil
    var rateLimits: [String: Any]? = nil
}

/// 解析一行 Codex rollout JSONL。非对象/无法识别返回 nil。
func parseCodexLine(_ line: String) -> CodexLine? {
    guard let d = line.data(using: .utf8),
          let o = try? JSONSerialization.jsonObject(with: d) as? [String: Any] else { return nil }
    let type = o["type"] as? String
    let payload = o["payload"] as? [String: Any] ?? [:]
    let ts = o["timestamp"] as? String
    switch type {
    case "session_meta":
        var l = CodexLine(kind: .meta, timestampRaw: ts)
        l.cwd = payload["cwd"] as? String
        l.sessionId = payload["id"] as? String
        return l
    case "turn_context":
        var l = CodexLine(kind: .turnContext, timestampRaw: ts)
        l.model = payload["model"] as? String
        if l.cwd == nil { l.cwd = payload["cwd"] as? String }
        return l
    case "event_msg":
        guard (payload["type"] as? String) == "token_count" else { return CodexLine(kind: .other, timestampRaw: ts) }
        var l = CodexLine(kind: .tokenCount, timestampRaw: ts)
        if let info = payload["info"] as? [String: Any] {
            l.lastUsage = info["last_token_usage"] as? [String: Any]
            l.totalUsage = info["total_token_usage"] as? [String: Any]
            l.contextWindow = cint(info["model_context_window"])
        }
        l.rateLimits = payload["rate_limits"] as? [String: Any]
        return l
    default:
        return CodexLine(kind: .other, timestampRaw: ts)
    }
}

// MARK: - 文件枚举（共享）

extension CodexPaths {
    /// 枚举 `sessions/**/rollout-*.jsonl`，带 mtime 与大小。
    static func allSessionFiles() -> [(url: URL, mtime: Date, size: Int)] {
        let fm = FileManager.default
        guard let en = fm.enumerator(at: sessionsDir,
                                     includingPropertiesForKeys: [.isRegularFileKey, .contentModificationDateKey, .fileSizeKey],
                                     options: [.skipsHiddenFiles]) else { return [] }
        var out: [(URL, Date, Int)] = []
        for case let url as URL in en where url.pathExtension == "jsonl" && url.lastPathComponent.hasPrefix("rollout-") {
            let v = try? url.resourceValues(forKeys: [.contentModificationDateKey, .fileSizeKey])
            out.append((url, v?.contentModificationDate ?? .distantPast, v?.fileSize ?? 0))
        }
        return out
    }
}

// MARK: - 额度来源（Codex 把额度内嵌在会话 JSONL 的 token_count.rate_limits）

/// 从最近写入的 Codex 会话文件里取 `rate_limits` 快照——Codex 没有 statusLine 钩子，
/// 额度随每轮响应写进会话 JSONL（`event_msg`/`token_count` 的 `rate_limits`）。
/// 取最新 mtime 的若干文件中**最后一条**带非空 rate_limits 的事件。
struct CodexUsageProvider: UsageProvider {
    func fetchUsage() async -> FetchOutcome {
        let files = CodexPaths.allSessionFiles().sorted { $0.mtime > $1.mtime }
        guard !files.isEmpty else {
            return .failure(tr("尚未发现 Codex 会话（在任意终端跑一次 codex 即可）",
                               "No Codex sessions found yet (run codex once in any terminal)"))
        }
        // 只看最近的几个文件，找最后一条 rate_limits。
        var found: (rl: [String: Any], at: Date)?
        for f in files.prefix(6) {
            guard let content = try? String(contentsOf: f.url, encoding: .utf8) else { continue }
            content.enumerateLines { line, _ in
                if let l = parseCodexLine(line), l.kind == .tokenCount, let rl = l.rateLimits, !rl.isEmpty {
                    found = (rl, f.mtime)
                }
            }
            if found != nil { break }   // 最新文件里已有就用它的
        }
        guard let snap = found else {
            return .failure(tr("Codex 会话里暂无额度信息（多跑几轮 codex）",
                               "No quota info in Codex sessions yet (run codex a few more turns)"))
        }

        // RateLimitWindow: { used_percent, window_minutes, resets_at }。按 window_minutes 区分 5h / 周。
        func window(_ obj: Any?) -> (percent: Int, resetAt: Date?, minutes: Int)? {
            guard let w = obj as? [String: Any], let used = cdouble(w["used_percent"]) else { return nil }
            let at = cdouble(w["resets_at"]).map { Date(timeIntervalSince1970: $0) }
            let mins = cint(w["window_minutes"])
            return (max(0, min(100, Int(used.rounded()))), at, mins)
        }
        let wins = [window(snap.rl["primary"]), window(snap.rl["secondary"])].compactMap { $0 }
        guard !wins.isEmpty else {
            return .failure(tr("Codex 额度字段为空", "Codex quota fields are empty"))
        }
        // 较短窗口 = 当前会话(5h)，较长 = 周。
        let sorted = wins.sorted { $0.minutes < $1.minutes }
        var r = ScrapeResult()
        r.capturedAt = snap.at
        if let s = sorted.first { r.sessionPercent = s.percent; r.sessionResetAt = s.resetAt }
        if sorted.count > 1 { let w = sorted[1]; r.weeklyAllModelsPercent = w.percent; r.weeklyAllModelsResetAt = w.resetAt }
        if let plan = snap.rl["plan_type"] as? String, !plan.isEmpty { r.modelName = plan }
        return .success(r)
    }
}

// MARK: - 活跃会话扫描（mtime 近期 = 活跃；Codex 进程 cwd 难取，与 Windows 同策略）

final class CodexSessionScanner {
    private var cache: [String: (mtime: Date, info: SessionInfo?)] = [:]
    /// 活跃窗口：最近 N 内写过的会话视为活跃。
    let activeWindow: TimeInterval = 10 * 60

    func scan(maxAge: TimeInterval) -> [SessionInfo] {
        let cutoff = Date().addingTimeInterval(-activeWindow)
        var result: [SessionInfo] = []
        for f in CodexPaths.allSessionFiles() where f.mtime >= cutoff {
            var info: SessionInfo?
            if let cached = cache[f.url.path], cached.mtime == f.mtime {
                info = cached.info
            } else {
                info = Self.parse(file: f.url, mtime: f.mtime)
                cache[f.url.path] = (f.mtime, info)
            }
            if let i = info { result.append(i) }
        }
        return result.sorted { $0.lastActivity > $1.lastActivity }
    }

    private static func parse(file: URL, mtime: Date) -> SessionInfo? {
        guard let content = try? String(contentsOf: file, encoding: .utf8) else { return nil }
        var cwd = "", sid = "", model = ""
        var cost = 0.0
        var lastCtx = 0, peakCtx = 0, ctxWindow = 0
        var sawUsage = false

        content.enumerateLines { line, _ in
            guard let l = parseCodexLine(line) else { return }
            switch l.kind {
            case .meta:
                if let c = l.cwd { cwd = c }
                if let s = l.sessionId { sid = s }
            case .turnContext:
                if let m = l.model, !m.isEmpty { model = m }
                if cwd.isEmpty, let c = l.cwd { cwd = c }
            case .tokenCount:
                if let last = l.lastUsage {
                    let b = codexBuckets(last)
                    cost += b.cost(model: model)
                    let ctx = b.input + b.cacheRead   // 本轮输入(含缓存) ≈ 当前上下文占用
                    if ctx > 0 { lastCtx = ctx; peakCtx = max(peakCtx, ctx) }
                    sawUsage = true
                }
                if let w = l.contextWindow, w > 0 { ctxWindow = w }
            case .other:
                break
            }
        }
        guard sawUsage || !sid.isEmpty else { return nil }

        let window = ctxWindow > 0 ? ctxWindow : (lastCtx > 200_000 ? 400_000 : 272_000)
        let name = cwd.isEmpty ? "(unknown)" : (cwd as NSString).lastPathComponent
        return SessionInfo(
            id: sid.isEmpty ? file.lastPathComponent : sid,
            projectName: name, cwd: cwd, gitBranch: nil,
            model: model.isEmpty ? "gpt-5-codex" : model, costUSD: cost,
            contextTokens: lastCtx, peakContextTokens: peakCtx,
            contextWindow: window, lastActivity: mtime)
    }
}

// MARK: - 历史聚合（按本地日；token_count 的 last_token_usage 即每轮增量）

enum CodexHistory {
    /// 单个 Codex 会话文件 → 每天贡献。逐行扫描、跟踪当前模型(turn_context)，
    /// 把每条 token_count 的 last_token_usage 按其时间戳计入对应日。
    static func contribution(of url: URL, formatter: ISO8601DateFormatter) -> FileContribution {
        guard let content = try? String(contentsOf: url, encoding: .utf8) else { return FileContribution(days: [:]) }
        var days: [LocalDay: DayStat] = [:]
        var model = "gpt-5-codex"
        var cwd = ""
        let cal = Calendar.current
        content.enumerateLines { line, _ in
            guard let l = parseCodexLine(line) else { return }
            switch l.kind {
            case .meta:
                if let c = l.cwd { cwd = c }
            case .turnContext:
                if let m = l.model, !m.isEmpty { model = m }
                if cwd.isEmpty, let c = l.cwd { cwd = c }
            case .tokenCount:
                guard let last = l.lastUsage else { return }
                let b = codexBuckets(last)
                guard b.total > 0 else { return }
                guard let raw = l.timestampRaw, let date = HistoryScanner.parseISO(raw, formatter) else { return }
                let day = DayKey.from(date, cal)
                let hour = cal.component(.hour, from: date)
                let project = cwd.isEmpty ? "(unknown)" : (cwd as NSString).lastPathComponent
                days[day, default: DayStat()].add(b, model: model, project: project, hour: hour)
            case .other:
                break
            }
        }
        return FileContribution(days: days)
    }
}
