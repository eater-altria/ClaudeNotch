using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ClaudeNotch.UI;

/// <summary>Windows 11 原生观感：圆角 + 沉浸式深色标题栏 + （可选）Mica 背景。</summary>
public static class Win11
{
    enum Attr
    {
        ImmersiveDarkMode = 20,
        WindowCornerPreference = 33,
        SystemBackdropType = 38,
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>对带标题栏的窗口应用：圆角 + 深色标题栏（Win10/旧版自动忽略，无害）。</summary>
    public static void Modernize(Window w, bool mica = false)
    {
        void Apply()
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;
            int dark = 1; DwmSetWindowAttribute(hwnd, (int)Attr.ImmersiveDarkMode, ref dark, sizeof(int));
            int round = 2; DwmSetWindowAttribute(hwnd, (int)Attr.WindowCornerPreference, ref round, sizeof(int)); // 2 = Round
            if (mica) { int backdrop = 2; DwmSetWindowAttribute(hwnd, (int)Attr.SystemBackdropType, ref backdrop, sizeof(int)); }
        }
        if (w.IsLoaded && new WindowInteropHelper(w).Handle != IntPtr.Zero) Apply();
        else w.SourceInitialized += (_, _) => Apply();
    }
}
