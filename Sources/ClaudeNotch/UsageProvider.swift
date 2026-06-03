import Foundation

/// 取额度的结果。来源唯一：Claude Code statusLine 钩子。
enum FetchOutcome {
    case success(ScrapeResult)
    case failure(String)
}

/// 额度数据来源的统一抽象（保留抽象边界，便于将来新增来源）。
@MainActor
protocol UsageProvider {
    func fetchUsage() async -> FetchOutcome
}

/// 从 Claude Code 的 statusLine 钩子取额度——本 app 唯一的额度来源。
///
/// 数据来路（见 `StatuslineHook`）：Claude Code 每次渲染状态栏时，会把一段含
/// `rate_limits.five_hour / .seven_day`（`used_percentage` 0–100、`resets_at` Unix 秒）
/// 的 JSON 通过 stdin 喂给我们注册的命令；钩子把它落盘到 `ratelimits.json`。
/// 这是官方文档化的第三方钩子契约——不抓网页、不复用 OAuth 令牌。
///
/// 局限：仅在 Claude Code 运行时更新。无数据时上层进入「等待」态，提示用户跑一次 Claude Code。
struct StatuslineProvider: UsageProvider {

    func fetchUsage() async -> FetchOutcome {
        guard let data = try? Data(contentsOf: StatuslineHook.ratelimitsFile),
              let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            return .failure("尚未收到 Claude Code 状态栏数据（在任意终端跑一次 claude 即可）")
        }
        guard let rl = obj["rate_limits"] as? [String: Any] else {
            return .failure("状态栏数据缺少 rate_limits 字段")
        }

        // rate_limits.<window> = { used_percentage: 0-100, resets_at: unix 秒 }
        func window(_ key: String) -> (percent: Int, resetAt: Date?)? {
            guard let w = rl[key] as? [String: Any],
                  let used = (w["used_percentage"] as? NSNumber)?.doubleValue else { return nil }
            let at = (w["resets_at"] as? NSNumber).map { Date(timeIntervalSince1970: $0.doubleValue) }
            return (max(0, min(100, Int(used.rounded()))), at)
        }

        var r = ScrapeResult()
        r.capturedAt = (obj["capturedAt"] as? NSNumber).map { Date(timeIntervalSince1970: $0.doubleValue) }
        if let s = window("five_hour") { r.sessionPercent = s.percent; r.sessionResetAt = s.resetAt }
        if let w = window("seven_day") { r.weeklyAllModelsPercent = w.percent; r.weeklyAllModelsResetAt = w.resetAt }
        if let so = window("seven_day_sonnet") { r.weeklySonnetPercent = so.percent; r.weeklySonnetResetAt = so.resetAt }

        guard r.sessionPercent != nil || r.weeklyAllModelsPercent != nil else {
            return .failure("状态栏数据暂无额度字段")
        }
        return .success(r)
    }
}
