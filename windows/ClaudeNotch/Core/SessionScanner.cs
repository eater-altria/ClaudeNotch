namespace ClaudeNotch.Core;

/// <summary>
/// 扫 %USERPROFILE%\.claude\projects\*\&lt;uuid&gt;.jsonl，解析活跃会话的花费与上下文。
/// Windows 无法可靠取进程 cwd，故「活跃」以 transcript 近期写入(mtime 在 ActiveWindow 内)判定。
/// 按 (路径, mtime) 缓存，避免重复解析大文件。约定后台调用。
/// </summary>
public sealed class SessionScanner
{
    public TimeSpan ActiveWindow { get; set; } = TimeSpan.FromMinutes(8);

    readonly Dictionary<string, (DateTime mtime, SessionInfo? info)> _cache = new();

    public List<SessionInfo> Scan()
    {
        var dir = Paths.ProjectsDir;
        if (!Directory.Exists(dir)) return new();
        var cutoff = DateTime.Now - ActiveWindow;
        var result = new List<SessionInfo>();

        foreach (var projDir in SafeDirs(dir))
        {
            foreach (var file in SafeFiles(projDir, "*.jsonl"))
            {
                DateTime mtime;
                try { mtime = File.GetLastWriteTime(file); } catch { continue; }
                if (mtime < cutoff) continue;

                SessionInfo? info;
                if (_cache.TryGetValue(file, out var cached) && cached.mtime == mtime)
                    info = cached.info;
                else
                {
                    info = Parse(file, mtime);
                    _cache[file] = (mtime, info);
                }
                if (info is not null) result.Add(info);
            }
        }
        return result.OrderByDescending(s => s.LastActivity).ToList();
    }

    static IEnumerable<string> SafeDirs(string root)
    {
        try { return Directory.EnumerateDirectories(root); } catch { return Array.Empty<string>(); }
    }
    static IEnumerable<string> SafeFiles(string dir, string pattern)
    {
        try { return Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly); }
        catch { return Array.Empty<string>(); }
    }

    sealed class FileParse
    {
        public double Cost;
        public string LastModel = "";
        public int LastCtx, PeakCtx;
        public string Sid = "", Cwd = "";
        public string? Branch;
        public bool SawAssistant;
    }

    static FileParse? ParseFile(string path)
    {
        string[] lines;
        try { lines = File.ReadAllLines(path); } catch { return null; }
        var r = new FileParse();
        var seen = new HashSet<string>();
        foreach (var line in lines)
        {
            var p = TranscriptParser.ParseAssistantUsageLine(line);
            if (p is null) continue;
            var v = p.Value;
            if (!string.IsNullOrEmpty(v.Model)) r.LastModel = v.Model;
            if (!string.IsNullOrEmpty(v.SessionId)) r.Sid = v.SessionId;
            if (!string.IsNullOrEmpty(v.Cwd)) r.Cwd = v.Cwd;
            if (v.GitBranch is not null) r.Branch = v.GitBranch;
            r.LastCtx = v.ContextTokens;
            r.PeakCtx = Math.Max(r.PeakCtx, v.ContextTokens);

            if (!seen.Add(v.MessageId)) continue;
            r.SawAssistant = true;
            r.Cost += v.Tokens.Cost(v.Model);
        }
        return r.SawAssistant ? r : null;
    }

    static SessionInfo? Parse(string file, DateTime mtime)
    {
        var main = ParseFile(file);
        if (main is null || (string.IsNullOrEmpty(main.Cwd) && string.IsNullOrEmpty(main.Sid))) return null;

        // 子代理花费（<session>\subagents\**\*.jsonl）也算进本会话，与 /cost 一致。
        double subCost = 0;
        var subDir = Path.Combine(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file), "subagents");
        if (Directory.Exists(subDir))
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(subDir, "*.jsonl", SearchOption.AllDirectories))
                {
                    var pf = ParseFile(f);
                    if (pf is not null) subCost += pf.Cost;
                }
            }
            catch { }
        }

        var p = ModelPricing.Lookup(main.LastModel);
        int window = Math.Max(p.Window, main.PeakCtx > 200_000 ? 1_000_000 : 200_000);
        var name = string.IsNullOrEmpty(main.Cwd) ? "(unknown)" : LastPathSegment(main.Cwd);
        var branch = (main.Branch == "HEAD" || string.IsNullOrEmpty(main.Branch)) ? null : main.Branch;

        return new SessionInfo
        {
            Id = string.IsNullOrEmpty(main.Sid) ? Path.GetFileName(file) : main.Sid,
            ProjectName = name,
            Cwd = main.Cwd,
            GitBranch = branch,
            Model = main.LastModel,
            CostUSD = main.Cost + subCost,
            ContextTokens = main.LastCtx,
            PeakContextTokens = main.PeakCtx,
            ContextWindow = window,
            LastActivity = mtime,
        };
    }

    static string LastPathSegment(string p)
    {
        var trimmed = p.Replace('/', '\\').TrimEnd('\\');
        int i = trimmed.LastIndexOf('\\');
        return i >= 0 ? trimmed[(i + 1)..] : trimmed;
    }
}
