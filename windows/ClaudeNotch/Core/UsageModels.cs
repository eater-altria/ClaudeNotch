using System.Text.RegularExpressions;

namespace ClaudeNotch.Core;

public enum UsageLevel { Ok, Warn, Critical }

public static class UsageLevels
{
    public static UsageLevel From(int percentUsed) =>
        percentUsed >= 95 ? UsageLevel.Critical : percentUsed >= 80 ? UsageLevel.Warn : UsageLevel.Ok;

    /// <summary>0xAARRGGBB 便于 UI 构造 Color。</summary>
    public static (byte r, byte g, byte b) Color(this UsageLevel l) => l switch
    {
        UsageLevel.Ok => (77, 212, 115),
        UsageLevel.Warn => (250, 171, 51),
        _ => (242, 77, 77),
    };
}

/// <summary>一个额度指标。</summary>
public sealed class UsageMetric
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public int PercentUsed { get; init; }
    public string? ResetRaw { get; init; }
    public DateTime? ResetAt { get; init; }

    public int PercentRemaining => Math.Max(0, 100 - PercentUsed);
    public UsageLevel Level => UsageLevels.From(PercentUsed);

    public int? ResetMinutesRemaining =>
        ResetAt is DateTime at ? Math.Max(0, (int)((at - DateTime.Now).TotalMinutes)) : ParseRelativeMinutes(ResetRaw);

    public string ResetDisplay
    {
        get
        {
            if (ResetAt is DateTime at)
                return FormatDuration(Math.Max(0, (int)((at - DateTime.Now).TotalMinutes))) + L.Tr("后", " left");
            if (string.IsNullOrEmpty(ResetRaw)) return "—";
            var mins = ParseRelativeMinutes(ResetRaw);
            return mins is int m ? FormatDuration(m) + L.Tr("后", " left") : ResetRaw!;
        }
    }

    static readonly Regex HrRe = new(@"(\d+)\s*hr", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex MinRe = new(@"(\d+)\s*min", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static int? ParseRelativeMinutes(string? raw)
    {
        if (raw is null) return null;
        int? hr = HrRe.Match(raw) is { Success: true } h ? int.Parse(h.Groups[1].Value) : null;
        int? min = MinRe.Match(raw) is { Success: true } mm ? int.Parse(mm.Groups[1].Value) : null;
        if (hr is null && min is null) return null;
        return (hr ?? 0) * 60 + (min ?? 0);
    }

    public static string FormatDuration(int minutes)
    {
        if (minutes <= 0) return L.Tr("即将", "soon");
        int h = minutes / 60, m = minutes % 60;
        if (h > 24) { int d = h / 24; return L.Tr($"{d} 天", $"{d} day" + (d == 1 ? "" : "s")); }
        if (h > 0 && m > 0) return L.Tr($"{h} 小时 {m} 分", $"{h} hr {m} min");
        if (h > 0) return L.Tr($"{h} 小时", $"{h} hr");
        return L.Tr($"{m} 分钟", $"{m} min");
    }

    public static string ShortDuration(int minutes)
    {
        if (minutes <= 0) return "0m";
        int h = minutes / 60;
        if (h >= 24) return $"{h / 24}d";
        if (h > 0) return $"{h}h";
        return $"{minutes}m";
    }
}

/// <summary>从 statusline 钩子归一出的额度快照原料。</summary>
public sealed class ScrapeResult
{
    public int? SessionPercent; public DateTime? SessionResetAt;
    public int? WeeklyAllPercent; public DateTime? WeeklyAllResetAt;
    public int? WeeklySonnetPercent; public DateTime? WeeklySonnetResetAt;
    public DateTime? CapturedAt;
    public double? OfficialCostUSD;
    public string? ModelName;
    public string? CliVersion;
}

public sealed class UsageSnapshot
{
    public UsageMetric? Session, WeeklyAll, WeeklySonnet;
    public double? OfficialCostUSD;
    public string? ModelName, CliVersion;
    public DateTime FetchedAt;

    public UsageMetric? Headline =>
        Session ?? new[] { WeeklyAll, WeeklySonnet }.Where(x => x is not null)
            .OrderByDescending(x => x!.PercentUsed).FirstOrDefault();

    public List<UsageMetric> AllMetrics =>
        new[] { Session, WeeklyAll, WeeklySonnet }.Where(x => x is not null).Select(x => x!).ToList();

    public static UsageSnapshot From(ScrapeResult r, DateTime fetchedAt)
    {
        var s = new UsageSnapshot { FetchedAt = fetchedAt };
        if (r.SessionPercent is int sp)
            s.Session = new UsageMetric { Id = "session", Title = L.Tr("当前会话", "Current session"), PercentUsed = sp, ResetAt = r.SessionResetAt };
        if (r.WeeklyAllPercent is int wp)
            s.WeeklyAll = new UsageMetric { Id = "weeklyAll", Title = L.Tr("本周 · 全模型", "Weekly · All models"), PercentUsed = wp, ResetAt = r.WeeklyAllResetAt };
        if (r.WeeklySonnetPercent is int wsp)
            s.WeeklySonnet = new UsageMetric { Id = "weeklySonnet", Title = L.Tr("本周 · Sonnet", "Weekly · Sonnet"), PercentUsed = wsp, ResetAt = r.WeeklySonnetResetAt };
        s.OfficialCostUSD = r.OfficialCostUSD;
        s.ModelName = r.ModelName;
        s.CliVersion = r.CliVersion;
        return s;
    }
}

/// <summary>消耗速率投影。</summary>
public sealed class BurnEstimator
{
    record struct Sample(DateTime T, int Used);
    readonly List<Sample> _samples = new();
    const int MaxSamples = 12;

    public void Record(int used, DateTime time)
    {
        if (_samples.Count > 0 && used < _samples[^1].Used - 2) _samples.Clear();
        _samples.Add(new Sample(time, used));
        if (_samples.Count > MaxSamples) _samples.RemoveRange(0, _samples.Count - MaxSamples);
    }

    public double? RatePerMinute()
    {
        if (_samples.Count < 2) return null;
        var first = _samples[0]; var last = _samples[^1];
        double dt = (last.T - first.T).TotalMinutes;
        if (dt < 1.0) return null;
        double dUsed = last.Used - first.Used;
        if (dUsed <= 0) return null;
        return dUsed / dt;
    }

    public string Project(int currentUsed, int? resetMinutesRemaining)
    {
        if (currentUsed >= 100) return L.Tr("已用尽", "Exhausted");
        var rate = RatePerMinute();
        if (rate is null || rate <= 0)
            return _samples.Count < 2 ? L.Tr("计算中…", "Calculating…") : L.Tr("无明显消耗", "No notable usage");
        int mins = (int)Math.Round((100 - currentUsed) / rate.Value);
        if (resetMinutesRemaining is int rm && mins >= rm) return L.Tr("刷新前充足", "Enough until reset");
        return UsageMetric.FormatDuration(mins) + L.Tr("后用尽", " to empty");
    }
}
