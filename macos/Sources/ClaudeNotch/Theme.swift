import SwiftUI

/// 配色模式
enum AppearanceMode: String, CaseIterable, Identifiable {
    case system, light, dark
    var id: String { rawValue }

    var label: String {
        switch self {
        case .system: return tr("跟随系统", "System")
        case .light: return tr("日间模式", "Light")
        case .dark: return tr("夜间模式", "Dark")
        }
    }

    var nsAppearance: NSAppearance? {
        switch self {
        case .system: return nil
        case .light: return NSAppearance(named: .aqua)
        case .dark: return NSAppearance(named: .darkAqua)
        }
    }
}

/// 主题色板：灵动岛的背景/文字/轨道随明暗自适应。
struct Palette {
    var isDark: Bool

    var islandBG: Color { isDark ? Color.black.opacity(0.92) : Color(white: 0.96).opacity(0.97) }
    var border: Color { isDark ? Color.white.opacity(0.08) : Color.black.opacity(0.10) }
    var track: Color { isDark ? Color.white.opacity(0.12) : Color.black.opacity(0.10) }
    var text: Color { isDark ? .white : Color(white: 0.12) }
    var shadow: Color { isDark ? Color.black.opacity(0.35) : Color.black.opacity(0.18) }

    func text(_ opacity: Double) -> Color { text.opacity(opacity) }
}

private struct PaletteKey: EnvironmentKey {
    static let defaultValue = Palette(isDark: true)
}
extension EnvironmentValues {
    var palette: Palette {
        get { self[PaletteKey.self] }
        set { self[PaletteKey.self] = newValue }
    }
}

/// 平滑的“绿 → 黄 → 红”渐变取色（按已用百分比 0–100）。
/// 替代原来的三档阈值跳变，颜色随用量连续过渡。
func rampColor(_ percentUsed: Int) -> Color {
    let t = Double(max(0, min(100, percentUsed))) / 100.0
    let hue = (1.0 - t) * 0.33   // 0.33=绿, 0.16≈黄, 0=红
    return Color(hue: hue, saturation: 0.82, brightness: 0.95)
}
