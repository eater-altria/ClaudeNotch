namespace ClaudeNotch.Core;

/// <summary>一个活跃 Claude Code 会话的汇总（从 transcript JSONL 解析）。</summary>
public sealed class SessionInfo
{
    public required string Id { get; init; }
    public required string ProjectName { get; init; }
    public required string Cwd { get; init; }
    public string? GitBranch { get; init; }
    public required string Model { get; init; }
    public double CostUSD { get; init; }
    public int ContextTokens { get; init; }
    public int PeakContextTokens { get; init; }
    public int ContextWindow { get; init; }
    public DateTime LastActivity { get; init; }

    public int ContextPercent => ContextWindow > 0
        ? Math.Max(0, Math.Min(100, (int)Math.Round((double)ContextTokens / ContextWindow * 100))) : 0;

    public int PeakContextPercent => ContextWindow > 0
        ? Math.Max(0, Math.Min(100, (int)Math.Round((double)PeakContextTokens / ContextWindow * 100))) : 0;

    public bool HasMeaningfulPeak => PeakContextTokens > ContextTokens;

    public string ModelShort => TranscriptParser.ShortModelName(Model);
}
