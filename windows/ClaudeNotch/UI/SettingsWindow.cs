using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClaudeNotch.Core;

namespace ClaudeNotch.UI;

/// <summary>设置窗口：语言、通用、通知、模型价格(+覆盖)、货币与汇率、集成诊断。</summary>
public sealed class SettingsWindow : Window
{
    readonly AppSettings _settings;
    readonly ModelPriceStore _prices;
    readonly ExchangeRateStore _rates;
    readonly StackPanel _root;

    public Action? OnSettingsApplied;   // App 据此回写 stores / statusline

    public SettingsWindow(AppSettings settings, ModelPriceStore prices, ExchangeRateStore rates)
    {
        _settings = settings; _prices = prices; _rates = rates;
        Title = "ClaudeNotch";
        Width = 480; Height = 660;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Theme.Brush(Theme.WindowBg);
        FontFamily = Theme.FontText;
        Win11.Modernize(this);

        _root = new StackPanel { Margin = new Thickness(20) };
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _root, Padding = new Thickness(0) };

        L.Changed += Build;
        _prices.Changed += () => Dispatcher.BeginInvoke(Build);
        _rates.Changed += () => Dispatcher.BeginInvoke(Build);
        Build();
    }

    void Build()
    {
        Title = L.Tr("ClaudeNotch 设置", "ClaudeNotch Settings");
        _root.Children.Clear();

        // 语言
        _root.Children.Add(Group(L.Tr("外观与语言", "Appearance & Language"), g =>
        {
            var combo = new ComboBox { Margin = new Thickness(0, 4, 0, 0) };
            foreach (var p in new[] { LangPref.System, LangPref.Zh, LangPref.En })
                combo.Items.Add(new ComboBoxItem { Content = L.PrefLabel(p), Tag = p });
            combo.SelectedIndex = _settings.Lang switch { LangPref.Zh => 1, LangPref.En => 2, _ => 0 };
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedItem is ComboBoxItem it && it.Tag is LangPref pref)
                {
                    _settings.LanguagePreference = L.PrefRaw(pref);
                    _settings.Save();
                    L.Apply(pref);     // 触发 L.Changed → 全 app（含本窗口）重建
                    OnSettingsApplied?.Invoke();
                }
            };
            g.Children.Add(Label(L.Tr("语言", "Language")));
            g.Children.Add(combo);
        }));

        // 通用
        _root.Children.Add(Group(L.Tr("通用", "General"), g =>
        {
            g.Children.Add(Toggle(L.Tr("启用悬浮挂件", "Enable floating widget"), _settings.WidgetEnabled, v => { _settings.WidgetEnabled = v; _settings.Save(); OnSettingsApplied?.Invoke(); }));
            g.Children.Add(Toggle(L.Tr("开机自启动", "Launch at login"), _settings.LaunchAtLogin, v => { _settings.LaunchAtLogin = v; _settings.Save(); StartupRegistry.Apply(v); }));
            g.Children.Add(Toggle(L.Tr("接管 Claude Code 的 statusLine", "Manage Claude Code's statusLine"), _settings.ManageStatusline, v => { _settings.ManageStatusline = v; _settings.Save(); OnSettingsApplied?.Invoke(); }));
            g.Children.Add(Caption(L.Tr("关闭后不再改写 ~/.claude/settings.json，额度停留在最后一次。",
                "When off, ~/.claude/settings.json is left untouched; quota stays at the last value.")));
        }));

        // 通知
        _root.Children.Add(Group(L.Tr("通知", "Notifications"), g =>
        {
            g.Children.Add(Toggle(L.Tr("额度 / 上下文通知", "Quota / context alerts"), _settings.NotificationsEnabled, v => { _settings.NotificationsEnabled = v; _settings.Save(); OnSettingsApplied?.Invoke(); }));
            g.Children.Add(Stepper(L.Tr("提示档 %", "Warning %"), _settings.QuotaWarn, 50, 95, v => { _settings.QuotaWarn = v; _settings.Save(); OnSettingsApplied?.Invoke(); }));
            g.Children.Add(Stepper(L.Tr("严重档 %", "Critical %"), _settings.QuotaCritical, 55, 99, v => { _settings.QuotaCritical = v; _settings.Save(); OnSettingsApplied?.Invoke(); }));
            g.Children.Add(Stepper(L.Tr("上下文告警 %", "Context alert %"), _settings.ContextThreshold, 70, 99, v => { _settings.ContextThreshold = v; _settings.Save(); OnSettingsApplied?.Invoke(); }));
        }));

        // 模型价格
        _root.Children.Add(Group(L.Tr("模型价格", "Model pricing"), g =>
        {
            var status = L.Tr($"已载入 {_prices.ModelCount} 个模型单价", $"{_prices.ModelCount} model prices loaded")
                + (_prices.OverrideCount > 0 ? L.Tr($"（含 {_prices.OverrideCount} 条覆盖）", $" (incl. {_prices.OverrideCount} overrides)") : "");
            g.Children.Add(Label(status));
            g.Children.Add(Caption(_prices.LastUpdated is DateTime d
                ? L.Tr("已联网更新于 ", "Updated online at ") + d.ToString("yyyy-MM-dd HH:mm")
                : L.Tr("来源：内置快照", "Source: bundled snapshot")));
            if (_prices.LastError is string e1) g.Children.Add(Warn(e1));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            row.Children.Add(Btn(_prices.IsRefreshing ? L.Tr("刷新中…", "Refreshing…") : L.Tr("刷新价格", "Refresh prices"), () => _ = _prices.RefreshAsync()));
            row.Children.Add(Btn(L.Tr("编辑价格覆盖…", "Edit overrides…"), () => _prices.OpenOverridesForEditing()));
            g.Children.Add(row);
            g.Children.Add(Caption(L.Tr("第三方模型用 LiteLLM 真实价；未收录型号可在覆盖里手填（编辑后点刷新价格生效）。",
                "Third-party models use real LiteLLM prices; set missing ones via overrides (click refresh after editing).")));
        }));

        // 货币与汇率
        _root.Children.Add(Group(L.Tr("货币与汇率", "Currency & rate"), g =>
        {
            g.Children.Add(Label(L.Current == AppLang.Zh
                ? $"金额按人民币(¥)显示，1 USD = ¥{_rates.Rate:F4}"
                : "Amounts shown in US dollars ($)"));
            g.Children.Add(Caption(_rates.LastUpdated is DateTime d
                ? L.Tr("汇率更新于 ", "Rate updated at ") + d.ToString("yyyy-MM-dd HH:mm")
                : L.Tr("汇率：内置默认值", "Rate: built-in default")));
            if (_rates.LastError is string e2) g.Children.Add(Warn(e2));
            g.Children.Add(Btn(_rates.IsRefreshing ? L.Tr("刷新中…", "Refreshing…") : L.Tr("刷新汇率", "Refresh rate"), () => _ = _rates.RefreshAsync()));
        }));

        // 集成诊断
        _root.Children.Add(Group(L.Tr("集成状态", "Integration"), g =>
        {
            var d = StatuslineHook.GetDiagnostics();
            g.Children.Add(Label(L.Tr("接入状态：", "Status: ") + (d.Installed ? L.Tr("已接入 ✓", "Installed ✓") : L.Tr("未接入 ✗", "Not installed ✗"))));
            g.Children.Add(Caption(L.Tr("额度数据：", "Quota data: ") + (d.CapturedAt is DateTime c ? c.ToString("MM-dd HH:mm") : L.Tr("尚无（去跑一次 claude）", "none yet (run claude once)"))));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            row.Children.Add(Btn(L.Tr("重新接入", "Reinstall"), () => { StatuslineHook.EnsureInstalled(); Build(); }));
            row.Children.Add(Btn(L.Tr("打开支持目录", "Open support folder"), () => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Paths.SupportDir) { UseShellExecute = true }); } catch { } }));
            row.Children.Add(Btn(L.Tr("复制诊断", "Copy diagnostics"), () => { try { Clipboard.SetText(d.CopyText()); } catch { } }));
            g.Children.Add(row);
            g.Children.Add(Caption(L.Tr("额度来自 Claude Code 的 statusLine 钩子（不抓网页、不复用令牌），仅在 Claude Code 运行时更新。",
                "Quota comes from Claude Code's statusLine hook (no scraping, no token reuse); updates only while Claude Code runs.")));
        }));
    }

    // ── 控件工厂 ──
    static readonly Color Fg = Color.FromRgb(0xEC, 0xEC, 0xEE);
    static readonly Color Dim = Color.FromArgb(0xBB, 0xFF, 0xFF, 0xFF);

    Border Group(string title, Action<StackPanel> fill)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 13, Foreground = new SolidColorBrush(Fg), Margin = new Thickness(0, 0, 0, 6) });
        fill(sp);
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 12),
            Child = sp,
        };
    }

    static TextBlock Label(string t) => new() { Text = t, FontSize = 12, Foreground = new SolidColorBrush(Fg), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
    static TextBlock Caption(string t) => new() { Text = t, FontSize = 11, Foreground = new SolidColorBrush(Dim), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    static TextBlock Warn(string t) => new() { Text = t, FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0x9B, 0x33)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };

    CheckBox Toggle(string text, bool value, Action<bool> onChange)
    {
        var cb = new CheckBox { Content = text, IsChecked = value, Foreground = new SolidColorBrush(Fg), Margin = new Thickness(0, 4, 0, 0) };
        cb.Checked += (_, _) => onChange(true);
        cb.Unchecked += (_, _) => onChange(false);
        return cb;
    }

    Grid Stepper(string text, int value, int min, int max, Action<int> onChange)
    {
        var g = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var lbl = new TextBlock { Text = $"{text}: {value}", Foreground = new SolidColorBrush(Fg), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(lbl, 0); g.Children.Add(lbl);
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, Width = 160, TickFrequency = 1, IsSnapToTickEnabled = true };
        slider.ValueChanged += (_, e) => { int v = (int)e.NewValue; lbl.Text = $"{text}: {v}"; onChange(v); };
        Grid.SetColumn(slider, 1); g.Children.Add(slider);
        return g;
    }

    Button Btn(string text, Action act)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(8, 3, 8, 3) };
        b.Click += (_, _) => act();
        return b;
    }
}
