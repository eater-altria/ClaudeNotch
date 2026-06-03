using System.Windows.Media;

namespace ClaudeNotch.UI;

/// <summary>明暗色板 + 绿→橙→红渐变取色（与 macOS 版口径一致）。</summary>
public static class Theme
{
    public static readonly Color PanelBg = Color.FromArgb(0xF2, 0x1C, 0x1C, 0x1E);
    public static readonly Color PillBg = Color.FromArgb(0xF2, 0x10, 0x10, 0x12);
    public static readonly Color Track = Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF);
    public static readonly Color Text = Color.FromRgb(0xF2, 0xF2, 0xF4);
    public static readonly Color TextDim = Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF);
    public static readonly Color Green = Color.FromRgb(0x2E, 0xC7, 0x71);

    public static SolidColorBrush Brush(Color c) => new(c) { };

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
