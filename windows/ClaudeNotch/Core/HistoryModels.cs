using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeNotch.Core;

/// <summary>token 桶（按种类，可加）。</summary>
public struct TokenBuckets
{
    public int Input { get; set; }
    public int Output { get; set; }
    public int CacheRead { get; set; }
    public int CacheWrite5m { get; set; }
    public int CacheWrite1h { get; set; }

    public int Total => Input + Output + CacheRead + CacheWrite5m + CacheWrite1h;
    public int Billable => Input + Output + CacheWrite5m + CacheWrite1h;

    public static TokenBuckets operator +(TokenBuckets a, TokenBuckets b) => new()
    {
        Input = a.Input + b.Input,
        Output = a.Output + b.Output,
        CacheRead = a.CacheRead + b.CacheRead,
        CacheWrite5m = a.CacheWrite5m + b.CacheWrite5m,
        CacheWrite1h = a.CacheWrite1h + b.CacheWrite1h,
    };

    /// <summary>按某模型单价折算 ≈花费（USD）。</summary>
    public readonly double Cost(string model)
    {
        var p = ModelPricing.Lookup(model);
        return (Input * p.Input + Output * p.Output + CacheRead * p.CacheRead
                + CacheWrite5m * p.CacheWrite5m + CacheWrite1h * p.CacheWrite1h) / 1_000_000.0;
    }
}

public enum HeatmapMetric { Billable, Cost, Total }
public enum HistoryRange { M3, M6, M12, All }

public static class HistoryEnums
{
    public static string Label(this HeatmapMetric m) => m switch
    {
        HeatmapMetric.Billable => L.Tr("计费 token", "Billable tokens"),
        HeatmapMetric.Cost => L.Tr("≈ 花费", "≈ Cost"),
        _ => L.Tr("总 token", "Total tokens"),
    };
    public static string Label(this HistoryRange r) => r switch
    {
        HistoryRange.M3 => L.Tr("3 个月", "3 mo"),
        HistoryRange.M6 => L.Tr("6 个月", "6 mo"),
        HistoryRange.M12 => L.Tr("12 个月", "12 mo"),
        _ => L.Tr("全部", "All"),
    };
    public static DateTime? StartDate(this HistoryRange r, DateTime now) => r switch
    {
        HistoryRange.M3 => now.AddMonths(-3),
        HistoryRange.M6 => now.AddMonths(-6),
        HistoryRange.M12 => now.AddMonths(-12),
        _ => null,
    };
}

/// <summary>本地日键 yyyymmdd。</summary>
public static class DayKey
{
    public static int From(DateTime d) => d.Year * 10000 + d.Month * 100 + d.Day;
    public static DateTime? ToDate(int day)
    {
        try { return new DateTime(day / 10000, (day / 100) % 100, day % 100); }
        catch { return null; }
    }
}

/// <summary>单日统计。</summary>
public sealed class DayStat
{
    public TokenBuckets Tokens { get; set; }
    public double Cost { get; set; }
    public int MessageCount { get; set; }
    public Dictionary<string, TokenBuckets> PerModel { get; set; } = new();
    public Dictionary<string, int> PerProject { get; set; } = new();
    public Dictionary<int, int> ByHour { get; set; } = new();

    public void Add(TokenBuckets t, string model, string project, int hour)
    {
        Tokens += t;
        Cost += t.Cost(model);
        MessageCount++;
        PerModel[model] = (PerModel.TryGetValue(model, out var pm) ? pm : default) + t;
        PerProject[project] = (PerProject.TryGetValue(project, out var pp) ? pp : 0) + t.Billable;
        ByHour[hour] = (ByHour.TryGetValue(hour, out var bh) ? bh : 0) + t.Billable;
    }

    public void Merge(DayStat o)
    {
        Tokens += o.Tokens;
        Cost += o.Cost;
        MessageCount += o.MessageCount;
        foreach (var (k, v) in o.PerModel) PerModel[k] = (PerModel.TryGetValue(k, out var e) ? e : default) + v;
        foreach (var (k, v) in o.PerProject) PerProject[k] = (PerProject.TryGetValue(k, out var e) ? e : 0) + v;
        foreach (var (k, v) in o.ByHour) ByHour[k] = (ByHour.TryGetValue(k, out var e) ? e : 0) + v;
    }

    public double MetricValue(HeatmapMetric m) => m switch
    {
        HeatmapMetric.Billable => Tokens.Billable,
        HeatmapMetric.Cost => Cost,
        _ => Tokens.Total,
    };
}

/// <summary>整段历史。</summary>
public sealed class UsageHistory
{
    public Dictionary<int, DayStat> Days { get; set; } = new();
    public DateTime LastBuiltAt { get; set; } = DateTime.MinValue;

    public DayStat Aggregate(IEnumerable<int> keys)
    {
        var acc = new DayStat();
        foreach (var k in keys) if (Days.TryGetValue(k, out var s)) acc.Merge(s);
        return acc;
    }

    public List<int> ActiveDayKeys => Days.Where(kv => kv.Value.MessageCount > 0).Select(kv => kv.Key).OrderBy(x => x).ToList();
    public DayStat Lifetime => Aggregate(Days.Keys);
    public DayStat Today() => Days.TryGetValue(DayKey.From(DateTime.Now), out var s) ? s : new DayStat();

    public DayStat Recent(int n)
    {
        var cutoff = DateTime.Today.AddDays(-(n - 1));
        int cutKey = DayKey.From(cutoff);
        return Aggregate(Days.Keys.Where(k => k >= cutKey));
    }

    public List<int> DayKeysIn(HistoryRange range)
    {
        var start = range.StartDate(DateTime.Now);
        if (start is null) return Days.Keys.OrderBy(x => x).ToList();
        int s = DayKey.From(start.Value);
        return Days.Keys.Where(k => k >= s).OrderBy(x => x).ToList();
    }

    /// <summary>连续活跃天数（含今天或昨天起算）、最长连续、最忙一天（按某指标）。</summary>
    public (int current, int longest, (int day, double value)? busiest) Streaks(HeatmapMetric metric)
    {
        var active = new HashSet<int>(ActiveDayKeys);
        if (active.Count == 0) return (0, 0, null);
        var sorted = active.OrderBy(x => x).ToList();

        int longest = 1, run = 1;
        for (int i = 1; i < sorted.Count; i++)
        {
            if (DayKey.ToDate(sorted[i - 1]) is DateTime pd && DayKey.ToDate(sorted[i]) is DateTime cd
                && pd.Date.AddDays(1) == cd.Date) { run++; longest = Math.Max(longest, run); }
            else run = 1;
        }

        int current = 0;
        var cursor = DateTime.Today;
        if (!active.Contains(DayKey.From(cursor)))
        {
            cursor = cursor.AddDays(-1);
            if (!active.Contains(DayKey.From(cursor))) { current = 0; cursor = DateTime.Today; }
        }
        while (active.Contains(DayKey.From(cursor))) { current++; cursor = cursor.AddDays(-1); }

        (int day, double value)? busiest = null;
        foreach (var d in active)
        {
            double v = Days.TryGetValue(d, out var s) ? s.MetricValue(metric) : 0;
            if (busiest is null || v > busiest.Value.value) busiest = (d, v);
        }
        return (current, longest, busiest);
    }
}

/// <summary>一行 transcript 的解析结果。</summary>
public readonly record struct ParsedUsageLine(
    string MessageId, string? TimestampRaw, string Model, string Cwd, string SessionId, string? GitBranch, TokenBuckets Tokens)
{
    public int ContextTokens => Tokens.Input + Tokens.CacheRead + Tokens.CacheWrite5m + Tokens.CacheWrite1h;
}

public static class TranscriptParser
{
    static int JInt(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    static string? JStr(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>仅 type=assistant 且带 usage 的行返回非 null。按 messageId 去重 + 按模型计价的唯一真源。</summary>
    public static ParsedUsageLine? ParseAssistantUsageLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line[0] != '{') return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var o = doc.RootElement;
            if (o.ValueKind != JsonValueKind.Object) return null;
            if (JStr(o, "type") != "assistant") return null;
            if (!o.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) return null;
            if (!msg.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;

            int cw5 = 0, cw1h = 0;
            if (usage.TryGetProperty("cache_creation", out var cc) && cc.ValueKind == JsonValueKind.Object)
            {
                cw5 = JInt(cc, "ephemeral_5m_input_tokens");
                cw1h = JInt(cc, "ephemeral_1h_input_tokens");
            }
            else cw5 = JInt(usage, "cache_creation_input_tokens");

            var tokens = new TokenBuckets
            {
                Input = JInt(usage, "input_tokens"),
                Output = JInt(usage, "output_tokens"),
                CacheRead = JInt(usage, "cache_read_input_tokens"),
                CacheWrite5m = cw5,
                CacheWrite1h = cw1h,
            };

            return new ParsedUsageLine(
                MessageId: JStr(msg, "id") ?? JStr(o, "uuid") ?? "",
                TimestampRaw: JStr(o, "timestamp"),
                Model: JStr(msg, "model") ?? "",
                Cwd: JStr(o, "cwd") ?? "",
                SessionId: JStr(o, "sessionId") ?? "",
                GitBranch: JStr(o, "gitBranch"),
                Tokens: tokens);
        }
        catch { return null; }
    }

    static readonly Regex VerRe = new(@"\d+-\d+", RegexOptions.Compiled);

    public static string ShortModelName(string model)
    {
        var m = model.ToLowerInvariant();
        var match = VerRe.Match(model);
        var v = match.Success ? match.Value.Replace('-', '.') : "";
        if (m.Contains("opus")) return ("Opus " + v).Trim();
        if (m.Contains("sonnet")) return ("Sonnet " + v).Trim();
        if (m.Contains("haiku")) return ("Haiku " + v).Trim();
        return model;
    }

    public static bool IsSyntheticModel(string model) => string.IsNullOrEmpty(model) || model == "<synthetic>" || model == "?";

    public static bool IsApproxPriced(string model)
    {
        if (PriceCatalog.Shared.Match(model) is not null) return false;
        var l = model.ToLowerInvariant();
        return !(l.Contains("opus") || l.Contains("sonnet") || l.Contains("haiku"));
    }

    public static string TokensShort(int n)
    {
        if (n >= 1_000_000) return (n / 1_000_000.0).ToString("0.#") + "M";
        if (n >= 1_000) return (n / 1_000.0).ToString("0.#") + "k";
        return n.ToString();
    }
}
