using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using ClaudeNotch.Core;

namespace ClaudeNotch.UI;

/// <summary>置顶可拖拽的悬浮球（替代 macOS 刘海）：圆形球显示订阅剩余容量 ↔ 点击展开现代面板。</summary>
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
        FontFamily = Theme.FontText;
        Title = "ClaudeNotch";

        _content = new StackPanel();
        _root = new Border
        {
            Child = _content,
            Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 0, Opacity = 0.5, Color = Colors.Black },
        };
        Content = new Border { Padding = new Thickness(16), Child = _root };   // padding 给阴影留空间

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
        menu.Items.Add(Item(L.Tr("数据统计…", "Analytics…"), () => OpenAnalytics?.Invoke()));
        menu.Items.Add(Item(L.Tr("设置…", "Settings…"), () => OpenSettings?.Invoke()));
        menu.Items.Add(Item(L.Tr("立即刷新", "Refresh Now"), () => RefreshAll?.Invoke()));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(L.Tr("退出", "Quit"), () => Quit?.Invoke()));
        return menu;
    }

    // ── 拖拽 vs 点击 ──
    void OnDown(object s, MouseButtonEventArgs e) { _down = true; _dragged = false; _downPos = e.GetPosition(this); }
    void OnMove(object s, MouseEventArgs e)
    {
        if (_down && e.LeftButton == MouseButtonState.Pressed && (e.GetPosition(this) - _downPos).Length > 4)
        {
            _dragged = true; _down = false;
            try { DragMove(); } catch { }
            SavePosition();
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
        Rebuild();
    }

    void RestorePosition()
    {
        var area = SystemParameters.WorkArea;
        if (_settings.WidgetX is double x && _settings.WidgetY is double y)
        {
            Left = Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - ActualWidth));
            Top = Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - ActualHeight));
        }
        else { Left = area.Right - ActualWidth - 24; Top = area.Top + 24; }
    }

    void SavePosition() { _settings.WidgetX = Left; _settings.WidgetY = Top; _settings.Save(); }

    // ── 内容构建 ──
    void Rebuild()
    {
        _content.Children.Clear();
        if (_expanded) BuildExpanded(); else BuildOrb();
    }

    static TextBlock Tb(string text, double size, Color color, bool bold = false) => new()
    {
        Text = text,
        FontSize = size,
        FontFamily = Theme.FontText,
        Foreground = Theme.Brush(color),
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        VerticalAlignment = VerticalAlignment.Center,
    };

    // 折叠：圆形“球”——环弧=剩余容量，中心大数字=剩余%
    void BuildOrb()
    {
        _content.Width = double.NaN;
        _root.CornerRadius = new CornerRadius(36);
        _root.Background = Theme.Brush(Theme.PillBg);
        _root.Padding = new Thickness(0);
        _root.Width = 72; _root.Height = 72;

        var head = _usage.Snapshot?.Headline;
        var grid = new Grid { Width = 72, Height = 72 };
        if (head is not null)
        {
            int remain = head.PercentRemaining;
            grid.Children.Add(new RingControl
            {
                Percent = remain,                              // 弧长 = 剩余容量
                Thickness = 6,
                RingColor = Theme.ForPercent(head.PercentUsed), // 余量越少越红
                Margin = new Thickness(6),
            });
            var center = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            center.Children.Add(new TextBlock { Text = remain.ToString(), FontSize = 21, FontWeight = FontWeights.SemiBold, Foreground = Theme.Brush(Theme.Text), HorizontalAlignment = HorizontalAlignment.Center, FontFamily = Theme.Font });
            center.Children.Add(new TextBlock { Text = L.Tr("剩余", "left"), FontSize = 9, Foreground = Theme.Brush(Theme.TextDim), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -2, 0, 0) });
            grid.Children.Add(center);
        }
        else
        {
            grid.Children.Add(new TextBlock
            {
                Text = _usage.State == UsageState.Waiting ? "…" : "·",
                FontSize = 22, Foreground = Theme.Brush(Theme.TextDim),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            });
            grid.ToolTip = WaitingText();
        }
        _content.Children.Add(grid);
    }

    // 展开：现代面板——剩余容量环组 + 活跃会话
    void BuildExpanded()
    {
        _root.CornerRadius = new CornerRadius(16);
        _root.Background = Theme.Brush(Theme.PanelBg);
        _root.Width = double.NaN; _root.Height = double.NaN;
        _root.Padding = new Thickness(16, 14, 16, 14);
        _content.Width = 312;

        // 标题行
        var headerRow = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 10) };
        var titleTb = Tb("ClaudeNotch", 13, Theme.Text, true);
        DockPanel.SetDock(titleTb, Dock.Left); headerRow.Children.Add(titleTb);
        var collapse = IconButton("⤡", () => ToggleExpand());
        DockPanel.SetDock(collapse, Dock.Right); headerRow.Children.Add(collapse);
        _content.Children.Add(headerRow);

        var snap = _usage.Snapshot;
        if (snap is not null && snap.AllMetrics.Count > 0)
        {
            var rings = new UniformGrid { Rows = 1, Columns = snap.AllMetrics.Count, Margin = new Thickness(0, 2, 0, 4) };
            foreach (var m in snap.AllMetrics)
            {
                var cell = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                cell.Children.Add(BigRing(m.PercentRemaining, Theme.ForPercent(m.PercentUsed), $"{m.PercentRemaining}%", L.Tr("剩余", "left")));
                cell.Children.Add(Center(Tb(ShortMetricLabel(m), 11, Theme.TextDim)));
                cell.Children.Add(Center(Tb(m.ResetDisplay, 10, Theme.TextFaint)));
                rings.Children.Add(cell);
            }
            _content.Children.Add(rings);

            if (snap.OfficialCostUSD is double oc)
                _content.Children.Add(Center(Tb(L.Tr("最近会话官方花费 ", "Latest session cost ") + Money.Format(oc), 11, Theme.TextDim)));
        }
        else _content.Children.Add(Center(Tb(WaitingText(), 12, Theme.TextDim)));

        _content.Children.Add(Divider());
        _content.Children.Add(Tb(L.Tr("活跃会话", "Active sessions"), 11, Theme.TextDim, true));
        var sessions = _sessions.Sessions;
        if (sessions.Count == 0)
            _content.Children.Add(Tb(L.Tr("无运行中的会话", "No running sessions"), 11, Theme.TextFaint));
        else
            foreach (var ses in sessions.Take(6))
                _content.Children.Add(SessionRow(ses));

        // 操作行
        _content.Children.Add(Divider());
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(PillButton(L.Tr("数据统计", "Analytics"), () => OpenAnalytics?.Invoke()));
        actions.Children.Add(PillButton(L.Tr("设置", "Settings"), () => OpenSettings?.Invoke()));
        actions.Children.Add(PillButton(L.Tr("刷新", "Refresh"), () => RefreshAll?.Invoke()));
        _content.Children.Add(actions);
    }

    UIElement SessionRow(SessionInfo s)
    {
        var grid = new Grid { Margin = new Thickness(0, 5, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var ring = SmallRing(s.ContextPercent, Theme.ContextColor(s.ContextPercent), $"{s.ContextPercent}");
        Grid.SetColumn(ring, 0); grid.Children.Add(ring);

        var mid = new StackPanel { Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        var titleText = s.GitBranch is null ? s.ProjectName : $"{s.ProjectName} · {s.GitBranch}";
        mid.Children.Add(Tb(titleText, 12, Theme.Text, true));
        mid.Children.Add(Tb($"{s.ModelShort} · {Money.Approx(s.CostUSD)}", 10, Theme.TextDim));
        Grid.SetColumn(mid, 1); grid.Children.Add(mid);

        var ctx = Tb(TranscriptParser.TokensShort(s.ContextTokens), 10, Theme.TextDim);
        Grid.SetColumn(ctx, 2); grid.Children.Add(ctx);
        return grid;
    }

    static FrameworkElement Center(FrameworkElement e) { e.HorizontalAlignment = HorizontalAlignment.Center; return e; }

    Grid BigRing(int arcPercent, Color color, string centerText, string sub) => RingCell(arcPercent, 58, 6, color, centerText, 17, sub);
    Grid SmallRing(int arcPercent, Color color, string centerText) => RingCell(arcPercent, 30, 4, color, centerText, 10, null);

    Grid RingCell(int arcPercent, double diameter, double thickness, Color color, string centerText, double centerSize, string? sub)
    {
        var g = new Grid { Width = diameter, Height = diameter, Margin = new Thickness(4, 0, 4, 2) };
        g.Children.Add(new RingControl { Percent = arcPercent, Thickness = thickness, RingColor = color });
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock { Text = centerText, FontSize = centerSize, FontWeight = FontWeights.SemiBold, Foreground = Theme.Brush(Theme.Text), HorizontalAlignment = HorizontalAlignment.Center, FontFamily = Theme.Font });
        if (sub is not null) stack.Children.Add(new TextBlock { Text = sub, FontSize = 8, Foreground = Theme.Brush(Theme.TextFaint), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -2, 0, 0) });
        g.Children.Add(stack);
        return g;
    }

    Border Divider() => new()
    {
        Height = 1,
        Background = Theme.Brush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)),
        Margin = new Thickness(0, 9, 0, 7),
    };

    Button IconButton(string glyph, Action act)
    {
        var b = new Button
        {
            Content = glyph, FontSize = 12, Foreground = Theme.Brush(Theme.TextDim),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 2, 6, 2), Cursor = Cursors.Hand,
        };
        b.Click += (_, _) => act();
        return b;
    }

    Button PillButton(string text, Action act)
    {
        var b = new Button
        {
            Content = text, FontSize = 11, Foreground = Theme.Brush(Theme.Text),
            Background = Theme.Brush(Theme.CardBg), BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 0), Cursor = Cursors.Hand,
        };
        b.Click += (_, _) => act();
        return b;
    }

    string WaitingText() => _usage.State == UsageState.Waiting
        ? L.Tr("等待数据 · 在终端跑一次 claude", "Waiting · run claude once in a terminal")
        : L.Tr("加载中…", "Loading…");

    static string ShortMetricLabel(UsageMetric m) => m.Id switch
    {
        "session" => L.Tr("会话", "Session"),
        "weeklyAll" => L.Tr("周·全部", "Wk·All"),
        "weeklySonnet" => "Wk·Sonnet",
        _ => m.Title,
    };
}
