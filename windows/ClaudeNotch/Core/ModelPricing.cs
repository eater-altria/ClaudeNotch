using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeNotch.Core;

/// <summary>各模型 API 单价（$/MTok）与默认上下文窗口。</summary>
public readonly record struct ModelPricing(
    double Input, double Output, double CacheRead, double CacheWrite5m, double CacheWrite1h, int Window)
{
    /// <summary>优先 LiteLLM/覆盖价表，未命中退回本地按族近似。</summary>
    public static ModelPricing Lookup(string model) => PriceCatalog.Shared.Match(model) ?? Fallback(model);

    public static ModelPricing Fallback(string model)
    {
        var m = model.ToLowerInvariant();
        // OpenAI / Codex 家族（LiteLLM 命中时优先用真实价；此处离线/未命中兜底，cacheWrite 对 OpenAI 无意义置 0）。
        if (m.Contains("gpt-5") || m.Contains("codex"))
        {
            if (m.Contains("nano")) return new(0.05, 0.40, 0.005, 0, 0, 400_000);
            if (m.Contains("mini")) return new(0.25, 2.0, 0.025, 0, 0, 400_000);
            return new(1.25, 10, 0.125, 0, 0, 400_000);
        }
        if (m.StartsWith("o3") || m.StartsWith("o4") || m.Contains("gpt-4"))
        {
            if (m.Contains("mini")) return new(1.10, 4.40, 0.275, 0, 0, 200_000);
            return new(2.0, 8.0, 0.5, 0, 0, 200_000);
        }
        // Fable 5（Opus 之上的新档）：$10 / $50，cache read $1，5m 写 $12.5，1h 写 $20，1M 上下文
        if (m.Contains("fable")) return new(10, 50, 1.0, 12.5, 20, 1_000_000);
        if (m.Contains("opus")) return new(5, 25, 0.5, 6.25, 10, 1_000_000);
        if (m.Contains("sonnet")) return new(3, 15, 0.30, 3.75, 6, 200_000);
        if (m.Contains("haiku")) return new(1, 5, 0.10, 1.25, 2, 200_000);
        return new(3, 15, 0.30, 3.75, 6, 200_000);
    }

    public static int FallbackWindow(string model) => Fallback(model).Window;
}

/// <summary>归一化 + 线程安全价表（LiteLLM 表 + 手动覆盖）。</summary>
public sealed class PriceCatalog
{
    public static readonly PriceCatalog Shared = new();

    readonly object _lock = new();
    Dictionary<string, ModelPricing> _table = new();
    Dictionary<string, ModelPricing> _overrides = new();

    static readonly Regex DateSuffix = new(@"-[0-9]{8}$", RegexOptions.Compiled);
    static readonly Regex Brackets = new(@"\[[^\]]*\]", RegexOptions.Compiled);
    static readonly HashSet<string> DropHeads = new()
    { "us", "eu", "global", "au", "apac", "anthropic", "bedrock", "azure", "vertex_ai", "openai", "gemini" };

    public int Count { get { lock (_lock) return _table.Count; } }
    public int OverrideCount { get { lock (_lock) return _overrides.Count; } }

    public void Install(Dictionary<string, ModelPricing> t) { lock (_lock) _table = t; }
    public void InstallOverrides(Dictionary<string, ModelPricing> t) { lock (_lock) _overrides = t; }

    public ModelPricing? Match(string model)
    {
        var key = Normalize(model);
        lock (_lock)
        {
            if (_overrides.TryGetValue(key, out var o)) return o;
            if (_table.TryGetValue(key, out var v)) return v;
        }
        return null;
    }

    /// <summary>两侧用同一归一化（去 [变体]/provider/region/日期前后缀），只求一致。</summary>
    public static string Normalize(string raw)
    {
        var m = raw.ToLowerInvariant();
        m = Brackets.Replace(m, "");
        int slash = m.LastIndexOf('/');
        if (slash >= 0) m = m[(slash + 1)..];
        while (true)
        {
            int dot = m.IndexOf('.');
            if (dot < 0) break;
            var head = m[..dot];
            if (DropHeads.Contains(head)) m = m[(dot + 1)..];
            else break;
        }
        foreach (var suf in new[] { "-v1:0", "-v2:0", ":0", "-latest" })
            if (m.EndsWith(suf)) m = m[..^suf.Length];
        m = DateSuffix.Replace(m, "");
        if (m.EndsWith("-v1")) m = m[..^3];
        return m;
    }

    static double? Num(JsonElement e, string key)
    {
        if (e.TryGetProperty(key, out var v))
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
            if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), out var ds)) return ds;
        }
        return null;
    }

    /// <summary>解析 LiteLLM 整表 JSON（每 token 价 ×1e6 转 $/MTok）。</summary>
    public static Dictionary<string, ModelPricing>? Parse(string json)
    {
        Dictionary<string, (ModelPricing p, int score)> best = new();
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var e = prop.Value;
                if (e.ValueKind != JsonValueKind.Object) continue;
                var inTok = Num(e, "input_cost_per_token");
                if (inTok is null) continue;
                double i = inTok.Value;
                double o = Num(e, "output_cost_per_token") ?? 0;
                double cr = Num(e, "cache_read_input_token_cost") ?? i * 0.1;
                double cw5 = Num(e, "cache_creation_input_token_cost") ?? i * 1.25;
                double cw1h = Num(e, "cache_creation_input_token_cost_above_1hr") ?? cw5;
                var norm = Normalize(prop.Name);
                int llmWin = (int)(Num(e, "max_input_tokens") ?? 0);
                int win = Math.Max(llmWin, ModelPricing.FallbackWindow(norm));
                var pricing = new ModelPricing(i * 1e6, o * 1e6, cr * 1e6, cw5 * 1e6, cw1h * 1e6, win);
                int score = -(prop.Name.Count(c => c == '/') * 10 + prop.Name.Count(c => c == '.'));
                if (best.TryGetValue(norm, out var cur) && cur.score >= score) continue;
                best[norm] = (pricing, score);
            }
        }
        catch { return null; }
        if (best.Count == 0) return null;
        return best.ToDictionary(kv => kv.Key, kv => kv.Value.p);
    }

    /// <summary>解析手动覆盖文件（单价单位 $/MTok）。`_` 开头键当注释跳过。</summary>
    public static Dictionary<string, ModelPricing> ParseOverrides(string json)
    {
        var outp = new Dictionary<string, ModelPricing>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Name.StartsWith("_")) continue;
                var e = prop.Value;
                if (e.ValueKind != JsonValueKind.Object) continue;
                var input = Num(e, "input");
                if (input is null) continue;
                double i = input.Value;
                double o = Num(e, "output") ?? 0;
                double cr = Num(e, "cache_read") ?? i * 0.1;
                double cw5 = Num(e, "cache_write_5m") ?? i * 1.25;
                double cw1h = Num(e, "cache_write_1h") ?? cw5;
                var norm = Normalize(prop.Name);
                int win = (int)(Num(e, "window") ?? 0);
                outp[norm] = new ModelPricing(i, o, cr, cw5, cw1h, Math.Max(win, ModelPricing.FallbackWindow(norm)));
            }
        }
        catch { /* malformed -> empty */ }
        return outp;
    }
}

/// <summary>价表加载/刷新：内置快照即时 + 每周后台刷新 + 手动覆盖。</summary>
public sealed class ModelPriceStore
{
    const string RemoteUrl = "https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json";
    static readonly TimeSpan RefreshInterval = TimeSpan.FromDays(7);

    public int ModelCount { get; private set; }
    public int OverrideCount { get; private set; }
    public DateTime? LastUpdated { get; private set; }
    public bool IsRefreshing { get; private set; }
    public string? LastError { get; private set; }
    public event Action? Changed;

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    const string FetchedAtMarker = "litellm-fetched-at.txt";
    string FetchedAtFile => Path.Combine(Paths.SupportDir, FetchedAtMarker);

    public void Bootstrap()
    {
        Task.Run(() =>
        {
            bool loaded = false;
            try
            {
                if (File.Exists(Paths.LiteLLMCacheFile))
                {
                    var t = PriceCatalog.Parse(File.ReadAllText(Paths.LiteLLMCacheFile));
                    if (t is not null) { PriceCatalog.Shared.Install(t); loaded = true; }
                }
                if (!loaded && File.Exists(Paths.BundledLiteLLM))
                {
                    var t = PriceCatalog.Parse(File.ReadAllText(Paths.BundledLiteLLM));
                    if (t is not null) { PriceCatalog.Shared.Install(t); loaded = true; }
                }
            }
            catch { /* ignore */ }

            ReloadOverridesSync();
            DateTime? fetchedAt = ReadFetchedAt();
            ModelCount = PriceCatalog.Shared.Count;
            OverrideCount = PriceCatalog.Shared.OverrideCount;
            LastUpdated = File.Exists(Paths.LiteLLMCacheFile) ? fetchedAt : null;
            Changed?.Invoke();

            bool stale = fetchedAt is null || DateTime.Now - fetchedAt.Value > RefreshInterval;
            if (!loaded || stale) _ = RefreshAsync();
        });
    }

    public async Task RefreshAsync()
    {
        if (IsRefreshing) return;
        ReloadOverridesSync();
        IsRefreshing = true; LastError = null; Changed?.Invoke();
        try
        {
            var json = await Http.GetStringAsync(RemoteUrl).ConfigureAwait(false);
            var table = PriceCatalog.Parse(json);
            if (table is not null)
            {
                PriceCatalog.Shared.Install(table);
                File.WriteAllText(Paths.LiteLLMCacheFile, json);
                var now = DateTime.Now;
                File.WriteAllText(FetchedAtFile, now.ToString("o"));
                ModelCount = table.Count;
                LastUpdated = now;
            }
            else LastError = L.Tr("价表解析失败", "Failed to parse price table");
        }
        catch (Exception e) { LastError = e.Message; }
        finally { OverrideCount = PriceCatalog.Shared.OverrideCount; IsRefreshing = false; Changed?.Invoke(); }
    }

    void ReloadOverridesSync()
    {
        try
        {
            var t = File.Exists(Paths.OverridesFile)
                ? PriceCatalog.ParseOverrides(File.ReadAllText(Paths.OverridesFile))
                : new Dictionary<string, ModelPricing>();
            PriceCatalog.Shared.InstallOverrides(t);
            OverrideCount = t.Count;
        }
        catch { /* ignore */ }
    }

    public void ReloadOverrides() { ReloadOverridesSync(); Changed?.Invoke(); }

    DateTime? ReadFetchedAt()
    {
        try { if (File.Exists(FetchedAtFile) && DateTime.TryParse(File.ReadAllText(FetchedAtFile), out var d)) return d; }
        catch { }
        return null;
    }

    public void OpenOverridesForEditing()
    {
        if (!File.Exists(Paths.OverridesFile))
            File.WriteAllText(Paths.OverridesFile, OverrideTemplate);
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Paths.OverridesFile) { UseShellExecute = true }); }
        catch { }
    }

    const string OverrideTemplate = """
    {
      "_说明": "手动价格覆盖。单价单位 = 美元/百万 token ($/MTok)。键为模型名(大小写、provider/日期前后缀无关)。优先级高于 LiteLLM 在线表。编辑保存后回设置点『刷新价格』生效。",
      "_可选字段": "input(必填) / output / cache_read / cache_write_5m / cache_write_1h / window",
      "deepseek-v4-pro": {
        "input": 0.28,
        "output": 0.42,
        "cache_read": 0.028,
        "cache_write_5m": 0.28
      }
    }
    """;
}
