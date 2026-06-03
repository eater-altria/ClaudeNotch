import Foundation
import Combine
import AppKit

/// 对接 LiteLLM 的在线模型价表（`model_prices_and_context_window.json`），把第三方子代理模型
/// 的「按 Sonnet 近似」换成真实单价。数据来自 BerriAI/litellm 的公开 raw JSON（无需认证、不复用任何令牌）。
///
/// 取数：构建时把快照打进 app 资源（首次/离线即用真实价）→ 后台每周拉一次最新表落盘缓存覆盖 →
/// 设置里可手动刷新。线程安全：解析后的不可变表存进 `PriceCatalog.shared`，扫描线程只读。

// MARK: - 名字归一化

/// 把 transcript 里记的裸模型名与 LiteLLM 带 provider/region/日期前后缀的 key 收敛到同一形式，
/// 两侧用**同一函数**归一化，故只要能对上就匹配（不追求归一化后的「正确学名」，只追求两边一致）。
func normalizeModelName(_ raw: String) -> String {
    var m = raw.lowercased()
    // 0. 去括号变体标记：claude-opus-4-8[1m] -> claude-opus-4-8（1M 上下文变体，价表无单独条目）
    m = m.replacingOccurrences(of: "\\[[^\\]]*\\]", with: "", options: .regularExpression)
    // 1. provider 路径前缀：openrouter/xiaomi/mimo-v2.5-pro -> 取最后一段
    if let slash = m.lastIndex(of: "/") { m = String(m[m.index(after: slash)...]) }
    // 2. region/provider 点号前缀：us.anthropic.claude-... -> claude-...（注意版本号里的点如 v2.5 不能误删）
    let dropHeads: Set<String> = ["us", "eu", "global", "au", "apac", "anthropic",
                                  "bedrock", "azure", "vertex_ai", "openai", "gemini"]
    while let dot = m.firstIndex(of: ".") {
        let head = String(m[..<dot])
        if dropHeads.contains(head) { m = String(m[m.index(after: dot)...]) } else { break }
    }
    // 3. 去尾巴：版本/快照后缀
    for suf in ["-v1:0", "-v2:0", ":0", "-latest"] where m.hasSuffix(suf) {
        m = String(m.dropLast(suf.count))
    }
    // 4. 去结尾 8 位日期 "-20250514"
    if let r = m.range(of: "-[0-9]{8}$", options: .regularExpression) { m = String(m[..<r.lowerBound]) }
    if m.hasSuffix("-v1") { m = String(m.dropLast(3)) }
    return m
}

// MARK: - 线程安全的价表

/// 解析好的不可变价表，按归一化名索引。扫描线程（后台串行队列 / SessionScanner）只读。
final class PriceCatalog: @unchecked Sendable {
    static let shared = PriceCatalog()

    private let lock = NSLock()
    private var table: [String: ModelPricing] = [:]        // LiteLLM 价表：normalized name -> pricing
    private var overrides: [String: ModelPricing] = [:]    // 用户手动覆盖（优先级最高）

    var count: Int { lock.lock(); defer { lock.unlock() }; return table.count }
    var overrideCount: Int { lock.lock(); defer { lock.unlock() }; return overrides.count }

    func install(_ t: [String: ModelPricing]) { lock.lock(); table = t; lock.unlock() }
    func installOverrides(_ t: [String: ModelPricing]) { lock.lock(); overrides = t; lock.unlock() }

    /// 命中返回真实价（手动覆盖优先于 LiteLLM 表）；未命中（含表为空）返回 nil，由 `ModelPricing.fallback` 兜底。
    func match(_ model: String) -> ModelPricing? {
        let key = normalizeModelName(model)
        lock.lock(); defer { lock.unlock() }
        return overrides[key] ?? table[key]
    }

    // MARK: 从 LiteLLM JSON 构建

    /// 解析 LiteLLM 的整表 JSON。失败返回 nil（保留当前已装载的表）。
    static func parse(_ data: Data) -> [String: ModelPricing]? {
        guard let root = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] else { return nil }
        var best: [String: (pricing: ModelPricing, score: Int)] = [:]
        for (rawKey, any) in root {
            guard let e = any as? [String: Any],
                  let inTok = num(e["input_cost_per_token"]) else { continue }   // 无 input 价 = 非 chat 条目，跳过
            let outTok = num(e["output_cost_per_token"]) ?? 0
            let crTok = num(e["cache_read_input_token_cost"]) ?? (inTok * 0.1)
            let cw5Tok = num(e["cache_creation_input_token_cost"]) ?? (inTok * 1.25)
            let cw1hTok = num(e["cache_creation_input_token_cost_above_1hr"]) ?? cw5Tok
            let norm = normalizeModelName(rawKey)
            // 窗口：LiteLLM 的 max_input_tokens 不反映 Claude 的 1M beta，故取「它」与本地按族兜底的较大者
            let llmWin = Int(num(e["max_input_tokens"]) ?? 0)
            let win = max(llmWin, ModelPricing.fallbackWindow(norm))
            let pricing = ModelPricing(input: inTok * 1e6, output: outTok * 1e6, cacheRead: crTok * 1e6,
                                       cacheWrite5m: cw5Tok * 1e6, cacheWrite1h: cw1hTok * 1e6, window: win)
            // 同一归一化名可能来自多个 key（直连 / bedrock 区域镜像 / openrouter 等），保留「最规范」的：
            // 原始 key 里的 '/' 和 '.' 越少越规范。
            let score = -(rawKey.filter { $0 == "/" }.count * 10 + rawKey.filter { $0 == "." }.count)
            if let cur = best[norm], cur.score >= score { continue }
            best[norm] = (pricing, score)
        }
        guard !best.isEmpty else { return nil }
        return best.mapValues { $0.pricing }
    }

    /// 解析用户手动覆盖文件。**单价单位 = 美元/百万 token（$/MTok）**，比 LiteLLM 的每 token 更好填。
    /// 缺省的缓存项按 input 推算；`_` 开头的键当注释跳过。
    static func parseOverrides(_ data: Data) -> [String: ModelPricing]? {
        guard let root = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] else { return nil }
        var out: [String: ModelPricing] = [:]
        for (rawKey, any) in root where !rawKey.hasPrefix("_") {
            guard let e = any as? [String: Any], let input = num(e["input"]) else { continue }
            let norm = normalizeModelName(rawKey)
            let output = num(e["output"]) ?? 0
            let cr = num(e["cache_read"]) ?? input * 0.1
            let cw5 = num(e["cache_write_5m"]) ?? input * 1.25
            let cw1h = num(e["cache_write_1h"]) ?? cw5
            let win = Int(num(e["window"]) ?? 0)
            out[norm] = ModelPricing(input: input, output: output, cacheRead: cr,
                                     cacheWrite5m: cw5, cacheWrite1h: cw1h,
                                     window: max(win, ModelPricing.fallbackWindow(norm)))
        }
        return out
    }

    private static func num(_ a: Any?) -> Double? {
        if let n = a as? NSNumber { return n.doubleValue }
        if let s = a as? String { return Double(s) }
        return nil
    }
}

// MARK: - 加载 / 刷新（内置快照 + 每周后台刷新 + 手动刷新）

@MainActor
final class ModelPriceStore: ObservableObject {
    /// LiteLLM 公开整表（无认证）。
    static let remoteURL = URL(string:
        "https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json")!
    private static let refreshInterval: TimeInterval = 7 * 24 * 3600   // 每周

    @Published private(set) var modelCount = 0
    @Published private(set) var lastUpdated: Date?       // 联网刷新成功的时刻；nil = 仅内置快照
    @Published private(set) var isRefreshing = false
    @Published private(set) var lastError: String?
    @Published private(set) var overrideCount = 0        // 已生效的手动价格覆盖条数

    private static var cacheFile: URL { StatuslineHook.supportDir.appendingPathComponent("litellm_prices.json") }
    static var overridesFile: URL { StatuslineHook.supportDir.appendingPathComponent("model-price-overrides.json") }
    private static let fetchedAtKey = "litellmFetchedAt"

    /// 启动调用：先用「磁盘缓存或内置快照」即时装表，再按需后台刷新（缺缓存或超 7 天）。
    func bootstrap() {
        DispatchQueue.global(qos: .utility).async { [weak self] in
            // 1. 优先磁盘缓存（联网刷新过的最新表），否则退回打进包里的内置快照
            let cache = Self.cacheFile
            var loaded = false
            if let data = try? Data(contentsOf: cache), let t = PriceCatalog.parse(data) {
                PriceCatalog.shared.install(t); loaded = true
            } else if let bundled = Bundle.main.url(forResource: "litellm_prices", withExtension: "json"),
                      let data = try? Data(contentsOf: bundled), let t = PriceCatalog.parse(data) {
                PriceCatalog.shared.install(t); loaded = true
            }
            // 装载用户手动覆盖（独立于网络，优先级最高）
            let ov = Self.readOverrides()
            PriceCatalog.shared.installOverrides(ov)
            let count = PriceCatalog.shared.count
            let ovCount = ov.count
            let fetchedAt = (UserDefaults.standard.object(forKey: Self.fetchedAtKey) as? Date)
            DispatchQueue.main.async {
                self?.modelCount = count
                self?.overrideCount = ovCount
                self?.lastUpdated = FileManager.default.fileExists(atPath: cache.path) ? fetchedAt : nil
            }
            // 2. 没缓存、或缓存过期 -> 后台拉最新（失败静默，保留已装载的快照）
            let stale = fetchedAt.map { Date().timeIntervalSince($0) > Self.refreshInterval } ?? true
            if loaded && stale || !loaded { DispatchQueue.main.async { self?.refresh() } }
        }
    }

    /// 手动 / 自动刷新：先重读手动覆盖（便宜，拾取用户刚才的编辑），再联网拉最新表 → 解析 → 落盘缓存 → 热替换。
    func refresh() {
        if isRefreshing { return }
        reloadOverrides()
        isRefreshing = true
        lastError = nil
        var req = URLRequest(url: Self.remoteURL)
        req.timeoutInterval = 20
        URLSession.shared.dataTask(with: req) { [weak self] data, _, err in
            let parsed = data.flatMap { PriceCatalog.parse($0) }
            DispatchQueue.main.async {
                guard let self else { return }
                self.isRefreshing = false
                if let table = parsed, let data {
                    PriceCatalog.shared.install(table)
                    try? FileManager.default.createDirectory(at: StatuslineHook.supportDir,
                                                             withIntermediateDirectories: true)
                    try? data.write(to: Self.cacheFile)
                    let now = Date()
                    UserDefaults.standard.set(now, forKey: Self.fetchedAtKey)
                    self.modelCount = table.count
                    self.lastUpdated = now
                } else {
                    self.lastError = err?.localizedDescription ?? "价表解析失败"
                }
            }
        }.resume()
    }

    // MARK: 手动价格覆盖

    private static func readOverrides() -> [String: ModelPricing] {
        guard let data = try? Data(contentsOf: overridesFile),
              let t = PriceCatalog.parseOverrides(data) else { return [:] }
        return t
    }

    /// 重读覆盖文件并热替换（设置里「刷新价格」会一并触发，也可单独调用）。
    func reloadOverrides() {
        DispatchQueue.global(qos: .utility).async { [weak self] in
            let t = Self.readOverrides()
            PriceCatalog.shared.installOverrides(t)
            DispatchQueue.main.async { self?.overrideCount = t.count }
        }
    }

    /// 打开覆盖文件供编辑（不存在则先写入带注释的模板）。编辑保存后回设置点「刷新价格」生效。
    func openOverridesForEditing() {
        let url = Self.overridesFile
        if !FileManager.default.fileExists(atPath: url.path) {
            try? FileManager.default.createDirectory(at: StatuslineHook.supportDir, withIntermediateDirectories: true)
            try? Data(Self.overrideTemplate.utf8).write(to: url)
        }
        NSWorkspace.shared.open(url)
    }

    /// 模板：单价单位 $/MTok；键为模型名（大小写、provider/日期前后缀均无关）。deepseek 示例为占位真实价。
    private static let overrideTemplate = """
    {
      "_说明": "手动价格覆盖。单价单位 = 美元/百万 token ($/MTok)。键为模型名(大小写、provider/日期前后缀无关)。优先级高于 LiteLLM 在线表。编辑保存后回设置点『刷新价格』生效。",
      "_可选字段": "input(必填) / output / cache_read / cache_write_5m / cache_write_1h / window;缺省的缓存项按 input 推算。",
      "deepseek-v4-pro": {
        "input": 0.28,
        "output": 0.42,
        "cache_read": 0.028,
        "cache_write_5m": 0.28
      }
    }
    """
}
