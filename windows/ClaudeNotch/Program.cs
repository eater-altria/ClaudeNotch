using System.Threading;
using ClaudeNotch.Core;

namespace ClaudeNotch;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // --statusline 助手：被 Claude Code 调起时，读 stdin、落盘额度、透传原命令，不启动 GUI。
        if (Array.IndexOf(args, "--statusline") >= 0)
        {
            StatuslineHook.RunHelper();
            return 0;
        }

        // 单实例
        using var mutex = new Mutex(initiallyOwned: true, "ClaudeNotch.SingleInstance.Mutex", out bool isNew);
        if (!isNew) return 0;

        var app = new App();
        return app.Run();
    }
}
