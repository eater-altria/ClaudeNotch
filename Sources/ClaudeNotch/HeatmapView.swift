import SwiftUI

/// GitHub 贡献图风格的每日用量瓷砖墙。列=周，行=星期；格子强度=当天所选指标，量化成 5 档。
struct HeatmapView: View {
    let history: UsageHistory
    let metric: HeatmapMetric
    let range: HistoryRange
    @Binding var selectedDay: LocalDay?
    let hover: HoverModel

    @Environment(\.colorScheme) private var colorScheme
    private var palette: Palette { Palette(isDark: colorScheme == .dark) }

    private let cell: CGFloat = 12
    private let gap: CGFloat = 3

    var body: some View {
        let model = HeatmapModel(history: history, metric: metric, range: range)
        VStack(alignment: .leading, spacing: 6) {
            ScrollViewReader { proxy in
                ScrollView(.horizontal, showsIndicators: false) {
                    VStack(alignment: .leading, spacing: gap) {
                        monthLabels(model)
                        HStack(alignment: .top, spacing: gap) {
                            weekdayLabels(model.calendar)
                            ForEach(Array(model.weeks.enumerated()), id: \.offset) { idx, week in
                                VStack(spacing: gap) {
                                    ForEach(week) { c in cellView(c, p95: model.p95) }
                                }
                                .id(idx)
                            }
                        }
                    }
                    .padding(.vertical, 2)
                }
                // 数据是异步构建的：开窗时还空，要在历史就绪 / 切范围后再滚到最新一周。
                .onAppear { scrollToEnd(proxy, model.weeks.count) }
                .onChange(of: history.lastBuiltAt) { _, _ in scrollToEnd(proxy, model.weeks.count) }
                .onChange(of: range) { _, _ in scrollToEnd(proxy, model.weeks.count) }
            }
            legend
        }
    }

    private func scrollToEnd(_ proxy: ScrollViewProxy, _ count: Int) {
        guard count > 0 else { return }
        DispatchQueue.main.async { withAnimation { proxy.scrollTo(count - 1, anchor: .trailing) } }
    }

    // MARK: 单元格

    @ViewBuilder
    private func cellView(_ c: HeatCell, p95: Double) -> some View {
        if c.inRange {
            let level = HeatmapModel.level(c.value, p95: p95)
            RoundedRectangle(cornerRadius: 2.5)
                .fill(heatColor(level: level))
                .frame(width: cell, height: cell)
                .overlay(RoundedRectangle(cornerRadius: 2.5)
                    .stroke(selectedDay == c.day ? palette.text(0.8) : .clear, lineWidth: 1.5))
                .onContinuousHover(coordinateSpace: .named(kAnalyticsSpace)) { phase in
                    switch phase {
                    case .active(let p):
                        hover.payload = HoverPayload(title: dateString(c.date), lines: hoverLines(c.day), point: p)
                    case .ended:
                        if hover.payload?.title == dateString(c.date) { hover.payload = nil }
                    }
                }
                .onTapGesture { selectedDay = (selectedDay == c.day ? nil : c.day) }
                .accessibilityLabel(tooltip(c))
        } else {
            RoundedRectangle(cornerRadius: 2.5).fill(.clear).frame(width: cell, height: cell)
        }
    }

    private func heatColor(level: Int) -> Color {
        if level == 0 { return palette.track }
        let base = Color(red: 0.18, green: 0.78, blue: 0.44)   // GitHub 绿（单色相，高用量≠坏）
        let op = [0.30, 0.52, 0.76, 1.0][level - 1]
        return base.opacity(op)
    }

    private func tooltip(_ c: HeatCell) -> String {
        guard let s = history.days[c.day] else { return "\(dateString(c.date)) · 无活动" }
        return "\(dateString(c.date)) · Billable \(formatTokens(s.tokens.billable)) · Total \(formatTokens(s.tokens.total)) · "
             + String(format: "≈$%.2f", s.cost) + " · \(s.messageCount) 条"
    }

    private func hoverLines(_ day: LocalDay) -> [String] {
        guard let s = history.days[day] else { return ["无活动"] }
        var lines = ["Billable \(formatTokens(s.tokens.billable)) · Total \(formatTokens(s.tokens.total))",
                     String(format: "≈$%.2f · %d 条", s.cost, s.messageCount)]
        let proj = s.perProject.sorted { $0.value > $1.value }.prefix(2)
            .map { "\($0.key) \(formatTokens($0.value))" }.joined(separator: " · ")
        if !proj.isEmpty { lines.append(proj) }
        return lines
    }

    private func dateString(_ d: Date) -> String {
        let f = DateFormatter(); f.dateFormat = "yyyy-MM-dd"; return f.string(from: d)
    }

    // MARK: 轴标签

    private func weekdayLabels(_ cal: Calendar) -> some View {
        let syms = orderedVeryShortWeekdays(cal)
        return VStack(spacing: gap) {
            ForEach(0..<7, id: \.self) { row in
                Text(row % 2 == 1 ? syms[row] : "")
                    .font(.system(size: 8)).foregroundStyle(palette.text(0.45))
                    .frame(width: 16, height: cell, alignment: .trailing)
            }
        }
    }

    private func monthLabels(_ model: HeatmapModel) -> some View {
        HStack(spacing: gap) {
            Color.clear.frame(width: 16, height: 9)   // 对齐星期标签列
            ForEach(Array(model.monthLabels.enumerated()), id: \.offset) { _, label in
                Text(label).font(.system(size: 8)).foregroundStyle(palette.text(0.45))
                    .frame(width: cell, height: 9, alignment: .leading)
            }
        }
    }

    private var legend: some View {
        HStack(spacing: 4) {
            Text("少").font(.system(size: 8)).foregroundStyle(palette.text(0.45))
            ForEach(0..<5, id: \.self) { l in
                RoundedRectangle(cornerRadius: 2).fill(heatColor(level: l)).frame(width: 10, height: 10)
            }
            Text("多").font(.system(size: 8)).foregroundStyle(palette.text(0.45))
        }
    }

    private func orderedVeryShortWeekdays(_ cal: Calendar) -> [String] {
        let syms = cal.veryShortStandaloneWeekdaySymbols
        let start = cal.firstWeekday - 1
        return (0..<7).map { syms[(start + $0) % 7] }
    }
}

// MARK: - 网格布局计算（纯数据）

struct HeatmapModel {
    let calendar = Calendar.current
    let weeks: [[HeatCell]]      // 每列 7 格（顶=本周首日）
    let monthLabels: [String]    // 与 weeks 一一对应
    let p95: Double

    init(history: UsageHistory, metric: HeatmapMetric, range: HistoryRange, now: Date = Date()) {
        let cal = Calendar.current
        let today = cal.startOfDay(for: now)
        let earliest = history.days.keys.min().flatMap { DayKey.toDate($0, cal) }
        let rawStart = range.startDate(now: now).map { cal.startOfDay(for: $0) }
            ?? earliest.map { cal.startOfDay(for: $0) } ?? today
        let gridStart = HeatmapModel.startOfWeek(rawStart, cal)
        let dayCount = (cal.dateComponents([.day], from: gridStart, to: today).day ?? 0) + 1
        let weekCount = max(1, Int(ceil(Double(dayCount) / 7.0)))

        var weeks: [[HeatCell]] = []
        var labels: [String] = []
        var values: [Double] = []
        var lastMonth = -1
        var idCounter = 0
        let monthFmt = DateFormatter(); monthFmt.dateFormat = "M月"

        for w in 0..<weekCount {
            var col: [HeatCell] = []
            var colFirstMonth = -1
            for r in 0..<7 {
                let offset = w * 7 + r
                let date = cal.date(byAdding: .day, value: offset, to: gridStart) ?? gridStart
                let day = DayKey.from(date, cal)
                let inRange = date >= rawStart && date <= today
                let stat = history.days[day]
                let value = inRange ? (stat?.metricValue(metric) ?? 0) : 0
                if inRange && value > 0 { values.append(value) }
                if r == 0 { colFirstMonth = cal.component(.month, from: date) }
                col.append(HeatCell(id: idCounter, date: date, day: day, value: value,
                                    inRange: inRange, hasData: (stat?.messageCount ?? 0) > 0))
                idCounter += 1
            }
            weeks.append(col)
            // 月份换列时打标签
            if colFirstMonth != lastMonth, let d = DayKey.toDate(col.first!.day, cal) {
                labels.append(monthFmt.string(from: d)); lastMonth = colFirstMonth
            } else {
                labels.append("")
            }
        }

        self.weeks = weeks
        self.monthLabels = labels
        self.p95 = HeatmapModel.percentile(values, 0.95)
    }

    static func startOfWeek(_ d: Date, _ cal: Calendar) -> Date {
        let wd = cal.component(.weekday, from: d)
        let delta = (wd - cal.firstWeekday + 7) % 7
        return cal.date(byAdding: .day, value: -delta, to: cal.startOfDay(for: d)) ?? d
    }

    static func level(_ value: Double, p95: Double) -> Int {
        if value <= 0 { return 0 }
        if p95 <= 0 { return 1 }
        let r = value / p95
        if r < 0.25 { return 1 }
        if r < 0.5 { return 2 }
        if r < 0.75 { return 3 }
        return 4
    }

    static func percentile(_ xs: [Double], _ p: Double) -> Double {
        guard !xs.isEmpty else { return 0 }
        let s = xs.sorted()
        let idx = Int((Double(s.count - 1) * p).rounded())
        return s[min(max(0, idx), s.count - 1)]
    }
}

struct HeatCell: Identifiable {
    let id: Int
    let date: Date
    let day: LocalDay
    let value: Double
    let inRange: Bool
    let hasData: Bool
}
