using System.Diagnostics;
using ClaudeNotch.Core;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using WinRT.Interop;

namespace ClaudeNotch.UI;

/// <summary>设置窗(Win11 风):Mica + 自定义标题栏 + 卡片式分组(ToggleSwitch / ComboBox / Slider / InfoBar)。</summary>
public sealed class SettingsWindow : Window
{
    readonly AppSettings _settings;
    readonly ModelPriceStore _prices;
    readonly ExchangeRateStore _rates;
    readonly StackPanel _root;
    readonly AppWindow _appWindow;

    public Action? OnSettingsApplied;

    public SettingsWindow(AppSettings settings, ModelPriceStore prices, ExchangeRateStore rates)
    {
        _settings = settings; _prices = prices; _rates = rates;

        Title = "ClaudeNotch";
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;

        var hwnd = WindowNative.GetWindowHandle(this);
        var id = Win32Interop.GetWindowIdFromWindow(hwnd);
        _appWindow = AppWindow.GetFromWindowId(id);
        _appWindow.Resize(new SizeInt32(560, 780));
        CenterOnScreen(id);

        var titleBar = new Grid { Height = 40, Padding = new Thickness(16, 0, 16, 0), VerticalAlignment = VerticalAlignment.Top };
        titleBar.Children.Add(new TextBlock
        {
            Text = "ClaudeNotch",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Theme.Brush(Theme.TextDim),
        });
        SetTitleBar(titleBar);

        _root = new StackPanel { Margin = new Thickness(24, 48, 24, 24), Spacing = 0 };
        var scroller = new ScrollViewer { Content = _root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        var rootGrid = new Grid();
        rootGrid.Children.Add(scroller);
        rootGrid.Children.Add(titleBar);
        // 见 AnalyticsWindow:深色调色板 + Mica 强制深色,避免系统浅色下全白看不见。
        rootGrid.RequestedTheme = ElementTheme.Dark;
        Content = rootGrid;
        DarkCaptionButtons();

        L.Changed += () => DispatcherQueue.TryEnqueue(Build);
        _prices.Changed += () => DispatcherQueue.TryEnqueue(Build);
        _rates.Changed += () => DispatcherQueue.TryEnqueue(Build);
        Build();
    }

    void Build()
    {
        _root.Children.Clear();

        // 外观与语言
        Section(L.Tr("外观与语言", "Appearance & Language"), () =>
        {
            var combo = new ComboBox { MinWidth = 160 };
            foreach (var pf in new[] { LangPref.System, LangPref.Zh, LangPref.En })
                combo.Items.Add(new ComboBoxItem { Content = L.PrefLabel(pf), Tag = pf });
            combo.SelectedIndex = _settings.Lang switch { LangPref.Zh => 1, LangPref.En => 2, _ => 0 };
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedItem is ComboBoxItem it && it.Tag is LangPref pref)
                {
                    _settings.LanguagePreference = L.PrefRaw(pref);
                    _settings.Save();
                    L.Apply(pref);
                    OnSettingsApplied?.Invoke();
                }
            };
            _root.Children.Add(Card(Row(L.Tr("语言", "Language"), null, combo)));
        });

        // 通用
        Section(L.Tr("通用", "General"), () =>
        {
            _root.Children.Add(ToggleCard(L.Tr("启用悬浮挂件", "Enable floating widget"), null, _settings.WidgetEnabled,
                v => { _settings.WidgetEnabled = v; _settings.Save(); OnSettingsApplied?.Invoke(); }));
            _root.Children.Add(ToggleCard(L.Tr("开机自启动", "Launch at login"), null, _settings.LaunchAtLogin,
                v => { _settings.LaunchAtLogin = v; _settings.Save(); StartupRegistry.Apply(v); }));
            _root.Children.Add(ToggleCard(L.Tr("接管 Claude Code 的 statusLine", "Manage Claude Code's statusLine"),
                L.Tr("关闭后不再改写 ~/.claude/settings.json,额度停留在最后一次。",
                     "When off, ~/.claude/settings.json is left untouched; quota stays at the last value."),
                _settings.ManageStatusline,
                v => { _settings.ManageStatusline = v; _settings.Save(); OnSettingsApplied?.Invoke(); }));
        });

        // 通知
        Section(L.Tr("通知", "Notifications"), () =>
        {
            _root.Children.Add(ToggleCard(L.Tr("额度 / 上下文通知", "Quota / context alerts"), null, _settings.NotificationsEnabled,
                v => { _settings.NotificationsEnabled = v; _settings.Save(); OnSettingsApplied?.Invoke(); }));
            _root.Children.Add(SliderCard(L.Tr("提示档 %", "Warning %"), _settings.QuotaWarn, 50, 95,
                v => { _settings.QuotaWarn = v; _settings.Save(); OnSettingsApplied?.Invoke(); }));
            _root.Children.Add(SliderCard(L.Tr("严重档 %", "Critical %"), _settings.QuotaCritical, 55, 99,
                v => { _settings.QuotaCritical = v; _settings.Save(); OnSettingsApplied?.Invoke(); }));
            _root.Children.Add(SliderCard(L.Tr("上下文告警 %", "Context alert %"), _settings.ContextThreshold, 70, 99,
                v => { _settings.ContextThreshold = v; _settings.Save(); OnSettingsApplied?.Invoke(); }));
        });

        // 模型价格
        Section(L.Tr("模型价格", "Model pricing"), () =>
        {
            var status = L.Tr($"已载入 {_prices.ModelCount} 个模型单价", $"{_prices.ModelCount} model prices loaded")
                + (_prices.OverrideCount > 0 ? L.Tr($"（含 {_prices.OverrideCount} 条覆盖）", $" (incl. {_prices.OverrideCount} overrides)") : "");
            var sub = _prices.LastUpdated is DateTime d
                ? L.Tr("已联网更新于 ", "Updated online at ") + d.ToString("yyyy-MM-dd HH:mm")
                : L.Tr("来源：内置快照", "Source: bundled snapshot");
            _root.Children.Add(Card(Row(status, sub, null)));
            if (_prices.LastError is string e1) _root.Children.Add(Info(e1, InfoBarSeverity.Warning));
            var btns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            btns.Children.Add(Btn(_prices.IsRefreshing ? L.Tr("刷新中…", "Refreshing…") : L.Tr("刷新价格", "Refresh prices"), () => _ = _prices.RefreshAsync()));
            btns.Children.Add(Btn(L.Tr("编辑价格覆盖…", "Edit overrides…"), () => _prices.OpenOverridesForEditing()));
            _root.Children.Add(Card(btns));
        });

        // 货币与汇率
        Section(L.Tr("货币与汇率", "Currency & rate"), () =>
        {
            var line = L.Current == AppLang.Zh
                ? $"金额按人民币(¥)显示，1 USD = ¥{_rates.Rate:F4}"
                : "Amounts shown in US dollars ($)";
            var sub = _rates.LastUpdated is DateTime d
                ? L.Tr("汇率更新于 ", "Rate updated at ") + d.ToString("yyyy-MM-dd HH:mm")
                : L.Tr("汇率：内置默认值", "Rate: built-in default");
            _root.Children.Add(Card(Row(line, sub, null)));
            if (_rates.LastError is string e2) _root.Children.Add(Info(e2, InfoBarSeverity.Warning));
            _root.Children.Add(Card(Btn(_rates.IsRefreshing ? L.Tr("刷新中…", "Refreshing…") : L.Tr("刷新汇率", "Refresh rate"), () => _ = _rates.RefreshAsync())));
        });

        // 集成状态
        Section(L.Tr("集成状态", "Integration"), () =>
        {
            var diag = StatuslineHook.GetDiagnostics();
            _root.Children.Add(Info(
                diag.Installed ? L.Tr("已接入 Claude Code 的 statusLine ✓", "Connected to Claude Code's statusLine ✓")
                               : L.Tr("尚未接入 ✗", "Not connected ✗"),
                diag.Installed ? InfoBarSeverity.Success : InfoBarSeverity.Error));
            _root.Children.Add(Card(Row(L.Tr("额度数据", "Quota data"),
                diag.CapturedAt is DateTime c ? c.ToString("MM-dd HH:mm") : L.Tr("尚无（去跑一次 claude）", "none yet (run claude once)"), null)));
            var btns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            btns.Children.Add(Btn(L.Tr("重新接入", "Reinstall"), () => { StatuslineHook.EnsureInstalled(); Build(); }));
            btns.Children.Add(Btn(L.Tr("打开支持目录", "Open support folder"), () => { try { Process.Start(new ProcessStartInfo(Paths.SupportDir) { UseShellExecute = true }); } catch { } }));
            btns.Children.Add(Btn(L.Tr("复制诊断", "Copy diagnostics"), () => { var dp = new DataPackage(); dp.SetText(diag.CopyText()); Clipboard.SetContent(dp); }));
            _root.Children.Add(Card(btns));
            _root.Children.Add(new TextBlock
            {
                Text = L.Tr("额度来自 Claude Code 的 statusLine 钩子（不抓网页、不复用令牌），仅在 Claude Code 运行时更新。",
                            "Quota comes from Claude Code's statusLine hook (no scraping, no token reuse); updates only while Claude Code runs."),
                FontSize = 11, Foreground = Theme.Brush(Theme.TextFaint), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 6, 0, 0),
            });
        });
    }

    // ── 分组 / 卡片 工厂 ──
    void Section(string title, Action fill)
    {
        _root.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 14,
            Foreground = Theme.Brush(Theme.Text),
            Margin = new Thickness(2, 16, 0, 8),
        });
        fill();
    }

    static Border Card(UIElement content) => new()
    {
        Background = Theme.Brush(Theme.CardBg),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(16, 12, 16, 12),
        Margin = new Thickness(0, 0, 0, 4),
        Child = content,
    };

    Grid Row(string title, string? desc, FrameworkElement? control)
    {
        var g = new Grid { VerticalAlignment = VerticalAlignment.Center };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontSize = 14, Foreground = Theme.Brush(Theme.Text), TextWrapping = TextWrapping.Wrap });
        if (desc is not null)
            text.Children.Add(new TextBlock { Text = desc, FontSize = 12, Foreground = Theme.Brush(Theme.TextDim), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        Grid.SetColumn(text, 0); g.Children.Add(text);

        if (control is not null)
        {
            control.VerticalAlignment = VerticalAlignment.Center;
            control.Margin = new Thickness(12, 0, 0, 0);
            Grid.SetColumn(control, 1); g.Children.Add(control);
        }
        return g;
    }

    Border ToggleCard(string title, string? desc, bool value, Action<bool> onChange)
    {
        var sw = new ToggleSwitch { IsOn = value, OnContent = "", OffContent = "", MinWidth = 0 };
        sw.Toggled += (_, _) => onChange(sw.IsOn);
        return Card(Row(title, desc, sw));
    }

    Border SliderCard(string title, int value, int min, int max, Action<int> onChange)
    {
        var sp = new StackPanel();
        var label = new TextBlock { Text = $"{title}: {value}", FontSize = 14, Foreground = Theme.Brush(Theme.Text) };
        sp.Children.Add(label);
        var slider = new Slider
        {
            Minimum = min, Maximum = max, Value = value,
            StepFrequency = 1, SnapsTo = Microsoft.UI.Xaml.Controls.Primitives.SliderSnapsTo.StepValues,
            Margin = new Thickness(0, 2, 0, 0),
        };
        slider.ValueChanged += (_, e) => { int v = (int)e.NewValue; label.Text = $"{title}: {v}"; onChange(v); };
        sp.Children.Add(slider);
        return Card(sp);
    }

    InfoBar Info(string message, InfoBarSeverity severity) => new()
    {
        Message = message,
        Severity = severity,
        IsOpen = true,
        IsClosable = false,
        Margin = new Thickness(0, 0, 0, 4),
    };

    Button Btn(string text, Action act)
    {
        var b = new Button { Content = text };
        b.Click += (_, _) => act();
        return b;
    }

    void CenterOnScreen(WindowId id)
    {
        var work = DisplayArea.GetFromWindowId(id, DisplayAreaFallback.Primary).WorkArea;
        var s = _appWindow.Size;
        _appWindow.Move(new PointInt32(work.X + (work.Width - s.Width) / 2, work.Y + (work.Height - s.Height) / 2));
    }

    void DarkCaptionButtons()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;
        var tb = _appWindow.TitleBar;
        tb.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        tb.ButtonForegroundColor = Microsoft.UI.Colors.White;
        tb.ButtonInactiveForegroundColor = Theme.TextFaint;
        tb.ButtonHoverForegroundColor = Microsoft.UI.Colors.White;
        tb.ButtonHoverBackgroundColor = Theme.Argb(0x22, 0xFF, 0xFF, 0xFF);
    }
}
