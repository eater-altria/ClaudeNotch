using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeNotch.Core;

/// <summary>应用设置，持久化为 %APPDATA%\ClaudeNotch\settings.json。</summary>
public sealed class AppSettings
{
    public string LanguagePreference { get; set; } = "system";
    public string Agent { get; set; } = "claudeCode";    // claudeCode | codex（监控的 CLI 代理）
    public string ThemeMode { get; set; } = "system";   // system | light | dark（配色，跟随系统/日间/夜间）
    public bool LaunchAtLogin { get; set; }
    public bool WidgetEnabled { get; set; } = true;
    public bool ManageStatusline { get; set; } = true;
    public bool NotificationsEnabled { get; set; } = true;
    public int QuotaWarn { get; set; } = 80;
    public int QuotaCritical { get; set; } = 95;
    public int ContextThreshold { get; set; } = 90;
    public double? WidgetX { get; set; }
    public double? WidgetY { get; set; }
    public bool WidgetExpanded { get; set; }

    public event Action? Changed;   // 事件不会被 System.Text.Json 序列化

    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(Paths.SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Paths.SettingsFile)) ?? new AppSettings();
        }
        catch { /* fall through */ }
        return new AppSettings();
    }

    public void Save()
    {
        try { File.WriteAllText(Paths.SettingsFile, JsonSerializer.Serialize(this, Opts)); }
        catch { /* best effort */ }
        Changed?.Invoke();
    }

    public LangPref Lang => L.ParsePref(LanguagePreference);
    public AgentKind AgentKind => AgentKinds.Parse(Agent);
}
