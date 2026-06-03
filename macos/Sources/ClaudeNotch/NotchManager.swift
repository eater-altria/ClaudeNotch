import AppKit

/// 管理"每个选中显示器一个挂件"。根据设置里选中的屏集合增删 NotchWindowController。
@MainActor
final class NotchManager {

    private let store: UsageStore
    private let sessionStore: SessionStore
    private let settings: SettingsStore
    private let onSessionTap: (SessionInfo) -> Void

    private var controllers: [String: NotchWindowController] = [:]   // screenID -> controller
    private var enabled = true

    init(store: UsageStore, sessionStore: SessionStore, settings: SettingsStore,
         onSessionTap: @escaping (SessionInfo) -> Void) {
        self.store = store
        self.sessionStore = sessionStore
        self.settings = settings
        self.onSessionTap = onSessionTap
        self.enabled = settings.islandEnabled
    }

    /// 灵动岛总开关
    func setEnabled(_ on: Bool) {
        enabled = on
        rebuild()
    }

    /// 目标屏：设置里选中的（且当前已连接）；为空则回退到自动屏（刘海/主屏）。
    private func desiredScreens() -> [NSScreen] {
        let connected = NSScreen.screens
        let selected = settings.selectedScreens
        if selected.isEmpty { return [NotchGeometry.autoScreen()] }
        let chosen = connected.filter { selected.contains($0.uniqueID) }
        return chosen.isEmpty ? [NotchGeometry.autoScreen()] : chosen
    }

    /// 按目标屏集合增删/重定位挂件。设置变化、屏幕插拔、开关切换时调用。
    func rebuild() {
        guard enabled else { closeAll(); return }

        let desired = desiredScreens()
        let desiredIDs = Set(desired.map { $0.uniqueID })

        // 移除不再需要的
        for (id, controller) in controllers where !desiredIDs.contains(id) {
            controller.close()
            controllers[id] = nil
        }
        // 新增缺少的（按唯一 displayID 键，同型号两台也不会碰撞）
        for screen in desired where controllers[screen.uniqueID] == nil {
            let c = NotchWindowController(screen: screen, store: store,
                                         sessionStore: sessionStore, onSessionTap: onSessionTap)
            c.show()
            controllers[screen.uniqueID] = c
        }
        // 其余重定位（几何可能随分辨率变化）
        for controller in controllers.values { controller.relocate() }
    }

    private func closeAll() {
        for controller in controllers.values { controller.close() }
        controllers.removeAll()
    }
}
