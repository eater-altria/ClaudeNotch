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
    Tray _tray = null!;
    WidgetWindow? _widget;

    public App() { }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 控件默认样式(Fluent)。必须在 App 完全构造后加载。
        Resources.MergedDictionaries.Add(new XamlControlsResources());

        _ui = DispatcherQueue.GetForCurrentThread();

        _settings = AppSettings.Load();
        L.Init(_settings.Lang);

        _usage = new UsageStore();
        _sessions = new SessionStore();

        _usage.QuotaWarn = Math.Min(_settings.QuotaWarn, _settings.QuotaCritical);
        _usage.QuotaCritical = Math.Max(_settings.QuotaWarn, _settings.QuotaCritical);
        _usage.NotificationsEnabled = _settings.NotificationsEnabled;
        _sessions.ContextThreshold = _settings.ContextThreshold;
        _sessions.NotificationsEnabled = _settings.NotificationsEnabled;

        _tray = new Tray
        {
            RefreshAll = RefreshAll,
            ToggleWidget = ToggleWidget,
            Quit = () => Exit(),
        };

        Notifier.Show = (t, b) => _ui.TryEnqueue(() => _tray.ShowBalloon(t, b));
        _usage.Changed += () => _ui.TryEnqueue(UpdateTrayTooltip);

        if (_settings.ManageStatusline) StatuslineHook.EnsureInstalled();

        _usage.Start();
        _sessions.Start();

        if (_settings.WidgetEnabled) ShowWidget();
    }

    void ShowWidget()
    {
        if (_widget is null)
        {
            _widget = new WidgetWindow(_usage, _sessions, _settings)
            {
                OpenSettings = () => _tray.ShowBalloon("ClaudeNotch", L.Tr("设置页开发中", "Settings page coming soon")),
                OpenAnalytics = () => _tray.ShowBalloon("ClaudeNotch", L.Tr("数据统计页开发中", "Analytics page coming soon")),
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
