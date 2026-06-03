using System.Diagnostics;

namespace ClaudeNotch.Core;

/// <summary>共享路径。Windows 下 Claude 数据目录在 %USERPROFILE%\.claude，本 app 支持目录在 %APPDATA%\ClaudeNotch。</summary>
public static class Paths
{
    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string ClaudeDir => Path.Combine(Home, ".claude");
    public static string ClaudeSettings => Path.Combine(ClaudeDir, "settings.json");
    public static string ProjectsDir => Path.Combine(ClaudeDir, "projects");

    public static string SupportDir
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeNotch");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string RatelimitsFile => Path.Combine(SupportDir, "ratelimits.json");
    public static string InnerStatusLineFile => Path.Combine(SupportDir, "inner-statusline.json");
    public static string HistoryCacheFile => Path.Combine(SupportDir, "usage-history.json");
    public static string OverridesFile => Path.Combine(SupportDir, "model-price-overrides.json");
    public static string LiteLLMCacheFile => Path.Combine(SupportDir, "litellm_prices.json");
    public static string SettingsFile => Path.Combine(SupportDir, "settings.json");

    /// <summary>当前可执行文件完整路径（注册 statusLine 命令时用）。</summary>
    public static string ExePath =>
        Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "ClaudeNotch.exe";

    /// <summary>专用 statusLine 助手 exe（与主 exe 同目录；高频 stdin 钩子用它，而非重型 WPF 主程序）。</summary>
    public static string StatuslineHelperExe
    {
        get
        {
            var dir = Path.GetDirectoryName(ExePath);
            return dir is null ? "ClaudeNotch.Statusline.exe" : Path.Combine(dir, "ClaudeNotch.Statusline.exe");
        }
    }

    /// <summary>内置 LiteLLM 价表快照（与可执行文件同目录）。</summary>
    public static string BundledLiteLLM =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "litellm_prices.json");
}
