import AppKit

/// 计算挂件应该贴在哪块屏幕的什么位置。
/// - 有真刘海：用 safeAreaInsets / auxiliary 区域算出刘海宽高。
/// - 无刘海（外接屏 / 合盖夹脸）：在顶部正中模拟一个灵动岛。
struct NotchGeometry {
    let screen: NSScreen
    let hasRealNotch: Bool
    let notchSize: CGSize      // 真刘海或模拟岛“缺口”的尺寸
    let menuBarHeight: CGFloat // 菜单栏高度（折叠态药丸高度据此，避免露出状态栏下沿）

    static func current() -> NotchGeometry {
        // 优先选带刘海的屏；否则用主屏
        let notched = NSScreen.screens.first(where: { $0.safeAreaInsets.top > 0 })
        let screen = notched ?? NSScreen.main ?? NSScreen.screens.first!

        let topInset = screen.safeAreaInsets.top
        // 菜单栏高度：刘海屏用 safeArea，否则用 frame 顶到 visibleFrame 顶的距离
        let measuredBar = topInset > 0 ? topInset : (screen.frame.maxY - screen.visibleFrame.maxY)
        let menuBar = measuredBar > 1 ? measuredBar : 24

        if topInset > 0 {
            // 刘海宽度 = 屏宽 - 左侧菜单区 - 右侧菜单区（含合理性夹紧，异常时回退 200）
            let full = screen.frame.width
            let left = screen.auxiliaryTopLeftArea?.width ?? 0
            let right = screen.auxiliaryTopRightArea?.width ?? 0
            let raw = full - left - right
            let width: CGFloat = (raw > 100 && raw < 400) ? raw : 200
            return NotchGeometry(screen: screen, hasRealNotch: true,
                                 notchSize: CGSize(width: width, height: topInset),
                                 menuBarHeight: menuBar)
        } else {
            // 模拟岛
            return NotchGeometry(screen: screen, hasRealNotch: false,
                                 notchSize: CGSize(width: 200, height: menuBar),
                                 menuBarHeight: menuBar)
        }
    }

    /// 给定挂件目标尺寸，返回窗口在全局坐标系（原点左下）中的 frame，顶部居中。
    func windowFrame(for size: CGSize) -> NSRect {
        let sf = screen.frame
        let x = sf.midX - size.width / 2
        let y = sf.maxY - size.height   // 紧贴屏幕顶部
        return NSRect(x: x, y: y, width: size.width, height: size.height)
    }
}
