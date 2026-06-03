using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeNotch.Core;

/// <summary>
/// 与 Claude Code 的 statusLine 钩子对接（额度的唯一来源，合规、不复用令牌）。
/// 把本 app（exe --statusline）注册进 ~/.claude/settings.json 的 statusLine；Claude Code 渲染状态栏时
/// 经 stdin 把 rate_limits 喂进来，钩子落盘 ratelimits.json，再透传原命令。
/// </summary>
public static class StatuslineHook
{
    const string OwnerMarker = "_claudenotch";

    static bool IsOurs(string command) =>
        command.Contains("ClaudeNotch.Statusline") || (command.Contains("--statusline") && command.Contains("ClaudeNotch"));

    /// <summary>
    /// 注册用的 statusLine 命令：优先专用助手 exe，缺失时退回主 exe --statusline。
    /// Claude Code 在 Windows 用 **PowerShell** 执行 statusLine：**带引号的裸字符串只会被回显、不执行**，
    /// 故用「裸正斜杠路径」(PowerShell 会当命令执行；正斜杠在 PS/cmd/bash 都不被转义)。
    /// 路径含空格时用调用运算符 `& '...'`（PowerShell 下对带空格路径有效）。
    /// </summary>
    static string HookCommand()
    {
        bool helperExists = File.Exists(Paths.StatuslineHelperExe);
        var exe = (helperExists ? Paths.StatuslineHelperExe : Paths.ExePath).Replace('\\', '/');
        var args = helperExists ? "" : " --statusline";
        return exe.Contains(' ') ? $"& '{exe}'{args}" : $"{exe}{args}";
    }

    static bool ObjectIsOurs(JsonObject sl)
    {
        if (sl.TryGetPropertyValue(OwnerMarker, out var marker) && marker is JsonValue v && v.TryGetValue<bool>(out var b) && b)
            return true;
        return IsOurs((sl["command"]?.GetValue<string>()) ?? "");
    }

    // ── 作为 statusLine 命令运行（Program.Main 检测到 --statusline 时调用） ──

    public static void RunHelper()
    {
        string input;
        using (var stdin = Console.OpenStandardInput())
        using (var reader = new StreamReader(stdin, Encoding.UTF8))
            input = reader.ReadToEnd();

        JsonObject? root = null;
        try { root = JsonNode.Parse(input) as JsonObject; } catch { }
        var rateLimits = root?["rate_limits"] as JsonObject;

        if (rateLimits is not null) Persist(rateLimits, root);

        var inner = InnerCommand();
        if (inner is not null) Forward(input, inner, rateLimits);
        else Console.Out.Write(DefaultLine(root, rateLimits));
    }

    static void Persist(JsonObject rateLimits, JsonObject? root)
    {
        var payload = new JsonObject
        {
            ["capturedAt"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["rate_limits"] = rateLimits.DeepClone(),
        };
        if (root is not null)
            foreach (var key in new[] { "cost", "model", "version", "workspace", "output_style", "session_id" })
                if (root[key] is JsonNode n) payload[key] = n.DeepClone();
        try
        {
            Directory.CreateDirectory(Paths.SupportDir);
            File.WriteAllText(Paths.RatelimitsFile, payload.ToJsonString());
        }
        catch { }
    }

    static JsonObject? InnerStatusLine()
    {
        try
        {
            if (File.Exists(Paths.InnerStatusLineFile))
                return JsonNode.Parse(File.ReadAllText(Paths.InnerStatusLineFile)) as JsonObject;
        }
        catch { }
        return null;
    }

    static string? InnerCommand()
    {
        var cmd = InnerStatusLine()?["command"]?.GetValue<string>()?.Trim();
        return string.IsNullOrEmpty(cmd) ? null : cmd;
    }

    static void Forward(string input, string command, JsonObject? rateLimitsFallback)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c " + command)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            var p = Process.Start(psi)!;
            p.StandardInput.Write(input);
            p.StandardInput.Close();
            var outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            Console.Out.Write(outp);
        }
        catch
        {
            Console.Out.Write(DefaultLine(null, rateLimitsFallback));
        }
    }

    static string DefaultLine(JsonObject? root, JsonObject? rateLimits)
    {
        var parts = new List<string>();
        if (root?["model"] is JsonObject model && model["display_name"]?.GetValue<string>() is string name)
            parts.Add(name);
        void Pct(string key, string label)
        {
            if (rateLimits?[key] is JsonObject w && w["used_percentage"] is JsonValue pv && pv.TryGetValue<double>(out var p))
                parts.Add($"{label} {(int)Math.Round(p)}%");
        }
        Pct("five_hour", "5h");
        Pct("seven_day", "7d");
        return string.Join(" · ", parts);
    }

    // ── 安装 / 卸载 ──

    public static void EnsureInstalled()
    {
        // 已安装且命令已指向“当前应注册的可执行文件”才跳过；否则重装。
        // 按正斜杠归一化比较（注册命令现用正斜杠裸路径），避免反斜杠/正斜杠差异导致每次启动都重写。
        bool helperExists = File.Exists(Paths.StatuslineHelperExe);
        var target = (helperExists ? Paths.StatuslineHelperExe : Paths.ExePath).Replace('\\', '/');
        var current = CurrentCommand()?.Replace('\\', '/');
        if (IsInstalled && current is not null && current.Contains(target)) return;
        Install();
    }

    public static string? CurrentCommand()
    {
        try
        {
            if (!File.Exists(Paths.ClaudeSettings)) return null;
            var root = JsonNode.Parse(File.ReadAllText(Paths.ClaudeSettings)) as JsonObject;
            return (root?["statusLine"] as JsonObject)?["command"]?.GetValue<string>();
        }
        catch { return null; }
    }

    public static void Install()
    {
        try { Directory.CreateDirectory(Paths.SupportDir); } catch { }

        JsonObject settings = new();
        if (File.Exists(Paths.ClaudeSettings))
        {
            try
            {
                var text = File.ReadAllText(Paths.ClaudeSettings);
                settings = JsonNode.Parse(text) as JsonObject ?? new JsonObject();
                var backup = Paths.ClaudeSettings + ".claudenotch-bak";
                if (!File.Exists(backup)) File.WriteAllText(backup, text);
            }
            catch { settings = new JsonObject(); }
        }

        if (settings["statusLine"] is JsonObject sl && !ObjectIsOurs(sl))
        {
            try { File.WriteAllText(Paths.InnerStatusLineFile, sl.ToJsonString()); } catch { }
        }

        settings["statusLine"] = new JsonObject
        {
            ["type"] = "command",
            ["command"] = HookCommand(),
            ["padding"] = 0,
            [OwnerMarker] = true,
        };
        WriteSettings(settings);
    }

    public static void Uninstall(bool purgeData = true)
    {
        try
        {
            if (File.Exists(Paths.ClaudeSettings)
                && JsonNode.Parse(File.ReadAllText(Paths.ClaudeSettings)) is JsonObject settings
                && settings["statusLine"] is JsonObject sl && ObjectIsOurs(sl))
            {
                var inner = InnerStatusLine();
                if (inner is not null) settings["statusLine"] = inner.DeepClone();
                else settings.Remove("statusLine");
                WriteSettings(settings);
                if (File.Exists(Paths.InnerStatusLineFile)) File.Delete(Paths.InnerStatusLineFile);
            }
        }
        catch { }
        if (purgeData) { try { if (File.Exists(Paths.RatelimitsFile)) File.Delete(Paths.RatelimitsFile); } catch { } }
    }

    public static bool IsInstalled
    {
        get
        {
            try
            {
                if (!File.Exists(Paths.ClaudeSettings)) return false;
                var settings = JsonNode.Parse(File.ReadAllText(Paths.ClaudeSettings)) as JsonObject;
                return settings?["statusLine"] is JsonObject sl && ObjectIsOurs(sl);
            }
            catch { return false; }
        }
    }

    static void WriteSettings(JsonObject settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Paths.ClaudeSettings)!);
            File.WriteAllText(Paths.ClaudeSettings, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    // ── 诊断（设置页用） ──

    public record Diagnostics(bool Installed, string? Command, string? WrappedInner,
        bool RatelimitsExists, DateTime? CapturedAt, string SettingsPath, string SupportDirPath)
    {
        public string CopyText()
        {
            var lines = new List<string>
            {
                L.Tr("ClaudeNotch 集成诊断", "ClaudeNotch integration diagnostics"),
                L.Tr("已接入: ", "Installed: ") + (Installed ? L.Tr("是", "Yes") : L.Tr("否", "No")),
                L.Tr("statusLine 命令: ", "statusLine command: ") + (Command ?? L.Tr("（无）", "(none)")),
                L.Tr("透传的原命令: ", "Wrapped original command: ") + (WrappedInner ?? L.Tr("（无）", "(none)")),
                L.Tr("ratelimits.json: ", "ratelimits.json: ") + (RatelimitsExists ? L.Tr("存在", "present") : L.Tr("缺失", "missing")),
                CapturedAt is DateTime c
                    ? L.Tr("上次额度数据: ", "Last usage data: ") + c.ToString("yyyy-MM-dd HH:mm:ss")
                    : L.Tr("上次额度数据: 尚无", "Last usage data: none yet"),
                L.Tr("settings.json: ", "settings.json: ") + SettingsPath,
                L.Tr("支持目录: ", "Support directory: ") + SupportDirPath,
            };
            return string.Join("\n", lines);
        }
    }

    public static Diagnostics GetDiagnostics()
    {
        DateTime? captured = null;
        bool exists = File.Exists(Paths.RatelimitsFile);
        if (exists)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(Paths.RatelimitsFile));
                if (doc.RootElement.TryGetProperty("capturedAt", out var t) && t.TryGetDouble(out var secs))
                    captured = DateTimeOffset.FromUnixTimeSeconds((long)secs).LocalDateTime;
            }
            catch { }
        }
        return new Diagnostics(IsInstalled, CurrentCommand(), InnerCommand(), exists, captured,
            Paths.ClaudeSettings, Paths.SupportDir);
    }
}
