import AppKit
import SwiftUI

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate, NSWindowDelegate {

    private let store = UsageStore()
    private let sessionStore = SessionStore()
    private let settings = SettingsStore()
    private var notchManager: NotchManager!
    private var statusItem: NSStatusItem!
    private var settingsWindow: NSWindow?
    private var cancellable: Any?

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)   // 无 Dock 图标
        settings.applyAppearance()               // init 不触发 didSet，启动时手动应用一次

        notchManager = NotchManager(
            store: store, sessionStore: sessionStore, settings: settings,
            onSessionTap: { session in
                if let jump = session.jump { TerminalJumper.jump(jump) }
            })
        settings.onIslandEnabledChange = { [weak self] on in self?.notchManager.setEnabled(on) }
        settings.onDisplaySettingsChange = { [weak self] in self?.notchManager.rebuild() }
        notchManager.rebuild()

        NotificationManager.shared.setEnabled(settings.notificationsEnabled)

        setupStatusItem()
        observeStore()

        store.start()
        sessionStore.start()

        NotificationCenter.default.addObserver(
            self, selector: #selector(screensChanged),
            name: NSApplication.didChangeScreenParametersNotification, object: nil)
    }

    @objc private func screensChanged() {
        settings.refreshScreens()
        notchManager.rebuild()
    }

    private func setupStatusItem() {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        if let button = statusItem.button {
            button.image = makeStatusIcon()
            button.imagePosition = .imageLeading
        }
        let menu = NSMenu()
        menu.delegate = self          // 打开时动态重建（登录/退出登录随状态变化）
        statusItem.menu = menu
    }

    private func makeStatusIcon() -> NSImage? {
        let image = Bundle.main.image(forResource: "MenuBarIcon")
        image?.isTemplate = true
        image?.size = NSSize(width: 18, height: 18)
        image?.accessibilityDescription = "Claude 额度"
        return image
    }

    private func observeStore() {
        cancellable = store.objectWillChange.sink { [weak self] _ in
            DispatchQueue.main.async { self?.updateStatusTitle() }
        }
    }

    private func updateStatusTitle() {
        guard let button = statusItem.button else { return }
        switch store.state {
        case .loggedOut:
            button.title = " 未登录"
        case .ready:
            if let h = store.snapshot?.headline {
                button.title = " \(h.percentRemaining)%"
            } else {
                button.title = ""
            }
        default:
            button.title = ""
        }
    }

    // MARK: - 动态菜单

    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()

        menu.addItem(withTitle: "设置…", action: #selector(openSettings), keyEquivalent: ",").target = self
        menu.addItem(withTitle: "立即刷新", action: #selector(refreshAction), keyEquivalent: "r").target = self

        // 自行判断登录态：已登录显示“退出登录”，否则“登录 Claude…”
        if store.isLoggedIn {
            menu.addItem(withTitle: "退出登录", action: #selector(logoutAction), keyEquivalent: "").target = self
        } else {
            menu.addItem(withTitle: "登录 Claude…", action: #selector(loginAction), keyEquivalent: "l").target = self
        }

        menu.addItem(.separator())
        menu.addItem(withTitle: "退出", action: #selector(quitAction), keyEquivalent: "q").target = self
    }

    @objc private func refreshAction() {
        Task { await store.refresh() }
        sessionStore.refresh()
    }
    @objc private func loginAction() { store.presentLogin() }
    @objc private func logoutAction() { store.logout() }
    @objc private func quitAction() { NSApp.terminate(nil) }

    @objc private func openSettings() {
        if settingsWindow == nil {
            let hosting = NSHostingController(rootView: SettingsView(settings: settings))
            let win = NSWindow(contentViewController: hosting)
            win.title = "ClaudeNotch 设置"
            win.styleMask = [.titled, .closable]
            win.isReleasedWhenClosed = false
            win.delegate = self
            win.center()
            settingsWindow = win
        }
        NSApp.setActivationPolicy(.regular)   // 让设置窗口能正常聚焦显示
        settingsWindow?.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    func windowWillClose(_ notification: Notification) {
        if (notification.object as? NSWindow) === settingsWindow {
            NSApp.setActivationPolicy(.accessory)
        }
    }
}
