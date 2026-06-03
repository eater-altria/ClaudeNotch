using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ClaudeNotch.Core;

namespace ClaudeNotch.UI;

/// <summary>置顶可拖拽的悬浮挂件（替代 macOS 刘海）：折叠药丸 ↔ Mac 风格展开面板。</summary>
public sealed class WidgetWindow : Window
{
    readonly UsageStore _usage;
    readonly SessionStore _sessions;
    readonly AppSettings _settings;
    readonly Border _root;
    readonly StackPanel _content;
    readonly DispatcherTimer _tick;

    bool _expanded;
    bool _down, _dragged;
    Point _downPos;

    public Action? OpenSettings, OpenAnalytics, RefreshAll, Quit;

    public WidgetWindow(UsageStore usage, SessionStore sessions, AppSettings settings)
    {
        _usage = usage; _sessions = sessions; _settings = settings;
        _expanded = settings.WidgetExpanded;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Title = "ClaudeNotch";

        _content = new StackPanel { Margin = new Thickness(10, 8, 10, 8) };
        _root = new Border
        {
            Background = Theme.Brush(_expanded ? Theme.PanelBg : Theme.PillBg),
            CornerRadius = new CornerRadius(_expanded ? 14 : 18),
            Child = _content,
        };
        Content = _root;

        ContextMenu = BuildContextMenu();

        Loaded += (_, _) => RestorePosition();
        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;

        _usage.Changed += OnStoreChanged;
        _sessions.Changed += OnStoreChanged;
        L.Changed += OnStoreChanged;

        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _tick.Tick += (_, _) => Rebuild();
        _tick.Start();

        Rebuild();
    }

    void OnStoreChanged() => Dispatcher.BeginInvoke(Rebuild);

    ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        MenuItem Item(string text, Action? act)
        {
            var mi = new MenuItem { Header = text };
            mi.Click += (_, _) => act?.Invoke();
            return mi;
        }
        menu.Items.Add(Item(L.Tr("设置…", "Settings…"), () => OpenSettings?.Invoke()));
        menu.Items.Add(Item(L.Tr("数据统计…", "Analytics…"), () => OpenAnalytics?.Invoke()));
        menu.Items.Add(Item(L.Tr("立即刷新", "Refresh Now"), () => RefreshAll?.Invoke()));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(L.Tr("退出", "Quit"), () => Quit?.Invoke()));
        return menu;
    }

    // ── 拖拽 vs 点击 ──
    void OnDown(object s, MouseButtonEventArgs e) { _down = true; _dragged = false; _downPos = e.GetPosition(this); }
    void OnMove(object s, MouseEventArgs e)
    {
        if (_down && e.LeftButton == MouseButtonState.Pressed)
        {
            var p = e.GetPosition(this);
            if ((p - _downPos).Length > 4)
            {
                _dragged = true; _down = false;
                try { DragMove(); } catch { }
                SavePosition();
            }
        }
    }
    void OnUp(object s, MouseButtonEventArgs e)
    {
        if (_down && !_dragged) ToggleExpand();
        _down = false; _dragged = false;
    }

    void ToggleExpand()
    {
        _expanded = !_expanded;
        _settings.WidgetExpanded = _expanded;
        _settings.Save();
        _root.CornerRadius = new CornerRadius(_expanded ? 14 : 18);
        _root.Background = Theme.Brush(_expanded ? Theme.PanelBg : Theme.PillBg);
        Rebuild();
    }

    void RestorePosition()
    {
        var area = SystemParameters.WorkArea;
        if (_settings.WidgetX is double x && _settings.WidgetY is double y)
        {
            Left = Math.Clamp(x, area.Left, area.Right - ActualWidth);
            Top = Math.Clamp(y, area.Top, area.Bottom - ActualHeight);
        }
        else
        {
            Left = area.Right - ActualWidth - 16;
            Top = area.Top + 12;
        }
    }

    void SavePosition() { _settings.WidgetX = Left; _settings.WidgetY = Top; _settings.Save(); }

    // ── 内容构建 ──
    void Rebuild()
    {
        _content.Children.Clear();
        if (_expanded) BuildExpanded(); else BuildCollapsed();
    }

    static TextBlock Tb(string text, double size, Color color, bool bold = false) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = Theme.Brush(color),
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        VerticalAlignment = VerticalAlignment.Center,
    };

    void BuildCollapsed()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var snap = _usage.Snapshot;
        var head = snap?.Headline;
        if (head is not null)
        {
            row.Children.Add(MakeRing(head.PercentUsed, 26, 4, Theme.ForPercent(head.PercentUsed), $"{head.PercentUsed}"));
            var col = new StackPanel { Margin = new Thickness(8, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center };
            col.Children.Add(Tb(ShortMetricLabel(head), 11, Theme.TextDim));
            col.Children.Add(Tb($"{head.PercentUsed}% · {head.ResetDisplay}", 12, Theme.Text, true));
            row.Children.Add(col);
        }
        else
        {
            row.Children.Add(Tb(WaitingText(), 12, Theme.TextDim));
        }
        _content.Children.Add(row);
    }

    void BuildExpanded()
    {
        _content.Width = 300;

        // 额度环组
        var snap = _usage.Snapshot;
        if (snap is not null && snap.AllMetrics.Count > 0)
        {
            var rings = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 8) };
            foreach (var m in snap.AllMetrics)
            {
                var cell = new StackPanel { Margin = new Thickness(8, 0, 8, 0), HorizontalAlignment = HorizontalAlignment.Center };
                cell.Children.Add(MakeRing(m.PercentUsed, 56, 6, Theme.ForPercent(m.PercentUsed), $"{m.PercentUsed}%"));
                cell.Children.Add(Tb(ShortMetricLabel(m), 10, Theme.TextDim) is var t ? Center(t) : t);
                cell.Children.Add(Center(Tb(m.ResetDisplay, 10, Theme.TextDim)));
                rings.Children.Add(cell);
            }
            _content.Children.Add(rings);

            if (snap.OfficialCostUSD is double oc)
                _content.Children.Add(Tb(L.Tr("最近会话官方花费 ", "Latest session cost ") + Money.Format(oc), 11, Theme.TextDim));
        }
        else
        {
            _content.Children.Add(Tb(WaitingText(), 12, Theme.TextDim));
        }

        // 活跃会话列表
        var sessions = _sessions.Sessions;
        _content.Children.Add(Divider());
        _content.Children.Add(Tb(L.Tr("活跃会话", "Active sessions"), 11, Theme.TextDim, true));
        if (sessions.Count == 0)
            _content.Children.Add(Tb(L.Tr("无运行中的会话", "No running sessions"), 11, Theme.TextDim));
        else
            foreach (var s in sessions.Take(6))
                _content.Children.Add(SessionRow(s));
    }

    UIElement SessionRow(SessionInfo s)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var ring = MakeRing(s.ContextPercent, 30, 4, Theme.ContextColor(s.ContextPercent), $"{s.ContextPercent}");
        Grid.SetColumn(ring, 0);
        grid.Children.Add(ring);

        var mid = new StackPanel { Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        var title = s.GitBranch is null ? s.ProjectName : $"{s.ProjectName} · {s.GitBranch}";
        mid.Children.Add(Tb(title, 12, Theme.Text, true));
        mid.Children.Add(Tb($"{s.ModelShort} · {Money.Approx(s.CostUSD)}", 10, Theme.TextDim));
        Grid.SetColumn(mid, 1);
        grid.Children.Add(mid);

        var ctx = Tb($"{TranscriptParser.TokensShort(s.ContextTokens)}", 10, Theme.TextDim);
        Grid.SetColumn(ctx, 2);
        grid.Children.Add(ctx);
        return grid;
    }

    static FrameworkElement Center(FrameworkElement e) { e.HorizontalAlignment = HorizontalAlignment.Center; return e; }

    Grid MakeRing(int percent, double diameter, double thickness, Color color, string centerText)
    {
        var g = new Grid { Width = diameter, Height = diameter };
        g.Children.Add(new RingControl { Percent = percent, Thickness = thickness, RingColor = color });
        g.Children.Add(new TextBlock
        {
            Text = centerText,
            FontSize = diameter * 0.30,
            Foreground = Theme.Brush(Theme.Text),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold,
        });
        return g;
    }

    static Border Divider() => new()
    {
        Height = 1,
        Background = Theme.Brush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
        Margin = new Thickness(0, 8, 0, 6),
    };

    string WaitingText() => _usage.State == UsageState.Waiting
        ? L.Tr("等待数据 · 跑一次 claude", "Waiting · run claude once")
        : L.Tr("加载中…", "Loading…");

    static string ShortMetricLabel(UsageMetric m) => m.Id switch
    {
        "session" => L.Tr("会话", "Session"),
        "weeklyAll" => L.Tr("周·全部", "Wk·All"),
        "weeklySonnet" => "Wk·Sonnet",
        _ => m.Title,
    };
}
