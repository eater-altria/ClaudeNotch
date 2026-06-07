import SwiftUI
import Combine

enum StoreState: Equatable {
    case idle
    case loading
    case ready
    case waiting      // 已接好 statusline，但还没收到 Claude Code 喂来的额度数据
    case error(String)
}

@MainActor
final class UsageStore: ObservableObject {

    @Published private(set) var state: StoreState = .idle
    @Published private(set) var snapshot: UsageSnapshot?
    @Published private(set) var lastUpdated: Date?
    /// 是否已接入 statusLine（每次 refresh 时缓存一次，供「等待」空状态读取——
    /// 别在 SwiftUI body 里直接读 StatuslineHook.isInstalled，那会每帧做磁盘 IO）。
    @Published private(set) var statuslineInstalled = false

    // 各指标的消耗速率估算器
    private var estimators: [String: BurnEstimator] = [:]
    @Published private(set) var projections: [String: BurnProjection] = [:]

    // 额度来源按当前代理选择：Claude Code = statusLine 钩子；Codex = 会话 JSONL 内嵌的 rate_limits。
    private let claudeProvider = StatuslineProvider()
    private let codexProvider = CodexUsageProvider()
    private var provider: UsageProvider {
        AgentContext.current == .codex ? codexProvider : claudeProvider
    }
    private var refreshTimer: Timer?
    let refreshInterval: TimeInterval = 300   // 5 分钟

    func start() {
        Task { await refresh() }
        refreshTimer = Timer.scheduledTimer(withTimeInterval: refreshInterval, repeats: true) { [weak self] _ in
            Task { @MainActor in await self?.refresh() }
        }
    }

    /// 切换代理时调用：清掉旧快照与投影，立即按新来源重取（避免显示上一个代理的额度）。
    func agentChanged() {
        snapshot = nil
        lastUpdated = nil
        projections = [:]
        estimators = [:]
        notifiedThreshold = [:]
        state = .idle
        Task { await refresh() }
    }

    func refresh() async {
        if case .loading = state { return }
        // statusLine 仅 Claude 相关；Codex 直接读会话文件，不需要钩子。
        statuslineInstalled = AgentContext.current == .codex ? true : StatuslineHook.isInstalled
        if snapshot == nil { state = .loading }
        let outcome = await provider.fetchUsage()
        if ProcessInfo.processInfo.environment["CLAUDENOTCH_DEBUG"] != nil {
            switch outcome {
            case .failure(let m): NSLog("[ClaudeNotch] fetch -> waiting/failure: %@", m)
            case .success(let r): NSLog("[ClaudeNotch] fetch -> success session=%@ weeklyAll=%@",
                                        String(describing: r.sessionPercent), String(describing: r.weeklyAllModelsPercent))
            }
        }
        switch outcome {
        case .failure:
            // 没数据：首次进入「等待」态；已有旧快照则保留显示，不动 state。
            if snapshot == nil { state = .waiting }
        case .success(let result):
            let when = result.capturedAt ?? Date()
            let snap = UsageSnapshot(from: result, fetchedAt: when)
            updateProjections(for: snap, now: Date())
            checkThresholdNotifications(snap)
            snapshot = snap
            lastUpdated = when
            state = .ready
        }
    }

    // 额度阈值通知：跨过 提示档 / 严重档 各提醒一次；用量回落（窗口刷新）后复位可再次提醒。
    // 阈值与是否出声由设置驱动（AppDelegate 在设置变化时回写）。提示档静默、严重档出声。
    var quotaThresholds: [Int] = [95, 80]   // 高→低，第一个是严重档
    var quotaCriticalBand: Int = 95
    var criticalSoundEnabled: Bool = true
    private var notifiedThreshold: [String: Int] = [:]

    private func checkThresholdNotifications(_ snap: UsageSnapshot) {
        for m in snap.allMetrics {
            let used = m.percentUsed
            let band = quotaThresholds.first(where: { used >= $0 }) ?? 0   // 当前所处档位
            let last = notifiedThreshold[m.id] ?? 0
            if band > last {
                notifiedThreshold[m.id] = band
                NotificationManager.shared.notify(
                    id: "quota-\(m.id)-\(band)",
                    title: tr("\(AgentContext.current.displayName) 额度提醒", "\(AgentContext.current.displayName) Usage Alert"),
                    body: tr("\(m.title) 已用 \(used)%，仅剩 \(m.percentRemaining)%", "\(m.title) at \(used)% used, only \(m.percentRemaining)% left"),
                    sound: criticalSoundEnabled && band >= quotaCriticalBand)
            } else if band < last {
                // 用量回落到更低档：重新武装，使再次升到该档时仍会提醒
                notifiedThreshold[m.id] = band
            }
        }
    }

    private func updateProjections(for snap: UsageSnapshot, now: Date) {
        var newProjections: [String: BurnProjection] = [:]
        for metric in snap.allMetrics {
            let est = estimators[metric.id] ?? BurnEstimator()
            est.record(used: metric.percentUsed, at: now)
            estimators[metric.id] = est
            newProjections[metric.id] = est.project(currentUsed: metric.percentUsed,
                                                    resetMinutesRemaining: metric.resetMinutesRemaining,
                                                    now: now)
        }
        projections = newProjections
    }

    // 折叠态药丸文案
    var headlineText: String {
        switch state {
        case .loading where snapshot == nil: return "…"
        case .waiting: return "—"
        case .error: return "!"
        default:
            if let h = snapshot?.headline { return "\(h.percentRemaining)%" }
            return "—"
        }
    }

    var headlineLevel: UsageLevel {
        snapshot?.headline?.level ?? .ok
    }

    var lastUpdatedText: String {
        guard let t = lastUpdated else { return tr("尚未更新", "Not updated yet") }
        let f = DateFormatter()
        f.dateFormat = "HH:mm"
        return tr("更新于 ", "Updated at ") + f.string(from: t)
    }

    // MARK: - 数据新鲜度（额度只在 Claude Code 渲染状态栏时更新，可能悄悄过期）

    /// 超过此时长仍未更新即视为「陈旧」：环/药丸调暗、隐藏消耗投影（基于旧样本会越算越离谱）。
    let staleAfter: TimeInterval = 30 * 60

    var isStale: Bool {
        guard let t = lastUpdated else { return false }
        return Date().timeIntervalSince(t) > staleAfter
    }

    /// 相对新鲜度文案：「刚刚更新」/「12 分钟前更新」/「2 小时前更新」。
    var freshnessText: String {
        guard let t = lastUpdated else { return tr("尚未更新", "Not updated yet") }
        let secs = Date().timeIntervalSince(t)
        if secs < 90 { return tr("刚刚更新", "Just updated") }
        let mins = Int(secs / 60)
        if mins < 60 { return tr("\(mins) 分钟前更新", "Updated \(mins) min ago") }
        let h = mins / 60
        if h < 24 { return tr("\(h) 小时前更新", "Updated \(h) h ago") }
        return tr("\(h / 24) 天前更新", "Updated \(h / 24) d ago")
    }

    /// 仅在不陈旧时给出投影（陈旧数据的「还剩 N 分钟用尽」会脱离现实地一直缩小）。
    func liveProjection(for id: String) -> BurnProjection? {
        isStale ? nil : projections[id]
    }
}
