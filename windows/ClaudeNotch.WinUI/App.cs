using ClaudeNotch.Core;
using ClaudeNotch.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClaudeNotch;

/// <summary>
/// 代码态 Application(无 App.xaml)。编排:装配 Core stores、托盘、悬浮挂件。
/// 注意:XamlControlsResources 必须在 OnLaunched(而非构造函数)里 merge,否则 COMException。
/// </summary>
public sealed class App : Application
{
    DispatcherQueue _ui = null!;
    AppSettings _settings = null!;
    UsageStore _usage = null!;
    SessionStore _sessions = null!;
    ModelPriceStore _prices = null!;
    ExchangeRateStore _rates = null!;
    HistoryStore _history = null!;
    Tray _tray = null!;
    WidgetWindow? _widget;
    SettingsWindow? _settingsWin;
    AnalyticsWindow? _analyticsWin;

    public App() { }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try { OnLaunchedCore(); }
        catch (Exception ex) { CrashLog.Write("App.OnLaunched", ex); throw; }
    }

    void OnLaunchedCore()
    {
        // WinUI 线程未捕获异常也落盘。
        UnhandledException += (_, e) => CrashLog.Write("App.UnhandledException", e.Exception);

        // 控件默认样式(Fluent)。必须在 App 完全构造后加载。
        Resources.MergedDictionaries.Add(new XamlControlsResources());

        _ui = DispatcherQueue.GetForCurrentThread();

        _settings = AppSettings.Load();
        L.Init(_settings.Lang);

        _usage = new UsageStore();
        _sessions = new SessionStore();
        _prices = new ModelPriceStore();
        _rates = new ExchangeRateStore();
        _history = new HistoryStore();
        _rates.Bootstrap();
        _prices.Bootstrap();

        _tray = new Tray
        {
            OpenSettings = ShowSettings,
            OpenAnalytics = ShowAnalytics,
            RefreshAll = RefreshAll,
            ToggleWidget = ToggleWidget,
            Quit = () => Exit(),
        };

        Notifier.Show = (t, b) => _ui.TryEnqueue(() => _tray.ShowBalloon(t, b));
        _usage.Changed += () => _ui.TryEnqueue(UpdateTrayTooltip);

        _usage.Start();
        _sessions.Start();

        ApplySettings();
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
        else if (_widget is not null) _widget.AppWindow.Hide();
    }

    void ShowSettings()
    {
        if (_settingsWin is null)
        {
            _settingsWin = new SettingsWindow(_settings, _prices, _rates) { OnSettingsApplied = ApplySettings };
            _settingsWin.Closed += (_, _) => _settingsWin = null;
        }
        _settingsWin.Activate();
    }

    void ShowAnalytics()
    {
        if (_analyticsWin is null)
        {
            _analyticsWin = new AnalyticsWindow(_history);
            _analyticsWin.Closed += (_, _) => _analyticsWin = null;
        }
        _analyticsWin.Activate();
        _history.RefreshIfNeeded();
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
                Quit = () => Exit(),
            };
        }
        _widget.Activate();
    }

    void ToggleWidget()
    {
        if (_widget is { Visible: true }) _widget.AppWindow.Hide();
        else ShowWidget();
    }

    void RefreshAll() { _usage.Refresh(); _sessions.Refresh(); }

    void UpdateTrayTooltip()
    {
        var head = _usage.Snapshot?.Headline;
        _tray.SetTooltip(head is not null ? $"ClaudeNotch · {head.PercentUsed}%" : "ClaudeNotch");
    }
}
