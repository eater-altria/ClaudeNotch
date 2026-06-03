using System.Windows.Media;

namespace ClaudeNotch.UI;

/// <summary>Fluent / Windows 11 风格调色板 + 绿→橙→红渐变取色（与 macOS 版口径一致）。</summary>
public static class Theme
{
    // 窗口 / 卡片（Fluent 深色）
    public static readonly Color WindowBg = Color.FromRgb(0x20, 0x20, 0x20);
    public static readonly Color CardBg = Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF);
    public static readonly Color CardBgHover = Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF);
    public static readonly Color CardStroke = Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF);
    public static readonly Color Accent = Color.FromRgb(0x60, 0xCD, 0xFF);   // Win11 默认强调蓝

    // 悬浮挂件（半透明亚克力感）
    public static readonly Color PanelBg = Color.FromArgb(0xEE, 0x2A, 0x2A, 0x2E);
    public static readonly Color PillBg = Color.FromArgb(0xF0, 0x20, 0x20, 0x24);
    public static readonly Color Track = Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF);

    public static readonly Color Text = Color.FromRgb(0xFA, 0xFA, 0xFC);
    public static readonly Color TextDim = Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF);
    public static readonly Color TextFaint = Color.FromArgb(0x77, 0xFF, 0xFF, 0xFF);
    public static readonly Color Green = Color.FromRgb(0x2E, 0xC7, 0x71);

    public static readonly FontFamily Font = new("Segoe UI Variable Display, Segoe UI Variable, Segoe UI");
    public static readonly FontFamily FontText = new("Segoe UI Variable Text, Segoe UI Variable, Segoe UI");

    static readonly Dictionary<Color, SolidColorBrush> _cache = new();
    public static SolidColorBrush Brush(Color c)
    {
        if (!_cache.TryGetValue(c, out var b)) { b = new SolidColorBrush(c); b.Freeze(); _cache[c] = b; }
        return b;
    }

    public static Color Ramp(double t)
    {
        t = Math.Clamp(t, 0, 1);
        byte Lerp(double a, double b, double u) => (byte)Math.Round(a + (b - a) * u);
        if (t < 0.5)
        {
            double u = t / 0.5;
            return Color.FromRgb(Lerp(77, 250, u), Lerp(212, 171, u), Lerp(115, 51, u));
        }
        else
        {
            double u = (t - 0.5) / 0.5;
            return Color.FromRgb(Lerp(250, 242, u), Lerp(171, 77, u), Lerp(51, 77, u));
        }
    }

    public static Color ForPercent(int percent) => Ramp(percent / 100.0);
    public static Color ContextColor(int percent) =>
        percent >= 90 ? Color.FromRgb(0xF2, 0x4D, 0x4D)
        : percent >= 75 ? Color.FromRgb(0xFA, 0xAB, 0x33)
        : Color.FromRgb(0x59, 0xB8, 0xF2);
}
