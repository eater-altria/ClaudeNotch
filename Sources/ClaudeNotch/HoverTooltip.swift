import SwiftUI

/// 分析窗口统一的悬浮坐标空间名（所有可悬浮元素都把指针位置上报到这里）。
let kAnalyticsSpace = "analyticsRoot"

/// 一条悬浮提示的内容 + 位置（位置在 kAnalyticsSpace 坐标系内）。
struct HoverPayload: Equatable {
    var title: String
    var lines: [String]
    var point: CGPoint
}

/// 引用类型：可悬浮元素只**写**它（不观察，故不会因悬浮而重绘自身——
/// 关键：避免连续悬浮把昂贵的 Swift Charts 每次鼠标移动都重建）。只有 `HoverOverlay` 观察它。
@MainActor
final class HoverModel: ObservableObject {
    @Published var payload: HoverPayload?
}

/// 浮动提示卡片。
struct TooltipCard: View {
    let title: String
    let lines: [String]
    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(title).font(.caption.bold())
            ForEach(lines, id: \.self) { Text($0).font(.caption2).foregroundStyle(.secondary) }
        }
        .padding(.horizontal, 8).padding(.vertical, 6)
        .background(RoundedRectangle(cornerRadius: 6).fill(.regularMaterial))
        .overlay(RoundedRectangle(cornerRadius: 6).stroke(Color.primary.opacity(0.12)))
        .shadow(radius: 5, y: 2)
        .frame(maxWidth: 260, alignment: .leading)
    }
}

/// 唯一观察 HoverModel 的视图：把卡片定位到光标附近（夹紧在容器内），不拦截点击/悬浮。
struct HoverOverlay: View {
    @ObservedObject var model: HoverModel
    var body: some View {
        GeometryReader { geo in
            if let h = model.payload {
                TooltipCard(title: h.title, lines: h.lines)
                    .fixedSize()
                    .allowsHitTesting(false)
                    .offset(x: min(max(8, h.point.x + 14), max(8, geo.size.width - 270)),
                            y: min(max(8, h.point.y + 16), max(8, geo.size.height - 96)))
            }
        }
    }
}
