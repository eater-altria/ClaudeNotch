import SwiftUI
import ServiceManagement

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

    /// 灵动岛开关变化回调（由 AppDelegate 绑定到显示/隐藏挂件）
    var onIslandEnabledChange: ((Bool) -> Void)?

    private enum Keys {
        static let appearance = "appearance"
        static let islandEnabled = "islandEnabled"
    }

    init() {
        let d = UserDefaults.standard
        appearance = AppearanceMode(rawValue: d.string(forKey: Keys.appearance) ?? "") ?? .system
        islandEnabled = (d.object(forKey: Keys.islandEnabled) as? Bool) ?? true   // 默认开启
        launchAtLogin = (SMAppService.mainApp.status == .enabled)
        // 注意：在 init 中设置 stored property 不会触发 didSet，
        // 故 appearance 需在启动后由 AppDelegate 调一次 applyAppearance()。
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
