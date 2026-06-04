using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ClaudeNotch.UI;

/// <summary>设计令牌:挂件深色调色板 + 绿→橙→红渐变取色(与 macOS / WPF 版口径一致)。</summary>
public static class Theme
{
    public static Color Rgb(byte r, byte g, byte b) => new() { A = 255, R = r, G = g, B = b };
    public static Color Argb(byte a, byte r, byte g, byte b) => new() { A = a, R = r, G = g, B = b };

    // 悬浮挂件(半透明,叠在 Acrylic 背景上)
    public static readonly Color PanelBg = Argb(0xEE, 0x2A, 0x2A, 0x2E);
    public static readonly Color PillBg = Argb(0xF0, 0x20, 0x20, 0x24);
    public static readonly Color CardBg = Argb(0x18, 0xFF, 0xFF, 0xFF);
    public static readonly Color Track = Argb(0x33, 0xFF, 0xFF, 0xFF);
    public static readonly Color Divider = Argb(0x1F, 0xFF, 0xFF, 0xFF);

    public static readonly Color Text = Rgb(0xFA, 0xFA, 0xFC);
    public static readonly Color TextDim = Argb(0xB0, 0xFF, 0xFF, 0xFF);
    public static readonly Color TextFaint = Argb(0x77, 0xFF, 0xFF, 0xFF);
    public static readonly Color Green = Rgb(0x2E, 0xC7, 0x71);

    public static readonly FontFamily Font = new("Segoe UI Variable Display, Segoe UI Variable, Segoe UI");
    public static readonly FontFamily FontText = new("Segoe UI Variable Text, Segoe UI Variable, Segoe UI");

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
