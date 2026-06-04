using System.Windows.Input;
using ClaudeNotch.Core;
using H.NotifyIcon;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ClaudeNotch.UI;

/// <summary>系统托盘图标 + WinUI MenuFlyout 菜单(Acrylic 圆角)+ 气泡通知。</summary>
public sealed class Tray : IDisposable
{
    readonly TaskbarIcon _icon;

    public Action? OpenSettings, OpenAnalytics, RefreshAll, ToggleWidget, Quit;

    public Tray()
    {
        _icon = new TaskbarIcon
        {
            ToolTipText = "ClaudeNotch",
            IconSource = new BitmapImage(new Uri("ms-appx:///Assets/tray.ico")),
            ContextMenuMode = ContextMenuMode.SecondWindow,
            LeftClickCommand = new RelayCommand(() => ToggleWidget?.Invoke()),
        };
        _icon.ContextFlyout = BuildMenu();
        L.Changed += () => _icon.ContextFlyout = BuildMenu();
        _icon.ForceCreate();
    }

    MenuFlyout BuildMenu()
    {
        var menu = new MenuFlyout();
        MenuFlyoutItem Item(string text, string glyph, Action? act)
        {
            var mi = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
            mi.Click += (_, _) => act?.Invoke();
            return mi;
        }
        menu.Items.Add(Item(L.Tr("设置…", "Settings…"), "", () => OpenSettings?.Invoke()));
        menu.Items.Add(Item(L.Tr("数据统计…", "Analytics…"), "", () => OpenAnalytics?.Invoke()));
        menu.Items.Add(Item(L.Tr("显示/隐藏挂件", "Show/Hide widget"), "", () => ToggleWidget?.Invoke()));
        menu.Items.Add(Item(L.Tr("立即刷新", "Refresh Now"), "", () => RefreshAll?.Invoke()));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item(L.Tr("退出", "Quit"), "", () => Quit?.Invoke()));
        return menu;
    }

    public void SetTooltip(string text) => _icon.ToolTipText = text.Length > 127 ? text[..127] : text;

    public void ShowBalloon(string title, string body) =>
        _icon.ShowNotification(title: title, message: body);

    public void Dispose() => _icon.Dispose();
}

/// <summary>极简 ICommand,用于 TaskbarIcon.LeftClickCommand。</summary>
sealed class RelayCommand : ICommand
{
    readonly Action _run;
    public RelayCommand(Action run) => _run = run;
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _run();
}
