using System.Windows;
using ClaudeNotch.Core;
using ClaudeNotch.UI;

namespace ClaudeNotch;

/// <summary>应用编排：装配 stores、托盘、悬浮挂件，串联设置/统计窗口与生命周期。</summary>
public sealed class App : Application
{
    AppSettings _settings = null!;
    UsageStore _usage = null!;
    SessionStore _sessions = null!;
    HistoryStore _history = null!;
    ModelPriceStore _prices = null!;
    ExchangeRateStore _rates = null!;
    Tray _tray = null!;
    WidgetWindow? _widget;
    SettingsWindow? _settingsWin;
    AnalyticsWindow? _analyticsWin;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _settings = AppSettings.Load();
        L.Init(_settings.Lang);

        _usage = new UsageStore();
        _sessions = new SessionStore();
        _history = new HistoryStore();
        _prices = new ModelPriceStore();
        _rates = new ExchangeRateStore();

        _rates.Bootstrap();
        _prices.Bootstrap();
        ApplySettings();

        _tray = new Tray
        {
            OpenSettings = ShowSettings,
            OpenAnalytics = ShowAnalytics,
            RefreshAll = RefreshAll,
            ToggleWidget = ToggleWidget,
            Quit = () => Shutdown(),
        };
        Notifier.Show = (t, b) => Dispatcher.BeginInvoke(() => _tray.ShowBalloon(t, b));

        _usage.Changed += () => Dispatcher.BeginInvoke(UpdateTrayTooltip);

        _usage.Start();
        _sessions.Start();

        if (_settings.ManageStatusline) StatuslineHook.EnsureInstalled();

        if (_settings.WidgetEnabled) ShowWidget();

        Exit += (_, _) =>
        {
            try { if (_settings.ManageStatusline) StatuslineHook.Uninstall(purgeData: false); } catch { }
            _tray.Dispose();
        };
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
        else { _widget?.Close(); _widget = null; }
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
                Quit = () => Shutdown(),
            };
            _widget.Closed += (_, _) => _widget = null;
        }
        _widget.Show();
        _widget.Activate();
    }

    void ToggleWidget()
    {
        if (_widget is { IsVisible: true }) { _widget.Hide(); }
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

    void UpdateTrayTooltip()
    {
        var head = _usage.Snapshot?.Headline;
        _tray.SetTooltip(head is not null ? $"ClaudeNotch · {head.PercentUsed}%" : "ClaudeNotch");
    }
}
