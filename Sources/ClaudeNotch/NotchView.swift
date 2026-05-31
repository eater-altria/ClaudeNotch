import SwiftUI

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
    var onLogin: () -> Void
    var onRefresh: () -> Void

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
        HStack(spacing: 7) {
            if let h = store.snapshot?.headline {
                GradientRing(fraction: Double(h.percentRemaining) / 100, usedPercent: h.percentUsed, lineWidth: 2.5)
                    .frame(width: 15, height: 15)
            } else {
                Circle().fill(palette.text(0.4)).frame(width: 7, height: 7)
            }
            Text(store.headlineText)
                .font(.system(size: 12, weight: .semibold, design: .rounded))
                .foregroundStyle(palette.text)
            Text("剩余")
                .font(.system(size: 9.5))
                .foregroundStyle(palette.text(0.5))
        }
        .frame(maxHeight: .infinity)
        .padding(.horizontal, 13)
    }

    // MARK: 展开态

    private var expandedContent: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack {
                Text("Claude 额度")
                    .font(.system(size: 12, weight: .semibold))
                    .foregroundStyle(palette.text)
                Spacer()
                Text(store.lastUpdatedText)
                    .font(.system(size: 10))
                    .foregroundStyle(palette.text(0.45))
                Button(action: onRefresh) {
                    Image(systemName: "arrow.clockwise")
                        .font(.system(size: 11, weight: .semibold))
                        .foregroundStyle(palette.text(0.7))
                }
                .buttonStyle(.plain)
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
        case .loggedOut:
            loggedOutView
        case .error(let msg):
            errorView(msg)
        case .loading where store.snapshot == nil:
            HStack {
                ProgressView().controlSize(.small)
                Text("读取中…").foregroundStyle(palette.text(0.6)).font(.system(size: 12))
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
                        MetricRingTile(metric: m, projection: store.projections[m.id])
                            .frame(maxWidth: .infinity)
                    }
                }
                if let ep = snap.extraPercent {
                    extraRow(percent: ep, snap: snap)
                }
            } else {
                Text("暂无数据").foregroundStyle(palette.text(0.5)).font(.system(size: 12))
            }
        }
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
                Text("额外用量").font(.system(size: 11, weight: .medium)).foregroundStyle(palette.text(0.85))
                if let spent = snap.extraSpent {
                    Text(String(format: "已花 %.2f", spent)
                         + (snap.extraLimit.map { String(format: " / %.0f", $0) } ?? "")
                         + (snap.extraBalance.map { String(format: " · 余 %.2f", $0) } ?? ""))
                        .font(.system(size: 10)).foregroundStyle(palette.text(0.5))
                }
            }
            Spacer()
        }
        .padding(.top, 2)
    }

    private var loggedOutView: some View {
        VStack(spacing: 10) {
            Text("未登录 Claude").font(.system(size: 13, weight: .medium)).foregroundStyle(palette.text)
            Text("登录后即可读取你的订阅额度").font(.system(size: 11)).foregroundStyle(palette.text(0.5))
            Button(action: onLogin) {
                Text("登录 Claude")
                    .font(.system(size: 12, weight: .semibold))
                    .foregroundStyle(palette.isDark ? .black : .white)
                    .padding(.horizontal, 18).padding(.vertical, 7)
                    .background(Capsule().fill(palette.text))
            }
            .buttonStyle(.plain)
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, 16)
    }

    private func errorView(_ msg: String) -> some View {
        VStack(spacing: 8) {
            Image(systemName: "exclamationmark.triangle").foregroundStyle(.orange)
            Text(msg).font(.system(size: 11)).foregroundStyle(palette.text(0.7))
                .multilineTextAlignment(.center).fixedSize(horizontal: false, vertical: true)
            Button("重试", action: onRefresh).buttonStyle(.plain)
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
                Text("活跃会话").font(.system(size: 11, weight: .semibold)).foregroundStyle(palette.text(0.85))
                Text("\(sessionStore.sessions.count)")
                    .font(.system(size: 9, weight: .semibold, design: .rounded))
                    .foregroundStyle(palette.text(0.7))
                    .padding(.horizontal, 5).padding(.vertical, 1)
                    .background(Capsule().fill(palette.text(0.12)))
                Spacer()
                Text("≈ API 等价花费").font(.system(size: 8)).foregroundStyle(palette.text(0.32))
            }
            if sessionStore.sessions.isEmpty {
                Text("无运行中的会话")
                    .font(.system(size: 12)).foregroundStyle(palette.text(0.4))
                    .padding(.vertical, 5)
            } else {
                ScrollView(.vertical, showsIndicators: false) {
                    VStack(spacing: 11) {
                        ForEach(sessionStore.sessions) { s in
                            SessionRowView(session: s)
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

    var body: some View {
        VStack(spacing: 6) {
            ZStack {
                GradientRing(fraction: Double(metric.percentRemaining) / 100, usedPercent: metric.percentUsed, lineWidth: 6)
                VStack(spacing: -1) {
                    Text("\(metric.percentRemaining)")
                        .font(.system(size: 17, weight: .bold, design: .rounded))
                        .foregroundStyle(palette.text)
                    Text("剩%")
                        .font(.system(size: 8, weight: .medium))
                        .foregroundStyle(palette.text(0.5))
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
    @Environment(\.palette) private var palette

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
                Text("\(session.modelShort) · 上下文 \(formatTokens(session.contextTokens))/\(formatTokens(session.contextWindow))")
                    .font(.system(size: 11))
                    .foregroundStyle(palette.text(0.45))
                    .lineLimit(1)
            }

            Spacer(minLength: 7)

            Text(String(format: "≈$%.2f", session.costUSD))
                .font(.system(size: 13, weight: .semibold, design: .rounded))
                .foregroundStyle(palette.text(0.85))
        }
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
