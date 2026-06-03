using System.Globalization;

namespace ClaudeNotch.Core;

public enum AppLang { En, Zh }
public enum LangPref { System, Zh, En }

/// <summary>双语取值。默认跟随系统语言，匹配不到中文则用英语。`L.Tr(中, 英)`。</summary>
public static class L
{
    public static AppLang Current { get; private set; } = AppLang.En;

    /// <summary>语言变化事件（UI 订阅以即时重绘）。</summary>
    public static event Action? Changed;

    public static string Tr(string zh, string en) => Current == AppLang.Zh ? zh : en;

    public static AppLang Resolve(LangPref pref) => pref switch
    {
        LangPref.Zh => AppLang.Zh,
        LangPref.En => AppLang.En,
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase)
            ? AppLang.Zh : AppLang.En,
    };

    public static void Apply(LangPref pref)
    {
        var lang = Resolve(pref);
        if (lang == Current) return;
        Current = lang;
        Changed?.Invoke();
    }

    /// <summary>启动时按偏好设定一次（不触发 Changed）。</summary>
    public static void Init(LangPref pref) => Current = Resolve(pref);

    public static LangPref ParsePref(string raw) => raw switch
    {
        "zh" => LangPref.Zh,
        "en" => LangPref.En,
        _ => LangPref.System,
    };
    public static string PrefRaw(LangPref p) => p switch { LangPref.Zh => "zh", LangPref.En => "en", _ => "system" };
    public static string PrefLabel(LangPref p) => p switch
    {
        LangPref.System => Tr("跟随系统", "System"),
        LangPref.Zh => "中文",
        _ => "English",
    };
}
