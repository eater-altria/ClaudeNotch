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

    /// 灵动岛开关变化回调（由 AppDelegate 绑定到显示/隐藏挂件）
    var onIslandEnabledChange: ((Bool) -> Void)?
    /// 显示器选择变化回调（重建挂件）
    var onDisplaySettingsChange: (() -> Void)?

    private enum Keys {
        static let appearance = "appearance"
        static let islandEnabled = "islandEnabled"
        static let selectedScreens = "selectedScreens"
        static let notifications = "notificationsEnabled"
    }

    init() {
        let d = UserDefaults.standard
        appearance = AppearanceMode(rawValue: d.string(forKey: Keys.appearance) ?? "") ?? .system
        islandEnabled = (d.object(forKey: Keys.islandEnabled) as? Bool) ?? true   // 默认开启
        selectedScreens = Set((d.array(forKey: Keys.selectedScreens) as? [String]) ?? [])
        notificationsEnabled = (d.object(forKey: Keys.notifications) as? Bool) ?? true
        launchAtLogin = (SMAppService.mainApp.status == .enabled)
        // 注意：在 init 中设置 stored property 不会触发 didSet，
        // 故 appearance 需在启动后由 AppDelegate 调一次 applyAppearance()。
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
