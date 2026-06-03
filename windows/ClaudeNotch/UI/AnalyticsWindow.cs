using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClaudeNotch.Core;
using Microsoft.Win32;

namespace ClaudeNotch.UI;

/// <summary>数据统计：KPI + 每日用量热力图 + 按模型/项目 + 导出。</summary>
public sealed class AnalyticsWindow : Window
{
    readonly HistoryStore _store;
    readonly StackPanel _root;
    HeatmapMetric _metric = HeatmapMetric.Billable;
    HistoryRange _range = HistoryRange.M12;

    static readonly Color Fg = Color.FromRgb(0xEC, 0xEC, 0xEE);
    static readonly Color Dim = Color.FromArgb(0xBB, 0xFF, 0xFF, 0xFF);
    static readonly Color Green = Color.FromRgb(0x2E, 0xC7, 0x71);

    public AnalyticsWindow(HistoryStore store)
    {
        _store = store;
        Title = "ClaudeNotch";
        Width = 900; Height = 700;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x20));

        _root = new StackPanel { Margin = new Thickness(16) };
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _root };

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

        // 顶栏：标题 + 指标/范围 + 刷新 + 导出
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var title = new TextBlock { Text = L.Tr("数据统计", "Analytics"), FontSize = 18, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Fg) };
        DockPanel.SetDock(title, Dock.Left); header.Children.Add(title);

        var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        controls.Children.Add(MetricCombo());
        controls.Children.Add(RangeCombo());
        controls.Children.Add(Btn(L.Tr("重新扫描", "Rescan"), () => _store.Refresh()));
        controls.Children.Add(Btn("CSV", ExportCsv));
        header.Children.Add(controls);
        _root.Children.Add(header);

        if (_store.IsBuilding)
            _root.Children.Add(new TextBlock
            {
                Text = L.Tr($"正在扫描历史… {(int)((_store.Progress ?? 0) * 100)}%", $"Scanning history… {(int)((_store.Progress ?? 0) * 100)}%"),
                Foreground = new SolidColorBrush(Dim), Margin = new Thickness(0, 0, 0, 8),
            });

        // KPI
        var kpis = new UniformGrid { Rows = 1, Columns = 4, Margin = new Thickness(0, 0, 0, 14) };
        kpis.Children.Add(Kpi(L.Tr("今日", "Today"), h.Today()));
        kpis.Children.Add(Kpi(L.Tr("7 天", "7 days"), h.Recent(7)));
        kpis.Children.Add(Kpi(L.Tr("30 天", "30 days"), h.Recent(30)));
        kpis.Children.Add(Kpi(L.Tr("累计", "All time"), h.Lifetime));
        _root.Children.Add(kpis);

        // 热力图
        _root.Children.Add(GroupTitle(L.Tr($"每日用量 · {_metric.Label()}", $"Daily usage · {_metric.Label()}")));
        _root.Children.Add(BuildHeatmap(h));

        // 按模型 / 按项目
        var agg = h.Aggregate(h.DayKeysIn(_range));
        var cols = new UniformGrid { Rows = 1, Columns = 2, Margin = new Thickness(0, 14, 0, 0) };
        cols.Children.Add(ModelPanel(agg));
        cols.Children.Add(ProjectPanel(agg));
        _root.Children.Add(cols);

        _root.Children.Add(new TextBlock
        {
            Text = L.Tr("「花费」按 API 单价折算，订阅用户并不按此单独计费；第三方模型未收录时按 Sonnet 近似（标「估」）。",
                "“Cost” is estimated at API rates; subscription users aren't billed this way. Uncatalogued third-party models fall back to Sonnet (marked “est”)."),
            Foreground = new SolidColorBrush(Dim), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0),
        });
    }

    ComboBox MetricCombo()
    {
        var c = new ComboBox { Margin = new Thickness(0, 0, 8, 0), MinWidth = 130 };
        foreach (var m in new[] { HeatmapMetric.Billable, HeatmapMetric.Cost, HeatmapMetric.Total })
            c.Items.Add(new ComboBoxItem { Content = m.Label(), Tag = m });
        c.SelectedIndex = (int)_metric;
        c.SelectionChanged += (_, _) => { if (c.SelectedItem is ComboBoxItem it && it.Tag is HeatmapMetric m) { _metric = m; Build(); } };
        return c;
    }
    ComboBox RangeCombo()
    {
        var c = new ComboBox { Margin = new Thickness(0, 0, 8, 0), MinWidth = 90 };
        foreach (var r in new[] { HistoryRange.M3, HistoryRange.M6, HistoryRange.M12, HistoryRange.All })
            c.Items.Add(new ComboBoxItem { Content = r.Label(), Tag = r });
        c.SelectedIndex = (int)_range;
        c.SelectionChanged += (_, _) => { if (c.SelectedItem is ComboBoxItem it && it.Tag is HistoryRange r) { _range = r; Build(); } };
        return c;
    }

    Border Kpi(string title, DayStat s)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = title, FontSize = 12, Foreground = new SolidColorBrush(Dim) });
        sp.Children.Add(new TextBlock { Text = Money.Approx(s.Cost), FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Fg) });
        sp.Children.Add(new TextBlock
        {
            Text = $"{TranscriptParser.TokensShort(s.Tokens.Billable)} billable · " + L.Tr($"{s.MessageCount} 条", $"{s.MessageCount} msgs"),
            FontSize = 11, Foreground = new SolidColorBrush(Dim),
        });
        return new Border { Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)), CornerRadius = new CornerRadius(8), Padding = new Thickness(10), Margin = new Thickness(0, 0, 8, 0), Child = sp };
    }

    UIElement BuildHeatmap(UsageHistory h)
    {
        var keys = h.DayKeysIn(_range);
        var values = keys.Where(k => (h.Days.TryGetValue(k, out var s) ? s.MetricValue(_metric) : 0) > 0)
                         .Select(k => h.Days[k].MetricValue(_metric)).ToList();
        double p95 = Percentile(values, 0.95);

        var start = _range.StartDate(DateTime.Now) ?? (keys.Count > 0 ? DayKey.ToDate(keys[0]) ?? DateTime.Today : DateTime.Today);
        var today = DateTime.Today;
        var gridStart = StartOfWeek(start.Date);
        int dayCount = (today - gridStart).Days + 1;
        int weekCount = Math.Max(1, (int)Math.Ceiling(dayCount / 7.0));

        var weeks = new StackPanel { Orientation = Orientation.Horizontal };
        for (int w = 0; w < weekCount; w++)
        {
            var col = new StackPanel { Margin = new Thickness(0, 0, 2, 0) };
            for (int r = 0; r < 7; r++)
            {
                var date = gridStart.AddDays(w * 7 + r);
                bool inRange = date >= start.Date && date <= today;
                double v = 0;
                if (inRange && h.Days.TryGetValue(DayKey.From(date), out var st)) v = st.MetricValue(_metric);
                int level = Level(v, p95);
                col.Children.Add(new Border
                {
                    Width = 12, Height = 12, Margin = new Thickness(0, 0, 0, 2),
                    CornerRadius = new CornerRadius(2),
                    Background = new SolidColorBrush(HeatColor(level, inRange)),
                    ToolTip = inRange ? $"{date:yyyy-MM-dd} · {CellTip(h, date)}" : null,
                });
            }
            weeks.Children.Add(col);
        }
        return new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = weeks, Margin = new Thickness(0, 6, 0, 0) };
    }

    string CellTip(UsageHistory h, DateTime date)
    {
        if (!h.Days.TryGetValue(DayKey.From(date), out var s)) return L.Tr("无活动", "No activity");
        return L.Tr($"计费 {TranscriptParser.TokensShort(s.Tokens.Billable)}", $"Billable {TranscriptParser.TokensShort(s.Tokens.Billable)}")
            + " · " + Money.Approx(s.Cost) + " · " + L.Tr($"{s.MessageCount} 条", $"{s.MessageCount} msgs");
    }

    Border ModelPanel(DayStat agg)
    {
        var kept = agg.PerModel.Where(kv => !TranscriptParser.IsSyntheticModel(kv.Key)).ToList();
        int max = kept.Count > 0 ? kept.Max(kv => kv.Value.Billable) : 1;
        var items = kept.OrderByDescending(kv => kv.Value.Billable).Take(6);
        var sp = new StackPanel();
        foreach (var kv in items)
        {
            var label = TranscriptParser.ShortModelName(kv.Key) + (TranscriptParser.IsApproxPriced(kv.Key) ? L.Tr(" ·估", " ·est") : "");
            sp.Children.Add(BarRow(label, kv.Value.Billable, max, Money.Format(kv.Value.Cost(kv.Key))));
        }
        if (kept.Count == 0) sp.Children.Add(Hint());
        return Group(L.Tr("按模型", "By model"), sp);
    }

    Border ProjectPanel(DayStat agg)
    {
        int max = agg.PerProject.Count > 0 ? agg.PerProject.Values.Max() : 1;
        var items = agg.PerProject.OrderByDescending(kv => kv.Value).Take(6);
        var sp = new StackPanel();
        foreach (var kv in items)
            sp.Children.Add(BarRow(kv.Key, kv.Value, max, TranscriptParser.TokensShort(kv.Value)));
        if (agg.PerProject.Count == 0) sp.Children.Add(Hint());
        return Group(L.Tr("按项目 Top", "Top projects"), sp);
    }

    UIElement BarRow(string label, int value, int max, string trailing)
    {
        var g = new Grid { Margin = new Thickness(0, 3, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        var lbl = new TextBlock { Text = label, FontSize = 12, Foreground = new SolidColorBrush(Fg), TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(lbl, 0); g.Children.Add(lbl);
        var track = new Border { Height = 10, CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)) };
        var bar = new Border { Height = 10, CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(Green), HorizontalAlignment = HorizontalAlignment.Left, Width = Math.Max(2, 180.0 * value / Math.Max(1, max)) };
        var bg = new Grid(); bg.Children.Add(track); bg.Children.Add(bar);
        Grid.SetColumn(bg, 1); g.Children.Add(bg);
        var tr = new TextBlock { Text = trailing, FontSize = 12, Foreground = new SolidColorBrush(Dim), HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(tr, 2); g.Children.Add(tr);
        return g;
    }

    TextBlock Hint() => new() { Text = L.Tr("暂无数据", "No data"), FontSize = 12, Foreground = new SolidColorBrush(Dim) };

    Border Group(string title, UIElement body)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 13, Foreground = new SolidColorBrush(Fg), Margin = new Thickness(0, 0, 0, 6) });
        sp.Children.Add(body);
        return new Border { Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)), CornerRadius = new CornerRadius(8), Padding = new Thickness(12), Margin = new Thickness(0, 0, 8, 0), Child = sp };
    }

    TextBlock GroupTitle(string t) => new() { Text = t, FontWeight = FontWeights.SemiBold, FontSize = 13, Foreground = new SolidColorBrush(Fg), Margin = new Thickness(0, 4, 0, 0) };

    Button Btn(string text, Action act)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(8, 3, 8, 3) };
        b.Click += (_, _) => act();
        return b;
    }

    void ExportCsv()
    {
        var dlg = new SaveFileDialog { FileName = "claudenotch-usage.csv", Filter = "CSV|*.csv" };
        if (dlg.ShowDialog() != true) return;
        var sb = new StringBuilder("date,billable,total,input,output,cache_read,cost_usd,messages\n");
        foreach (var day in _store.History.Days.Keys.OrderBy(x => x))
        {
            var s = _store.History.Days[day];
            var d = DayKey.ToDate(day);
            if (d is null) continue;
            var t = s.Tokens;
            sb.Append($"{d:yyyy-MM-dd},{t.Billable},{t.Total},{t.Input},{t.Output},{t.CacheRead},{s.Cost.ToString("F4", CultureInfo.InvariantCulture)},{s.MessageCount}\n");
        }
        try { File.WriteAllText(dlg.FileName, sb.ToString()); } catch { }
    }

    // ── 热力图工具 ──
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
        if (level == 0) return Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF);
        double[] ops = { 0.30, 0.52, 0.76, 1.0 };
        byte a = (byte)(ops[level - 1] * 255);
        return Color.FromArgb(a, 0x2E, 0xC7, 0x71);
    }
}
