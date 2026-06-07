import SwiftUI
import ServiceManagement

/// 一个可勾选的显示器（id=唯一显示器ID，label=可见名，可能带消歧后缀）
struct ScreenOption: Identifiable, Equatable {
    let id: String
    let label: String
}

/// 应用设置：配色、开机自启、是否启用灵动岛。持久化到 UserDefaults。
@MainActor
final class SettingsStore: ObservableObject {

    /// 当前监控的 CLI 代理（Claude Code / Codex）。切换即更换额度/会话/历史的数据来源。
    @Published var agent: AgentKind {
        didSet {
            guard agent != oldValue else { return }
            UserDefaults.standard.set(agent.rawValue, forKey: Keys.agent)
            AgentContext.current = agent
            onAgentChange?(agent)
        }
    }

    @Published var appearance: AppearanceMode {
        didSet {
            UserDefaults.standard.set(appearance.rawValue, forKey: Keys.appearance)
            applyAppearance()
        }
    }

    @Published var launchAtLogin: Bool {
        didSet { applyLaunchAtLogin() }
    }

    @Published var islandEnabled: Bool {
        didSet {
            UserDefaults.standard.set(islandEnabled, forKey: Keys.islandEnabled)
            onIslandEnabledChange?(islandEnabled)
        }
    }

    /// 在哪些显示器上展示挂件（按屏幕名集合）。为空 = 自动（刘海/主屏）。
    @Published var selectedScreens: Set<String> {
        didSet {
            UserDefaults.standard.set(Array(selectedScreens), forKey: Keys.selectedScreens)
            onDisplaySettingsChange?()
        }
    }

    /// 当前已连接显示器（设置 UI 用，插拔时刷新）
    @Published private(set) var screenOptions: [ScreenOption] = []

    /// 是否开启系统通知（额度阈值 / 上下文告警）
    @Published var notificationsEnabled: Bool {
        didSet {
            UserDefaults.standard.set(notificationsEnabled, forKey: Keys.notifications)
            NotificationManager.shared.setEnabled(notificationsEnabled)
        }
    }

    /// 额度「提示档」百分比（静默通知，默认 80）
    @Published var quotaWarnThreshold: Int {
        didSet { UserDefaults.standard.set(quotaWarnThreshold, forKey: Keys.quotaWarn); onNotificationConfigChange?() }
    }
    /// 额度「严重档」百分比（出声通知，默认 95）
    @Published var quotaCriticalThreshold: Int {
        didSet { UserDefaults.standard.set(quotaCriticalThreshold, forKey: Keys.quotaCritical); onNotificationConfigChange?() }
    }
    /// 会话上下文告警阈值（默认 90）
    @Published var contextThreshold: Int {
        didSet { UserDefaults.standard.set(contextThreshold, forKey: Keys.context); onNotificationConfigChange?() }
    }
    /// 严重档是否出声（默认 true）
    @Published var criticalSoundEnabled: Bool {
        didSet { UserDefaults.standard.set(criticalSoundEnabled, forKey: Keys.criticalSound); onNotificationConfigChange?() }
    }

    /// 是否由本 app 接管 Claude Code 的 statusLine（关闭 = 暂停接管，不再改写 settings.json）。默认开启。
    @Published var manageStatusline: Bool {
        didSet {
            UserDefaults.standard.set(manageStatusline, forKey: Keys.manageStatusline)
            onManageStatuslineChange?(manageStatusline)
        }
    }

    /// 用户是否已同意接入 statusLine（首次运行知情同意页写入）。
    @Published var statuslineConsented: Bool {
        didSet { UserDefaults.standard.set(statuslineConsented, forKey: Keys.consented) }
    }
    /// 是否已展示过首次引导。
    @Published var didOnboard: Bool {
        didSet { UserDefaults.standard.set(didOnboard, forKey: Keys.onboard) }
    }

    /// 灵动岛开关变化回调（由 AppDelegate 绑定到显示/隐藏挂件）
    var onIslandEnabledChange: ((Bool) -> Void)?
    /// 显示器选择变化回调（重建挂件）
    var onDisplaySettingsChange: (() -> Void)?
    /// 通知阈值/声音变化回调（AppDelegate 回写到 UsageStore/SessionStore）
    var onNotificationConfigChange: (() -> Void)?
    /// statusLine 接管开关变化回调（AppDelegate 执行 install/uninstall）
    var onManageStatuslineChange: ((Bool) -> Void)?
    /// 代理切换回调（AppDelegate 重置数据来源、重接/卸 statusLine、刷新各 store）
    var onAgentChange: ((AgentKind) -> Void)?

    private enum Keys {
        static let agent = "agent"
        static let appearance = "appearance"
        static let islandEnabled = "islandEnabled"
        static let selectedScreens = "selectedScreens"
        static let notifications = "notificationsEnabled"
        static let quotaWarn = "quotaWarnThreshold"
        static let quotaCritical = "quotaCriticalThreshold"
        static let context = "contextThreshold"
        static let criticalSound = "criticalSoundEnabled"
        static let manageStatusline = "manageStatusline"
        static let consented = "statuslineConsented"
        static let onboard = "didOnboard"
    }

    init() {
        let d = UserDefaults.standard
        agent = AgentKind(rawValue: d.string(forKey: Keys.agent) ?? "") ?? .claudeCode
        appearance = AppearanceMode(rawValue: d.string(forKey: Keys.appearance) ?? "") ?? .system
        islandEnabled = (d.object(forKey: Keys.islandEnabled) as? Bool) ?? true   // 默认开启
        selectedScreens = Set((d.array(forKey: Keys.selectedScreens) as? [String]) ?? [])
        notificationsEnabled = (d.object(forKey: Keys.notifications) as? Bool) ?? true
        quotaWarnThreshold = (d.object(forKey: Keys.quotaWarn) as? Int) ?? 80
        quotaCriticalThreshold = (d.object(forKey: Keys.quotaCritical) as? Int) ?? 95
        contextThreshold = (d.object(forKey: Keys.context) as? Int) ?? 90
        criticalSoundEnabled = (d.object(forKey: Keys.criticalSound) as? Bool) ?? true
        manageStatusline = (d.object(forKey: Keys.manageStatusline) as? Bool) ?? true
        statuslineConsented = (d.object(forKey: Keys.consented) as? Bool) ?? false
        didOnboard = (d.object(forKey: Keys.onboard) as? Bool) ?? false
        launchAtLogin = (SMAppService.mainApp.status == .enabled)
        // 注意：在 init 中设置 stored property 不会触发 didSet，
        // 故 appearance 需在启动后由 AppDelegate 调一次 applyAppearance()。
        AgentContext.current = agent   // 在任何后台扫描启动前先就位（init 不触发 didSet）
        refreshScreens()
    }

    /// 重新计算已连接显示器列表（启动 + 屏幕插拔时调用）。
    /// 同名显示器（同型号两台）用序号消歧标签，但 id 始终是唯一 displayID。
    func refreshScreens() {
        var seen: [String: Int] = [:]
        screenOptions = NSScreen.screens.map { s in
            let base = s.displayLabel
            seen[base, default: 0] += 1
            let label = seen[base]! > 1 ? "\(base) (\(seen[base]!))" : base
            return ScreenOption(id: s.uniqueID, label: label)
        }
    }

    func applyAppearance() {
        NSApp.appearance = appearance.nsAppearance
    }

    private func applyLaunchAtLogin() {
        do {
            let svc = SMAppService.mainApp
            if launchAtLogin {
                if svc.status != .enabled { try svc.register() }
            } else {
                if svc.status == .enabled { try svc.unregister() }
            }
        } catch {
            NSLog("[ClaudeNotch] 开机自启设置失败: %@", String(describing: error))
        }
    }
}
