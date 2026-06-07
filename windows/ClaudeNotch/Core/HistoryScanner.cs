using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeNotch.Core;

public sealed class FileContribution
{
    public Dictionary<int, DayStat> Days { get; set; } = new();
}

public sealed class HistoryCache
{
    public const int CurrentVersion = 1;
    public int Version { get; set; } = CurrentVersion;
    public Dictionary<string, FileContribution> Files { get; set; } = new();
}

/// <summary>
/// 扫 ~/.claude/projects/**/*.jsonl（含 subagents/**），按本地日聚合 token/花费。
/// 增量：按 (path, mtime, size) 缓存每文件「每天贡献」到 SupportDir\usage-history.json，未变文件复用。
/// </summary>
public static class HistoryScanner
{
    static readonly JsonSerializerOptions CacheOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>历史缓存按代理分文件，避免 Claude/Codex 互相污染。</summary>
    static string CacheFile => AgentContext.Current == AgentKind.Codex
        ? Path.Combine(Paths.SupportDir, "usage-history-codex.json")
        : Paths.HistoryCacheFile;

    public static UsageHistory Build(Action<double>? progress = null)
    {
        var codex = AgentContext.Current == AgentKind.Codex;
        var cache = LoadCache();
        var files = codex
            ? CodexPaths.AllSessionFiles()
            : AllTranscriptFiles();
        var fresh = new Dictionary<string, FileContribution>();

        int total = Math.Max(1, files.Count);
        for (int i = 0; i < files.Count; i++)
        {
            var f = files[i];
            var key = FileKey(f.path, f.mtime, f.size);
            if (cache.Files.TryGetValue(key, out var cached)) fresh[key] = cached;
            else fresh[key] = codex ? CodexHistory.Contribution(f.path) : Contribution(f.path);
            if (i % 8 == 0 || i == files.Count - 1) progress?.Invoke((double)(i + 1) / total);
        }

        cache.Files = fresh;
        cache.Version = HistoryCache.CurrentVersion;
        SaveCache(cache);

        var history = new UsageHistory();
        foreach (var contrib in fresh.Values)
            foreach (var (day, stat) in contrib.Days)
            {
                if (!history.Days.TryGetValue(day, out var d)) { d = new DayStat(); history.Days[day] = d; }
                d.Merge(stat);
            }
        history.LastBuiltAt = DateTime.Now;
        return history;
    }

    static List<(string path, DateTime mtime, long size)> AllTranscriptFiles()
    {
        var outp = new List<(string, DateTime, long)>();
        if (!Directory.Exists(Paths.ProjectsDir)) return outp;
        IEnumerable<string> all;
        try { all = Directory.EnumerateFiles(Paths.ProjectsDir, "*.jsonl", SearchOption.AllDirectories); }
        catch { return outp; }
        foreach (var path in all)
        {
            try { var fi = new FileInfo(path); outp.Add((path, fi.LastWriteTime, fi.Length)); }
            catch { }
        }
        return outp;
    }

    static string FileKey(string path, DateTime mtime, long size) =>
        $"{path}|{new DateTimeOffset(mtime).ToUnixTimeMilliseconds()}|{size}";

    static FileContribution Contribution(string path)
    {
        var contrib = new FileContribution();
        string[] lines;
        try { lines = File.ReadAllLines(path); } catch { return contrib; }
        var seen = new HashSet<string>();
        foreach (var line in lines)
        {
            var p = TranscriptParser.ParseAssistantUsageLine(line);
            if (p is null) continue;
            var v = p.Value;
            if (!string.IsNullOrEmpty(v.MessageId) && !seen.Add(v.MessageId)) continue;
            if (v.TimestampRaw is null || !TryParseIso(v.TimestampRaw, out var date)) continue;
            int day = DayKey.From(date);
            int hour = date.Hour;
            var project = string.IsNullOrEmpty(v.Cwd) ? "(unknown)" : LastSeg(v.Cwd);
            if (!contrib.Days.TryGetValue(day, out var ds)) { ds = new DayStat(); contrib.Days[day] = ds; }
            ds.Add(v.Tokens, v.Model, project, hour);
        }
        return contrib;
    }

    static bool TryParseIso(string s, out DateTime date) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out date)
        && (date = date.ToLocalTime()) != default;

    static string LastSeg(string p)
    {
        var t = p.Replace('/', '\\').TrimEnd('\\');
        int i = t.LastIndexOf('\\');
        return i >= 0 ? t[(i + 1)..] : t;
    }

    static HistoryCache LoadCache()
    {
        try
        {
            if (File.Exists(CacheFile))
            {
                var c = JsonSerializer.Deserialize<HistoryCache>(File.ReadAllText(CacheFile), CacheOpts);
                if (c is not null && c.Version == HistoryCache.CurrentVersion) return c;
            }
        }
        catch { }
        return new HistoryCache();
    }

    static void SaveCache(HistoryCache c)
    {
        try
        {
            Directory.CreateDirectory(Paths.SupportDir);
            File.WriteAllText(CacheFile, JsonSerializer.Serialize(c, CacheOpts));
        }
        catch { }
    }
}
