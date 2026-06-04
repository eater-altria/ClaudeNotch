using System.Threading;
using Avalonia;
using ClaudeNotch.Core;

namespace ClaudeNotch;

/// <summary>
/// 入口。--statusline 分支在任何 UI 初始化之前跑完并退出(Claude Code 状态栏钩子的快进快出)。
/// 否则单实例锁 → 启动 Avalonia(托盘常驻,无主窗口,OnExplicitShutdown)。
/// </summary>
static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // 快路径:statusline 助手。读 stdin → 落盘额度 → 透传,绝不起 UI。
        if (args.Length > 0 && Array.IndexOf(args, "--statusline") >= 0)
        {
            StatuslineHook.RunHelper();
            return 0;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, ev) =>
        {
            if (ev.ExceptionObject is Exception ex) CrashLog.Write("AppDomain.UnhandledException", ex);
        };

        using var mutex = new Mutex(initiallyOwned: true, @"Local\ClaudeNotch.SingleInstance.Mutex", out bool isNew);
        if (!isNew) return 0;

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        }
        catch (Exception ex) { CrashLog.Write("Program.Main", ex); throw; }
        return 0;
    }

    static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
