using System.Globalization;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ClaudeNotch.Core;

namespace ClaudeNotch.UI;

/// <summary>数据统计窗:KPI + 热力图 + 趋势(连续日轴) + 时段打卡 + 模型/项目/缓存/连续 + 导出。深色。</summary>
public sealed class AnalyticsWindow : Window
{
    readonly HistoryStore _store;
    readonly StackPanel _root;
    HeatmapMetric _metric = HeatmapMetric.Billable;
    HistoryRange _range = HistoryRange.M12;
    int? _selectedDay;

    static Color Fg => Palette.Text;
    static Color Dim => Palette.TextDim;
    static Color Faint => Palette.TextFaint;
    static Color Green => Palette.Green;

    public AnalyticsWindow(HistoryStore store)
    {
        _store = store;
        Title = L.Tr("数据统计", "Analytics");
        Width = 980; Height = 800;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Palette.Brush(Palette.WindowBg);

        _root = new StackPanel { Margin = new Thickness(24) };
        Content = new ScrollViewer { Content = _root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        _store.Changed += () => Dispatcher.UIThread.Post(Build);
        L.Changed += () => Dispatcher.UIThread.Post(Build);
        ActualThemeVariantChanged += (_, _) => { Background = Palette.Brush(Palette.WindowBg); Build(); };
        Opened += (_, _) => _store.RefreshIfNeeded();
        Build();
    }

    void Build()
    {
        _root.Children.Clear();
        var h = _store.History;

        _root.Children.Add(Header());

        if (_store.IsBuilding)
        {
            _root.Children.Add(new ProgressBar { IsIndeterminate = _store.Progress is null, Maximum = 1, Value = _store.Progress ?? 0, Margin = new Thickness(0, 0, 0, 10) });
            _root.Children.Add(T(L.Tr($"正在扫描历史… {(int)((_store.Progress ?? 0) * 100)}%", $"Scanning history… {(int)((_store.Progress ?? 0) * 100)}%"), 11, Dim, bottom: 8));
        }

        // KPI
        var kpis = new Grid { Margin = new Thickness(0, 0, 0, 12), ColumnDefinitions = new ColumnDefinitions("*,*,*,*") };
        AddCol(kpis, Kpi(L.Tr("今日", "Today"), h.Today()), 0);
        AddCol(kpis, Kpi(L.Tr("7 天", "7 days"), h.Recent(7)), 1);
        AddCol(kpis, Kpi(L.Tr("30 天", "30 days"), h.Recent(30)), 2);
        AddCol(kpis, Kpi(L.Tr("累计", "All time"), h.Lifetime), 3);
        _root.Children.Add(kpis);

        // 热力图
        _root.Children.Add(Card(L.Tr($"每日用量 · {_metric.Label()}", $"Daily usage · {_metric.Label()}"), inner =>
        {
            inner.Children.Add(BuildHeatmap(h));
            inner.Children.Add(Legend());
            if (_selectedDay is int day && h.Days.TryGetValue(day, out var s) && DayKey.ToDate(day) is DateTime dt)
            {
                inner.Children.Add(Divider());
                inner.Children.Add(DayDetail(dt, s));
            }
        }));

        _root.Children.Add(Card(L.Tr($"趋势 · {_metric.Label()}（{_range.Label()}）", $"Trend · {_metric.Label()} ({_range.Label()})"), inner => inner.Children.Add(BuildTrend(h))));
        _root.Children.Add(Card(L.Tr($"时段打卡（计费，{_range.Label()}）", $"Hourly punch card (billable, {_range.Label()})"), inner => inner.Children.Add(BuildPunchCard(h))));

        var agg = h.Aggregate(h.DayKeysIn(_range));
        var row1 = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        AddCol(row1, ModelPanel(agg), 0);
        AddCol(row1, ProjectPanel(agg), 1);
        _root.Children.Add(row1);

        var row2 = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        AddCol(row2, CachePanel(agg), 0);
        AddCol(row2, StreaksPanel(h), 1);
        _root.Children.Add(row2);

        _root.Children.Add(T(L.Tr("「花费」按 API 单价折算，订阅用户并不按此单独计费；第三方模型未收录时按 Sonnet 近似（标「估」）。时间按本地日历分桶。",
            "“Cost” is estimated at API rates; subscription users aren't billed this way. Uncatalogued third-party models fall back to Sonnet (marked “est”). Times are bucketed by local calendar."), 11, Faint, top: 6));
    }

    static void AddCol(Grid g, Control e, int col) { Grid.SetColumn(e, col); g.Children.Add(e); }

    // ── 顶栏 ──
    Control Header()
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 16), ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var title = new TextBlock { Text = L.Tr("数据统计", "Analytics"), FontSize = 22, FontWeight = FontWeight.Bold, Foreground = Palette.Brush(Fg), FontFamily = new FontFamily(Palette.FontDisplay), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(title, 0); grid.Children.Add(title);

        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };

        // 指标分段控件(等高,与右侧按钮/下拉对齐 —— 修旧版 SelectorBar 高出一截)
        controls.Children.Add(BuildSegmented());

        var rangeCombo = new ComboBox { MinWidth = 96, Height = 32, VerticalAlignment = VerticalAlignment.Center };
        foreach (var r in new[] { HistoryRange.M3, HistoryRange.M6, HistoryRange.M12, HistoryRange.All })
            rangeCombo.Items.Add(new ComboBoxItem { Content = r.Label(), Tag = r });
        rangeCombo.SelectedIndex = (int)_range;
        rangeCombo.SelectionChanged += (_, _) =>
        {
            if (rangeCombo.SelectedItem is ComboBoxItem it && it.Tag is HistoryRange r) { _range = r; _selectedDay = null; Build(); }
        };
        controls.Children.Add(rangeCombo);

        controls.Children.Add(Btn(L.Tr("重新扫描", "Rescan"), () => _store.Refresh()));
        controls.Children.Add(Btn(L.Tr("导出 CSV", "Export CSV"), () => _ = Export(false)));
        controls.Children.Add(Btn(L.Tr("导出 JSON", "Export JSON"), () => _ = Export(true)));
        Grid.SetColumn(controls, 1); grid.Children.Add(controls);
        return grid;
    }

    Control BuildSegmented()
    {
        var box = new Border
        {
            Height = 32, CornerRadius = new CornerRadius(6), Padding = new Thickness(2),
            Background = Palette.Brush(Palette.SubtleFill),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var m in new[] { HeatmapMetric.Billable, HeatmapMetric.Cost, HeatmapMetric.Total })
        {
            bool sel = m == _metric;
            var b = new Button
            {
                Content = m.Label(),
                FontSize = 12,
                Padding = new Thickness(10, 0, 10, 0),
                CornerRadius = new CornerRadius(4),
                Background = sel ? Palette.Brush(Palette.Track) : Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = Palette.Brush(sel ? Fg : Dim),
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            var metric = m;
            b.Click += (_, _) => { if (_metric != metric) { _metric = metric; _selectedDay = null; Build(); } };
            row.Children.Add(b);
        }
        box.Child = row;
        return box;
    }

    // ── KPI ──
    Border Kpi(string title, DayStat s)
    {
        var sp = new StackPanel();
        sp.Children.Add(T(title, 12, Dim));
        sp.Children.Add(new TextBlock { Text = Money.Approx(s.Cost), FontSize = 22, FontWeight = FontWeight.SemiBold, Foreground = Palette.Brush(Fg), FontFamily = new FontFamily(Palette.FontDisplay) });
        sp.Children.Add(T($"{TranscriptParser.TokensShort(s.Tokens.Billable)} billable · " + L.Tr($"{s.MessageCount} 条", $"{s.MessageCount} msgs"), 11, Dim));
        return new Border { Background = Palette.Brush(Palette.CardBg), CornerRadius = new CornerRadius(10), Padding = new Thickness(14), Margin = new Thickness(0, 0, 8, 0), Child = sp };
    }

    // ── 热力图 ──
    Control BuildHeatmap(UsageHistory h)
    {
        var keys = h.DayKeysIn(_range);
        var values = keys.Where(k => (h.Days.TryGetValue(k, out var s) ? s.MetricValue(_metric) : 0) > 0).Select(k => h.Days[k].MetricValue(_metric)).ToList();
        double p95 = Percentile(values, 0.95);

        var start = (_range.StartDate(DateTime.Now) ?? (keys.Count > 0 ? DayKey.ToDate(keys[0]) ?? DateTime.Today : DateTime.Today)).Date;
        var today = DateTime.Today;
        var gridStart = StartOfWeek(start);
        int weekCount = Math.Max(1, (int)Math.Ceiling(((today - gridStart).Days + 1) / 7.0));
        var fmt = CultureInfo.CurrentCulture.DateTimeFormat;

        var outer = new StackPanel();

        var monthRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24, 0, 0, 2) };
        int lastMonth = -1;
        for (int w = 0; w < weekCount; w++)
        {
            var d = gridStart.AddDays(w * 7);
            string label = (d.Month != lastMonth) ? fmt.GetAbbreviatedMonthName(d.Month) : "";
            lastMonth = d.Month;
            monthRow.Children.Add(new TextBlock { Text = label, FontSize = 9, Foreground = Palette.Brush(Faint), Width = 13, FontFamily = new FontFamily(Palette.FontFamily) });
        }
        outer.Children.Add(monthRow);

        var body = new StackPanel { Orientation = Orientation.Horizontal };
        var weekdayCol = new StackPanel { Width = 24 };
        for (int r = 0; r < 7; r++)
            weekdayCol.Children.Add(new TextBlock { Text = (r % 2 == 1) ? fmt.AbbreviatedDayNames[r] : "", FontSize = 9, Foreground = Palette.Brush(Faint), Height = 13, FontFamily = new FontFamily(Palette.FontFamily) });
        body.Children.Add(weekdayCol);

        var weeks = new StackPanel { Orientation = Orientation.Horizontal };
        for (int w = 0; w < weekCount; w++)
        {
            var colSp = new StackPanel { Margin = new Thickness(0, 0, 2, 0) };
            for (int r = 0; r < 7; r++)
            {
                var date = gridStart.AddDays(w * 7 + r);
                bool inRange = date >= start && date <= today;
                double v = 0;
                if (inRange && h.Days.TryGetValue(DayKey.From(date), out var st)) v = st.MetricValue(_metric);
                int level = Level(v, p95);
                int dayKey = DayKey.From(date);
                var cell = new Border
                {
                    Width = 11, Height = 11, Margin = new Thickness(0, 0, 0, 2),
                    CornerRadius = new CornerRadius(2.5),
                    Background = Palette.Brush(HeatColor(level, inRange)),
                };
                if (inRange) ToolTip.SetTip(cell, $"{date:yyyy-MM-dd} · {CellTip(h, date)}");
                if (inRange && _selectedDay == dayKey)
                {
                    cell.BorderBrush = Palette.Brush(Fg);
                    cell.BorderThickness = new Thickness(1.5);
                }
                if (inRange) cell.PointerPressed += (_, _) => { _selectedDay = (_selectedDay == dayKey) ? null : dayKey; Build(); };
                colSp.Children.Add(cell);
            }
            weeks.Children.Add(colSp);
        }
        var scroller = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = weeks };
        body.Children.Add(scroller);
        outer.Children.Add(body);
        return outer;
    }

    Control Legend()
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24, 8, 0, 0) };
        sp.Children.Add(new TextBlock { Text = L.Tr("少", "Less"), FontSize = 10, Foreground = Palette.Brush(Faint), Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
        for (int l = 0; l < 5; l++)
            sp.Children.Add(new Border { Width = 11, Height = 11, CornerRadius = new CornerRadius(2.5), Background = Palette.Brush(HeatColor(l, true)), Margin = new Thickness(1, 0, 1, 0) });
        sp.Children.Add(new TextBlock { Text = L.Tr("多", "More"), FontSize = 10, Foreground = Palette.Brush(Faint), Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return sp;
    }

    string CellTip(UsageHistory h, DateTime date)
    {
        if (!h.Days.TryGetValue(DayKey.From(date), out var s)) return L.Tr("无活动", "No activity");
        return L.Tr($"计费 {TranscriptParser.TokensShort(s.Tokens.Billable)} · 合计 {TranscriptParser.TokensShort(s.Tokens.Total)}",
                    $"Billable {TranscriptParser.TokensShort(s.Tokens.Billable)} · Total {TranscriptParser.TokensShort(s.Tokens.Total)}")
            + " · " + Money.Approx(s.Cost) + " · " + L.Tr($"{s.MessageCount} 条", $"{s.MessageCount} msgs");
    }

    Control DayDetail(DateTime d, DayStat s)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = d.ToString("yyyy-MM-dd"), FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = Palette.Brush(Fg) });
        sp.Children.Add(T(L.Tr($"计费 {TranscriptParser.TokensShort(s.Tokens.Billable)} · 合计 {TranscriptParser.TokensShort(s.Tokens.Total)}",
            $"Billable {TranscriptParser.TokensShort(s.Tokens.Billable)} · Total {TranscriptParser.TokensShort(s.Tokens.Total)}")
            + " · " + Money.Approx(s.Cost) + " · " + L.Tr($"{s.MessageCount} 条", $"{s.MessageCount} msgs"), 12, Dim));
        var models = string.Join(" / ", s.PerModel.Where(kv => !TranscriptParser.IsSyntheticModel(kv.Key)).OrderByDescending(kv => kv.Value.Billable).Take(4).Select(kv => TranscriptParser.ShortModelName(kv.Key)));
        if (models.Length > 0) sp.Children.Add(T(L.Tr("模型：", "Models: ") + models, 11, Faint));
        var projs = string.Join(" · ", s.PerProject.OrderByDescending(kv => kv.Value).Take(3).Select(kv => $"{kv.Key} {TranscriptParser.TokensShort(kv.Value)}"));
        if (projs.Length > 0) sp.Children.Add(T(L.Tr("项目：", "Projects: ") + projs, 11, Faint));
        return sp;
    }

    // ── 趋势(自适应聚合) ──
    // 按所选范围自动选粒度:≤60 天→按日,≤240 天→按周,更长→按月。
    // 这样长范围里活跃的那个桶是一根又宽又高的柱(可见),而不是 365 根细到看不见的日柱。
    // 时间正序(左=早,右=今),贴合卡片宽度。
    Control BuildTrend(UsageHistory h)
    {
        var today = DateTime.Today;
        DateTime start = _range.StartDate(DateTime.Now)?.Date
            ?? (h.Days.Count > 0 ? (DayKey.ToDate(h.Days.Keys.Min())?.Date ?? today) : today);
        if (start > today) start = today;
        int totalDays = (today - start).Days + 1;

        double DayVal(DateTime d) => h.Days.TryGetValue(DayKey.From(d), out var s) ? s.MetricValue(_metric) : 0;
        double SumRange(DateTime a, DateTime b) { double sum = 0; for (var d = a; d <= b; d = d.AddDays(1)) if (d >= start && d <= today) sum += DayVal(d); return sum; }

        var buckets = new List<(string label, double val)>();
        if (totalDays <= 60)
            for (var d = start; d <= today; d = d.AddDays(1)) buckets.Add((d.ToString("MM-dd"), DayVal(d)));
        else if (totalDays <= 240)
            for (var w = StartOfWeek(start); w <= today; w = w.AddDays(7)) buckets.Add(($"{w:MM-dd} +7d", SumRange(w, w.AddDays(6))));
        else
            for (var m = new DateTime(start.Year, start.Month, 1); m <= today; m = m.AddMonths(1)) buckets.Add(($"{m:yyyy-MM}", SumRange(m, m.AddMonths(1).AddDays(-1))));

        double max = buckets.Count > 0 ? Math.Max(1, buckets.Max(b => b.val)) : 1;
        const double height = 140, targetW = 860;
        double slot = Math.Max(6, targetW / Math.Max(1, buckets.Count));
        double barW = Math.Max(4, slot * 0.72), gap = Math.Max(2, slot - barW);

        var bars = new StackPanel { Orientation = Orientation.Horizontal, Height = height, VerticalAlignment = VerticalAlignment.Bottom };
        foreach (var b in buckets)
        {
            bool has = b.val > 0;
            double bh = has ? Math.Max(4, b.val / max * (height - 4)) : 2;
            var bar = new Border
            {
                Width = barW, Height = bh, Margin = new Thickness(0, 0, gap, 0),
                CornerRadius = new CornerRadius(2, 2, 0, 0),
                Background = Palette.Brush(has ? Green : Palette.HeatEmpty),
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            ToolTip.SetTip(bar, b.label + " · " + (_metric == HeatmapMetric.Cost ? Money.Approx(b.val) : TranscriptParser.TokensShort((int)b.val)));
            bars.Children.Add(bar);
        }
        return new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Height = height + 8, Content = bars };
    }

    // ── 时段打卡 7×24 ──
    Control BuildPunchCard(UsageHistory h)
    {
        var data = new int[7, 24];
        foreach (var k in h.DayKeysIn(_range))
        {
            if (DayKey.ToDate(k) is not DateTime date || !h.Days.TryGetValue(k, out var s)) continue;
            int wd = (int)date.DayOfWeek;
            foreach (var kv in s.ByHour) if (kv.Key >= 0 && kv.Key < 24) data[wd, kv.Key] += kv.Value;
        }
        int max = 1;
        for (int r = 0; r < 7; r++) for (int c = 0; c < 24; c++) max = Math.Max(max, data[r, c]);
        var fmt = CultureInfo.CurrentCulture.DateTimeFormat;

        var table = new Grid();
        table.ColumnDefinitions.Add(new ColumnDefinition(30, GridUnitType.Pixel));
        for (int c = 0; c < 24; c++) table.ColumnDefinitions.Add(new ColumnDefinition(16, GridUnitType.Pixel));
        for (int r = 0; r < 7; r++) table.RowDefinitions.Add(new RowDefinition(15, GridUnitType.Pixel));
        table.RowDefinitions.Add(new RowDefinition(16, GridUnitType.Pixel));

        for (int r = 0; r < 7; r++)
        {
            var lbl = new TextBlock { Text = fmt.AbbreviatedDayNames[r], FontSize = 9, Foreground = Palette.Brush(Faint), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(lbl, r); Grid.SetColumn(lbl, 0); table.Children.Add(lbl);
            for (int c = 0; c < 24; c++)
            {
                int v = data[r, c];
                if (v <= 0) continue;
                double size = 4 + 9.0 * Math.Sqrt((double)v / max);
                var dot = new Ellipse
                {
                    Width = size, Height = size,
                    Fill = Palette.Brush(Green), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                };
                ToolTip.SetTip(dot, $"{fmt.AbbreviatedDayNames[r]} {c}:00 · {TranscriptParser.TokensShort(v)}");
                Grid.SetRow(dot, r); Grid.SetColumn(dot, c + 1); table.Children.Add(dot);
            }
        }
        // 横轴小时刻度:每 4 小时一个,跨多列防裁切(修旧版“横坐标展示不全”)
        for (int c = 0; c < 24; c += 4)
        {
            var hr = new TextBlock { Text = L.Tr($"{c}时", $"{c}:00"), FontSize = 8, Foreground = Palette.Brush(Faint) };
            Grid.SetRow(hr, 7); Grid.SetColumn(hr, c + 1); Grid.SetColumnSpan(hr, 4); table.Children.Add(hr);
        }
        return new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = table };
    }

    // ── 模型 / 项目 / 缓存 / 连续 ──
    Border ModelPanel(DayStat agg)
    {
        var kept = agg.PerModel.Where(kv => !TranscriptParser.IsSyntheticModel(kv.Key)).ToList();
        int max = kept.Count > 0 ? kept.Max(kv => kv.Value.Billable) : 1;
        return Card(L.Tr("按模型", "By model"), inner =>
        {
            if (kept.Count == 0) { inner.Children.Add(Hint()); return; }
            foreach (var kv in kept.OrderByDescending(kv => kv.Value.Billable).Take(6))
            {
                var label = TranscriptParser.ShortModelName(kv.Key) + (TranscriptParser.IsApproxPriced(kv.Key) ? L.Tr(" ·估", " ·est") : "");
                inner.Children.Add(BarRow(label, kv.Value.Billable, max, Money.Format(kv.Value.Cost(kv.Key))));
            }
        });
    }

    Border ProjectPanel(DayStat agg)
    {
        int max = agg.PerProject.Count > 0 ? agg.PerProject.Values.Max() : 1;
        return Card(L.Tr("按项目 Top", "Top projects"), inner =>
        {
            if (agg.PerProject.Count == 0) { inner.Children.Add(Hint()); return; }
            foreach (var kv in agg.PerProject.OrderByDescending(kv => kv.Value).Take(6))
                inner.Children.Add(BarRow(kv.Key, kv.Value, max, TranscriptParser.TokensShort(kv.Value)));
        });
    }

    Border CachePanel(DayStat agg)
    {
        int cr = agg.Tokens.CacheRead, bill = agg.Tokens.Billable;
        double ratio = bill > 0 ? (double)cr / bill : 0;
        return Card(L.Tr("缓存效率", "Cache efficiency"), inner =>
        {
            inner.Children.Add(InfoRow("cache_read", TranscriptParser.TokensShort(cr)));
            inner.Children.Add(InfoRow("billable", TranscriptParser.TokensShort(bill)));
            inner.Children.Add(InfoRow("read / billable", ratio.ToString("0.0", CultureInfo.InvariantCulture) + "×"));
            inner.Children.Add(T(ratio > 8 ? L.Tr("上下文重放较多，可留意 /compact 或新开会话", "Heavy context replay; consider /compact or a new session") : L.Tr("正常范围", "Normal range"), 11, Faint, top: 4));
        });
    }

    Border StreaksPanel(UsageHistory h)
    {
        var (current, longest, busiest) = h.Streaks(_metric);
        return Card(L.Tr("连续 & 峰值", "Streaks & peak"), inner =>
        {
            inner.Children.Add(InfoRow(L.Tr("当前连续", "Current streak"), L.Tr($"{current} 天", $"{current} d")));
            inner.Children.Add(InfoRow(L.Tr("最长连续", "Longest streak"), L.Tr($"{longest} 天", $"{longest} d")));
            if (busiest is { } b && DayKey.ToDate(b.day) is DateTime bd)
                inner.Children.Add(InfoRow(L.Tr("最忙一天", "Busiest day"), $"{bd:yyyy-MM-dd} · " + (_metric == HeatmapMetric.Cost ? Money.Approx(b.value) : TranscriptParser.TokensShort((int)b.value))));
        });
    }

    Control BarRow(string label, int value, int max, string trailing)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 0), ColumnDefinitions = new ColumnDefinitions("132,*,66") };
        var lbl = new TextBlock { Text = label, FontSize = 12, Foreground = Palette.Brush(Fg), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(lbl, 0); g.Children.Add(lbl);
        var track = new Border { Height = 10, CornerRadius = new CornerRadius(5), Background = Palette.Brush(Palette.SubtleFill), HorizontalAlignment = HorizontalAlignment.Stretch };
        var bar = new Border { Height = 10, CornerRadius = new CornerRadius(5), Background = Palette.Brush(Green), HorizontalAlignment = HorizontalAlignment.Left, Width = Math.Max(2, 190.0 * value / Math.Max(1, max)) };
        var bg = new Grid { VerticalAlignment = VerticalAlignment.Center }; bg.Children.Add(track); bg.Children.Add(bar);
        Grid.SetColumn(bg, 1); g.Children.Add(bg);
        var tr = new TextBlock { Text = trailing, FontSize = 12, Foreground = Palette.Brush(Dim), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(tr, 2); g.Children.Add(tr);
        return g;
    }

    Control InfoRow(string k, string v)
    {
        var g = new Grid { Margin = new Thickness(0, 3, 0, 0), ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var kt = new TextBlock { Text = k, FontSize = 12, Foreground = Palette.Brush(Dim) };
        var vt = new TextBlock { Text = v, FontSize = 12, Foreground = Palette.Brush(Fg), FontWeight = FontWeight.SemiBold };
        Grid.SetColumn(kt, 0); Grid.SetColumn(vt, 1);
        g.Children.Add(kt); g.Children.Add(vt);
        return g;
    }

    TextBlock Hint() => T(L.Tr("暂无数据", "No data"), 12, Dim);

    // ── 卡片 / 文本工厂 ──
    Border Card(string title, Action<StackPanel> fill)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 13, Foreground = Palette.Brush(Fg), Margin = new Thickness(0, 0, 0, 8) });
        fill(sp);
        return new Border { Background = Palette.Brush(Palette.CardBg), CornerRadius = new CornerRadius(10), Padding = new Thickness(14), Margin = new Thickness(0, 0, 8, 10), Child = sp };
    }

    static TextBlock T(string text, double size, Color color, bool bold = false, double top = 0, double bottom = 0) => new()
    {
        Text = text, FontSize = size, Foreground = Palette.Brush(color), FontFamily = new FontFamily(Palette.FontFamily),
        FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, bottom),
    };

    Border Divider() => new() { Height = 1, Background = Palette.Brush(Palette.Divider), Margin = new Thickness(0, 10, 0, 8) };

    Button Btn(string text, Action act)
    {
        var b = new Button { Content = text, Height = 32, VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        b.Click += (_, _) => act();
        return b;
    }

    // ── 导出 ──
    async Task Export(bool json)
    {
        var top = GetTopLevel(this);
        if (top is null) return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = "claudenotch-usage",
            DefaultExtension = json ? "json" : "csv",
            FileTypeChoices = new[]
            {
                json ? new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } }
                     : new FilePickerFileType("CSV") { Patterns = new[] { "*.csv" } },
            },
        });
        if (file is null) return;

        string content;
        if (json)
        {
            var arr = new List<Dictionary<string, object>>();
            foreach (var day in _store.History.Days.Keys.OrderBy(x => x))
            {
                var s = _store.History.Days[day]; var d = DayKey.ToDate(day); if (d is null) continue;
                var t = s.Tokens;
                arr.Add(new() { ["date"] = d.Value.ToString("yyyy-MM-dd"), ["billable"] = t.Billable, ["total"] = t.Total, ["input"] = t.Input, ["output"] = t.Output, ["cache_read"] = t.CacheRead, ["cost_usd"] = Math.Round(s.Cost, 4), ["messages"] = s.MessageCount });
            }
            content = JsonSerializer.Serialize(arr, new JsonSerializerOptions { WriteIndented = true });
        }
        else
        {
            var sb = new StringBuilder("date,billable,total,input,output,cache_read,cost_usd,messages\n");
            foreach (var day in _store.History.Days.Keys.OrderBy(x => x))
            {
                var s = _store.History.Days[day]; var d = DayKey.ToDate(day); if (d is null) continue;
                var t = s.Tokens;
                sb.Append($"{d:yyyy-MM-dd},{t.Billable},{t.Total},{t.Input},{t.Output},{t.CacheRead},{s.Cost.ToString("F4", CultureInfo.InvariantCulture)},{s.MessageCount}\n");
            }
            content = sb.ToString();
        }
        try { await using var stream = await file.OpenWriteAsync(); await using var w = new StreamWriter(stream); await w.WriteAsync(content); } catch { }
    }

    // ── 工具 ──
    static DateTime StartOfWeek(DateTime d) { int delta = ((int)d.DayOfWeek + 7) % 7; return d.AddDays(-delta); }

    static double Percentile(List<double> xs, double p)
    {
        if (xs.Count == 0) return 0;
        var s = xs.OrderBy(x => x).ToList();
        int idx = (int)Math.Round((s.Count - 1) * p);
        return s[Math.Clamp(idx, 0, s.Count - 1)];
    }

    static int Level(double v, double p95)
    {
        if (v <= 0) return 0;
        if (p95 <= 0) return 1;
        double r = v / p95;
        if (r < 0.25) return 1; if (r < 0.5) return 2; if (r < 0.75) return 3; return 4;
    }

    static Color HeatColor(int level, bool inRange)
    {
        if (!inRange) return Palette.Argb(0, 0, 0, 0);
        if (level == 0) return Palette.HeatEmpty;
        double[] ops = { 0.30, 0.52, 0.76, 1.0 };
        return Palette.Argb((byte)(ops[level - 1] * 255), 0x2E, 0xC7, 0x71);
    }
}
