import SwiftUI
import AppKit

/// 严重度字形（按已用百分比）：正常不显示，警告=!，严重=⚠。
/// 用于「色盲友好」模式（系统开启「无颜色区分」时），不只靠红→绿色相传达状态。
func severityGlyph(usedPercent: Int) -> String? {
    switch UsageLevel(percentUsed: usedPercent) {
    case .ok: return nil
    case .warn: return "exclamationmark"
    case .critical: return "exclamationmark.triangle.fill"
    }
}

// 顶部贴边、底部圆角的“岛”形状
struct IslandShape: Shape {
    var topRadius: CGFloat
    var bottomRadius: CGFloat
    func path(in rect: CGRect) -> Path {
        // 同时按宽、高的一半夹紧，避免矮高度时上下圆角重叠
        let limit = min(rect.width / 2, rect.height / 2)
        let tr = min(topRadius, limit)
        let br = min(bottomRadius, limit)
        var p = Path()
        p.move(to: CGPoint(x: rect.minX, y: rect.minY + tr))
        p.addQuadCurve(to: CGPoint(x: rect.minX + tr, y: rect.minY),
                       control: CGPoint(x: rect.minX, y: rect.minY))
        p.addLine(to: CGPoint(x: rect.maxX - tr, y: rect.minY))
        p.addQuadCurve(to: CGPoint(x: rect.maxX, y: rect.minY + tr),
                       control: CGPoint(x: rect.maxX, y: rect.minY))
        p.addLine(to: CGPoint(x: rect.maxX, y: rect.maxY - br))
        p.addQuadCurve(to: CGPoint(x: rect.maxX - br, y: rect.maxY),
                       control: CGPoint(x: rect.maxX, y: rect.maxY))
        p.addLine(to: CGPoint(x: rect.minX + br, y: rect.maxY))
        p.addQuadCurve(to: CGPoint(x: rect.minX, y: rect.maxY - br),
                       control: CGPoint(x: rect.minX, y: rect.maxY))
        p.closeSubpath()
        return p
    }
}

struct NotchRootView: View {
    @ObservedObject var store: UsageStore
    @ObservedObject var sessionStore: SessionStore
    @ObservedObject var islandState: IslandState
    var hasRealNotch: Bool = false
    var onRefresh: () -> Void
    var onSessionTap: (SessionInfo) -> Void

    @Environment(\.colorScheme) private var colorScheme

    private var topRadius: CGFloat { 14 }   // 顶部圆角（真刘海/模拟岛一致）
    private var expanded: Bool { islandState.expanded }
    private var palette: Palette { Palette(isDark: colorScheme == .dark) }

    var body: some View {
        ZStack(alignment: .top) {
            IslandShape(topRadius: topRadius, bottomRadius: 18)
                .fill(palette.islandBG)
                .overlay(
                    IslandShape(topRadius: topRadius, bottomRadius: 18)
                        .stroke(palette.border, lineWidth: 0.5)
                )
                .shadow(color: palette.shadow, radius: 10, y: 4)

            if expanded {
                expandedContent
                    .transition(.opacity.combined(with: .move(edge: .top)))
            } else {
                collapsedContent
                    .transition(.opacity)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        .environment(\.palette, palette)
        .animation(.easeOut(duration: 0.22), value: expanded)
    }

    // MARK: 折叠态

    private var collapsedContent: some View {
        let h = store.snapshot?.headline
        let proj = h.flatMap { store.liveProjection(for: $0.id) }
        let burning = proj?.willRunOutBeforeReset == true
        return HStack(spacing: 7) {
            if let h {
                GradientRing(fraction: Double(h.percentRemaining) / 100, usedPercent: h.percentUsed, lineWidth: 2.5)
                    .frame(width: 15, height: 15)
            } else {
                Circle().fill(palette.text(0.4)).frame(width: 7, height: 7)
            }
            Text(store.headlineText)
                .font(.system(size: 12, weight: .semibold, design: .rounded))
                .foregroundStyle(palette.text)
            Text(tr("剩余", "left"))
                .font(.system(size: 9.5))
                .foregroundStyle(palette.text(0.5))
            if burning {
                Image(systemName: "bolt.fill")
                    .font(.system(size: 9))
                    .foregroundStyle(Color.orange)
            }
        }
        .opacity(store.isStale ? 0.45 : 1)     // 数据过期：整体调暗
        .frame(maxHeight: .infinity)
        .padding(.horizontal, 13)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(collapsedA11yLabel(h, proj))
    }

    private func collapsedA11yLabel(_ h: UsageMetric?, _ proj: BurnProjection?) -> String {
        guard let h else { return tr("Claude 额度：暂无数据", "Claude usage: no data") }
        var s = tr("\(h.title)，剩余 \(h.percentRemaining)%", "\(h.title), \(h.percentRemaining)% left")
        if store.isStale { s += tr("，数据可能已过期", ", data may be stale") }
        else if proj?.willRunOutBeforeReset == true { s += tr("，预计刷新前用尽", ", likely to run out before reset") }
        return s
    }

    // MARK: 展开态

    private var expandedContent: some View {
        VStack(alignment: .leading, spacing: 0) {
            VStack(alignment: .leading, spacing: 3) {
                HStack {
                    Text(tr("Claude 额度", "Claude Usage"))
                        .font(.system(size: 12, weight: .semibold))
                        .foregroundStyle(palette.text)
                    Spacer()
                    Text(store.isStale ? store.freshnessText : store.lastUpdatedText)
                        .font(.system(size: 10))
                        .foregroundStyle(store.isStale ? Color.orange.opacity(0.9) : palette.text(0.45))
                    Button(action: onRefresh) {
                        Image(systemName: "arrow.clockwise")
                            .font(.system(size: 11, weight: .semibold))
                            .foregroundStyle(palette.text(0.7))
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel(tr("立即刷新", "Refresh now"))
                }
                if let snap = store.snapshot, let cost = snap.officialCostUSD {
                    HStack(spacing: 5) {
                        Image(systemName: "checkmark.seal.fill")
                            .font(.system(size: 8)).foregroundStyle(palette.text(0.4))
                        Text(officialCostLine(snap, cost))
                            .font(.system(size: 9.5)).foregroundStyle(palette.text(0.5))
                            .lineLimit(1).minimumScaleFactor(0.8)
                    }
                    .accessibilityElement(children: .ignore)
                    .accessibilityLabel(tr("最近会话官方花费 \(money(cost))", "Latest session official cost \(money(cost))"))
                }
            }
            .padding(.horizontal, 16)
            .padding(.top, 14)
            .padding(.bottom, 10)

            Divider().overlay(palette.border)

            content
                .padding(.horizontal, 16)
                .padding(.top, 12)

            Divider().overlay(palette.border)
                .padding(.top, 12)

            sessionsSection

            Spacer(minLength: 8)
        }
    }

    @ViewBuilder
    private var content: some View {
        switch store.state {
        case .waiting:
            waitingView
        case .error(let msg):
            errorView(msg)
        case .loading where store.snapshot == nil:
            HStack {
                ProgressView().controlSize(.small)
                Text(tr("读取中…", "Loading…")).foregroundStyle(palette.text(0.6)).font(.system(size: 12))
            }
            .frame(maxWidth: .infinity, alignment: .center)
            .padding(.vertical, 24)
        default:
            metricsView
        }
    }

    private var metricsView: some View {
        VStack(alignment: .leading, spacing: 14) {
            if let snap = store.snapshot {
                HStack(alignment: .top, spacing: 8) {
                    ForEach(snap.allMetrics) { m in
                        MetricRingTile(metric: m, projection: store.liveProjection(for: m.id))
                            .frame(maxWidth: .infinity)
                    }
                }
                if let ep = snap.extraPercent {
                    extraRow(percent: ep, snap: snap)
                }
            } else {
                Text(tr("暂无数据", "No data")).foregroundStyle(palette.text(0.5)).font(.system(size: 12))
            }
        }
    }

    private func officialCostLine(_ snap: UsageSnapshot, _ cost: Double) -> String {
        var s = tr("最近会话官方花费 \(money(cost))", "Latest session official cost \(money(cost))")
        if let m = snap.modelName { s += " · \(m)" }
        return s
    }

    private func extraRow(percent: Int, snap: UsageSnapshot) -> some View {
        HStack(spacing: 10) {
            ZStack {
                GradientRing(fraction: Double(percent) / 100, usedPercent: percent, lineWidth: 4)
                Text("\(percent)%")
                    .font(.system(size: 9, weight: .semibold, design: .rounded))
                    .foregroundStyle(palette.text)
            }
            .frame(width: 30, height: 30)
            VStack(alignment: .leading, spacing: 1) {
                Text(tr("额外用量", "Extra usage")).font(.system(size: 11, weight: .medium)).foregroundStyle(palette.text(0.85))
                if let spent = snap.extraSpent {
                    Text(tr("已花 \(money(spent))", "Spent \(money(spent))")
                         + (snap.extraLimit.map { " / \(money($0, decimals: 0))" } ?? "")
                         + (snap.extraBalance.map { tr(" · 余 \(money($0))", " · \(money($0)) left") } ?? ""))
                        .font(.system(size: 10)).foregroundStyle(palette.text(0.5))
                }
            }
            Spacer()
        }
        .padding(.top, 2)
    }

    private var waitingView: some View {
        // 自诊断：区分「已接入但还没数据」与「根本没接入 statusLine」两种情形。
        // 读 store 缓存值，不在 body 里直接做磁盘 IO。
        let installed = store.statuslineInstalled
        return VStack(spacing: 8) {
            Image(systemName: installed ? "terminal" : "link.badge.plus")
                .font(.system(size: 18)).foregroundStyle(palette.text(0.6))
            Text(installed ? tr("等待 Claude Code 额度数据", "Waiting for Claude Code usage data") : tr("尚未接入 Claude Code", "Not connected to Claude Code"))
                .font(.system(size: 13, weight: .medium)).foregroundStyle(palette.text)
            Text(installed
                 ? tr("在任意终端跑一次 claude，额度会自动出现", "Run claude once in any terminal and usage will appear")
                 : tr("去 设置 → 集成状态 重新接入，或在终端跑一次 claude", "Go to Settings → Integration to reconnect, or run claude in a terminal"))
                .font(.system(size: 11)).foregroundStyle(palette.text(0.5))
                .multilineTextAlignment(.center).fixedSize(horizontal: false, vertical: true)
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, 16)
        .accessibilityElement(children: .combine)
    }

    private func errorView(_ msg: String) -> some View {
        VStack(spacing: 8) {
            Image(systemName: "exclamationmark.triangle").foregroundStyle(.orange)
            Text(msg).font(.system(size: 11)).foregroundStyle(palette.text(0.7))
                .multilineTextAlignment(.center).fixedSize(horizontal: false, vertical: true)
            Button(tr("重试", "Retry"), action: onRefresh).buttonStyle(.plain)
                .font(.system(size: 12)).foregroundStyle(palette.text)
                .padding(.horizontal, 14).padding(.vertical, 5)
                .background(Capsule().fill(palette.text(0.15)))
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, 12)
    }

    // MARK: 活跃会话

    private var sessionsSection: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 6) {
                Text(tr("活跃会话", "Active sessions")).font(.system(size: 11, weight: .semibold)).foregroundStyle(palette.text(0.85))
                Text("\(sessionStore.sessions.count)")
                    .font(.system(size: 9, weight: .semibold, design: .rounded))
                    .foregroundStyle(palette.text(0.7))
                    .padding(.horizontal, 5).padding(.vertical, 1)
                    .background(Capsule().fill(palette.text(0.12)))
                Spacer()
                Text(tr("≈ API 等价花费", "≈ API-equivalent cost")).font(.system(size: 8)).foregroundStyle(palette.text(0.32))
            }
            if sessionStore.sessions.isEmpty {
                VStack(alignment: .leading, spacing: 2) {
                    Text(tr("无运行中的会话", "No running sessions"))
                        .font(.system(size: 12)).foregroundStyle(palette.text(0.4))
                    Text(tr("仅列出正在终端运行的会话；额度仍会随 Claude Code 更新", "Only sessions running in a terminal are listed; usage still updates with Claude Code"))
                        .font(.system(size: 9.5)).foregroundStyle(palette.text(0.3))
                        .fixedSize(horizontal: false, vertical: true)
                }
                .padding(.vertical, 5)
            } else {
                ScrollView(.vertical, showsIndicators: false) {
                    VStack(spacing: 11) {
                        ForEach(sessionStore.sessions) { s in
                            SessionRowView(session: s, onTap: { onSessionTap(s) })
                        }
                    }
                }
                .frame(maxHeight: 180)
            }
        }
        .padding(.horizontal, 16)
        .padding(.top, 10)
    }
}

// MARK: - 单指标圆环卡片

struct MetricRingTile: View {
    let metric: UsageMetric
    let projection: BurnProjection?
    @Environment(\.palette) private var palette
    // SwiftUI 环境值：系统「无颜色区分」开关，切换时自动重绘（不像全局读 NSWorkspace 那样无依赖）。
    @Environment(\.accessibilityDifferentiateWithoutColor) private var differentiateWithoutColor

    var body: some View {
        VStack(spacing: 6) {
            ZStack {
                GradientRing(fraction: Double(metric.percentRemaining) / 100, usedPercent: metric.percentUsed, lineWidth: 6)
                VStack(spacing: -1) {
                    Text("\(metric.percentRemaining)")
                        .font(.system(size: 17, weight: .bold, design: .rounded))
                        .foregroundStyle(palette.text)
                    Text(tr("剩%", "left%"))
                        .font(.system(size: 8, weight: .medium))
                        .foregroundStyle(palette.text(0.5))
                }
                // 色盲友好：开启系统「无颜色区分」时，在环顶叠加严重度字形（不只靠红绿）。
                if differentiateWithoutColor, let g = severityGlyph(usedPercent: metric.percentUsed) {
                    Image(systemName: g)
                        .font(.system(size: 9, weight: .bold))
                        .foregroundStyle(metric.level.color)
                        .offset(y: -21)
                }
            }
            .frame(width: 60, height: 60)

            Text(metric.title)
                .font(.system(size: 10, weight: .medium))
                .foregroundStyle(palette.text(0.85))
                .lineLimit(1).minimumScaleFactor(0.7)

            VStack(spacing: 1) {
                Label(metric.resetDisplay, systemImage: "arrow.triangle.2.circlepath")
                    .labelStyle(InlineLabelStyle())
                    .foregroundStyle(palette.text(0.45))
                if let proj = projection {
                    Label(proj.display, systemImage: proj.willRunOutBeforeReset ? "bolt.fill" : "checkmark")
                        .labelStyle(InlineLabelStyle())
                        .foregroundStyle(proj.willRunOutBeforeReset ? Color.orange.opacity(0.9) : palette.text(0.4))
                }
            }
            .font(.system(size: 9))
            .lineLimit(1).minimumScaleFactor(0.65)
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(metric.title)
        .accessibilityValue(a11yValue)
    }

    private var a11yValue: String {
        var s = tr("剩余 \(metric.percentRemaining)%，已用 \(metric.percentUsed)%，\(metric.resetDisplay)刷新", "\(metric.percentRemaining)% left, \(metric.percentUsed)% used, resets \(metric.resetDisplay)")
        if let p = projection { s += tr("，\(p.display)", ", \(p.display)") }
        return s
    }
}

struct InlineLabelStyle: LabelStyle {
    func makeBody(configuration: Configuration) -> some View {
        HStack(spacing: 2) {
            configuration.icon.font(.system(size: 7))
            configuration.title
        }
    }
}

// MARK: - 活跃会话行

struct SessionRowView: View {
    let session: SessionInfo
    var onTap: () -> Void = {}
    @Environment(\.palette) private var palette
    @State private var hovering = false

    var body: some View {
        HStack(spacing: 12) {
            ZStack {
                GradientRing(fraction: Double(session.contextPercent) / 100, usedPercent: session.contextPercent, lineWidth: 4.8)
                Text("\(session.contextPercent)")
                    .font(.system(size: 11, weight: .bold, design: .rounded))
                    .foregroundStyle(palette.text)
            }
            .frame(width: 36, height: 36)

            VStack(alignment: .leading, spacing: 2.5) {
                HStack(spacing: 6) {
                    Text(session.projectName)
                        .font(.system(size: 13, weight: .semibold))
                        .foregroundStyle(palette.text)
                        .lineLimit(1)
                    if let b = session.gitBranch {
                        Text(b)
                            .font(.system(size: 9.5))
                            .foregroundStyle(palette.text(0.4))
                            .lineLimit(1)
                    }
                }
                Text(subtitle)
                    .font(.system(size: 11))
                    .foregroundStyle(palette.text(0.45))
                    .lineLimit(1).minimumScaleFactor(0.85)   // 窄行时缩放而非把「峰 Xk」截掉
            }

            Spacer(minLength: 7)

            VStack(alignment: .trailing, spacing: 2) {
                Text(approxMoney(session.costUSD))
                    .font(.system(size: 13, weight: .semibold, design: .rounded))
                    .foregroundStyle(palette.text(0.85))
                if session.jump != nil {
                    Image(systemName: "arrow.up.forward.app")
                        .font(.system(size: 9))
                        .foregroundStyle(palette.text(hovering ? 0.7 : 0.3))
                }
            }
        }
        .padding(.vertical, 3).padding(.horizontal, 4)
        .background(RoundedRectangle(cornerRadius: 7).fill(palette.text(hovering ? 0.08 : 0)))
        .contentShape(Rectangle())
        .onHover { hovering = $0 }
        .onTapGesture { onTap() }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(tr("\(session.projectName) 会话", "\(session.projectName) session"))
        .accessibilityValue(a11yValue)
        .accessibilityAddTraits(session.jump != nil ? .isButton : [])
        .accessibilityHint(session.jump != nil ? tr("双击跳转到对应终端", "Double-tap to jump to the terminal") : "")
    }

    /// 副标题：模型 · 上下文占用（/ 窗口）；峰值明显高于当前时附「峰 Xk」。
    private var subtitle: String {
        var s = tr("\(session.modelShort) · 上下文 \(formatTokens(session.contextTokens))/\(formatTokens(session.contextWindow))", "\(session.modelShort) · ctx \(formatTokens(session.contextTokens))/\(formatTokens(session.contextWindow))")
        if session.hasMeaningfulPeak { s += tr(" · 峰 \(formatTokens(session.peakContextTokens))", " · peak \(formatTokens(session.peakContextTokens))") }
        return s
    }

    private var a11yValue: String {
        var s = tr("\(session.modelShort)，上下文 \(session.contextPercent)%", "\(session.modelShort), context \(session.contextPercent)%")
        if session.hasMeaningfulPeak { s += tr("，峰值 \(session.peakContextPercent)%", ", peak \(session.peakContextPercent)%") }
        s += tr("，约 \(money(session.costUSD))", ", about \(money(session.costUSD))")
        return s
    }
}

// MARK: - 渐变环

/// 通用环形：弧长 = `fraction`，描边用“绿→当前等级色”的角向渐变（连续过渡，无阈值跳变）。
/// `usedPercent` 决定终点颜色（越满越红）。
struct GradientRing: View {
    var fraction: Double
    var usedPercent: Int
    var lineWidth: CGFloat = 6
    @Environment(\.palette) private var palette

    var body: some View {
        let f = max(0.0, min(1.0, fraction))
        ZStack {
            Circle().stroke(palette.track, lineWidth: lineWidth)
            Circle()
                .trim(from: 0, to: max(0.0001, f))
                .stroke(
                    AngularGradient(
                        gradient: Gradient(colors: [rampColor(0), rampColor(usedPercent)]),
                        center: .center,
                        startAngle: .degrees(0),
                        endAngle: .degrees(360 * f)
                    ),
                    style: StrokeStyle(lineWidth: lineWidth, lineCap: .round)
                )
                .rotationEffect(.degrees(-90))
                .animation(.easeOut(duration: 0.4), value: f)
        }
    }
}
