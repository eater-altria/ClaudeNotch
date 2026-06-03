import SwiftUI
import AppKit
import UniformTypeIdentifiers

struct AnalyticsView: View {
    @ObservedObject var store: UsageHistoryStore
    let hover: HoverModel        // 由 AppDelegate 持有并传入；本视图不观察它，故连续悬浮不会重绘图表
    @State private var metric: HeatmapMetric = .billable
    @State private var range: HistoryRange = .m12
    @State private var selectedDay: LocalDay?

    private let green = Color(red: 0.18, green: 0.78, blue: 0.44)

    var body: some View {
        let h = store.history
        let agg = h.aggregate(h.dayKeys(in: range))
        VStack(spacing: 0) {
            header
            Divider()
            ScrollView {
                VStack(alignment: .leading, spacing: 14) {
                    if store.isBuilding, let p = store.progress {
                        ProgressView(value: p) { Text(tr("正在扫描历史 transcript… \(Int(p * 100))%", "Scanning history transcripts… \(Int(p * 100))%")).font(.caption) }
                    }
                    kpiStrip(h)

                    GroupBox(tr("每日用量 · \(metric.label)", "Daily usage · \(metric.label)")) {
                        VStack(alignment: .leading, spacing: 8) {
                            HeatmapView(history: h, metric: metric, range: range, selectedDay: $selectedDay, hover: hover)
                            if let day = selectedDay, let s = h.days[day], let d = DayKey.toDate(day) {
                                Divider()
                                dayDetail(d, s)
                            }
                        }.padding(.vertical, 4)
                    }

                    HStack(alignment: .top, spacing: 12) {
                        modelPanel(agg); projectPanel(agg)
                    }
                    HStack(alignment: .top, spacing: 12) {
                        cachePanel(agg); streaksPanel(h)
                    }

                    GroupBox(tr("趋势 · \(metric.label)（\(range.label)）", "Trend · \(metric.label) (\(range.label))")) { TrendChart(history: h, metric: metric, range: range, hover: hover) }
                    GroupBox(tr("时段打卡（billable，\(range.label)）", "Hourly punch card (billable, \(range.label))")) { PunchCardChart(history: h, range: range, hover: hover) }

                    Text(tr("「花费」按 API 单价折算，订阅用户并不按此单独计费；第三方子代理模型按 Sonnet 近似（标「估」）。时间按本地日历分桶。", "“Cost” is estimated at API rates; subscription users are not billed separately for this. Third-party subagent models are approximated as Sonnet (marked “est”). Times are bucketed by local calendar."))
                        .font(.system(size: 12)).foregroundStyle(.secondary)
                }
                .padding(16)
            }
        }
        .frame(minWidth: 820, minHeight: 560)
        .coordinateSpace(name: kAnalyticsSpace)
        .overlay { HoverOverlay(model: hover) }               // 唯一观察 hover 的视图，只它随悬浮重绘
        .onChange(of: range) { _, _ in selectedDay = nil; hover.payload = nil }
    }

    // MARK: 顶栏

    private var header: some View {
        HStack(spacing: 12) {
            Text(tr("数据统计", "Analytics")).font(.headline)
            Spacer()
            Picker("", selection: $metric) {
                ForEach(HeatmapMetric.allCases) { Text($0.label).tag($0) }
            }.pickerStyle(.segmented).frame(width: 320).labelsHidden()
            Picker("", selection: $range) {
                ForEach(HistoryRange.allCases) { Text($0.label).tag($0) }
            }.frame(width: 110).labelsHidden()
            Button { store.refresh() } label: { Image(systemName: "arrow.clockwise") }
                .disabled(store.isBuilding).help(tr("重新扫描", "Rescan"))
            Menu {
                Button(tr("导出 CSV…", "Export CSV…")) { exportCSV() }
                Button(tr("导出 JSON…", "Export JSON…")) { exportJSON() }
            } label: { Image(systemName: "square.and.arrow.up") }
            .frame(width: 44)
        }
        .padding(.horizontal, 16).padding(.vertical, 10)
    }

    // MARK: KPI

    private func kpiStrip(_ h: UsageHistory) -> some View {
        HStack(spacing: 10) {
            kpi(tr("今日", "Today"), h.today())
            kpi(tr("7 天", "7 days"), h.recent(days: 7))
            kpi(tr("30 天", "30 days"), h.recent(days: 30))
            kpi(tr("累计", "All time"), h.lifetime)
        }
    }
    private func kpi(_ title: String, _ s: DayStat) -> some View {
        VStack(alignment: .leading, spacing: 3) {
            Text(title).font(.system(size: 13)).foregroundStyle(.secondary)
            Text(approxMoney(s.cost)).font(.title2.weight(.semibold).monospacedDigit())
            Text("\(formatTokens(s.tokens.billable)) billable · " + tr("\(s.messageCount) 条", "\(s.messageCount) msgs"))
                .font(.system(size: 12)).foregroundStyle(.secondary).lineLimit(1)
        }
        .frame(maxWidth: .infinity, alignment: .leading).padding(10)
        .background(RoundedRectangle(cornerRadius: 8).fill(Color.primary.opacity(0.05)))
    }

    // MARK: 面板

    private func modelPanel(_ agg: DayStat) -> some View {
        let kept = agg.perModel.filter { !isSyntheticModel($0.key) }
        let total = max(1, kept.values.reduce(0) { $0 + $1.billable })
        let items = kept.map { (raw: $0.key, tokens: $0.value) }
            .sorted { $0.tokens.billable > $1.tokens.billable }.prefix(6)
        let maxV = items.map { $0.tokens.billable }.max() ?? 1
        return panel(tr("按模型", "By model")) {
            if items.isEmpty { hint(tr("暂无数据", "No data")) }
            else {
                ForEach(Array(items.enumerated()), id: \.offset) { _, it in
                    barRow(label: shortModelName(it.raw) + (isApproxPriced(it.raw) ? tr(" ·估", " ·est") : ""),
                           value: it.tokens.billable, maxValue: maxV,
                           trailing: money(it.tokens.cost(model: it.raw)),
                           hoverTitle: shortModelName(it.raw) + (isApproxPriced(it.raw) ? tr("（估价）", " (est.)") : ""),
                           hoverLines: ["Billable \(it.tokens.billable.formatted()) · \(pct(it.tokens.billable, total))",
                                        "Total \(it.tokens.total.formatted())",
                                        approxMoney(it.tokens.cost(model: it.raw))])
                }
            }
        }
    }

    private func projectPanel(_ agg: DayStat) -> some View {
        let total = max(1, agg.perProject.values.reduce(0, +))
        let items = agg.perProject.map { (name: $0.key, v: $0.value) }
            .sorted { $0.v > $1.v }.prefix(6)
        let maxV = items.map { $0.v }.max() ?? 1
        return panel(tr("按项目 Top", "Top projects")) {
            if items.isEmpty { hint(tr("暂无数据", "No data")) }
            else {
                ForEach(Array(items.enumerated()), id: \.offset) { _, it in
                    barRow(label: it.name, value: it.v, maxValue: maxV, trailing: formatTokens(it.v),
                           hoverTitle: it.name,
                           hoverLines: ["Billable \(it.v.formatted()) · \(pct(it.v, total))"])
                }
            }
        }
    }

    private func pct(_ v: Int, _ total: Int) -> String {
        total > 0 ? String(format: "%.0f%%", Double(v) / Double(total) * 100) : "—"
    }

    private func cachePanel(_ agg: DayStat) -> some View {
        let cr = agg.tokens.cacheRead, bill = agg.tokens.billable
        let ratio = bill > 0 ? Double(cr) / Double(bill) : 0
        return panel(tr("缓存效率", "Cache efficiency")) {
            infoRow("cache_read", formatTokens(cr))
            infoRow("billable", formatTokens(bill))
            infoRow("read / billable", String(format: "%.1f×", ratio))
            Text(ratio > 8 ? tr("上下文重放较多，可留意 /compact 或新开会话", "Heavy context replay; consider /compact or a fresh session") : tr("正常范围", "Normal range"))
                .font(.system(size: 12)).foregroundStyle(.secondary)
        }
    }

    private func streaksPanel(_ h: UsageHistory) -> some View {
        let st = h.streaks(metric: metric)
        return panel(tr("连续 & 峰值", "Streaks & peaks")) {
            infoRow(tr("当前连续", "Current streak"), tr("\(st.current) 天", "\(st.current) days"))
            infoRow(tr("最长连续", "Longest streak"), tr("\(st.longest) 天", "\(st.longest) days"))
            if let b = st.busiest, let d = DayKey.toDate(b.day) {
                infoRow(tr("最忙一天", "Busiest day"), dateStr(d) + " · " + metricStr(b.value))
            }
        }
    }

    // MARK: 选中日明细

    private func dayDetail(_ d: Date, _ s: DayStat) -> some View {
        let topProj = s.perProject.sorted { $0.value > $1.value }.prefix(3)
            .map { "\($0.key) \(formatTokens($0.value))" }.joined(separator: " · ")
        let models = s.perModel.filter { !isSyntheticModel($0.key) }.sorted { $0.value.billable > $1.value.billable }
            .prefix(4).map { shortModelName($0.key) }.joined(separator: " / ")
        return VStack(alignment: .leading, spacing: 4) {
            Text(dateStr(d)).font(.system(size: 14, weight: .semibold))
            Text("Billable \(formatTokens(s.tokens.billable)) · Total \(formatTokens(s.tokens.total)) · "
                 + approxMoney(s.cost) + tr(" · \(s.messageCount) 条", " · \(s.messageCount) msgs"))
                .font(.system(size: 13)).foregroundStyle(.secondary)
            if !models.isEmpty { Text(tr("模型：\(models)", "Models: \(models)")).font(.system(size: 12)).foregroundStyle(.secondary) }
            if !topProj.isEmpty { Text(tr("项目：\(topProj)", "Projects: \(topProj)")).font(.system(size: 12)).foregroundStyle(.secondary) }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    // MARK: 小组件

    private func panel<T: View>(_ title: String, @ViewBuilder _ content: () -> T) -> some View {
        GroupBox(title) {
            VStack(alignment: .leading, spacing: 6) { content() }
                .frame(maxWidth: .infinity, alignment: .leading)
        }
    }
    private func hint(_ s: String) -> some View { Text(s).font(.system(size: 13)).foregroundStyle(.secondary) }
    private func infoRow(_ k: String, _ v: String) -> some View {
        HStack { Text(k).font(.system(size: 13)).foregroundStyle(.secondary); Spacer()
            Text(v).font(.system(size: 13).monospacedDigit()) }
    }
    private func barRow(label: String, value: Int, maxValue: Int, trailing: String,
                        hoverTitle: String, hoverLines: [String]) -> some View {
        HStack(spacing: 8) {
            Text(label).font(.system(size: 13)).frame(width: 130, alignment: .leading)
                .lineLimit(1).truncationMode(.middle)
            GeometryReader { geo in
                ZStack(alignment: .leading) {
                    Capsule().fill(Color.primary.opacity(0.08))
                    Capsule().fill(green)
                        .frame(width: max(2, geo.size.width * (maxValue > 0 ? CGFloat(value) / CGFloat(maxValue) : 0)))
                }
            }.frame(height: 10)
            Text(trailing).font(.system(size: 13).monospacedDigit()).foregroundStyle(.secondary)
                .frame(width: 64, alignment: .trailing)
        }
        .contentShape(Rectangle())
        .onContinuousHover(coordinateSpace: .named(kAnalyticsSpace)) { phase in
            switch phase {
            case .active(let p): hover.payload = HoverPayload(title: hoverTitle, lines: hoverLines, point: p)
            case .ended: if hover.payload?.title == hoverTitle { hover.payload = nil }
            }
        }
    }

    private func isApproxPriced(_ m: String) -> Bool {
        if PriceCatalog.shared.match(m) != nil { return false }   // LiteLLM 有真实单价 → 非估
        let l = m.lowercased()
        return !(l.contains("opus") || l.contains("sonnet") || l.contains("haiku"))
    }
    private func metricStr(_ v: Double) -> String {
        metric == .cost ? approxMoney(v) : formatTokens(Int(v))
    }
    private func dateStr(_ d: Date) -> String {
        let f = DateFormatter(); f.dateFormat = "yyyy-MM-dd"; return f.string(from: d)
    }

    // MARK: 导出

    private func exportCSV() {
        var lines = ["date,billable,total,input,output,cache_read,cache_write_5m,cache_write_1h,cost_usd,messages"]
        for day in store.history.days.keys.sorted() {
            guard let s = store.history.days[day], let d = DayKey.toDate(day) else { continue }
            let t = s.tokens
            lines.append("\(dateStr(d)),\(t.billable),\(t.total),\(t.input),\(t.output),\(t.cacheRead),\(t.cacheWrite5m),\(t.cacheWrite1h),\(String(format: "%.4f", s.cost)),\(s.messageCount)")
        }
        save(text: lines.joined(separator: "\n"), name: "claudenotch-usage.csv", type: .commaSeparatedText)
    }
    private func exportJSON() {
        var arr: [[String: Any]] = []
        for day in store.history.days.keys.sorted() {
            guard let s = store.history.days[day], let d = DayKey.toDate(day) else { continue }
            arr.append(["date": dateStr(d), "billable": s.tokens.billable, "total": s.tokens.total,
                        "input": s.tokens.input, "output": s.tokens.output, "cache_read": s.tokens.cacheRead,
                        "cost_usd": s.cost, "messages": s.messageCount])
        }
        if let data = try? JSONSerialization.data(withJSONObject: arr, options: [.prettyPrinted]) {
            save(data: data, name: "claudenotch-usage.json", type: .json)
        }
    }
    private func save(text: String, name: String, type: UTType) { save(data: Data(text.utf8), name: name, type: type) }
    private func save(data: Data, name: String, type: UTType) {
        let panel = NSSavePanel()
        panel.nameFieldStringValue = name
        panel.allowedContentTypes = [type]
        panel.begin { resp in if resp == .OK, let url = panel.url { try? data.write(to: url) } }
    }
}
