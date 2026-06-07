using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ClaudeNotch.Core;

namespace ClaudeNotch.UI;

/// <summary>设置窗:语言 / 通用(挂件·自启·statusLine) / 通知(开关+阈值) / 模型价格 / 货币汇率 / 集成状态。深色。</summary>
public sealed class SettingsWindow : Window
{
    readonly AppSettings _settings;
    readonly ModelPriceStore _prices;
    readonly ExchangeRateStore _rates;
    readonly StackPanel _root;

    public Action? OnSettingsApplied;

    public SettingsWindow(AppSettings settings, ModelPriceStore prices, ExchangeRateStore rates)
    {
        _settings = settings; _prices = prices; _rates = rates;
        Title = L.Tr("设置", "Settings");
        Width = 540; Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Palette.Brush(Palette.WindowBg);

        _root = new StackPanel { Margin = new Thickness(24) };
        Content = new ScrollViewer { Content = _root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        L.Changed += () => Dispatcher.UIThread.Post(Build);
        _prices.Changed += () => Dispatcher.UIThread.Post(Build);
        _rates.Changed += () => Dispatcher.UIThread.Post(Build);
        ActualThemeVariantChanged += (_, _) => { Background = Palette.Brush(Palette.WindowBg); Build(); };
        Build();
    }

    void Build()
    {
        _root.Children.Clear();

        Section(L.Tr("监控对象", "Monitored CLI"), () =>
        {
            var combo = new ComboBox { MinWidth = 160 };
            combo.Items.Add(new ComboBoxItem { Content = "Claude Code", Tag = AgentKind.ClaudeCode });
            combo.Items.Add(new ComboBoxItem { Content = "Codex", Tag = AgentKind.Codex });
            combo.SelectedIndex = _settings.AgentKind == AgentKind.Codex ? 1 : 0;
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedItem is ComboBoxItem it && it.Tag is AgentKind a && a.Save() != _settings.Agent)
                {
                    _settings.Agent = a.Save();
                    _settings.Save();
                    OnSettingsApplied?.Invoke();   // → App.ApplySettings 检测代理变更并重置数据来源
                    Build();                        // 重绘:statusLine/集成状态随代理显隐
                }
            };
            _root.Children.Add(Card(Row(L.Tr("CLI 代理", "CLI agent"), null, combo)));
            _root.Children.Add(T(_settings.AgentKind == AgentKind.Codex
                ? L.Tr("读取 ~/.codex/sessions 里的会话记录(额度、花费、上下文均来自其中)。",
                       "Reads sessions from ~/.codex/sessions (quota, cost and context all come from there).")
                : L.Tr("通过 Claude Code 的 statusLine 钩子取额度，并扫描 ~/.claude/projects 的会话。",
                       "Quota via Claude Code's statusLine hook; sessions scanned from ~/.claude/projects."),
                11, Palette.TextFaint, top: 4));
        });

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

            // 配色:跟随系统 / 日间 / 夜间
            var themeCombo = new ComboBox { MinWidth = 160 };
            var modes = new[] { ("system", L.Tr("跟随系统", "System")), ("light", L.Tr("日间模式", "Light")), ("dark", L.Tr("夜间模式", "Dark")) };
            foreach (var (val, lbl) in modes) themeCombo.Items.Add(new ComboBoxItem { Content = lbl, Tag = val });
            themeCombo.SelectedIndex = _settings.ThemeMode switch { "light" => 1, "dark" => 2, _ => 0 };
            themeCombo.SelectionChanged += (_, _) =>
            {
                if (themeCombo.SelectedItem is ComboBoxItem it && it.Tag is string mode && mode != _settings.ThemeMode)
                {
                    _settings.ThemeMode = mode;
                    _settings.Save();
                    OnSettingsApplied?.Invoke();   // → App.ApplySettings → ApplyTheme
                }
            };
            _root.Children.Add(Card(Row(L.Tr("配色", "Appearance"), null, themeCombo)));
        });

        Section(L.Tr("通用", "General"), () =>
        {
            _root.Children.Add(ToggleCard(L.Tr("启用悬浮挂件", "Enable floating widget"), null, _settings.WidgetEnabled,
                v => { _settings.WidgetEnabled = v; _settings.Save(); OnSettingsApplied?.Invoke(); }));
            _root.Children.Add(ToggleCard(L.Tr("开机自启动", "Launch at login"), null, _settings.LaunchAtLogin,
                v => { _settings.LaunchAtLogin = v; _settings.Save(); StartupRegistry.Apply(v); }));
            if (_settings.AgentKind == AgentKind.ClaudeCode)
                _root.Children.Add(ToggleCard(L.Tr("接管 Claude Code 的 statusLine", "Manage Claude Code's statusLine"),
                    L.Tr("关闭后不再改写 ~/.claude/settings.json，额度停留在最后一次。",
                         "When off, ~/.claude/settings.json is left untouched; quota stays at the last value."),
                    _settings.ManageStatusline,
                    v => { _settings.ManageStatusline = v; _settings.Save(); OnSettingsApplied?.Invoke(); }));
        });

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

        Section(L.Tr("模型价格", "Model pricing"), () =>
        {
            var status = L.Tr($"已载入 {_prices.ModelCount} 个模型单价", $"{_prices.ModelCount} model prices loaded")
                + (_prices.OverrideCount > 0 ? L.Tr($"（含 {_prices.OverrideCount} 条覆盖）", $" (incl. {_prices.OverrideCount} overrides)") : "");
            var sub = _prices.LastUpdated is DateTime d
                ? L.Tr("已联网更新于 ", "Updated online at ") + d.ToString("yyyy-MM-dd HH:mm")
                : L.Tr("来源：内置快照", "Source: bundled snapshot");
            _root.Children.Add(Card(Row(status, sub, null)));
            if (_prices.LastError is string e1) _root.Children.Add(InfoCard(e1, true));
            var btns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            btns.Children.Add(Btn(_prices.IsRefreshing ? L.Tr("刷新中…", "Refreshing…") : L.Tr("刷新价格", "Refresh prices"), () => _ = _prices.RefreshAsync()));
            btns.Children.Add(Btn(L.Tr("编辑价格覆盖…", "Edit overrides…"), () => _prices.OpenOverridesForEditing()));
            _root.Children.Add(Card(btns));
        });

        Section(L.Tr("货币与汇率", "Currency & rate"), () =>
        {
            var line = L.Current == AppLang.Zh
                ? $"金额按人民币(¥)显示，1 USD = ¥{_rates.Rate:F4}"
                : "Amounts shown in US dollars ($)";
            var sub = _rates.LastUpdated is DateTime d
                ? L.Tr("汇率更新于 ", "Rate updated at ") + d.ToString("yyyy-MM-dd HH:mm")
                : L.Tr("汇率：内置默认值", "Rate: built-in default");
            _root.Children.Add(Card(Row(line, sub, null)));
            if (_rates.LastError is string e2) _root.Children.Add(InfoCard(e2, true));
            _root.Children.Add(Card(Btn(_rates.IsRefreshing ? L.Tr("刷新中…", "Refreshing…") : L.Tr("刷新汇率", "Refresh rate"), () => _ = _rates.RefreshAsync())));
        });

        if (_settings.AgentKind == AgentKind.ClaudeCode)
        Section(L.Tr("集成状态", "Integration"), () =>
        {
            var diag = StatuslineHook.GetDiagnostics();
            _root.Children.Add(InfoCard(
                diag.Installed ? L.Tr("已接入 Claude Code 的 statusLine ✓", "Connected to Claude Code's statusLine ✓")
                               : L.Tr("尚未接入 ✗", "Not connected ✗"),
                !diag.Installed));
            _root.Children.Add(Card(Row(L.Tr("额度数据", "Quota data"),
                diag.CapturedAt is DateTime c ? c.ToString("MM-dd HH:mm") : L.Tr("尚无（去跑一次 claude）", "none yet (run claude once)"), null)));
            var btns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            btns.Children.Add(Btn(L.Tr("重新接入", "Reinstall"), () => { StatuslineHook.EnsureInstalled(); Build(); }));
            btns.Children.Add(Btn(L.Tr("打开支持目录", "Open support folder"), () => { try { Process.Start(new ProcessStartInfo(Paths.SupportDir) { UseShellExecute = true }); } catch { } }));
            btns.Children.Add(Btn(L.Tr("复制诊断", "Copy diagnostics"), async () => { var cb = GetTopLevel(this)?.Clipboard; if (cb is not null) await cb.SetTextAsync(diag.CopyText()); }));
            _root.Children.Add(Card(btns));
            _root.Children.Add(T(L.Tr("额度来自 Claude Code 的 statusLine 钩子（不抓网页、不复用令牌），仅在 Claude Code 运行时更新。",
                "Quota comes from Claude Code's statusLine hook (no scraping, no token reuse); updates only while Claude Code runs."), 11, Palette.TextFaint, top: 6));
        });
    }

    // ── 工厂 ──
    void Section(string title, Action fill)
    {
        _root.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 14, Foreground = Palette.Brush(Palette.Text), Margin = new Thickness(2, 16, 0, 8) });
        fill();
    }

    static Border Card(Control content) => new()
    {
        Background = Palette.Brush(Palette.CardBg), CornerRadius = new CornerRadius(8),
        Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 0, 0, 4), Child = content,
    };

    Border InfoCard(string message, bool warn) => new()
    {
        Background = Palette.Brush(warn ? Palette.Argb(0x33, 0xF2, 0x4D, 0x4D) : Palette.Argb(0x33, 0x2E, 0xC7, 0x71)),
        CornerRadius = new CornerRadius(8), Padding = new Thickness(16, 10, 16, 10), Margin = new Thickness(0, 0, 0, 4),
        Child = T(message, 12, Palette.Text),
    };

    Grid Row(string title, string? desc, Control? control)
    {
        var g = new Grid { VerticalAlignment = VerticalAlignment.Center, ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontSize = 14, Foreground = Palette.Brush(Palette.Text), TextWrapping = TextWrapping.Wrap });
        if (desc is not null)
            text.Children.Add(new TextBlock { Text = desc, FontSize = 12, Foreground = Palette.Brush(Palette.TextDim), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
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
        var sw = new ToggleSwitch { IsChecked = value, OnContent = "", OffContent = "" };
        sw.IsCheckedChanged += (_, _) => onChange(sw.IsChecked == true);
        return Card(Row(title, desc, sw));
    }

    Border SliderCard(string title, int value, int min, int max, Action<int> onChange)
    {
        var sp = new StackPanel();
        var label = new TextBlock { Text = $"{title}: {value}", FontSize = 14, Foreground = Palette.Brush(Palette.Text) };
        sp.Children.Add(label);
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, TickFrequency = 1, IsSnapToTickEnabled = true, Margin = new Thickness(0, 2, 0, 0) };
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty) { int v = (int)slider.Value; label.Text = $"{title}: {v}"; onChange(v); }
        };
        sp.Children.Add(slider);
        return Card(sp);
    }

    Button Btn(string text, Action act)
    {
        var b = new Button { Content = text };
        b.Click += (_, _) => act();
        return b;
    }

    static TextBlock T(string text, double size, Color color, double top = 0) => new()
    {
        Text = text, FontSize = size, Foreground = Palette.Brush(color), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0),
    };
}
