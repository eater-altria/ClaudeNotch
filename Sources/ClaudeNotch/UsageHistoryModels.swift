import Foundation

// MARK: - token 桶（按种类，可加）

struct TokenBuckets: Codable, Equatable {
    var input = 0
    var output = 0
    var cacheRead = 0
    var cacheWrite5m = 0
    var cacheWrite1h = 0

    /// 全部 token（含 cache_read——通常远大于其余，会主导）
    var total: Int { input + output + cacheRead + cacheWrite5m + cacheWrite1h }
    /// 计费向 token：input+output+缓存写，排除 cache_read（默认热力指标）
    var billable: Int { input + output + cacheWrite5m + cacheWrite1h }

    static func + (a: TokenBuckets, b: TokenBuckets) -> TokenBuckets {
        TokenBuckets(input: a.input + b.input, output: a.output + b.output,
                     cacheRead: a.cacheRead + b.cacheRead,
                     cacheWrite5m: a.cacheWrite5m + b.cacheWrite5m,
                     cacheWrite1h: a.cacheWrite1h + b.cacheWrite1h)
    }
    static func += (a: inout TokenBuckets, b: TokenBuckets) { a = a + b }

    /// 按某模型单价折算 ≈花费（USD）。复用既有 ModelPricing。
    func cost(model: String) -> Double {
        let p = ModelPricing.lookup(model)
        return (Double(input) * p.input
                + Double(output) * p.output
                + Double(cacheRead) * p.cacheRead
                + Double(cacheWrite5m) * p.cacheWrite5m
                + Double(cacheWrite1h) * p.cacheWrite1h) / 1_000_000.0
    }
}

// MARK: - 热力指标 / 时间范围

enum HeatmapMetric: String, CaseIterable, Identifiable {
    case billable, cost, total
    var id: String { rawValue }
    var label: String {
        switch self {
        case .billable: return "Billable tokens"
        case .cost: return "≈ 花费"
        case .total: return "Total tokens"
        }
    }
}

enum HistoryRange: String, CaseIterable, Identifiable {
    case m3, m6, m12, all
    var id: String { rawValue }
    var label: String {
        switch self {
        case .m3: return "3 个月"
        case .m6: return "6 个月"
        case .m12: return "12 个月"
        case .all: return "全部"
        }
    }
    /// 起始日期（nil = 全部，用最早数据）。
    func startDate(now: Date, calendar: Calendar = .current) -> Date? {
        switch self {
        case .m3: return calendar.date(byAdding: .month, value: -3, to: now)
        case .m6: return calendar.date(byAdding: .month, value: -6, to: now)
        case .m12: return calendar.date(byAdding: .month, value: -12, to: now)
        case .all: return nil
        }
    }
}

// MARK: - 本地日键（yyyymmdd，按 Calendar.current）

typealias LocalDay = Int

enum DayKey {
    static func from(_ date: Date, _ cal: Calendar = .current) -> LocalDay {
        let c = cal.dateComponents([.year, .month, .day], from: date)
        return (c.year ?? 0) * 10000 + (c.month ?? 0) * 100 + (c.day ?? 0)
    }
    static func toDate(_ day: LocalDay, _ cal: Calendar = .current) -> Date? {
        var c = DateComponents()
        c.year = day / 10000; c.month = (day / 100) % 100; c.day = day % 100
        return cal.date(from: c)
    }
}

// MARK: - 单日统计

struct DayStat: Codable, Equatable {
    var tokens = TokenBuckets()
    var cost = 0.0                       // ≈USD，已去重、按各消息模型计价
    var messageCount = 0                 // 去重后的 assistant 响应数
    var perModel: [String: TokenBuckets] = [:]   // 原始 model id -> token
    var perProject: [String: Int] = [:]          // 项目名 -> billable token
    var byHour: [Int: Int] = [:]                  // 本地小时 0..23 -> billable token

    /// 累加一条（已去重的）响应。
    mutating func add(_ t: TokenBuckets, model: String, project: String, hour: Int) {
        tokens += t
        cost += t.cost(model: model)
        messageCount += 1
        perModel[model, default: TokenBuckets()] += t
        perProject[project, default: 0] += t.billable
        byHour[hour, default: 0] += t.billable
    }

    /// 合并另一日统计（聚合用）。
    mutating func merge(_ o: DayStat) {
        tokens += o.tokens
        cost += o.cost
        messageCount += o.messageCount
        for (k, v) in o.perModel { perModel[k, default: TokenBuckets()] += v }
        for (k, v) in o.perProject { perProject[k, default: 0] += v }
        for (k, v) in o.byHour { byHour[k, default: 0] += v }
    }

    func metricValue(_ m: HeatmapMetric) -> Double {
        switch m {
        case .billable: return Double(tokens.billable)
        case .cost: return cost
        case .total: return Double(tokens.total)
        }
    }
}

// MARK: - 整段历史

struct UsageHistory {
    var days: [LocalDay: DayStat] = [:]
    var lastBuiltAt: Date = .distantPast

    /// 在给定日键集合上聚合。
    func aggregate<S: Sequence>(_ keys: S) -> DayStat where S.Element == LocalDay {
        var acc = DayStat()
        for k in keys { if let s = days[k] { acc.merge(s) } }
        return acc
    }

    /// 有活动（消息数>0）的日键，升序。
    var activeDayKeys: [LocalDay] { days.keys.filter { (days[$0]?.messageCount ?? 0) > 0 }.sorted() }

    func keys(onOrAfter day: LocalDay) -> [LocalDay] { days.keys.filter { $0 >= day } }

    var lifetime: DayStat { aggregate(days.keys) }

    /// 今天的统计。
    func today(now: Date = Date()) -> DayStat { days[DayKey.from(now)] ?? DayStat() }

    /// 最近 n 个本地日（含今天）的聚合。
    func recent(days n: Int, now: Date = Date()) -> DayStat {
        let cal = Calendar.current
        let cutoffDate = cal.date(byAdding: .day, value: -(n - 1), to: cal.startOfDay(for: now)) ?? now
        return aggregate(keys(onOrAfter: DayKey.from(cutoffDate, cal)))
    }

    /// 某范围内的日键（升序）。
    func dayKeys(in range: HistoryRange, now: Date = Date()) -> [LocalDay] {
        guard let start = range.startDate(now: now) else { return days.keys.sorted() }
        return keys(onOrAfter: DayKey.from(start)).sorted()
    }

    /// 连续活跃天数（含今天或昨天起算），最长连续，最忙一天（按某指标）。
    func streaks(metric: HeatmapMetric, now: Date = Date()) -> (current: Int, longest: Int, busiest: (day: LocalDay, value: Double)?) {
        let active = Set(activeDayKeys)
        guard !active.isEmpty else { return (0, 0, nil) }
        let cal = Calendar.current

        // 最长连续：把日键转成可前后相邻判断
        let sorted = active.sorted()
        var longest = 1, run = 1
        for i in 1..<max(1, sorted.count) {
            if let prev = DayKey.toDate(sorted[i - 1]), let cur = DayKey.toDate(sorted[i]),
               let next = cal.date(byAdding: .day, value: 1, to: prev),
               cal.isDate(next, inSameDayAs: cur) {
                run += 1; longest = max(longest, run)
            } else { run = 1 }
        }
        if sorted.count == 1 { longest = 1 }

        // 当前连续：从今天往回数；今天没活动则允许从昨天起
        var current = 0
        var cursor = now
        if !active.contains(DayKey.from(now)) {
            cursor = cal.date(byAdding: .day, value: -1, to: now) ?? now
            if !active.contains(DayKey.from(cursor)) { current = 0; cursor = now }
        }
        if active.contains(DayKey.from(cursor)) {
            while active.contains(DayKey.from(cursor)) {
                current += 1
                cursor = cal.date(byAdding: .day, value: -1, to: cursor) ?? cursor
            }
        }

        let busiest = active.map { (day: $0, value: days[$0]?.metricValue(metric) ?? 0) }
            .max { $0.value < $1.value }
        return (current, longest, busiest)
    }
}

// MARK: - 单行解析结果（共享给「活跃会话」与「历史统计」两条路径）

struct ParsedUsageLine {
    let messageId: String       // message.id ?? uuid ?? ""（用于去重）
    let timestampRaw: String?   // ISO8601 字符串，按需懒解析（活跃路径用不上）
    let model: String
    let cwd: String
    let sessionId: String
    let gitBranch: String?
    let tokens: TokenBuckets
    /// 上下文占用 = 输入 + 缓存读 + 缓存写（与挂件口径一致）
    var contextTokens: Int { tokens.input + tokens.cacheRead + tokens.cacheWrite5m + tokens.cacheWrite1h }
}

@inline(__always) private func jint(_ a: Any?) -> Int { (a as? NSNumber)?.intValue ?? 0 }

/// 解析一行 transcript：仅 type=assistant 且带 usage 的行返回非 nil。
/// 这是「按 messageId 去重 + 按模型计价」的唯一真源，活跃会话与历史统计都 fold 它。
func parseAssistantUsageLine(_ line: String) -> ParsedUsageLine? {
    guard let d = line.data(using: .utf8),
          let o = try? JSONSerialization.jsonObject(with: d) as? [String: Any],
          (o["type"] as? String) == "assistant",
          let msg = o["message"] as? [String: Any],
          let usage = msg["usage"] as? [String: Any] else { return nil }

    var cw5 = 0, cw1h = 0
    if let cc = usage["cache_creation"] as? [String: Any] {
        cw5 = jint(cc["ephemeral_5m_input_tokens"])
        cw1h = jint(cc["ephemeral_1h_input_tokens"])
    } else {
        cw5 = jint(usage["cache_creation_input_tokens"])
    }
    let tokens = TokenBuckets(
        input: jint(usage["input_tokens"]),
        output: jint(usage["output_tokens"]),
        cacheRead: jint(usage["cache_read_input_tokens"]),
        cacheWrite5m: cw5, cacheWrite1h: cw1h)

    return ParsedUsageLine(
        messageId: (msg["id"] as? String) ?? (o["uuid"] as? String) ?? "",
        timestampRaw: o["timestamp"] as? String,
        model: (msg["model"] as? String) ?? "",
        cwd: (o["cwd"] as? String) ?? "",
        sessionId: (o["sessionId"] as? String) ?? "",
        gitBranch: o["gitBranch"] as? String,
        tokens: tokens)
}

/// 模型短名：claude-opus-4-8 -> Opus 4.8；未知模型原样返回。（独立于 SessionInfo 的同名逻辑）
func shortModelName(_ model: String) -> String {
    let m = model.lowercased()
    func ver() -> String {
        if let r = model.range(of: #"\d+-\d+"#, options: .regularExpression) {
            return String(model[r]).replacingOccurrences(of: "-", with: ".")
        }
        return ""
    }
    let v = ver()
    if m.contains("opus") { return "Opus \(v)".trimmingCharacters(in: .whitespaces) }
    if m.contains("sonnet") { return "Sonnet \(v)".trimmingCharacters(in: .whitespaces) }
    if m.contains("haiku") { return "Haiku \(v)".trimmingCharacters(in: .whitespaces) }
    return model
}

/// 合成 / 占位模型（无真实 token），从「按模型」面板排除。
func isSyntheticModel(_ model: String) -> Bool {
    model.isEmpty || model == "<synthetic>" || model == "?"
}

// MARK: - 磁盘缓存结构（每文件贡献，便于增量）

struct FileContribution: Codable {
    var days: [LocalDay: DayStat]
}

struct HistoryCache: Codable {
    static let currentVersion = 1
    var version: Int = HistoryCache.currentVersion
    var files: [String: FileContribution] = [:]   // FileKey "path|mtime|size" -> 贡献
}
