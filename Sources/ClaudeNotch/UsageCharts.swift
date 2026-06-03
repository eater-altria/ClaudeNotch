import SwiftUI
import Charts

private let chartGreen = Color(red: 0.18, green: 0.78, blue: 0.44)

private func dayDateString(_ d: Date) -> String {
    let f = DateFormatter(); f.dateFormat = "yyyy-MM-dd"; return f.string(from: d)
}

/// 所选范围内的每日值柱状趋势。悬浮显示当天明细。
struct TrendChart: View {
    let history: UsageHistory
    let metric: HeatmapMetric
    let range: HistoryRange
    let hover: HoverModel

    var body: some View {
        let pts: [(date: Date, value: Double)] = history.dayKeys(in: range).compactMap { day in
            guard let d = DayKey.toDate(day) else { return nil }
            return (d, history.days[day]?.metricValue(metric) ?? 0)
        }
        Chart {
            ForEach(Array(pts.enumerated()), id: \.offset) { _, p in
                BarMark(x: .value(tr("日期", "Date"), p.date, unit: .day),
                        y: .value(metric.label, p.value))
                    .foregroundStyle(chartGreen)
            }
        }
        .chartYAxis {
            AxisMarks { value in
                AxisGridLine()
                AxisValueLabel {
                    if let v = value.as(Double.self) {
                        Text(metric == .cost ? money(v, decimals: 0) : formatTokens(Int(v)))
                            .font(.system(size: 11))
                    }
                }
            }
        }
        .chartOverlay { proxy in
            GeometryReader { geo in
                Rectangle().fill(.clear).contentShape(Rectangle())
                    .onContinuousHover(coordinateSpace: .local) { phase in
                        switch phase {
                        case .active(let loc):
                            let plot = geo[proxy.plotAreaFrame]
                            guard plot.contains(loc), let date: Date = proxy.value(atX: loc.x - plot.minX) else {
                                hover.payload = nil; return
                            }
                            let day = DayKey.from(date)
                            if let s = history.days[day] {
                                let origin = geo.frame(in: .named(kAnalyticsSpace)).origin
                                hover.payload = HoverPayload(
                                    title: dayDateString(date),
                                    lines: [tr("计费 \(formatTokens(s.tokens.billable)) · 合计 \(formatTokens(s.tokens.total))",
                                               "Billable \(formatTokens(s.tokens.billable)) · Total \(formatTokens(s.tokens.total))"),
                                            approxMoney(s.cost) + " · " + tr("\(s.messageCount) 条", "\(s.messageCount) msgs")],
                                    point: CGPoint(x: origin.x + loc.x, y: origin.y + loc.y))
                            } else { hover.payload = nil }
                        case .ended:
                            hover.payload = nil
                        }
                    }
            }
        }
        .frame(height: 150)
    }
}

/// 时段打卡：7 星期 × 24 小时，点大小=该时段累计 billable token。悬浮显示具体时段。
struct PunchCardChart: View {
    let history: UsageHistory
    let range: HistoryRange
    let hover: HoverModel

    var body: some View {
        let cal = Calendar.current
        let syms = cal.veryShortStandaloneWeekdaySymbols   // index 0 = 周日
        var grid = Array(repeating: Array(repeating: 0, count: 24), count: 7)
        for day in history.dayKeys(in: range) {
            guard let date = DayKey.toDate(day, cal), let stat = history.days[day] else { continue }
            let wd = cal.component(.weekday, from: date) - 1
            for (h, v) in stat.byHour where h >= 0 && h < 24 { grid[wd][h] += v }
        }
        var pts: [(hour: Int, wd: Int, v: Int)] = []
        for wd in 0..<7 { for h in 0..<24 where grid[wd][h] > 0 { pts.append((h, wd, grid[wd][h])) } }

        return Chart {
            ForEach(Array(pts.enumerated()), id: \.offset) { _, p in
                PointMark(x: .value(tr("时", "Hour"), p.hour), y: .value(tr("星期", "Weekday"), p.wd))
                    .symbolSize(by: .value("tokens", p.v))
                    .foregroundStyle(chartGreen)
            }
        }
        .chartXScale(domain: -0.5...23.5)
        .chartXAxis { AxisMarks(values: [0, 6, 12, 18, 23]) { v in
            AxisValueLabel { if let h = v.as(Int.self) { Text(tr("\(h)时", "\(h):00")).font(.system(size: 11)) } } } }
        .chartYScale(domain: -0.5...6.5)
        .chartYAxis { AxisMarks(values: Array(0..<7)) { v in
            AxisValueLabel { if let i = v.as(Int.self), i < syms.count { Text(syms[i]).font(.system(size: 11)) } } } }
        .chartLegend(.hidden)
        .chartOverlay { proxy in
            GeometryReader { geo in
                Rectangle().fill(.clear).contentShape(Rectangle())
                    .onContinuousHover(coordinateSpace: .local) { phase in
                        switch phase {
                        case .active(let loc):
                            let plot = geo[proxy.plotAreaFrame]
                            guard plot.contains(loc),
                                  let hx: Double = proxy.value(atX: loc.x - plot.minX),
                                  let wy: Double = proxy.value(atY: loc.y - plot.minY) else {
                                hover.payload = nil; return
                            }
                            let hr = Int(hx.rounded()), wd = Int(wy.rounded())
                            if hr >= 0, hr < 24, wd >= 0, wd < 7, grid[wd][hr] > 0 {
                                let origin = geo.frame(in: .named(kAnalyticsSpace)).origin
                                hover.payload = HoverPayload(
                                    title: "\(syms[wd]) \(hr):00–\(hr):59",
                                    lines: [tr("计费 \(formatTokens(grid[wd][hr]))", "Billable \(formatTokens(grid[wd][hr]))")],
                                    point: CGPoint(x: origin.x + loc.x, y: origin.y + loc.y))
                            } else { hover.payload = nil }
                        case .ended:
                            hover.payload = nil
                        }
                    }
            }
        }
        .frame(height: 150)
    }
}
