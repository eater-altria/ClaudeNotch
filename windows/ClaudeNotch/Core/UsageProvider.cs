using System.Text.Json;

namespace ClaudeNotch.Core;

public abstract class FetchOutcome
{
    public sealed class Success : FetchOutcome { public required ScrapeResult Result; }
    public sealed class Failure : FetchOutcome { public required string Message; }
}

/// <summary>从 Claude Code 的 statusLine 钩子落盘的 ratelimits.json 读取额度——唯一来源。</summary>
public static class StatuslineProvider
{
    public static FetchOutcome FetchUsage()
    {
        if (!File.Exists(Paths.RatelimitsFile))
            return new FetchOutcome.Failure { Message = L.Tr("尚未收到 Claude Code 状态栏数据（在任意终端跑一次 claude 即可）",
                "No status bar data from Claude Code yet (run claude once in any terminal)") };

        JsonDocument doc;
        try { doc = JsonDocument.Parse(File.ReadAllText(Paths.RatelimitsFile)); }
        catch { return new FetchOutcome.Failure { Message = L.Tr("状态栏数据解析失败", "Failed to parse status bar data") }; }

        using (doc)
        {
            var obj = doc.RootElement;
            if (!obj.TryGetProperty("rate_limits", out var rl) || rl.ValueKind != JsonValueKind.Object)
                return new FetchOutcome.Failure { Message = L.Tr("状态栏数据缺少 rate_limits 字段", "Status bar data is missing rate_limits") };

            (int percent, DateTime? at)? Window(string key)
            {
                if (rl.TryGetProperty(key, out var w) && w.ValueKind == JsonValueKind.Object
                    && w.TryGetProperty("used_percentage", out var up) && up.TryGetDouble(out var used))
                {
                    DateTime? at = null;
                    if (w.TryGetProperty("resets_at", out var ra) && ra.TryGetDouble(out var secs))
                        at = DateTimeOffset.FromUnixTimeSeconds((long)secs).LocalDateTime;
                    return (Math.Max(0, Math.Min(100, (int)Math.Round(used))), at);
                }
                return null;
            }

            var r = new ScrapeResult();
            if (obj.TryGetProperty("capturedAt", out var c) && c.TryGetDouble(out var cs))
                r.CapturedAt = DateTimeOffset.FromUnixTimeSeconds((long)cs).LocalDateTime;
            if (Window("five_hour") is { } s) { r.SessionPercent = s.percent; r.SessionResetAt = s.at; }
            if (Window("seven_day") is { } w2) { r.WeeklyAllPercent = w2.percent; r.WeeklyAllResetAt = w2.at; }
            if (Window("seven_day_sonnet") is { } so) { r.WeeklySonnetPercent = so.percent; r.WeeklySonnetResetAt = so.at; }

            if (obj.TryGetProperty("cost", out var cost) && cost.ValueKind == JsonValueKind.Object
                && cost.TryGetProperty("total_cost_usd", out var tc) && tc.TryGetDouble(out var cv))
                r.OfficialCostUSD = cv;
            if (obj.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.Object)
                r.ModelName = (model.TryGetProperty("display_name", out var dn) ? dn.GetString() : null)
                              ?? (model.TryGetProperty("id", out var mid) ? mid.GetString() : null);
            if (obj.TryGetProperty("version", out var ver) && ver.ValueKind == JsonValueKind.String)
                r.CliVersion = ver.GetString();

            if (r.SessionPercent is null && r.WeeklyAllPercent is null)
                return new FetchOutcome.Failure { Message = L.Tr("状态栏数据暂无额度字段", "Status bar data has no quota fields yet") };

            return new FetchOutcome.Success { Result = r };
        }
    }
}
