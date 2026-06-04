using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using ClaudeNotch.Core;
using ClaudeNotch.UI;

namespace ClaudeNotch;

/// <summary>代码态 Application:FluentTheme(强制深色)+ 托盘常驻 + 编排各 store/窗口。</summary>
public sealed class App : Application
{
    AppSettings _settings = null!;
    UsageStore _usage = null!;
    SessionStore _sessions = null!;
    ModelPriceStore _prices = null!;
    ExchangeRateStore _rates = null!;
    HistoryStore _history = null!;

    TrayIcon? _tray;
    WidgetWindow? _widget;
    SettingsWindow? _settingsWin;
    AnalyticsWindow? _analyticsWin;

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { Init(); }
            catch (Exception ex) { CrashLog.Write("App.Init", ex); throw; }
        }
        base.OnFrameworkInitializationCompleted();
    }

    void Init()
    {
        _settings = AppSettings.Load();
        L.Init(_settings.Lang);

        _usage = new UsageStore();
        _sessions = new SessionStore();
        _prices = new ModelPriceStore();
        _rates = new ExchangeRateStore();
        _history = new HistoryStore();
        _rates.Bootstrap();
        _prices.Bootstrap();

        // TODO(notifications): Avalonia TrayIcon 无原生气泡;后续用 Win32 Shell_NotifyIcon 或 toast 实现。
        Notifier.Show = (_, _) => { };

        SetupTray();
        ApplySettings();
        _usage.Start();
        _sessions.Start();
        if (_settings.WidgetEnabled) ShowWidget();
    }

    void SetupTray()
    {
        _tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://ClaudeNotch/Assets/tray.ico"))),
            ToolTipText = "ClaudeNotch",
            IsVisible = true,
            Menu = BuildTrayMenu(),
        };
        _tray.Clicked += (_, _) => ToggleWidget();
        L.Changed += () => Dispatcher.UIThread.Post(() => { if (_tray is not null) _tray.Menu = BuildTrayMenu(); });
        TrayIcon.SetIcons(this, new TrayIcons { _tray });
    }

    NativeMenu BuildTrayMenu()
    {
        var m = new NativeMenu();
        NativeMenuItem Item(string text, Action act) { var i = new NativeMenuItem(text); i.Click += (_, _) => act(); return i; }
        m.Add(Item(L.Tr("设置…", "Settings…"), ShowSettings));
        m.Add(Item(L.Tr("数据统计…", "Analytics…"), ShowAnalytics));
        m.Add(Item(L.Tr("显示/隐藏挂件", "Show/Hide widget"), ToggleWidget));
        m.Add(Item(L.Tr("立即刷新", "Refresh Now"), RefreshAll));
        m.Add(new NativeMenuItemSeparator());
        m.Add(Item(L.Tr("退出", "Quit"), Quit));
        return m;
    }

    void ApplySettings()
    {
        _usage.QuotaWarn = Math.Min(_settings.QuotaWarn, _settings.QuotaCritical);
        _usage.QuotaCritical = Math.Max(_settings.QuotaWarn, _settings.QuotaCritical);
        _usage.NotificationsEnabled = _settings.NotificationsEnabled;
        _sessions.ContextThreshold = _settings.ContextThreshold;
        _sessions.NotificationsEnabled = _settings.NotificationsEnabled;

        if (_settings.ManageStatusline) StatuslineHook.EnsureInstalled();
        else StatuslineHook.Uninstall(purgeData: false);

        if (_settings.WidgetEnabled) ShowWidget();
        else _widget?.Hide();
    }

    void ShowWidget()
    {
        if (_widget is null)
        {
            _widget = new WidgetWindow(_usage, _sessions, _settings)
            {
                OpenSettings = ShowSettings,
                OpenAnalytics = ShowAnalytics,
                RefreshAll = RefreshAll,
                Quit = Quit,
            };
        }
        _widget.Show();
        _widget.Activate();
    }

    void ToggleWidget()
    {
        if (_widget is { IsVisible: true }) _widget.Hide();
        else ShowWidget();
    }

    void ShowSettings()
    {
        if (_settingsWin is null)
        {
            _settingsWin = new SettingsWindow(_settings, _prices, _rates) { OnSettingsApplied = ApplySettings };
            _settingsWin.Closed += (_, _) => _settingsWin = null;
        }
        _settingsWin.Show();
        _settingsWin.Activate();
    }

    void ShowAnalytics()
    {
        if (_analyticsWin is null)
        {
            _analyticsWin = new AnalyticsWindow(_history);
            _analyticsWin.Closed += (_, _) => _analyticsWin = null;
        }
        _analyticsWin.Show();
        _analyticsWin.Activate();
        _history.RefreshIfNeeded();
    }

    void RefreshAll() { _usage.Refresh(); _sessions.Refresh(); }

    void Quit()
    {
        try { StatuslineHook.Uninstall(purgeData: false); } catch { }
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
    }
}
