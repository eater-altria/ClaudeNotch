using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace ClaudeNotch.UI;

/// <summary>设计令牌:**随主题(明/暗)自适应**的调色板 + 绿→橙→红渐变取色(与 macOS 口径一致)。
/// 注意:类名不能叫 Theme —— 会与 Avalonia StyledElement.Theme(ControlTheme)属性在控件子类里冲突。
/// 颜色都是“属性”(按当前 ActualThemeVariant 实时取),切主题后各窗 Rebuild 即生效。</summary>
public static class Palette
{
    public static Color Rgb(byte r, byte g, byte b) => Color.FromArgb(255, r, g, b);
    public static Color Argb(byte a, byte r, byte g, byte b) => Color.FromArgb(a, r, g, b);

    public static bool IsDark => (Application.Current?.ActualThemeVariant ?? ThemeVariant.Dark) == ThemeVariant.Dark;

    // 窗口 / 卡片 / 挂件背景
    public static Color WindowBg => IsDark ? Rgb(0x1F, 0x1F, 0x23) : Rgb(0xF3, 0xF3, 0xF6);
    public static Color PanelBg => IsDark ? Argb(0xF2, 0x22, 0x22, 0x26) : Argb(0xFA, 0xFB, 0xFB, 0xFD);
    public static Color OrbBg => IsDark ? Argb(0xF2, 0x20, 0x20, 0x24) : Argb(0xFA, 0xFC, 0xFC, 0xFE);
    public static Color CardBg => IsDark ? Argb(0x18, 0xFF, 0xFF, 0xFF) : Rgb(0xFF, 0xFF, 0xFF);

    // 线/轨道/填充(暗=白叠加, 亮=黑叠加)
    public static Color Track => IsDark ? Argb(0x33, 0xFF, 0xFF, 0xFF) : Argb(0x22, 0x00, 0x00, 0x00);
    public static Color Divider => IsDark ? Argb(0x1F, 0xFF, 0xFF, 0xFF) : Argb(0x18, 0x00, 0x00, 0x00);
    public static Color SubtleFill => IsDark ? Argb(0x1A, 0xFF, 0xFF, 0xFF) : Argb(0x12, 0x00, 0x00, 0x00);
    public static Color HeatEmpty => IsDark ? Argb(0x1C, 0xFF, 0xFF, 0xFF) : Argb(0x14, 0x00, 0x00, 0x00);

    // 文本
    public static Color Text => IsDark ? Rgb(0xFA, 0xFA, 0xFC) : Rgb(0x1E, 0x1E, 0x22);
    public static Color TextDim => IsDark ? Argb(0xB0, 0xFF, 0xFF, 0xFF) : Argb(0xC8, 0x00, 0x00, 0x00);
    public static Color TextFaint => IsDark ? Argb(0x77, 0xFF, 0xFF, 0xFF) : Argb(0x80, 0x00, 0x00, 0x00);

    public static readonly Color Green = Rgb(0x2E, 0xC7, 0x71);

    public const string FontFamily = "Segoe UI Variable Text, Segoe UI Variable, Segoe UI";
    public const string FontDisplay = "Segoe UI Variable Display, Segoe UI Variable, Segoe UI";

    static readonly Dictionary<uint, SolidColorBrush> _cache = new();
    public static SolidColorBrush Brush(Color c)
    {
        uint key = ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
        if (!_cache.TryGetValue(key, out var b)) { b = new SolidColorBrush(c); _cache[key] = b; }
        return b;
    }

    public static Color Ramp(double t)
    {
        t = Math.Clamp(t, 0, 1);
        byte Lerp(double a, double b, double u) => (byte)Math.Round(a + (b - a) * u);
        if (t < 0.5)
        {
            double u = t / 0.5;
            return Rgb(Lerp(77, 250, u), Lerp(212, 171, u), Lerp(115, 51, u));
        }
        double v = (t - 0.5) / 0.5;
        return Rgb(Lerp(250, 242, v), Lerp(171, 77, v), Lerp(51, 77, v));
    }

    public static Color ForPercent(int percent) => Ramp(percent / 100.0);

    public static Color ContextColor(int percent) =>
        percent >= 90 ? Rgb(0xF2, 0x4D, 0x4D)
        : percent >= 75 ? Rgb(0xFA, 0xAB, 0x33)
        : Rgb(0x59, 0xB8, 0xF2);
}
