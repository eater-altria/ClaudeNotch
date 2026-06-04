using System.Runtime.InteropServices;
using System.Threading;
using ClaudeNotch.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace ClaudeNotch;

/// <summary>
/// 自定义入口(替代 WinUI 生成的 Main,见 csproj 的 DISABLE_XAML_GENERATED_MAIN)。
/// --statusline 分支在任何 UI 初始化之前就跑完并退出,保证作为 Claude Code 状态栏钩子的快进快出。
/// </summary>
public static class Program
{
    [DllImport("Microsoft.ui.xaml.dll")]
    private static extern void XamlCheckProcessRequirements();

    [STAThread]
    private static int Main(string[] args)
    {
        // ── 快路径:statusline 助手。读 stdin → 落盘额度 → 透传,绝不起 UI。 ──
        if (args.Length > 0 && Array.IndexOf(args, "--statusline") >= 0)
        {
            StatuslineHook.RunHelper();
            return 0;
        }

        // 兜底:任何线程的未捕获异常都落盘，便于排查闪退。
        AppDomain.CurrentDomain.UnhandledException += (_, ev) =>
        {
            if (ev.ExceptionObject is Exception ex) CrashLog.Write("AppDomain.UnhandledException", ex);
        };

        // ── 单实例 ──
        using var mutex = new Mutex(initiallyOwned: true, @"Local\ClaudeNotch.SingleInstance.Mutex", out bool isNew);
        if (!isNew) return 0;

        // ── 正常 UI 路径(镜像 WinUI 生成的 Program)──
        try
        {
            XamlCheckProcessRequirements();
            global::WinRT.ComWrappersSupport.InitializeComWrappers();

            Application.Start(p =>
            {
                var ctx = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(ctx);
                new App();
            });
        }
        catch (Exception ex)
        {
            CrashLog.Write("Program.Main", ex);
            throw;
        }

        return 0;
    }
}
