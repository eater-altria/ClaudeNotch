using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace ClaudeNotch.Core;

/// <summary>金额显示：内部一律存美元，显示时英文 $ / 中文 ¥(×实时汇率)。</summary>
public static class Money
{
    public static double UsdToCny = 7.15;   // 离线默认，联网成功后覆盖

    public static string Format(double usd, int decimals = 2)
    {
        var ci = CultureInfo.InvariantCulture;
        return L.Current == AppLang.Zh
            ? "¥" + (usd * UsdToCny).ToString("F" + decimals, ci)
            : "$" + usd.ToString("F" + decimals, ci);
    }

    public static string Approx(double usd, int decimals = 2) => "≈" + Format(usd, decimals);
}

/// <summary>USD→CNY 汇率：内置默认 + 联网每周刷新（open.er-api.com 公开数据，无需认证）+ 手动刷新。</summary>
public sealed class ExchangeRateStore
{
    const string RemoteUrl = "https://open.er-api.com/v6/latest/USD";
    static readonly TimeSpan RefreshInterval = TimeSpan.FromDays(7);
    const double DefaultRate = 7.15;

    public double Rate { get; private set; } = DefaultRate;
    public DateTime? LastUpdated { get; private set; }
    public bool IsRefreshing { get; private set; }
    public string? LastError { get; private set; }
    public event Action? Changed;

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    string CacheFile => Path.Combine(Paths.SupportDir, "exchange-rate.json");

    record Cache(double rate, long fetchedAtUnix);

    public void Bootstrap()
    {
        double r = DefaultRate;
        DateTime? fetchedAt = null;
        try
        {
            if (File.Exists(CacheFile))
            {
                var c = JsonSerializer.Deserialize<Cache>(File.ReadAllText(CacheFile));
                if (c is not null && c.rate > 0)
                {
                    r = c.rate;
                    fetchedAt = DateTimeOffset.FromUnixTimeSeconds(c.fetchedAtUnix).LocalDateTime;
                }
            }
        }
        catch { /* 用默认 */ }

        Rate = r;
        Money.UsdToCny = r;
        LastUpdated = fetchedAt;
        Changed?.Invoke();

        bool stale = fetchedAt is null || DateTime.Now - fetchedAt.Value > RefreshInterval;
        if (stale) _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (IsRefreshing) return;
        IsRefreshing = true; LastError = null; Changed?.Invoke();
        try
        {
            var json = await Http.GetStringAsync(RemoteUrl).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("result", out var res) && res.GetString() == "success"
                && root.TryGetProperty("rates", out var rates)
                && rates.TryGetProperty("CNY", out var cny) && cny.TryGetDouble(out var v) && v > 0)
            {
                Rate = v;
                Money.UsdToCny = v;
                LastUpdated = DateTime.Now;
                File.WriteAllText(CacheFile, JsonSerializer.Serialize(
                    new Cache(v, DateTimeOffset.UtcNow.ToUnixTimeSeconds())));
            }
            else LastError = L.Tr("汇率解析失败", "Failed to parse exchange rate");
        }
        catch (Exception e) { LastError = e.Message; }
        finally { IsRefreshing = false; Changed?.Invoke(); }
    }
}
