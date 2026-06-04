namespace ClaudeNotch.Core;

/// <summary>把启动/未捕获异常写到 %APPDATA%\ClaudeNotch\crash.log，便于无开发环境时排查闪退。</summary>
public static class CrashLog
{
    public static string FilePath => Path.Combine(Paths.SupportDir, "crash.log");

    public static void Write(string where, Exception e)
    {
        try
        {
            File.AppendAllText(FilePath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {where}\n{e}\n\n");
        }
        catch { /* 排查日志本身失败就算了 */ }
    }

    public static void Write(string where, string message)
    {
        try { File.AppendAllText(FilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {where}\n{message}\n\n"); }
        catch { }
    }
}
