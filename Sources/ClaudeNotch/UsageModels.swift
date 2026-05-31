import Foundation
import SwiftUI

// MARK: - 原始抓取结果（与 JS 注入脚本返回的 JSON 字段一一对应）

struct ScrapeResult: Codable {
    var sessionPercent: Int?
    var sessionResetTime: String?
    var weeklyAllModelsPercent: Int?
    var weeklyAllModelsReset: String?
    var weeklySonnetPercent: Int?
    var weeklySonnetReset: String?
    var extraSpent: Double?
    var extraLimit: Double?
    var extraBalance: Double?
    var extraPercent: Int?
    var extraReset: String?
    var isLoggedIn: Bool?
    var error: String?
}

// MARK: - 颜色阈值

enum UsageLevel {
    case ok, warn, critical

    init(percentUsed: Int) {
        if percentUsed >= 95 { self = .critical }
        else if percentUsed >= 80 { self = .warn }
        else { self = .ok }
    }

    var color: Color {
        switch self {
        case .ok: return Color(red: 0.30, green: 0.83, blue: 0.45)      // green
        case .warn: return Color(red: 0.98, green: 0.67, blue: 0.20)    // orange
        case .critical: return Color(red: 0.95, green: 0.30, blue: 0.30) // red
        }
    }
}

// MARK: - 单个额度指标

struct UsageMetric: Identifiable {
    let id: String
    let title: String          // “当前会话” / “本周·全模型” …
    let percentUsed: Int       // 已用百分比
    let resetRaw: String?      // 原始刷新文案，如 "1 hr 57 min" / "Thu 11:00 AM"

    var percentRemaining: Int { max(0, 100 - percentUsed) }
    var level: UsageLevel { UsageLevel(percentUsed: percentUsed) }

    /// 若刷新文案是相对时长（"X hr Y min"），解析成剩余分钟数，否则 nil
    var resetMinutesRemaining: Int? { Self.parseRelativeMinutes(resetRaw) }

    /// 友好的刷新时间展示
    var resetDisplay: String {
        guard let raw = resetRaw, !raw.isEmpty else { return "—" }
        if let mins = Self.parseRelativeMinutes(raw) {
            return Self.formatDuration(minutes: mins) + "后"
        }
        return raw   // 绝对时间（"Thu 11:00 AM"）直接显示
    }

    static func parseRelativeMinutes(_ raw: String?) -> Int? {
        guard let raw = raw else { return nil }
        let hr = firstInt(in: raw, pattern: #"(\d+)\s*hr"#)
        let min = firstInt(in: raw, pattern: #"(\d+)\s*min"#)
        if hr == nil && min == nil { return nil }
        return (hr ?? 0) * 60 + (min ?? 0)
    }

    static func formatDuration(minutes: Int) -> String {
        if minutes <= 0 { return "即将" }
        let h = minutes / 60
        let m = minutes % 60
        if h > 24 {
            let d = h / 24
            return "\(d) 天"
        }
        if h > 0 && m > 0 { return "\(h) 小时 \(m) 分" }
        if h > 0 { return "\(h) 小时" }
        return "\(m) 分钟"
    }

    private static func firstInt(in text: String, pattern: String) -> Int? {
        guard let re = try? NSRegularExpression(pattern: pattern, options: [.caseInsensitive]) else { return nil }
        let range = NSRange(text.startIndex..., in: text)
        guard let m = re.firstMatch(in: text, range: range), m.numberOfRanges > 1,
              let r = Range(m.range(at: 1), in: text) else { return nil }
        return Int(text[r])
    }
}

// MARK: - 一次完整的额度快照

struct UsageSnapshot {
    var session: UsageMetric?
    var weeklyAll: UsageMetric?
    var weeklySonnet: UsageMetric?
    var extraPercent: Int?
    var extraSpent: Double?
    var extraLimit: Double?
    var extraBalance: Double?
    var extraReset: String?
    var fetchedAt: Date

    /// 折叠态主指标：优先展示“当前会话”，否则取已用最高的周指标
    var headline: UsageMetric? {
        if let s = session { return s }
        let weeklies = [weeklyAll, weeklySonnet].compactMap { $0 }
        return weeklies.max(by: { $0.percentUsed < $1.percentUsed })
    }

    var allMetrics: [UsageMetric] {
        [session, weeklyAll, weeklySonnet].compactMap { $0 }
    }

    init(from r: ScrapeResult, fetchedAt: Date) {
        self.fetchedAt = fetchedAt
        if let p = r.sessionPercent {
            session = UsageMetric(id: "session", title: "当前会话",
                                  percentUsed: p, resetRaw: r.sessionResetTime)
        }
        if let p = r.weeklyAllModelsPercent {
            weeklyAll = UsageMetric(id: "weeklyAll", title: "本周 · 全模型",
                                    percentUsed: p, resetRaw: r.weeklyAllModelsReset)
        }
        if let p = r.weeklySonnetPercent {
            weeklySonnet = UsageMetric(id: "weeklySonnet", title: "本周 · Sonnet",
                                       percentUsed: p, resetRaw: r.weeklySonnetReset)
        }
        extraPercent = r.extraPercent
        extraSpent = r.extraSpent
        extraLimit = r.extraLimit
        extraBalance = r.extraBalance
        extraReset = r.extraReset
    }
}

// MARK: - 消耗速率投影（预计用完时间）

struct BurnProjection {
    /// 距离用尽（达到 100%）还剩多少分钟，nil 表示无法估算
    let minutesToEmpty: Int?
    /// 本周期内是否会在刷新前耗尽
    let willRunOutBeforeReset: Bool
    /// 文案
    let display: String
}

/// 维护某个指标的历史采样，估算消耗速率
final class BurnEstimator {
    private struct Sample { let t: Date; let used: Int }
    private var samples: [Sample] = []
    private let maxSamples = 12

    func record(used: Int, at time: Date) {
        // 若百分比回落（窗口刷新了），清空历史重新计
        if let last = samples.last, used < last.used - 2 {
            samples.removeAll()
        }
        samples.append(Sample(t: time, used: used))
        if samples.count > maxSamples { samples.removeFirst(samples.count - maxSamples) }
    }

    /// 每分钟消耗的百分点；nil 表示样本不足
    func ratePerMinute() -> Double? {
        guard let first = samples.first, let last = samples.last else { return nil }
        let dt = last.t.timeIntervalSince(first.t) / 60.0
        guard dt >= 1.0 else { return nil }   // 至少跨越 1 分钟
        let dUsed = Double(last.used - first.used)
        guard dUsed > 0 else { return nil }    // 没有净消耗
        return dUsed / dt
    }

    func project(currentUsed: Int, resetMinutesRemaining: Int?, now: Date) -> BurnProjection {
        guard currentUsed < 100 else {
            return BurnProjection(minutesToEmpty: 0, willRunOutBeforeReset: true, display: "已用尽")
        }
        guard let rate = ratePerMinute(), rate > 0 else {
            return BurnProjection(minutesToEmpty: nil, willRunOutBeforeReset: false,
                                  display: samples.count < 2 ? "计算中…" : "无明显消耗")
        }
        let remaining = Double(100 - currentUsed)
        let mins = Int((remaining / rate).rounded())
        if let resetMin = resetMinutesRemaining, mins >= resetMin {
            return BurnProjection(minutesToEmpty: mins, willRunOutBeforeReset: false,
                                  display: "刷新前充足")
        }
        return BurnProjection(minutesToEmpty: mins, willRunOutBeforeReset: true,
                              display: UsageMetric.formatDuration(minutes: mins) + "后用尽")
    }
}
