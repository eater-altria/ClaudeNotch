using System.Threading;

namespace ClaudeNotch.Core;

/// <summary>系统通知出口（由托盘实现 balloon tip）。</summary>
public static class Notifier
{
    public static Action<string, string>? Show;
    public static void Notify(string title, string body) => Show?.Invoke(title, body);
}

public enum UsageState { Idle, Loading, Ready, Waiting, Error }

/// <summary>订阅额度状态机 + 定时刷新（读 statusline 钩子落盘）+ 阈值通知。</summary>
public sealed class UsageStore
{
    public UsageState State { get; private set; } = UsageState.Idle;
    public UsageSnapshot? Snapshot { get; private set; }
    public string? ErrorMessage { get; private set; }
    public event Action? Changed;

    // 由设置驱动
    public int QuotaWarn = 80, QuotaCritical = 95;
    public bool NotificationsEnabled = true;

    Timer? _timer;
    readonly Dictionary<string, BurnEstimator> _burn = new();
    readonly Dictionary<string, int> _lastBand = new();   // metricId -> 上次所处档(0/warn/critical)

    public void Start()
    {
        Refresh();
        _timer = new Timer(_ => Refresh(), null, 15_000, 15_000);
    }

    public void Refresh()
    {
        var outcome = StatuslineProvider.FetchUsage();
        if (outcome is FetchOutcome.Success ok)
        {
            var snap = UsageSnapshot.From(ok.Result, DateTime.Now);
            Snapshot = snap;
            State = UsageState.Ready;
            ErrorMessage = null;
            var now = DateTime.Now;
            foreach (var m in snap.AllMetrics)
            {
                if (!_burn.TryGetValue(m.Id, out var est)) { est = new BurnEstimator(); _burn[m.Id] = est; }
                est.Record(m.PercentUsed, now);
                CheckThreshold(m);
            }
        }
        else if (outcome is FetchOutcome.Failure f)
        {
            State = File.Exists(Paths.RatelimitsFile) ? UsageState.Error : UsageState.Waiting;
            ErrorMessage = f.Message;
        }
        Changed?.Invoke();
    }

    public BurnEstimator BurnFor(string metricId) =>
        _burn.TryGetValue(metricId, out var e) ? e : (_burn[metricId] = new BurnEstimator());

    void CheckThreshold(UsageMetric m)
    {
        int band = m.PercentUsed >= QuotaCritical ? 2 : m.PercentUsed >= QuotaWarn ? 1 : 0;
        int prev = _lastBand.TryGetValue(m.Id, out var b) ? b : 0;
        _lastBand[m.Id] = band;
        if (NotificationsEnabled && band > prev && band > 0)
        {
            Notifier.Notify(L.Tr("Claude 额度提醒", "Claude Quota Alert"),
                L.Tr($"{m.Title} 已用 {m.PercentUsed}%，仅剩 {m.PercentRemaining}%",
                     $"{m.Title} at {m.PercentUsed}% used, only {m.PercentRemaining}% left"));
        }
    }
}

/// <summary>活跃会话存储（30s 轮询 + 上下文告警）。</summary>
public sealed class SessionStore
{
    public IReadOnlyList<SessionInfo> Sessions { get; private set; } = Array.Empty<SessionInfo>();
    public event Action? Changed;

    public int ContextThreshold = 90;
    public bool NotificationsEnabled = true;

    readonly SessionScanner _scanner = new();
    readonly HashSet<string> _notifiedContext = new();
    Timer? _timer;

    public void Start()
    {
        Refresh();
        _timer = new Timer(_ => Refresh(), null, 30_000, 30_000);
    }

    public void Refresh()
    {
        var result = _scanner.Scan();
        Sessions = result;
        CheckContextNotifications(result);
        Changed?.Invoke();
    }

    void CheckContextNotifications(IReadOnlyList<SessionInfo> sessions)
    {
        foreach (var s in sessions)
            if (s.ContextPercent >= ContextThreshold && _notifiedContext.Add(s.Id) && NotificationsEnabled)
                Notifier.Notify(L.Tr("上下文将满", "Context Almost Full"),
                    L.Tr($"{s.ProjectName} 上下文已用 {s.ContextPercent}%，建议 /compact 或新开会话",
                         $"{s.ProjectName} context at {s.ContextPercent}% used, consider /compact or a new session"));
        foreach (var s in sessions)
            if (s.ContextPercent < ContextThreshold) _notifiedContext.Remove(s.Id);
    }
}

/// <summary>历史用量存储：懒构建（首次打开统计窗口时后台扫描）。</summary>
public sealed class HistoryStore
{
    public UsageHistory History { get; private set; } = new();
    public bool IsBuilding { get; private set; }
    public double? Progress { get; private set; }
    public event Action? Changed;

    bool _hasLoaded;

    public void RefreshIfNeeded() { if (!_hasLoaded) Refresh(); }

    public void Refresh()
    {
        if (IsBuilding) return;
        IsBuilding = true;
        if (!_hasLoaded) Progress = 0;
        Changed?.Invoke();
        Task.Run(() =>
        {
            var result = HistoryScanner.Build(p => { Progress = p; Changed?.Invoke(); });
            History = result;
            IsBuilding = false;
            Progress = null;
            _hasLoaded = true;
            Changed?.Invoke();
        });
    }
}
