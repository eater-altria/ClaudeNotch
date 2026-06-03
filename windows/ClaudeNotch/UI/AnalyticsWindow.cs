using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ClaudeNotch.Core;
using Microsoft.Win32;

namespace ClaudeNotch.UI;

/// <summary>数据统计（Win11 风格，完整对齐 macOS）：KPI + 热力图(标签/图例/选中日明细) + 趋势 + 时段打卡 + 按模型/项目/缓存/连续峰值 + 导出。</summary>
public sealed class AnalyticsWindow : Window
{
    readonly HistoryStore _store;
    readonly StackPanel _root;
    HeatmapMetric _metric = HeatmapMetric.Billable;
    HistoryRange _range = HistoryRange.M12;
    int? _selectedDay;

    static readonly Color Fg = Theme.Text;
    static readonly Color Dim = Theme.TextDim;
    static readonly Color Faint = Theme.TextFaint;
    static readonly Color Green = Theme.Green;

    public AnalyticsWindow(HistoryStore store)
    {
        _store = store;
        Title = "ClaudeNotch";
        Width = 940; Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Theme.Brush(Theme.WindowBg);
        FontFamily = Theme.FontText;
        Win11.Modernize(this);

        _root = new StackPanel { Margin = new Thickness(20) };
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _root, Padding = new Thickness(0) };

        _store.Changed += () => Dispatcher.BeginInvoke(Build);
        L.Changed += Build;
        Loaded += (_, _) => _store.RefreshIfNeeded();
        Build();
    }

    void Build()
    {
        Title = L.Tr("ClaudeNotch 数据统计", "ClaudeNotch Analytics");
        _root.Children.Clear();
        var h = _store.History;

        _root.Children.Add(Header());

        if (_store.IsBuilding)
        {
            _root.Children.Add(new ProgressBar { Height = 4, IsIndeterminate = (_store.Progress is null), Maximum = 1, Value = _store.Progress ?? 0, Margin = new Thickness(0, 0, 0, 10), Foreground = Theme.Brush(Theme.Accent) });
            _root.Children.Add(T(L.Tr($"正在扫描历史… {(int)((_store.Progress ?? 0) * 100)}%", $"Scanning history… {(int)((_store.Progress ?? 0) * 100)}%"), 11, Dim, bottom: 8));
        }

        // KPI
        var kpis = new UniformGrid { Rows = 1, Columns = 4, Margin = new Thickness(0, 0, 0, 16) };
        kpis.Children.Add(Kpi(L.Tr("今日", "Today"), h.Today()));
        kpis.Children.Add(Kpi(L.Tr("7 天", "7 days"), h.Recent(7)));
        kpis.Children.Add(Kpi(L.Tr("30 天", "30 days"), h.Recent(30)));
        kpis.Children.Add(Kpi(L.Tr("累计", "All time"), h.Lifetime));
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

        // 趋势 + 时段打卡
        _root.Children.Add(Card(L.Tr($"趋势 · {_metric.Label()}（{_range.Label()}）", $"Trend · {_metric.Label()} ({_range.Label()})"), inner => inner.Children.Add(BuildTrend(h))));
        _root.Children.Add(Card(L.Tr($"时段打卡（计费，{_range.Label()}）", $"Hourly punch card (billable, {_range.Label()})"), inner => inner.Children.Add(BuildPunchCard(h))));

        // 模型 / 项目
        var agg = h.Aggregate(h.DayKeysIn(_range));
        var row1 = new UniformGrid { Rows = 1, Columns = 2 };
        row1.Children.Add(ModelPanel(agg));
        row1.Children.Add(ProjectPanel(agg));
        _root.Children.Add(row1);

        // 缓存 / 连续&峰值
        var row2 = new UniformGrid { Rows = 1, Columns = 2 };
        row2.Children.Add(CachePanel(agg));
        row2.Children.Add(StreaksPanel(h));
        _root.Children.Add(row2);

        _root.Children.Add(T(L.Tr("「花费」按 API 单价折算，订阅用户并不按此单独计费；第三方模型未收录时按 Sonnet 近似（标「估」）。时间按本地日历分桶。",
            "“Cost” is estimated at API rates; subscription users aren't billed this way. Uncatalogued third-party models fall back to Sonnet (marked “est”). Times are bucketed by local calendar."), 11, Faint, top: 6));
    }

    // ── 顶栏 ──
    UIElement Header()
    {
        var dock = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        var title = new TextBlock { Text = L.Tr("数据统计", "Analytics"), FontSize = 22, FontWeight = FontWeights.Bold, Foreground = Theme.Brush(Fg), FontFamily = Theme.Font, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(title, Dock.Left); dock.Children.Add(title);

        var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        controls.Children.Add(MetricCombo());
        controls.Children.Add(RangeCombo());
        controls.Children.Add(Btn(L.Tr("重新扫描", "Rescan"), () => _store.Refresh()));
        controls.Children.Add(Btn(L.Tr("导出 CSV", "Export CSV"), () => Export(false)));
        controls.Children.Add(Btn(L.Tr("导出 JSON", "Export JSON"), () => Export(true)));
        dock.Children.Add(controls);
        return dock;
    }

    ComboBox MetricCombo()
    {
        var c = StyledCombo(130);
        foreach (var m in new[] { HeatmapMetric.Billable, HeatmapMetric.Cost, HeatmapMetric.Total })
            c.Items.Add(new ComboBoxItem { Content = m.Label(), Tag = m });
        c.SelectedIndex = (int)_metric;
        c.SelectionChanged += (_, _) => { if (c.SelectedItem is ComboBoxItem it && it.Tag is HeatmapMetric m) { _metric = m; _selectedDay = null; Build(); } };
        return c;
    }
    ComboBox RangeCombo()
    {
        var c = StyledCombo(96);
        foreach (var r in new[] { HistoryRange.M3, HistoryRange.M6, HistoryRange.M12, HistoryRange.All })
            c.Items.Add(new ComboBoxItem { Content = r.Label(), Tag = r });
        c.SelectedIndex = (int)_range;
        c.SelectionChanged += (_, _) => { if (c.SelectedItem is ComboBoxItem it && it.Tag is HistoryRange r) { _range = r; _selectedDay = null; Build(); } };
        return c;
    }
    static ComboBox StyledCombo(double w) => new() { MinWidth = w, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(6, 2, 6, 2) };

    // ── KPI ──
    Border Kpi(string title, DayStat s)
    {
        var sp = new StackPanel();
        sp.Children.Add(T(title, 12, Dim));
        sp.Children.Add(new TextBlock { Text = Money.Approx(s.Cost), FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = Theme.Brush(Fg), FontFamily = Theme.Font });
        sp.Children.Add(T($"{TranscriptParser.TokensShort(s.Tokens.Billable)} billable · " + L.Tr($"{s.MessageCount} 条", $"{s.MessageCount} msgs"), 11, Dim));
        return new Border { Background = Theme.Brush(Theme.CardBg), CornerRadius = new CornerRadius(10), Padding = new Thickness(14), Margin = new Thickness(0, 0, 8, 0), Child = sp };
    }

    // ── 热力图 ──
    UIElement BuildHeatmap(UsageHistory h)
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

        // 月份标签行
        var monthRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24, 0, 0, 2) };
        int lastMonth = -1;
        for (int w = 0; w < weekCount; w++)
        {
            var d = gridStart.AddDays(w * 7);
            string label = (d.Month != lastMonth) ? fmt.GetAbbreviatedMonthName(d.Month) : "";
            lastMonth = d.Month;
            monthRow.Children.Add(new TextBlock { Text = label, FontSize = 9, Foreground = Theme.Brush(Faint), Width = 13, FontFamily = Theme.FontText });
        }
        outer.Children.Add(monthRow);

        // 周标签列 + 单元格
        var body = new StackPanel { Orientation = Orientation.Horizontal };
        var weekdayCol = new StackPanel { Width = 24 };
        for (int r = 0; r < 7; r++)
            weekdayCol.Children.Add(new TextBlock { Text = (r % 2 == 1) ? fmt.AbbreviatedDayNames[r] : "", FontSize = 9, Foreground = Theme.Brush(Faint), Height = 13, FontFamily = Theme.FontText });
        body.Children.Add(weekdayCol);

        var cellsScroller = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var weeks = new StackPanel { Orientation = Orientation.Horizontal };
        for (int w = 0; w < weekCount; w++)
        {
            var col = new StackPanel { Margin = new Thickness(0, 0, 2, 0) };
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
                    Background = Theme.Brush(HeatColor(level, inRange)),
                    ToolTip = inRange ? $"{date:yyyy-MM-dd} · {CellTip(h, date)}" : null,
                    Cursor = inRange ? Cursors.Hand : Cursors.Arrow,
                };
                if (inRange && _selectedDay == dayKey)
                {
                    cell.BorderBrush = Theme.Brush(Theme.Text);
                    cell.BorderThickness = new Thickness(1.5);
                }
                if (inRange) cell.MouseLeftButtonUp += (_, _) => { _selectedDay = (_selectedDay == dayKey) ? null : dayKey; Build(); };
                col.Children.Add(cell);
            }
            weeks.Children.Add(col);
        }
        cellsScroller.Content = weeks;
        body.Children.Add(cellsScroller);
        outer.Children.Add(body);
        return outer;
    }

    UIElement Legend()
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24, 8, 0, 0) };
        sp.Children.Add(new TextBlock { Text = L.Tr("少", "Less"), FontSize = 10, Foreground = Theme.Brush(Faint), Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
        for (int l = 0; l < 5; l++)
            sp.Children.Add(new Border { Width = 11, Height = 11, CornerRadius = new CornerRadius(2.5), Background = Theme.Brush(HeatColor(l, true)), Margin = new Thickness(1, 0, 1, 0) });
        sp.Children.Add(new TextBlock { Text = L.Tr("多", "More"), FontSize = 10, Foreground = Theme.Brush(Faint), Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return sp;
    }

    string CellTip(UsageHistory h, DateTime date)
    {
        if (!h.Days.TryGetValue(DayKey.From(date), out var s)) return L.Tr("无活动", "No activity");
        return L.Tr($"计费 {TranscriptParser.TokensShort(s.Tokens.Billable)} · 合计 {TranscriptParser.TokensShort(s.Tokens.Total)}",
                    $"Billable {TranscriptParser.TokensShort(s.Tokens.Billable)} · Total {TranscriptParser.TokensShort(s.Tokens.Total)}")
            + " · " + Money.Approx(s.Cost) + " · " + L.Tr($"{s.MessageCount} 条", $"{s.MessageCount} msgs");
    }

    UIElement DayDetail(DateTime d, DayStat s)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = d.ToString("yyyy-MM-dd"), FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Theme.Brush(Fg) });
        sp.Children.Add(T(L.Tr($"计费 {TranscriptParser.TokensShort(s.Tokens.Billable)} · 合计 {TranscriptParser.TokensShort(s.Tokens.Total)}",
            $"Billable {TranscriptParser.TokensShort(s.Tokens.Billable)} · Total {TranscriptParser.TokensShort(s.Tokens.Total)}")
            + " · " + Money.Approx(s.Cost) + " · " + L.Tr($"{s.MessageCount} 条", $"{s.MessageCount} msgs"), 12, Dim));
        var models = string.Join(" / ", s.PerModel.Where(kv => !TranscriptParser.IsSyntheticModel(kv.Key)).OrderByDescending(kv => kv.Value.Billable).Take(4).Select(kv => TranscriptParser.ShortModelName(kv.Key)));
        if (models.Length > 0) sp.Children.Add(T(L.Tr("模型：", "Models: ") + models, 11, Faint));
        var projs = string.Join(" · ", s.PerProject.OrderByDescending(kv => kv.Value).Take(3).Select(kv => $"{kv.Key} {TranscriptParser.TokensShort(kv.Value)}"));
        if (projs.Length > 0) sp.Children.Add(T(L.Tr("项目：", "Projects: ") + projs, 11, Faint));
        return sp;
    }

    // ── 趋势柱状 ──
    UIElement BuildTrend(UsageHistory h)
    {
        var keys = h.DayKeysIn(_range);
        var pts = keys.Select(k => (date: DayKey.ToDate(k) ?? DateTime.Today, val: h.Days.TryGetValue(k, out var s) ? s.MetricValue(_metric) : 0)).ToList();
        double max = pts.Count > 0 ? Math.Max(1, pts.Max(p => p.val)) : 1;
        const double barW = 4, gap = 1, height = 140;
        var canvas = new StackPanel { Orientation = Orientation.Horizontal, Height = height, VerticalAlignment = VerticalAlignment.Bottom };
        foreach (var p in pts)
        {
            double bh = Math.Max(1, p.val / max * (height - 4));
            var bar = new Border
            {
                Width = barW, Height = bh, Margin = new Thickness(0, 0, gap, 0),
                CornerRadius = new CornerRadius(1.5, 1.5, 0, 0),
                Background = Theme.Brush(Green), VerticalAlignment = VerticalAlignment.Bottom,
                ToolTip = $"{p.date:yyyy-MM-dd} · " + (_metric == HeatmapMetric.Cost ? Money.Approx(p.val) : TranscriptParser.TokensShort((int)p.val)),
            };
            canvas.Children.Add(bar);
        }
        return new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Height = height + 8, Content = canvas };
    }

    // ── 时段打卡 7×24 ──
    UIElement BuildPunchCard(UsageHistory h)
    {
        var grid = new int[7, 24];
        foreach (var k in h.DayKeysIn(_range))
        {
            if (DayKey.ToDate(k) is not DateTime date || !h.Days.TryGetValue(k, out var s)) continue;
            int wd = (int)date.DayOfWeek;
            foreach (var (hr, v) in s.ByHour) if (hr >= 0 && hr < 24) grid[wd, hr] += v;
        }
        int max = 1;
        for (int r = 0; r < 7; r++) for (int c = 0; c < 24; c++) max = Math.Max(max, grid[r, c]);
        var fmt = CultureInfo.CurrentCulture.DateTimeFormat;

        var table = new Grid();
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        for (int c = 0; c < 24; c++) table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(15) });
        for (int r = 0; r < 7; r++) table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(15) });
        table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(14) });

        for (int r = 0; r < 7; r++)
        {
            var lbl = new TextBlock { Text = fmt.AbbreviatedDayNames[r], FontSize = 9, Foreground = Theme.Brush(Faint), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(lbl, r); Grid.SetColumn(lbl, 0); table.Children.Add(lbl);
            for (int c = 0; c < 24; c++)
            {
                int v = grid[r, c];
                if (v > 0)
                {
                    double size = 4 + 9.0 * Math.Sqrt((double)v / max);
                    var dot = new Border
                    {
                        Width = size, Height = size, CornerRadius = new CornerRadius(size / 2),
                        Background = Theme.Brush(Green), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                        ToolTip = $"{fmt.AbbreviatedDayNames[r]} {c}:00 · {TranscriptParser.TokensShort(v)}",
                    };
                    Grid.SetRow(dot, r); Grid.SetColumn(dot, c + 1); table.Children.Add(dot);
                }
            }
        }
        for (int c = 0; c < 24; c += 6)
        {
            var hr = new TextBlock { Text = L.Tr($"{c}时", $"{c}:00"), FontSize = 8, Foreground = Theme.Brush(Faint) };
            Grid.SetRow(hr, 7); Grid.SetColumn(hr, c + 1); table.Children.Add(hr);
        }
        return table;
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

    UIElement BarRow(string label, int value, int max, string trailing)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(132) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(66) });
        var lbl = new TextBlock { Text = label, FontSize = 12, Foreground = Theme.Brush(Fg), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(lbl, 0); g.Children.Add(lbl);
        var track = new Border { Height = 10, CornerRadius = new CornerRadius(5), Background = Theme.Brush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)), HorizontalAlignment = HorizontalAlignment.Stretch };
        var bar = new Border { Height = 10, CornerRadius = new CornerRadius(5), Background = Theme.Brush(Green), HorizontalAlignment = HorizontalAlignment.Left, Width = Math.Max(2, 190.0 * value / Math.Max(1, max)) };
        var bg = new Grid { VerticalAlignment = VerticalAlignment.Center }; bg.Children.Add(track); bg.Children.Add(bar);
        Grid.SetColumn(bg, 1); g.Children.Add(bg);
        var tr = new TextBlock { Text = trailing, FontSize = 12, Foreground = Theme.Brush(Dim), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(tr, 2); g.Children.Add(tr);
        return g;
    }

    UIElement InfoRow(string k, string v)
    {
        var g = new Grid { Margin = new Thickness(0, 3, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var kt = new TextBlock { Text = k, FontSize = 12, Foreground = Theme.Brush(Dim) };
        var vt = new TextBlock { Text = v, FontSize = 12, Foreground = Theme.Brush(Fg), FontWeight = FontWeights.SemiBold };
        Grid.SetColumn(kt, 0); Grid.SetColumn(vt, 1);
        g.Children.Add(kt); g.Children.Add(vt);
        return g;
    }

    TextBlock Hint() => T(L.Tr("暂无数据", "No data"), 12, Dim);

    // ── 卡片 / 文本工厂 ──
    Border Card(string title, Action<StackPanel> fill)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 13, Foreground = Theme.Brush(Fg), Margin = new Thickness(0, 0, 0, 8) });
        fill(sp);
        return new Border { Background = Theme.Brush(Theme.CardBg), CornerRadius = new CornerRadius(10), Padding = new Thickness(14), Margin = new Thickness(0, 0, 8, 10), Child = sp };
    }

    static TextBlock T(string text, double size, Color color, bool bold = false, double top = 0, double bottom = 0) => new()
    {
        Text = text, FontSize = size, Foreground = Theme.Brush(color), FontFamily = Theme.FontText,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, bottom),
    };

    UIElement Divider() => new Border { Height = 1, Background = Theme.Brush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(0, 10, 0, 8) };

    Button Btn(string text, Action act)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 4, 10, 4), Cursor = Cursors.Hand };
        b.Click += (_, _) => act();
        return b;
    }

    // ── 导出 ──
    void Export(bool json)
    {
        var dlg = new SaveFileDialog { FileName = json ? "claudenotch-usage.json" : "claudenotch-usage.csv", Filter = json ? "JSON|*.json" : "CSV|*.csv" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            if (json)
            {
                var arr = new List<Dictionary<string, object>>();
                foreach (var day in _store.History.Days.Keys.OrderBy(x => x))
                {
                    var s = _store.History.Days[day]; var d = DayKey.ToDate(day); if (d is null) continue;
                    var t = s.Tokens;
                    arr.Add(new() { ["date"] = d.Value.ToString("yyyy-MM-dd"), ["billable"] = t.Billable, ["total"] = t.Total, ["input"] = t.Input, ["output"] = t.Output, ["cache_read"] = t.CacheRead, ["cost_usd"] = Math.Round(s.Cost, 4), ["messages"] = s.MessageCount });
                }
                File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(arr, new JsonSerializerOptions { WriteIndented = true }));
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
                File.WriteAllText(dlg.FileName, sb.ToString());
            }
        }
        catch { }
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
        if (!inRange) return Colors.Transparent;
        if (level == 0) return Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF);
        double[] ops = { 0.30, 0.52, 0.76, 1.0 };
        return Color.FromArgb((byte)(ops[level - 1] * 255), 0x2E, 0xC7, 0x71);
    }
}
