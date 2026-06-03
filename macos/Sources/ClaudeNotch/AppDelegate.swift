import AppKit
import SwiftUI

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate, NSWindowDelegate {

    private let store = UsageStore()
    private let sessionStore = SessionStore()
    private let settings = SettingsStore()
    private let historyStore = UsageHistoryStore()
    private let priceStore = ModelPriceStore()
    private let rateStore = ExchangeRateStore()
    private var notchManager: NotchManager!
    private var statusItem: NSStatusItem!
    private var settingsWindow: NSWindow?
    private var onboardingWindow: NSWindow?
    private var analyticsWindow: NSWindow?
    private var cancellable: Any?
    private var staleTimer: Timer?

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
        settings.onNotificationConfigChange = { [weak self] in self?.applyNotificationConfig() }
        settings.onManageStatuslineChange = { [weak self] on in
            guard let self else { return }
            if on { self.ensureStatuslineIfAllowed() }
            else { StatuslineHook.uninstall(purgeData: false) }
        }
        notchManager.rebuild()

        NotificationManager.shared.setEnabled(settings.notificationsEnabled)
        applyNotificationConfig()

        // 额度来源：首次运行先取得知情同意，再接管 Claude Code 的 statusLine。
        if StatuslineHook.isInstalled {
            // 已在用 = 视为已同意（老用户无感升级，不弹引导）。
            settings.statuslineConsented = true
            settings.didOnboard = true
        }
        if settings.didOnboard {
            ensureStatuslineIfAllowed()
        } else {
            presentOnboarding()
        }

        setupStatusItem()
        observeStore()

        store.start()
        sessionStore.start()
        priceStore.bootstrap()        // 装载 LiteLLM 价表（内置快照即时 + 后台每周刷新）
        rateStore.bootstrap()         // 汇率（缓存/默认即时 + 后台每周刷新）

        // 即使无新事件，也每分钟刷新一次药丸——好让数据过期 30 分钟后能自动调暗。
        staleTimer = Timer.scheduledTimer(withTimeInterval: 60, repeats: true) { [weak self] _ in
            DispatchQueue.main.async { self?.updateStatusTitle() }
        }

        NotificationCenter.default.addObserver(
            self, selector: #selector(screensChanged),
            name: NSApplication.didChangeScreenParametersNotification, object: nil)
    }

    /// 把通知阈值 / 声音设置回写到额度与会话两个 store。
    private func applyNotificationConfig() {
        // 容错：无论两档设成什么，较高者恒为「严重档（出声）」、较低者为「提示档（静默）」，
        // 保证总存在一个静默档，即便 UI 约束被绕过也不会让 80% 这类提示档意外出声。
        let crit = max(settings.quotaWarnThreshold, settings.quotaCriticalThreshold)
        let warn = min(settings.quotaWarnThreshold, settings.quotaCriticalThreshold)
        store.quotaThresholds = crit == warn ? [crit] : [crit, warn]
        store.quotaCriticalBand = crit
        store.criticalSoundEnabled = settings.criticalSoundEnabled
        sessionStore.contextThreshold = settings.contextThreshold
        sessionStore.criticalSoundEnabled = settings.criticalSoundEnabled
    }

    /// 仅在已同意且未暂停接管时，幂等接入 statusLine。
    private func ensureStatuslineIfAllowed() {
        if settings.statuslineConsented && settings.manageStatusline {
            StatuslineHook.ensureInstalled()
        }
    }

    private func presentOnboarding() {
        let existing = StatuslineHook.diagnostics().command   // 接管前的现有命令（通常为 nil 或用户自己的）
        let view = OnboardingView(
            existingCommand: existing,
            onContinue: { [weak self] in
                guard let self else { return }
                self.settings.statuslineConsented = true
                self.ensureStatuslineIfAllowed()
                self.onboardingWindow?.close()
            },
            onSkip: { [weak self] in self?.onboardingWindow?.close() })
        let win = NSWindow(contentViewController: NSHostingController(rootView: view))
        win.title = tr("欢迎使用 ClaudeNotch", "Welcome to ClaudeNotch")
        win.styleMask = [.titled, .closable]
        win.isReleasedWhenClosed = false
        win.delegate = self
        win.center()
        onboardingWindow = win
        NSApp.setActivationPolicy(.regular)
        win.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    func applicationWillTerminate(_ notification: Notification) {
        // 退出前还原 ~/.claude/settings.json（把 statusLine 还原成你原有的），避免退出/卸载后留下指向本 app 的悬空命令。
        // 保留 ratelimits.json，下次启动 ensureInstalled() 重新接回并能秒显上次额度。
        StatuslineHook.uninstall(purgeData: false)
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
        image?.accessibilityDescription = tr("Claude 额度", "Claude usage")
        return image
    }

    private func observeStore() {
        cancellable = store.objectWillChange.sink { [weak self] _ in
            DispatchQueue.main.async { self?.updateStatusTitle() }
        }
    }

    private func updateStatusTitle() {
        guard let button = statusItem.button else { return }
        guard let h = store.snapshot?.headline else {
            button.attributedTitle = NSAttributedString(string: "")
            button.setAccessibilityLabel(tr("Claude 额度", "Claude usage"))
            return
        }
        let stale = store.isStale
        var text = " \(h.percentRemaining)%"
        var a11y = tr("\(h.title)，剩余 \(h.percentRemaining)%", "\(h.title), \(h.percentRemaining)% remaining")
        // 即将在刷新前用尽：药丸追加 ⚡ + 极简时长（数据过期则不显示，避免误导）。
        // 用投影记录的「耗尽时刻」实时倒推，使 60s tick 真正把药丸读数往下减，而非重印冻结值。
        if !stale, let proj = store.liveProjection(for: h.id), proj.willRunOutBeforeReset {
            let remaining = proj.emptyAt.map { Int($0.timeIntervalSinceNow / 60) } ?? proj.minutesToEmpty
            if let r = remaining, r > 0 {
                text += " ⚡\(UsageMetric.shortDuration(minutes: r))"
                a11y += tr("，预计 \(UsageMetric.formatDuration(minutes: r))后用尽", ", running out in about \(UsageMetric.formatDuration(minutes: r))")
            } else {
                text += " ⚡"
                a11y += tr("，即将用尽", ", running out soon")
            }
        }
        if stale { a11y += tr("，数据可能已过期", ", data may be stale") }
        // 过期时用次级文字色（变灰），提示数据不新鲜。
        let color: NSColor = stale ? .secondaryLabelColor : .labelColor
        button.attributedTitle = NSAttributedString(string: text, attributes: [
            .foregroundColor: color,
            .font: NSFont.systemFont(ofSize: 12, weight: .semibold),
        ])
        button.setAccessibilityLabel(a11y)
    }

    // MARK: - 动态菜单

    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()

        menu.addItem(withTitle: tr("设置…", "Settings…"), action: #selector(openSettings), keyEquivalent: ",").target = self
        menu.addItem(withTitle: tr("数据统计…", "Analytics…"), action: #selector(openAnalytics), keyEquivalent: "d").target = self
        menu.addItem(withTitle: tr("立即刷新", "Refresh Now"), action: #selector(refreshAction), keyEquivalent: "r").target = self
        menu.addItem(withTitle: tr("检查更新…", "Check for Updates…"), action: #selector(checkUpdateAction), keyEquivalent: "").target = self

        menu.addItem(.separator())
        menu.addItem(withTitle: tr("退出", "Quit"), action: #selector(quitAction), keyEquivalent: "q").target = self
    }

    @objc private func refreshAction() {
        Task { await store.refresh() }
        sessionStore.refresh()
    }
    @objc private func checkUpdateAction() { UpdateChecker.checkInteractively() }
    @objc private func quitAction() { NSApp.terminate(nil) }

    @objc private func openAnalytics() {
        if analyticsWindow == nil {
            let hosting = NSHostingController(rootView: AnalyticsView(store: historyStore, hover: HoverModel()))
            let win = NSWindow(contentViewController: hosting)
            win.title = tr("ClaudeNotch 数据统计", "ClaudeNotch Analytics")
            win.styleMask = [.titled, .closable, .resizable, .miniaturizable]
            win.isReleasedWhenClosed = false
            win.delegate = self
            win.setContentSize(NSSize(width: 980, height: 720))
            win.minSize = NSSize(width: 820, height: 560)
            win.setFrameAutosaveName("ClaudeNotchAnalytics")
            win.center()
            analyticsWindow = win
        }
        historyStore.refreshIfNeeded()        // 懒构建：仅首次打开窗口时扫描历史
        NSApp.setActivationPolicy(.regular)
        analyticsWindow?.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    @objc private func openSettings() {
        if settingsWindow == nil {
            let hosting = NSHostingController(rootView: SettingsView(settings: settings, priceStore: priceStore, rateStore: rateStore))
            let win = NSWindow(contentViewController: hosting)
            win.title = tr("ClaudeNotch 设置", "ClaudeNotch Settings")
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
        let w = notification.object as? NSWindow
        if w === onboardingWindow {
            settings.didOnboard = true        // 关掉引导（含直接点 X）即视为已做过一次选择，不再每次弹
            onboardingWindow = nil
        } else if w === analyticsWindow {
            analyticsWindow = nil             // 下次打开重建（数据绑定保持新鲜）
        } else if w === settingsWindow {
            settingsWindow = nil              // 必须置 nil，否则下面的判断永远为假、退不回 accessory（Dock 图标残留）
        }
        // 仅当不再有任何前台窗口时，才退回无 Dock 图标的 accessory 策略。
        if settingsWindow == nil && onboardingWindow == nil && analyticsWindow == nil {
            NSApp.setActivationPolicy(.accessory)
        }
    }
}
