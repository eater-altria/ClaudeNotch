import AppKit
import SwiftUI
import Combine

/// 由窗口控制器驱动、SwiftUI 视图观察的展开状态（与窗口尺寸解耦）。
@MainActor
final class IslandState: ObservableObject {
    @Published var expanded = false
}

/// 承载灵动岛 SwiftUI 视图的无边框悬浮面板。
///
/// 悬停检测设计（两次迭代后的最终形态）：
/// - **轮询当前鼠标坐标**（非依赖移动事件）：快速甩动时系统会合并/丢弃 mouseMoved 事件，
///   导致“光标停下那一刻”的位置没有事件送达而漏判；定时读取 `NSEvent.mouseLocation`
///   保证静止位置一定被采到。
/// - **固定屏幕矩形判定**：触发区/展开区是常量，不随窗口动画变化，杜绝“放大→边缘扫过光标→抖动”的反馈环。
/// - **触发区放大并贴住屏幕最顶边**：让“甩到最上方”这一手势无需精确瞄准即可命中。
@MainActor
final class NotchWindowController {

    private let panel: NSPanel
    private let store: UsageStore
    private let sessionStore: SessionStore
    private let islandState = IslandState()
    private var hosting: NSHostingView<AnyView>!
    private var geometry: NotchGeometry

    // 折叠态高度跟随菜单栏高度，使药丸底边与状态栏底边齐平、不外露
    private var collapsedSize: CGSize { CGSize(width: 220, height: geometry.menuBarHeight) }
    private let expandedSize = CGSize(width: 440, height: 516)

    // 固定命中矩形（屏幕全局坐标，原点左下）
    private var triggerRect: NSRect = .zero    // 折叠态：进入则展开（比可见药丸大、贴顶边）
    private var expandedRect: NSRect = .zero   // 展开态：离开则收起（= 展开窗口区域）

    private var hoverTimer: Timer?
    private let pollInterval: TimeInterval = 0.045

    // 去抖：展开近即时，收起留宽限防边界闪烁
    private var expandWork: DispatchWorkItem?
    private var collapseWork: DispatchWorkItem?
    private let expandDelay: TimeInterval = 0.05
    private let collapseDelay: TimeInterval = 0.18

    var expanded: Bool { islandState.expanded }
    private var enabled = true   // 灵动岛总开关

    init(store: UsageStore, sessionStore: SessionStore) {
        self.store = store
        self.sessionStore = sessionStore
        self.geometry = NotchGeometry.current()

        panel = NSPanel(contentRect: NSRect(origin: .zero, size: CGSize(width: 220, height: geometry.menuBarHeight)),
                        styleMask: [.borderless, .nonactivatingPanel],
                        backing: .buffered, defer: false)
        panel.isFloatingPanel = true
        panel.level = .statusBar
        panel.backgroundColor = .clear
        panel.isOpaque = false
        panel.hasShadow = false
        panel.collectionBehavior = [.canJoinAllSpaces, .stationary, .fullScreenAuxiliary, .ignoresCycle]
        panel.isMovable = false
        panel.hidesOnDeactivate = false
        panel.ignoresMouseEvents = true   // 折叠态点击穿透，悬停靠轮询

        let root = NotchRootView(
            store: store,
            sessionStore: sessionStore,
            islandState: islandState,
            hasRealNotch: geometry.hasRealNotch,
            onLogin: { [weak self] in self?.store.presentLogin() },
            onRefresh: { [weak self] in
                Task { await self?.store.refresh() }
                self?.sessionStore.refresh()
            }
        )
        hosting = NSHostingView(rootView: AnyView(root))
        hosting.autoresizingMask = [.width, .height]
        panel.contentView = hosting

        recomputeRects()
        applyFrame(animated: false)
        startHoverPolling()
    }

    deinit {
        hoverTimer?.invalidate()
    }

    func show() {
        panel.orderFrontRegardless()
    }

    /// 启用/禁用灵动岛（设置项）。禁用时隐藏挂件并停止悬停响应。
    func setEnabled(_ on: Bool) {
        enabled = on
        if on {
            panel.orderFrontRegardless()
        } else {
            cancelExpand(); cancelCollapse()
            if islandState.expanded { setExpanded(false) }
            panel.orderOut(nil)
        }
    }

    func relocate() {
        geometry = NotchGeometry.current()
        recomputeRects()
        applyFrame(animated: false)
    }

    // MARK: - 固定命中矩形

    private func recomputeRects() {
        expandedRect = geometry.windowFrame(for: expandedSize)
        // 触发区：比可见药丸更宽，并贴住屏幕最顶边，方便“甩到顶部”命中
        let sf = geometry.screen.frame
        let tw = collapsedSize.width + 120        // 340
        let th: CGFloat = 70
        triggerRect = NSRect(x: sf.midX - tw / 2, y: sf.maxY - th, width: tw, height: th)
    }

    // MARK: - 轮询悬停判定

    private func startHoverPolling() {
        let timer = Timer(timeInterval: pollInterval, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.evaluateHover() }
        }
        RunLoop.main.add(timer, forMode: .common)   // .common 保证菜单/拖动时仍触发
        hoverTimer = timer
    }

    /// 顶部锚定区域的包含判定：上边**不封顶**（光标到不了屏幕顶以上），其余边闭区间。
    /// 解决 CGRect.contains 把上边缘当开区间、导致“光标贴最顶 y==maxY 被判 false”的死区问题。
    private func inTopZone(_ p: CGPoint, _ r: NSRect) -> Bool {
        return p.x >= r.minX && p.x <= r.maxX && p.y >= r.minY
    }

    private func evaluateHover() {
        guard enabled else { return }
        let p = NSEvent.mouseLocation
        if islandState.expanded {
            if inTopZone(p, expandedRect) { cancelCollapse() } else { scheduleCollapse() }
        } else {
            if inTopZone(p, triggerRect) { scheduleExpand() } else { cancelExpand() }
        }
    }

    private func scheduleExpand() {
        guard expandWork == nil else { return }
        cancelCollapse()
        let work = DispatchWorkItem { [weak self] in
            guard let self else { return }
            self.expandWork = nil
            // 触发前复核一次当前位置，避免延迟期间已离开
            if self.inTopZone(NSEvent.mouseLocation, self.triggerRect) { self.setExpanded(true) }
        }
        expandWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + expandDelay, execute: work)
    }

    private func cancelExpand() {
        expandWork?.cancel()
        expandWork = nil
    }

    private func scheduleCollapse() {
        guard collapseWork == nil else { return }
        cancelExpand()
        let work = DispatchWorkItem { [weak self] in
            guard let self else { return }
            self.collapseWork = nil
            if !self.inTopZone(NSEvent.mouseLocation, self.expandedRect) { self.setExpanded(false) }
        }
        collapseWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + collapseDelay, execute: work)
    }

    private func cancelCollapse() {
        collapseWork?.cancel()
        collapseWork = nil
    }

    // MARK: - 应用尺寸

    private func setExpanded(_ value: Bool) {
        guard value != islandState.expanded else { return }
        if ProcessInfo.processInfo.environment["CLAUDENOTCH_DEBUG"] != nil {
            NSLog("[ClaudeNotch] setExpanded -> %@", value ? "true" : "false")
        }
        islandState.expanded = value
        panel.ignoresMouseEvents = !value   // 展开后才接收按钮点击
        applyFrame(animated: true)
    }

    private func applyFrame(animated: Bool) {
        let size = islandState.expanded ? expandedSize : collapsedSize
        let frame = geometry.windowFrame(for: size)
        if animated {
            NSAnimationContext.runAnimationGroup { ctx in
                ctx.duration = 0.26
                ctx.timingFunction = CAMediaTimingFunction(name: .easeOut)
                ctx.allowsImplicitAnimation = true
                panel.animator().setFrame(frame, display: true)
            }
        } else {
            panel.setFrame(frame, display: true)
        }
    }
}
