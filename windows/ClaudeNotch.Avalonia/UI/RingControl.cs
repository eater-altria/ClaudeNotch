using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ClaudeNotch.UI;

/// <summary>进度环:底环 + 按百分比上色的弧(自绘 StreamGeometry.ArcTo)。固定直径。</summary>
public sealed class RingControl : Control
{
    readonly double _diameter, _thickness;
    Color _color;
    int _percent;

    public RingControl(double diameter, double thickness, Color color)
    {
        _diameter = diameter; _thickness = thickness; _color = color;
        Width = diameter; Height = diameter;
    }

    public int Percent
    {
        get => _percent;
        set { value = Math.Clamp(value, 0, 100); if (value != _percent) { _percent = value; InvalidateVisual(); } }
    }

    public Color RingColor
    {
        get => _color;
        set { if (value != _color) { _color = value; InvalidateVisual(); } }
    }

    public override void Render(DrawingContext ctx)
    {
        double r = (_diameter - _thickness) / 2;
        var center = new Point(_diameter / 2, _diameter / 2);

        var trackPen = new Pen(Palette.Brush(Palette.Track), _thickness);
        ctx.DrawEllipse(null, trackPen, center, r, r);

        if (_percent <= 0) return;
        var arcPen = new Pen(Palette.Brush(_color), _thickness) { LineCap = PenLineCap.Round };
        if (_percent >= 100) { ctx.DrawEllipse(null, arcPen, center, r, r); return; }

        double start = -Math.PI / 2;
        double sweep = 2 * Math.PI * (_percent / 100.0);
        double end = start + sweep;
        var sp = new Point(center.X + r * Math.Cos(start), center.Y + r * Math.Sin(start));
        var ep = new Point(center.X + r * Math.Cos(end), center.Y + r * Math.Sin(end));

        var geo = new StreamGeometry();
        using (var gc = geo.Open())
        {
            gc.BeginFigure(sp, false);
            gc.ArcTo(ep, new Size(r, r), 0, isLargeArc: sweep > Math.PI, SweepDirection.Clockwise);
            gc.EndFigure(false);
        }
        ctx.DrawGeometry(null, arcPen, geo);
    }
}
