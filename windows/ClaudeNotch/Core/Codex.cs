using System.Globalization;
using System.Text.Json;

namespace ClaudeNotch.Core;

/// <summary>受支持的 CLI 代理。两者数据来路不同，但落到同一套额度/会话/历史模型上。</summary>
public enum AgentKind { ClaudeCode, Codex }

public static class AgentKinds
{
    public static string DisplayName(this AgentKind a) => a == AgentKind.Codex ? "Codex" : "Claude Code";
    public static string CliName(this AgentKind a) => a == AgentKind.Codex ? "codex" : "claude";
    public static AgentKind Parse(string? s) =>
        string.Equals(s, "codex", StringComparison.OrdinalIgnoreCase) ? AgentKind.Codex : AgentKind.ClaudeCode;
    public static string Save(this AgentKind a) => a == AgentKind.Codex ? "codex" : "claudeCode";
}

/// <summary>当前选中的代理（从设置写入，扫描线程只读）。默认 Claude Code。</summary>
public static class AgentContext
{
    static int _current = (int)AgentKind.ClaudeCode;
    public static AgentKind Current
    {
        get => (AgentKind)System.Threading.Volatile.Read(ref _current);
        set => System.Threading.Volatile.Write(ref _current, (int)value);
    }
}

/// <summary>OpenAI Codex 数据目录（CODEX_HOME 优先，否则 %USERPROFILE%\.codex）。</summary>
public static class CodexPaths
{
    public static string Home
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (!string.IsNullOrEmpty(env)) return env;
            return Path.Combine(Paths.Home, ".codex");
        }
    }
    public static string SessionsDir => Path.Combine(Home, "sessions");

    /// <summary>枚举 sessions/**/rollout-*.jsonl，带 mtime 与大小。</summary>
    public static List<(string path, DateTime mtime, long size)> AllSessionFiles()
    {
        var outp = new List<(string, DateTime, long)>();
        if (!Directory.Exists(SessionsDir)) return outp;
        IEnumerable<string> all;
        try { all = Directory.EnumerateFiles(SessionsDir, "rollout-*.jsonl", SearchOption.AllDirectories); }
        catch { return outp; }
        foreach (var path in all)
        {
            try { var fi = new FileInfo(path); outp.Add((path, fi.LastWriteTime, fi.Length)); }
            catch { }
        }
        return outp;
    }
}

/// <summary>一条 Codex rollout 行的归一结果。</summary>
public struct CodexLine
{
    public enum Kind { Meta, TurnContext, TokenCount, Other }
    public Kind Type;
    public string? TimestampRaw;
    public string? Cwd;
    public string? SessionId;
    public string? Model;
    public TokenBuckets? LastUsage;     // token_count.info.last_token_usage（每轮增量）
    public int ContextWindow;           // token_count.info.model_context_window
    public bool HasRateLimits;
    public (int percent, DateTime? at, int minutes)? Primary;
    public (int percent, DateTime? at, int minutes)? Secondary;
    public string? PlanType;
    public bool LastUsageInputTotal;    // last_token_usage.input_tokens（含缓存）——供上下文占用
    public int LastInputTokens;
}

public static class CodexParser
{
    // 注意:JsonElement.TryGetProperty 在非 Object(含 default/Undefined)上会抛异常，故先判类型。
    static int JInt(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
    static double? JNum(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;
    static string? JStr(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>OpenAI input_tokens 含缓存命中：非缓存输入 = input - cached，缓存读 = cached。</summary>
    static TokenBuckets Buckets(JsonElement usage)
    {
        int input = JInt(usage, "input_tokens");
        int cached = JInt(usage, "cached_input_tokens");
        int output = JInt(usage, "output_tokens");
        return new TokenBuckets { Input = Math.Max(0, input - cached), Output = output, CacheRead = cached };
    }

    static (int percent, DateTime? at, int minutes)? Window(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object) return null;
        var used = JNum(w, "used_percent");
        if (used is null) return null;
        DateTime? at = JNum(w, "resets_at") is double s ? DateTimeOffset.FromUnixTimeSeconds((long)s).LocalDateTime : null;
        return ((int)Math.Max(0, Math.Min(100, Math.Round(used.Value))), at, JInt(w, "window_minutes"));
    }

    public static CodexLine? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line[0] != '{') return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var o = doc.RootElement;
            if (o.ValueKind != JsonValueKind.Object) return null;
            var type = JStr(o, "type");
            var ts = JStr(o, "timestamp");
            JsonElement payload = o.TryGetProperty("payload", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;

            switch (type)
            {
                case "session_meta":
                    return new CodexLine { Type = CodexLine.Kind.Meta, TimestampRaw = ts,
                        Cwd = JStr(payload, "cwd"), SessionId = JStr(payload, "id") };
                case "turn_context":
                    return new CodexLine { Type = CodexLine.Kind.TurnContext, TimestampRaw = ts,
                        Model = JStr(payload, "model"), Cwd = JStr(payload, "cwd") };
                case "event_msg":
                    if (JStr(payload, "type") != "token_count")
                        return new CodexLine { Type = CodexLine.Kind.Other, TimestampRaw = ts };
                    var l = new CodexLine { Type = CodexLine.Kind.TokenCount, TimestampRaw = ts };
                    if (payload.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object)
                    {
                        if (info.TryGetProperty("last_token_usage", out var last) && last.ValueKind == JsonValueKind.Object)
                        {
                            l.LastUsage = Buckets(last);
                            l.LastInputTokens = JInt(last, "input_tokens");
                            l.LastUsageInputTotal = true;
                        }
                        l.ContextWindow = JInt(info, "model_context_window");
                    }
                    if (payload.TryGetProperty("rate_limits", out var rl) && rl.ValueKind == JsonValueKind.Object)
                    {
                        l.HasRateLimits = true;
                        l.Primary = Window(rl, "primary");
                        l.Secondary = Window(rl, "secondary");
                        l.PlanType = JStr(rl, "plan_type");
                    }
                    return l;
                default:
                    return new CodexLine { Type = CodexLine.Kind.Other, TimestampRaw = ts };
            }
        }
        catch { return null; }
    }
}

/// <summary>从最近写入的 Codex 会话文件取 rate_limits（Codex 无 statusLine 钩子，额度内嵌会话 JSONL）。</summary>
public static class CodexUsageProvider
{
    public static FetchOutcome FetchUsage()
    {
        var files = CodexPaths.AllSessionFiles().OrderByDescending(f => f.mtime).ToList();
        if (files.Count == 0)
            return new FetchOutcome.Failure { Message = L.Tr("尚未发现 Codex 会话（在任意终端跑一次 codex 即可）",
                "No Codex sessions found yet (run codex once in any terminal)") };

        CodexLine? found = null;
        DateTime foundAt = default;
        foreach (var f in files.Take(6))
        {
            string[] lines;
            try { lines = File.ReadAllLines(f.path); } catch { continue; }
            foreach (var line in lines)
            {
                var l = CodexParser.Parse(line);
                if (l is { Type: CodexLine.Kind.TokenCount, HasRateLimits: true } cl && (cl.Primary is not null || cl.Secondary is not null))
                { found = cl; foundAt = f.mtime; }
            }
            if (found is not null) break;
        }
        if (found is null)
            return new FetchOutcome.Failure { Message = L.Tr("Codex 会话里暂无额度信息（多跑几轮 codex）",
                "No quota info in Codex sessions yet (run codex a few more turns)") };

        var snap = found.Value;
        var wins = new List<(int percent, DateTime? at, int minutes)>();
        if (snap.Primary is { } pr) wins.Add(pr);
        if (snap.Secondary is { } se) wins.Add(se);
        wins = wins.OrderBy(w => w.minutes).ToList();

        var r = new ScrapeResult { CapturedAt = foundAt };
        if (wins.Count > 0) { r.SessionPercent = wins[0].percent; r.SessionResetAt = wins[0].at; }
        if (wins.Count > 1) { r.WeeklyAllPercent = wins[1].percent; r.WeeklyAllResetAt = wins[1].at; }
        if (!string.IsNullOrEmpty(snap.PlanType)) r.ModelName = snap.PlanType;
        return new FetchOutcome.Success { Result = r };
    }
}

/// <summary>Codex 活跃会话扫描（mtime 近期 = 活跃；Codex 进程 cwd 难取，与 Claude/Windows 同策略）。</summary>
public sealed class CodexSessionScanner
{
    public TimeSpan ActiveWindow { get; set; } = TimeSpan.FromMinutes(10);
    readonly Dictionary<string, (DateTime mtime, SessionInfo? info)> _cache = new();

    public List<SessionInfo> Scan()
    {
        var cutoff = DateTime.Now - ActiveWindow;
        var result = new List<SessionInfo>();
        foreach (var f in CodexPaths.AllSessionFiles())
        {
            if (f.mtime < cutoff) continue;
            SessionInfo? info;
            if (_cache.TryGetValue(f.path, out var cached) && cached.mtime == f.mtime) info = cached.info;
            else { info = Parse(f.path, f.mtime); _cache[f.path] = (f.mtime, info); }
            if (info is not null) result.Add(info);
        }
        return result.OrderByDescending(s => s.LastActivity).ToList();
    }

    static SessionInfo? Parse(string file, DateTime mtime)
    {
        string[] lines;
        try { lines = File.ReadAllLines(file); } catch { return null; }
        string cwd = "", sid = "", model = "";
        double cost = 0;
        int lastCtx = 0, peakCtx = 0, ctxWindow = 0;
        bool sawUsage = false;
        foreach (var line in lines)
        {
            var lp = CodexParser.Parse(line);
            if (lp is null) continue;
            var l = lp.Value;
            switch (l.Type)
            {
                case CodexLine.Kind.Meta:
                    if (!string.IsNullOrEmpty(l.Cwd)) cwd = l.Cwd!;
                    if (!string.IsNullOrEmpty(l.SessionId)) sid = l.SessionId!;
                    break;
                case CodexLine.Kind.TurnContext:
                    if (!string.IsNullOrEmpty(l.Model)) model = l.Model!;
                    if (string.IsNullOrEmpty(cwd) && !string.IsNullOrEmpty(l.Cwd)) cwd = l.Cwd!;
                    break;
                case CodexLine.Kind.TokenCount:
                    if (l.LastUsage is { } b)
                    {
                        cost += b.Cost(model);
                        int ctx = b.Input + b.CacheRead;
                        if (ctx > 0) { lastCtx = ctx; peakCtx = Math.Max(peakCtx, ctx); }
                        sawUsage = true;
                    }
                    if (l.ContextWindow > 0) ctxWindow = l.ContextWindow;
                    break;
            }
        }
        if (!sawUsage && string.IsNullOrEmpty(sid)) return null;

        int window = ctxWindow > 0 ? ctxWindow : (lastCtx > 200_000 ? 400_000 : 272_000);
        var name = string.IsNullOrEmpty(cwd) ? "(unknown)" : LastSeg(cwd);
        return new SessionInfo
        {
            Id = string.IsNullOrEmpty(sid) ? Path.GetFileName(file) : sid,
            ProjectName = name, Cwd = cwd, GitBranch = null,
            Model = string.IsNullOrEmpty(model) ? "gpt-5-codex" : model,
            CostUSD = cost, ContextTokens = lastCtx, PeakContextTokens = peakCtx,
            ContextWindow = window, LastActivity = mtime,
        };
    }

    static string LastSeg(string p)
    {
        var t = p.Replace('/', '\\').TrimEnd('\\');
        int i = t.LastIndexOf('\\');
        return i >= 0 ? t[(i + 1)..] : t;
    }
}

/// <summary>Codex 历史聚合：逐文件跟踪当前模型(turn_context)，把每条 token_count 的 last_token_usage 按天计入。</summary>
public static class CodexHistory
{
    public static FileContribution Contribution(string path)
    {
        var contrib = new FileContribution();
        string[] lines;
        try { lines = File.ReadAllLines(path); } catch { return contrib; }
        string model = "gpt-5-codex", cwd = "";
        foreach (var line in lines)
        {
            var lp = CodexParser.Parse(line);
            if (lp is null) continue;
            var l = lp.Value;
            switch (l.Type)
            {
                case CodexLine.Kind.Meta:
                    if (!string.IsNullOrEmpty(l.Cwd)) cwd = l.Cwd!;
                    break;
                case CodexLine.Kind.TurnContext:
                    if (!string.IsNullOrEmpty(l.Model)) model = l.Model!;
                    if (string.IsNullOrEmpty(cwd) && !string.IsNullOrEmpty(l.Cwd)) cwd = l.Cwd!;
                    break;
                case CodexLine.Kind.TokenCount:
                    if (l.LastUsage is not { } b || b.Total <= 0) break;
                    if (l.TimestampRaw is null || !TryParseIso(l.TimestampRaw, out var date)) break;
                    int day = DayKey.From(date);
                    var project = string.IsNullOrEmpty(cwd) ? "(unknown)" : LastSeg(cwd);
                    if (!contrib.Days.TryGetValue(day, out var ds)) { ds = new DayStat(); contrib.Days[day] = ds; }
                    ds.Add(b, model, project, date.Hour);
                    break;
            }
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
}
