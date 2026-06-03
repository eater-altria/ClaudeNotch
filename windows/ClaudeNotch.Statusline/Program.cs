using System.Diagnostics;
using System.Text.Json;

namespace ClaudeNotch.Statusline;

/// <summary>
/// Claude Code statusLine 助手（控制台子系统，AOT）。
/// 读 stdin（Claude Code 喂的 JSON，含 rate_limits）→ 落盘 ratelimits.json → 透传用户原 statusline 命令并输出其结果。
/// 全程不读取/复用任何令牌；只写本 app 的支持目录。AOT 安全：仅用 JsonDocument / Utf8JsonWriter（无反射）。
/// </summary>
internal static class Program
{
    static string SupportDir
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeNotch");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
    static string RatelimitsFile => Path.Combine(SupportDir, "ratelimits.json");
    static string InnerStatusLineFile => Path.Combine(SupportDir, "inner-statusline.json");

    static int Main()
    {
        byte[] input;
        try
        {
            using var stdin = Console.OpenStandardInput();
            using var mem = new MemoryStream();
            stdin.CopyTo(mem);
            input = mem.ToArray();
        }
        catch { input = Array.Empty<byte>(); }

        JsonDocument? doc = null;
        try { if (input.Length > 0) doc = JsonDocument.Parse(input); } catch { }

        try
        {
            if (doc is not null && doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("rate_limits", out var rl) && rl.ValueKind == JsonValueKind.Object)
            {
                Persist(doc.RootElement, rl);
            }
        }
        catch { /* 落盘失败不影响透传 */ }

        var inner = InnerCommand();
        if (inner is not null) Forward(input, inner, doc);
        else Console.Out.Write(DefaultLine(doc));
        return 0;
    }

    static void Persist(JsonElement root, JsonElement rateLimits)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("capturedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            w.WritePropertyName("rate_limits"); rateLimits.WriteTo(w);
            foreach (var key in new[] { "cost", "model", "version", "workspace", "output_style", "session_id" })
                if (root.TryGetProperty(key, out var el)) { w.WritePropertyName(key); el.WriteTo(w); }
            w.WriteEndObject();
        }
        var tmp = RatelimitsFile + ".tmp";
        File.WriteAllBytes(tmp, ms.ToArray());
        File.Move(tmp, RatelimitsFile, overwrite: true);   // 原子替换
    }

    static string? InnerCommand()
    {
        try
        {
            if (!File.Exists(InnerStatusLineFile)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(InnerStatusLineFile));
            if (doc.RootElement.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String)
            {
                var s = c.GetString()?.Trim();
                return string.IsNullOrEmpty(s) ? null : s;
            }
        }
        catch { }
        return null;
    }

    static void Forward(byte[] input, string command, JsonDocument? fallback)
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
            using (var s = p.StandardInput.BaseStream) { s.Write(input, 0, input.Length); }
            var outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            Console.Out.Write(outp);
        }
        catch { Console.Out.Write(DefaultLine(fallback)); }
    }

    static string DefaultLine(JsonDocument? doc)
    {
        if (doc is null) return "";
        var root = doc.RootElement;
        var parts = new List<string>();
        if (root.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.Object
            && model.TryGetProperty("display_name", out var dn) && dn.ValueKind == JsonValueKind.String)
            parts.Add(dn.GetString()!);
        if (root.TryGetProperty("rate_limits", out var rl) && rl.ValueKind == JsonValueKind.Object)
        {
            void Pct(string key, string label)
            {
                if (rl.TryGetProperty(key, out var w) && w.ValueKind == JsonValueKind.Object
                    && w.TryGetProperty("used_percentage", out var up) && up.TryGetDouble(out var p))
                    parts.Add($"{label} {(int)Math.Round(p)}%");
            }
            Pct("five_hour", "5h");
            Pct("seven_day", "7d");
        }
        return string.Join(" · ", parts);
    }
}
