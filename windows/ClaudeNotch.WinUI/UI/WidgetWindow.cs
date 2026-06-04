using System.Runtime.InteropServices;
using ClaudeNotch.Core;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace ClaudeNotch.UI;

/// <summary>
/// 置顶可拖拽的悬浮挂件:折叠态为真·圆球(SetWindowRgn 把窗口裁成圆,环=订阅剩余,中心=剩余%)
/// ↔ 展开面板(剩余环组 + 活跃会话 + 操作)。拖拽用手动 AppWindow.Move(跟随光标)。
/// </summary>
public sealed class WidgetWindow : Window
{
    readonly UsageStore _usage;
    readonly SessionStore _sessions;
    readonly AppSettings _settings;
    readonly IntPtr _hwnd;
    readonly AppWindow _appWindow;
    readonly DispatcherTimer _tick;

    bool _expanded;
    bool _ptrDown, _dragged;
    POINT _cursorStart;
    PointInt32 _winStart;

    public Action? OpenSettings, OpenAnalytics, RefreshAll, Quit;

    public WidgetWindow(UsageStore usage, SessionStore sessions, AppSettings settings)
    {
        _usage = usage; _sessions = sessions; _settings = settings;
        _expanded = settings.WidgetExpanded;

        Title = "ClaudeNotch";
        SystemBackdrop = new DesktopAcrylicBackdrop();

        _hwnd = WindowNative.GetWindowHandle(this);
        var id = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(id);

        var p = OverlappedPresenter.Create();
        p.IsAlwaysOnTop = true;
        p.IsResizable = false;
        p.IsMaximizable = false;
        p.IsMinimizable = false;
        p.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
        _appWindow.SetPresenter(p);
        _appWindow.IsShownInSwitchers = false;
        RoundCorners();

        _usage.Changed += () => DispatcherQueue.TryEnqueue(Rebuild);
        _sessions.Changed += () => DispatcherQueue.TryEnqueue(Rebuild);
        L.Changed += () => DispatcherQueue.TryEnqueue(Rebuild);

        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _tick.Tick += (_, _) => Rebuild();
        _tick.Start();

        Rebuild();
        RestorePosition(id);
    }

    // ── 内容构建 ──
    void Rebuild()
    {
        FrameworkElement card = _expanded ? BuildPanel() : BuildOrb();
        card.PointerPressed += OnPointerPressed;
        card.PointerMoved += OnPointerMoved;
        card.PointerReleased += OnPointerReleased;
        card.ContextFlyout = BuildMenu();
        // 整个挂件强制深色:让默认按钮(收起/操作行)与深色面板一致,不随系统浅色发白。
        card.RequestedTheme = ElementTheme.Dark;
        // 两段式定尺寸:Loaded 时控件默认样式(按钮高度等主题资源)已应用,测量才准 ——
        // 否则首测低估按钮高度,展开面板底部按钮被裁(实测踩到)。订阅须在 Content 赋值前,避免错过。
        card.Loaded += (_, _) => FitToContent(card);
        Content = card;
        DispatcherQueue.TryEnqueue(() => FitToContent(card));
    }

    // 折叠:真·圆球(窗口被裁成圆形,见 ApplyShape)——圆盘底 + 环弧=剩余容量 + 中心大数字=剩余%。
    // 不再是方形圆角卡:返回纯 Grid(无方形 Border 背景/边框),球体由内嵌 Ellipse 充当。
    const int OrbSize = 76;
    Grid BuildOrb()
    {
        var grid = new Grid { Width = OrbSize, Height = OrbSize };
        // 球体本体:半透明深色圆盘(叠在 Acrylic 之上,保证中心数字可读)。
        grid.Children.Add(new Ellipse { Width = OrbSize, Height = OrbSize, Fill = Theme.Brush(Theme.Argb(0xE6, 0x22, 0x22, 0x26)) });
        var head = _usage.Snapshot?.Headline;
        if (head is not null)
        {
            int remain = head.PercentRemaining;
            grid.Children.Add(new RingControl(OrbSize, 6, Theme.ForPercent(head.PercentUsed)) { Percent = remain });
            var center = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            center.Children.Add(NumberTb(remain.ToString(), 21));
            var sub = Tb(L.Tr("剩余", "left"), 9, Theme.TextDim);
            sub.HorizontalAlignment = HorizontalAlignment.Center; sub.Margin = new Thickness(0, -2, 0, 0);
            center.Children.Add(sub);
            grid.Children.Add(center);
        }
        else
        {
            var t = Tb(_usage.State == UsageState.Waiting ? "…" : "·", 22, Theme.TextDim);
            t.HorizontalAlignment = HorizontalAlignment.Center; t.VerticalAlignment = VerticalAlignment.Center;
            ToolTipService.SetToolTip(grid, WaitingText());
            grid.Children.Add(t);
        }
        return grid;
    }

    // 展开:现代面板——剩余环组 + 活跃会话 + 操作
    Border BuildPanel()
    {
        var content = new StackPanel { Width = 312 };

        // 标题行
        var header = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = Tb("ClaudeNotch", 13, Theme.Text, true);
        Grid.SetColumn(title, 0); header.Children.Add(title);
        var collapse = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 12 },
            Background = Theme.Brush(Theme.Argb(0, 0, 0, 0)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 2, 6, 2),
        };
        collapse.Click += (_, _) => ToggleExpand();
        Grid.SetColumn(collapse, 1); header.Children.Add(collapse);
        content.Children.Add(header);

        var snap = _usage.Snapshot;
        if (snap is not null && snap.AllMetrics.Count > 0)
        {
            var rings = new Grid { Margin = new Thickness(0, 2, 0, 4) };
            for (int i = 0; i < snap.AllMetrics.Count; i++)
                rings.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            int col = 0;
            foreach (var m in snap.AllMetrics)
            {
                var cell = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                cell.Children.Add(RingWithText(m.PercentRemaining, 58, 6, Theme.ForPercent(m.PercentUsed), $"{m.PercentRemaining}%", 17, L.Tr("剩余", "left")));
                cell.Children.Add(Center(Tb(ShortMetricLabel(m), 11, Theme.TextDim)));
                cell.Children.Add(Center(Tb(m.ResetDisplay, 10, Theme.TextFaint)));
                Grid.SetColumn(cell, col++); rings.Children.Add(cell);
            }
            content.Children.Add(rings);

            if (snap.OfficialCostUSD is double oc)
                content.Children.Add(Center(Tb(L.Tr("最近会话官方花费 ", "Latest session cost ") + Money.Format(oc), 11, Theme.TextDim)));
        }
        else content.Children.Add(Center(Tb(WaitingText(), 12, Theme.TextDim)));

        content.Children.Add(Divider());
        content.Children.Add(Tb(L.Tr("活跃会话", "Active sessions"), 11, Theme.TextDim, true));
        var sessions = _sessions.Sessions;
        if (sessions.Count == 0)
            content.Children.Add(Tb(L.Tr("无运行中的会话", "No running sessions"), 11, Theme.TextFaint));
        else
            foreach (var ses in sessions.Take(6))
                content.Children.Add(SessionRow(ses));

        content.Children.Add(Divider());
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(Pill(L.Tr("数据统计", "Analytics"), () => OpenAnalytics?.Invoke()));
        actions.Children.Add(Pill(L.Tr("设置", "Settings"), () => OpenSettings?.Invoke()));
        actions.Children.Add(Pill(L.Tr("刷新", "Refresh"), () => RefreshAll?.Invoke()));
        content.Children.Add(actions);

        return new Border { CornerRadius = new CornerRadius(12), Background = Theme.Brush(Theme.PanelBg), Padding = new Thickness(16, 14, 16, 14), Child = content };
    }

    UIElement SessionRow(SessionInfo s)
    {
        var grid = new Grid { Margin = new Thickness(0, 5, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var ring = RingWithText(s.ContextPercent, 30, 4, Theme.ContextColor(s.ContextPercent), $"{s.ContextPercent}", 10, null);
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

    Grid RingWithText(int arcPercent, double diameter, double thickness, Color color, string centerText, double centerSize, string? sub)
    {
        var g = new Grid { Width = diameter, Height = diameter, Margin = new Thickness(4, 0, 4, 2) };
        g.Children.Add(new RingControl(diameter, thickness, color) { Percent = arcPercent });
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(NumberTb(centerText, centerSize));
        if (sub is not null)
        {
            var t = new TextBlock { Text = sub, FontSize = 8, Foreground = Theme.Brush(Theme.TextFaint), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -2, 0, 0) };
            stack.Children.Add(t);
        }
        g.Children.Add(stack);
        return g;
    }

    // ── 工厂 ──
    TextBlock Tb(string text, double size, Color color, bool bold = false) => new()
    {
        Text = text,
        FontSize = size,
        FontFamily = Theme.FontText,
        Foreground = Theme.Brush(color),
        FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        VerticalAlignment = VerticalAlignment.Center,
    };

    TextBlock NumberTb(string text, double size) => new()
    {
        Text = text,
        FontSize = size,
        FontFamily = Theme.Font,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Foreground = Theme.Brush(Theme.Text),
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    static FrameworkElement Center(FrameworkElement e) { e.HorizontalAlignment = HorizontalAlignment.Center; return e; }

    Border Divider() => new() { Height = 1, Background = Theme.Brush(Theme.Divider), Margin = new Thickness(0, 9, 0, 7) };

    Button Pill(string text, Action act)
    {
        var b = new Button { Content = text, FontSize = 12, Margin = new Thickness(0, 0, 6, 0) };
        b.Click += (_, _) => act();
        return b;
    }

    MenuFlyout BuildMenu()
    {
        var menu = new MenuFlyout();
        MenuFlyoutItem Item(string text, Action? act)
        {
            var mi = new MenuFlyoutItem { Text = text };
            mi.Click += (_, _) => act?.Invoke();
            return mi;
        }
        menu.Items.Add(Item(L.Tr("数据统计…", "Analytics…"), () => OpenAnalytics?.Invoke()));
        menu.Items.Add(Item(L.Tr("设置…", "Settings…"), () => OpenSettings?.Invoke()));
        menu.Items.Add(Item(L.Tr("立即刷新", "Refresh Now"), () => RefreshAll?.Invoke()));
        menu.Items.Add(new MenuFlyoutSeparator());
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

    // ── 拖拽 vs 点击 ──
    // 手动移动:按下记录光标(屏幕物理坐标)与窗口起点;移动时按光标增量实时 AppWindow.Move,
    // 全程跟随光标、松手即停。不再用 WM_NCLBUTTONDOWN(那条会进系统移动模式,出现“松手才动、再点才停”的错乱)。
    void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pp = e.GetCurrentPoint((UIElement)sender);
        if (!pp.Properties.IsLeftButtonPressed) return;
        _ptrDown = true; _dragged = false;
        GetCursorPos(out _cursorStart);
        _winStart = _appWindow.Position;
        ((UIElement)sender).CapturePointer(e.Pointer);
    }

    void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_ptrDown) return;
        GetCursorPos(out var cur);
        int dx = cur.X - _cursorStart.X, dy = cur.Y - _cursorStart.Y;
        if (!_dragged && Math.Abs(dx) + Math.Abs(dy) <= 4) return;  // 小抖动不算拖拽,留给点击
        _dragged = true;
        _appWindow.Move(new PointInt32(_winStart.X + dx, _winStart.Y + dy));
    }

    void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        if (_ptrDown && !_dragged) ToggleExpand();
        else if (_dragged) SavePosition();
        _ptrDown = false; _dragged = false;
    }

    void ToggleExpand()
    {
        _expanded = !_expanded;
        _settings.WidgetExpanded = _expanded;
        _settings.Save();
        Rebuild();
    }

    // ── 尺寸 / 位置 ──
    void FitToContent(FrameworkElement root)
    {
        try
        {
            root.UpdateLayout();
            root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var d = root.DesiredSize;
            // ResizeClient 收物理像素,DesiredSize 是 DIP → 必须按窗口 DPI 缩放。
            // 原先用 XamlRoot.RasterizationScale,首帧 XamlRoot 可能为 null 退化成 1.0,
            // 高 DPI(125%/150%)下窗口被算小。改用 GetDpiForWindow,稳定可靠。
            // 末尾 +2px 缓冲,吸收取整/子像素误差,避免边缘 1~2px 裁切。
            uint dpi = GetDpiForWindow(_hwnd);
            double scale = dpi > 0 ? dpi / 96.0 : (root.XamlRoot?.RasterizationScale ?? 1.0);
            if (d.Width > 0 && d.Height > 0)
            {
                // 展开面板留 +3 缓冲防裁切;折叠球不留缓冲,让圆形裁剪正好贴合球体。
                int pad = _expanded ? 3 : 0;
                int pw = (int)Math.Ceiling(d.Width * scale) + pad;
                int ph = (int)Math.Ceiling(d.Height * scale) + pad;
                _appWindow.ResizeClient(new SizeInt32(pw, ph));
                ApplyShape(pw, ph);
            }
        }
        catch { }
    }

    // 折叠态把窗口裁成圆形(真·圆球,无方角);展开态去掉区域,交回 DWM 圆角矩形。
    void ApplyShape(int w, int h)
    {
        try
        {
            if (_expanded) { SetWindowRgn(_hwnd, IntPtr.Zero, true); return; }
            var rgn = CreateEllipticRgn(0, 0, w + 1, h + 1);
            SetWindowRgn(_hwnd, rgn, true);   // 系统接管该区域句柄,勿再 DeleteObject
        }
        catch { }
    }

    void RestorePosition(WindowId id)
    {
        var work = DisplayArea.GetFromWindowId(id, DisplayAreaFallback.Primary).WorkArea;
        var size = _appWindow.Size;
        int x, y;
        if (_settings.WidgetX is double sx && _settings.WidgetY is double sy)
        {
            x = (int)Math.Clamp(sx, work.X, work.X + work.Width - size.Width);
            y = (int)Math.Clamp(sy, work.Y, work.Y + work.Height - size.Height);
        }
        else { x = work.X + work.Width - size.Width - 24; y = work.Y + 24; }
        _appWindow.Move(new PointInt32(x, y));
    }

    void SavePosition()
    {
        var pos = _appWindow.Position;
        _settings.WidgetX = pos.X;
        _settings.WidgetY = pos.Y;
        _settings.Save();
    }

    void RoundCorners()
    {
        // 圆角(DWMWA_WINDOW_CORNER_PREFERENCE=33, DWMWCP_ROUND=2)
        try { int round = 2; DwmSetWindowAttribute(_hwnd, 33, ref round, sizeof(int)); } catch { }
        // 去掉 Win11 默认的窗口白边(DWMWA_BORDER_COLOR=34, DWMWA_COLOR_NONE=0xFFFFFFFE)
        try { int none = unchecked((int)0xFFFFFFFE); DwmSetWindowAttribute(_hwnd, 34, ref none, sizeof(int)); } catch { }
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);
    [DllImport("gdi32.dll")] static extern IntPtr CreateEllipticRgn(int x1, int y1, int x2, int y2);

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
}
