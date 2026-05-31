import SwiftUI
import Combine

enum StoreState: Equatable {
    case idle
    case loading
    case ready
    case loggedOut
    case error(String)
}

@MainActor
final class UsageStore: ObservableObject {

    @Published private(set) var state: StoreState = .idle
    @Published private(set) var snapshot: UsageSnapshot?
    @Published private(set) var lastUpdated: Date?

    // 各指标的消耗速率估算器
    private var estimators: [String: BurnEstimator] = [:]
    @Published private(set) var projections: [String: BurnProjection] = [:]

    private let session = ClaudeSession()
    private var refreshTimer: Timer?
    let refreshInterval: TimeInterval = 300   // 5 分钟

    init() {
        session.onLoginSuccess = { [weak self] in
            Task { await self?.refresh() }
        }
    }

    func start() {
        Task { await refresh() }
        refreshTimer = Timer.scheduledTimer(withTimeInterval: refreshInterval, repeats: true) { [weak self] _ in
            Task { @MainActor in await self?.refresh() }
        }
    }

    func presentLogin() {
        session.presentLogin()
    }

    /// 是否已登录（用于状态栏菜单显示 登录/退出登录）
    var isLoggedIn: Bool {
        if case .loggedOut = state { return false }
        return snapshot != nil
    }

    func logout() {
        session.logout { [weak self] in
            Task { @MainActor in
                self?.snapshot = nil
                self?.lastUpdated = nil
                self?.state = .loggedOut
            }
        }
    }

    func refresh() async {
        if case .loading = state { return }
        if snapshot == nil { state = .loading }
        let outcome = await session.fetchUsage()
        if ProcessInfo.processInfo.environment["CLAUDENOTCH_DEBUG"] != nil {
            switch outcome {
            case .loggedOut: NSLog("[ClaudeNotch] fetch -> loggedOut")
            case .failure(let m): NSLog("[ClaudeNotch] fetch -> failure: %@", m)
            case .success(let r): NSLog("[ClaudeNotch] fetch -> success session=%@ weeklyAll=%@",
                                        String(describing: r.sessionPercent), String(describing: r.weeklyAllModelsPercent))
            }
        }
        switch outcome {
        case .loggedOut:
            state = .loggedOut
        case .failure(let msg):
            // 已有旧数据时保留展示，仅在无数据时进入 error
            if snapshot == nil { state = .error(msg) }
        case .success(let result):
            let now = Date()
            let snap = UsageSnapshot(from: result, fetchedAt: now)
            updateProjections(for: snap, now: now)
            snapshot = snap
            lastUpdated = now
            state = .ready
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
        case .loggedOut: return "登录"
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
        guard let t = lastUpdated else { return "尚未更新" }
        let f = DateFormatter()
        f.dateFormat = "HH:mm"
        return "更新于 " + f.string(from: t)
    }
}
