using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ClaudeNotch.Core;

namespace ClaudeNotch.UI;

/// <summary>
/// 置顶悬浮挂件:折叠态=真·圆球(透明窗 + 椭圆,无方块/无边框) ↔ 展开面板(剩余环组 + 活跃会话 + 操作)。
/// 透明 + SizeToContent → 窗口恰好贴合内容,折叠时只露出圆,展开时面板永不裁切。拖拽=手动跟随光标。
/// </summary>
public sealed class WidgetWindow : Window
{
    readonly UsageStore _usage;
    readonly SessionStore _sessions;
    readonly AppSettings _settings;
    readonly DispatcherTimer _tick;

    bool _expanded;
    bool _pressed, _dragged;
    PixelPoint _pressScreen, _winStart;
    bool _restored;

    public Action? OpenSettings, OpenAnalytics, RefreshAll, Quit;

    const int OrbSize = 76;

    public WidgetWindow(UsageStore usage, SessionStore sessions, AppSettings settings)
    {
        _usage = usage; _sessions = sessions; _settings = settings;
        _expanded = settings.WidgetExpanded;

        Title = "ClaudeNotch";
        SystemDecorations = SystemDecorations.None;
        Background = Brushes.Transparent;
        // 纯透明:折叠态椭圆之外全透(真·圆球)。不要 ExtendClientArea* —— 它会带一层系统背板/Mica,
        // 在圆球四周显出半透明方块。
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;

        _usage.Changed += () => Dispatcher.UIThread.Post(Rebuild);
        _sessions.Changed += () => Dispatcher.UIThread.Post(Rebuild);
        L.Changed += () => Dispatcher.UIThread.Post(Rebuild);
        ActualThemeVariantChanged += (_, _) => Rebuild();

        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _tick.Tick += (_, _) => Rebuild();
        _tick.Start();

        Opened += (_, _) => RestorePosition();
        Rebuild();
    }

    // ── 内容 ──
    void Rebuild()
    {
        Control card = _expanded ? BuildPanel() : BuildOrb();
        card.PointerPressed += OnPointerPressed;
        card.PointerMoved += OnPointerMoved;
        card.PointerReleased += OnPointerReleased;
        card.ContextMenu = BuildMenu();
        Content = card;
    }

    // 折叠:真·圆球——半透明圆盘 + 环 + 中心剩余%。
    Control BuildOrb()
    {
        var grid = new Grid { Width = OrbSize, Height = OrbSize };
        grid.Children.Add(new Ellipse { Width = OrbSize, Height = OrbSize, Fill = Palette.Brush(Palette.OrbBg) });

        var head = _usage.Snapshot?.Headline;
        if (head is not null)
        {
            grid.Children.Add(new RingControl(OrbSize, 6, Palette.ForPercent(head.PercentUsed)) { Percent = head.PercentRemaining });
            var center = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            center.Children.Add(NumberTb(head.PercentRemaining.ToString(), 21));
            var sub = Tb(L.Tr("剩余", "left"), 9, Palette.TextDim);
            sub.HorizontalAlignment = HorizontalAlignment.Center; sub.Margin = new Thickness(0, -2, 0, 0);
            center.Children.Add(sub);
            grid.Children.Add(center);
        }
        else
        {
            var t = Tb(_usage.State == UsageState.Waiting ? "…" : "·", 22, Palette.TextDim);
            t.HorizontalAlignment = HorizontalAlignment.Center; t.VerticalAlignment = VerticalAlignment.Center;
            ToolTip.SetTip(grid, WaitingText());
            grid.Children.Add(t);
        }
        return grid;
    }

    // 展开:现代面板——剩余环组 + 活跃会话 + 操作。
    Control BuildPanel()
    {
        var content = new StackPanel { Width = 312 };

        var header = new Grid { Margin = new Thickness(0, 0, 0, 10), ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var title = Tb("ClaudeNotch", 13, Palette.Text, true);
        Grid.SetColumn(title, 0); header.Children.Add(title);
        var collapse = new Button
        {
            Content = "›",
            FontSize = 15,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 0, 8, 2),
            Foreground = Palette.Brush(Palette.TextDim),
            VerticalAlignment = VerticalAlignment.Center,
        };
        collapse.Click += (_, _) => ToggleExpand();
        Grid.SetColumn(collapse, 1); header.Children.Add(collapse);
        content.Children.Add(header);

        var snap = _usage.Snapshot;
        if (snap is not null && snap.AllMetrics.Count > 0)
        {
            var rings = new Grid { Margin = new Thickness(0, 2, 0, 4) };
            for (int i = 0; i < snap.AllMetrics.Count; i++)
                rings.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            int col = 0;
            foreach (var m in snap.AllMetrics)
            {
                var cell = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                cell.Children.Add(RingWithText(m.PercentRemaining, 58, 6, Palette.ForPercent(m.PercentUsed), $"{m.PercentRemaining}%", 17, L.Tr("剩余", "left")));
                cell.Children.Add(Center(Tb(ShortMetricLabel(m), 11, Palette.TextDim)));
                cell.Children.Add(Center(Tb(m.ResetDisplay, 10, Palette.TextFaint)));
                Grid.SetColumn(cell, col++); rings.Children.Add(cell);
            }
            content.Children.Add(rings);

            if (snap.OfficialCostUSD is double oc)
                content.Children.Add(Center(Tb(L.Tr("最近会话官方花费 ", "Latest session cost ") + Money.Format(oc), 11, Palette.TextDim)));
        }
        else content.Children.Add(Center(Tb(WaitingText(), 12, Palette.TextDim)));

        content.Children.Add(Divider());
        content.Children.Add(Tb(L.Tr("活跃会话", "Active sessions"), 11, Palette.TextDim, true));
        var sessions = _sessions.Sessions;
        if (sessions.Count == 0)
            content.Children.Add(Tb(L.Tr("无运行中的会话", "No running sessions"), 11, Palette.TextFaint));
        else
            foreach (var ses in sessions.Take(6))
                content.Children.Add(SessionRow(ses));

        content.Children.Add(Divider());
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        actions.Children.Add(Pill(L.Tr("数据统计", "Analytics"), () => OpenAnalytics?.Invoke()));
        actions.Children.Add(Pill(L.Tr("设置", "Settings"), () => OpenSettings?.Invoke()));
        actions.Children.Add(Pill(L.Tr("刷新", "Refresh"), () => RefreshAll?.Invoke()));
        content.Children.Add(actions);

        return new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = Palette.Brush(Palette.PanelBg),
            Padding = new Thickness(16, 14, 16, 14),
            Child = content,
        };
    }

    Control SessionRow(SessionInfo s)
    {
        var grid = new Grid { Margin = new Thickness(0, 5, 0, 0), ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

        var ring = RingWithText(s.ContextPercent, 30, 4, Palette.ContextColor(s.ContextPercent), $"{s.ContextPercent}", 10, null);
        Grid.SetColumn(ring, 0); grid.Children.Add(ring);

        var mid = new StackPanel { Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        var titleText = s.GitBranch is null ? s.ProjectName : $"{s.ProjectName} · {s.GitBranch}";
        mid.Children.Add(Tb(titleText, 12, Palette.Text, true));
        mid.Children.Add(Tb($"{s.ModelShort} · {Money.Approx(s.CostUSD)}", 10, Palette.TextDim));
        Grid.SetColumn(mid, 1); grid.Children.Add(mid);

        var ctx = Tb(TranscriptParser.TokensShort(s.ContextTokens), 10, Palette.TextDim);
        ctx.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(ctx, 2); grid.Children.Add(ctx);
        return grid;
    }

    Grid RingWithText(int arcPercent, double diameter, double thickness, Color color, string centerText, double centerSize, string? sub)
    {
        var g = new Grid { Width = diameter, Height = diameter, Margin = new Thickness(4, 0, 4, 2) };
        g.Children.Add(new RingControl(diameter, thickness, color) { Percent = arcPercent });
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(NumberTb(centerText, centerSize));
        if (sub is not null)
        {
            var t = Tb(sub, 8, Palette.TextFaint);
            t.HorizontalAlignment = HorizontalAlignment.Center; t.Margin = new Thickness(0, -2, 0, 0);
            stack.Children.Add(t);
        }
        g.Children.Add(stack);
        return g;
    }

    // ── 工厂 ──
    static TextBlock Tb(string text, double size, Color color, bool bold = false) => new()
    {
        Text = text,
        FontSize = size,
        FontFamily = new FontFamily(Palette.FontFamily),
        Foreground = Palette.Brush(color),
        FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
        VerticalAlignment = VerticalAlignment.Center,
    };

    static TextBlock NumberTb(string text, double size) => new()
    {
        Text = text,
        FontSize = size,
        FontFamily = new FontFamily(Palette.FontDisplay),
        FontWeight = FontWeight.SemiBold,
        Foreground = Palette.Brush(Palette.Text),
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    static Control Center(Control e) { e.HorizontalAlignment = HorizontalAlignment.Center; return e; }

    static Border Divider() => new() { Height = 1, Background = Palette.Brush(Palette.Divider), Margin = new Thickness(0, 9, 0, 7) };

    Button Pill(string text, Action act)
    {
        var b = new Button { Content = text, FontSize = 12, Padding = new Thickness(10, 5, 10, 5) };
        b.Click += (_, _) => act();
        return b;
    }

    ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        MenuItem Item(string text, Action? act) { var i = new MenuItem { Header = text }; i.Click += (_, _) => act?.Invoke(); return i; }
        menu.Items.Add(Item(L.Tr("数据统计…", "Analytics…"), () => OpenAnalytics?.Invoke()));
        menu.Items.Add(Item(L.Tr("设置…", "Settings…"), () => OpenSettings?.Invoke()));
        menu.Items.Add(Item(L.Tr("立即刷新", "Refresh Now"), () => RefreshAll?.Invoke()));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(L.Tr("退出", "Quit"), () => Quit?.Invoke()));
        return menu;
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

    // ── 拖拽 vs 点击(手动跟随光标) ──
    void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var pt = e.GetCurrentPoint(this);
        if (!pt.Properties.IsLeftButtonPressed) return;
        _pressed = true; _dragged = false;
        _pressScreen = this.PointToScreen(pt.Position);
        _winStart = Position;
        e.Pointer.Capture((IInputElement)sender!);
    }

    void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_pressed) return;
        var cur = this.PointToScreen(e.GetPosition(this));
        int dx = cur.X - _pressScreen.X, dy = cur.Y - _pressScreen.Y;
        if (!_dragged && Math.Abs(dx) + Math.Abs(dy) <= 4) return;
        _dragged = true;
        Position = new PixelPoint(_winStart.X + dx, _winStart.Y + dy);
    }

    void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        if (_pressed && !_dragged) ToggleExpand();
        else if (_dragged) SavePosition();
        _pressed = false; _dragged = false;
    }

    void ToggleExpand()
    {
        _expanded = !_expanded;
        _settings.WidgetExpanded = _expanded;
        _settings.Save();
        Rebuild();
    }

    // ── 位置 ──
    void RestorePosition()
    {
        if (_restored) return;
        _restored = true;
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        var wa = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
        int w = (int)(Bounds.Width <= 0 ? OrbSize : Bounds.Width);
        int h = (int)(Bounds.Height <= 0 ? OrbSize : Bounds.Height);
        int x, y;
        if (_settings.WidgetX is double sx && _settings.WidgetY is double sy)
        {
            x = (int)Math.Clamp(sx, wa.X, wa.X + wa.Width - w);
            y = (int)Math.Clamp(sy, wa.Y, wa.Y + wa.Height - h);
        }
        else { x = wa.X + wa.Width - w - 32; y = wa.Y + 32; }
        Position = new PixelPoint(x, y);
    }

    void SavePosition()
    {
        _settings.WidgetX = Position.X;
        _settings.WidgetY = Position.Y;
        _settings.Save();
    }
}
