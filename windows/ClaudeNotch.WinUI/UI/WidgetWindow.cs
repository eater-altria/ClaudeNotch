using System.Runtime.InteropServices;
using ClaudeNotch.Core;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace ClaudeNotch.UI;

/// <summary>
/// 置顶可拖拽的悬浮挂件(替代 macOS 刘海)。WinUI 3 无法做逐像素透明/真圆窗,
/// 故采用「圆角(DWM)+ Acrylic 背景」的卡片形态。P1 为最小骨架,完整内容见 P3。
/// </summary>
public sealed class WidgetWindow : Window
{
    readonly UsageStore _usage;
    readonly SessionStore _sessions;
    readonly AppSettings _settings;
    readonly IntPtr _hwnd;
    readonly AppWindow _appWindow;

    public WidgetWindow(UsageStore usage, SessionStore sessions, AppSettings settings)
    {
        _usage = usage; _sessions = sessions; _settings = settings;

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
        _appWindow.Resize(new SizeInt32(220, 96));

        RoundCorners();
        BuildContent();
        RestorePosition(id);
    }

    void BuildContent()
    {
        var root = new Grid { Padding = new Thickness(14) };
        root.PointerPressed += OnDrag;

        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 2,
        };
        stack.Children.Add(new TextBlock
        {
            Text = "ClaudeNotch",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        stack.Children.Add(new TextBlock
        {
            Text = L.Tr("等待数据…", "Waiting…"),
            Opacity = 0.6,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        root.Children.Add(stack);
        Content = root;
    }

    void RestorePosition(WindowId id)
    {
        var work = DisplayArea.GetFromWindowId(id, DisplayAreaFallback.Primary).WorkArea;
        int x, y;
        if (_settings.WidgetX is double sx && _settings.WidgetY is double sy)
        {
            x = (int)Math.Clamp(sx, work.X, work.X + work.Width - 220);
            y = (int)Math.Clamp(sy, work.Y, work.Y + work.Height - 96);
        }
        else { x = work.X + work.Width - 220 - 24; y = work.Y + 24; }
        _appWindow.Move(new PointInt32(x, y));
    }

    void OnDrag(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint((UIElement)sender);
        if (!pt.Properties.IsLeftButtonPressed) return;
        ReleaseCapture();
        SendMessage(_hwnd, 0x00A1 /*WM_NCLBUTTONDOWN*/, (IntPtr)2 /*HTCAPTION*/, IntPtr.Zero);
        SavePosition();
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
        try { int round = 2 /*DWMWCP_ROUND*/; DwmSetWindowAttribute(_hwnd, 33, ref round, sizeof(int)); }
        catch { }
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
