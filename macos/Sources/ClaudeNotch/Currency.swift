import Foundation
import Combine

/// 金额显示：内部一律存**美元**（LiteLLM / 覆盖价都是 USD），仅在显示时按当前语言折算。
/// 英语 → `$1.23`；中文 → `¥8.79`（= USD × 实时汇率）。
func money(_ usd: Double, decimals: Int = 2) -> String {
    if LocalizationState.currentLanguage == .zh {
        return "¥" + String(format: "%.\(decimals)f", usd * LocalizationState.usdToCny)
    }
    return "$" + String(format: "%.\(decimals)f", usd)
}

/// 带「≈」前缀的金额（订阅用户为等价折算，非真实扣费，中英文都标）。
func approxMoney(_ usd: Double, decimals: Int = 2) -> String { "≈" + money(usd, decimals: decimals) }

// MARK: - 汇率（USD→CNY）：内置默认 + 联网每周刷新 + 手动刷新

@MainActor
final class ExchangeRateStore: ObservableObject {
    /// 免费公开汇率 API（无需认证）。
    static let remoteURL = URL(string: "https://open.er-api.com/v6/latest/USD")!
    private static let refreshInterval: TimeInterval = 7 * 24 * 3600
    private static let defaultRate = 7.15
    private static let rateKey = "usdToCnyRate"
    private static let fetchedAtKey = "usdToCnyFetchedAt"

    @Published private(set) var rate: Double = ExchangeRateStore.defaultRate
    @Published private(set) var lastUpdated: Date?     // 联网成功时刻；nil = 仅默认值
    @Published private(set) var isRefreshing = false
    @Published private(set) var lastError: String?

    /// 启动：先用缓存（或默认）即时生效，再按需后台刷新（缺缓存或超 7 天）。
    func bootstrap() {
        let d = UserDefaults.standard
        let cached = d.object(forKey: Self.rateKey) as? Double
        let fetchedAt = d.object(forKey: Self.fetchedAtKey) as? Date
        let r = cached ?? Self.defaultRate
        rate = r
        lastUpdated = fetchedAt
        LocalizationState.usdToCny = r
        let stale = fetchedAt.map { Date().timeIntervalSince($0) > Self.refreshInterval } ?? true
        if stale { refresh() }
    }

    /// 手动 / 自动刷新：联网取 USD→CNY → 落盘 → 即时生效。
    func refresh() {
        if isRefreshing { return }
        isRefreshing = true
        lastError = nil
        var req = URLRequest(url: Self.remoteURL)
        req.timeoutInterval = 15
        URLSession.shared.dataTask(with: req) { [weak self] data, _, err in
            let cny: Double? = {
                guard let data,
                      let root = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
                      (root["result"] as? String) == "success",
                      let rates = root["rates"] as? [String: Any],
                      let v = (rates["CNY"] as? NSNumber)?.doubleValue, v > 0 else { return nil }
                return v
            }()
            DispatchQueue.main.async {
                guard let self else { return }
                self.isRefreshing = false
                if let cny {
                    let now = Date()
                    self.rate = cny
                    self.lastUpdated = now
                    LocalizationState.usdToCny = cny
                    UserDefaults.standard.set(cny, forKey: Self.rateKey)
                    UserDefaults.standard.set(now, forKey: Self.fetchedAtKey)
                } else {
                    self.lastError = err?.localizedDescription ?? tr("汇率解析失败", "Failed to parse exchange rate")
                }
            }
        }.resume()
    }
}
