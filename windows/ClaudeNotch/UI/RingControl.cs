using System.Windows;
using System.Windows.Media;

namespace ClaudeNotch.UI;

/// <summary>渐变进度环：track 底环 + 按百分比上色的弧 + 可选峰值刻度。</summary>
public sealed class RingControl : FrameworkElement
{
    int _percent;
    int? _peakPercent;
    double _thickness = 6;
    Color _color = Theme.Green;

    public int Percent { get => _percent; set { _percent = Math.Clamp(value, 0, 100); InvalidateVisual(); } }
    public int? PeakPercent { get => _peakPercent; set { _peakPercent = value; InvalidateVisual(); } }
    public double Thickness { get => _thickness; set { _thickness = value; InvalidateVisual(); } }
    public Color RingColor { get => _color; set { _color = value; InvalidateVisual(); } }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        double size = Math.Min(w, h);
        double r = (size - _thickness) / 2;
        var center = new Point(w / 2, h / 2);

        var trackPen = new Pen(Theme.Brush(Theme.Track), _thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawEllipse(null, trackPen, center, r, r);

        if (_percent > 0)
        {
            var arcPen = new Pen(Theme.Brush(_color), _thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawGeometry(null, arcPen, ArcGeometry(center, r, _percent / 100.0));
        }

        if (_peakPercent is int peak && peak > _percent)
        {
            // 峰值刻度：一个小点
            double ang = -Math.PI / 2 + 2 * Math.PI * (peak / 100.0);
            var p = new Point(center.X + r * Math.Cos(ang), center.Y + r * Math.Sin(ang));
            dc.DrawEllipse(Theme.Brush(Theme.TextDim), null, p, _thickness * 0.5, _thickness * 0.5);
        }
    }

    static Geometry ArcGeometry(Point center, double r, double fraction)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        double startAng = -Math.PI / 2;
        double sweep = 2 * Math.PI * fraction;
        var start = new Point(center.X + r * Math.Cos(startAng), center.Y + r * Math.Sin(startAng));

        if (fraction >= 0.999)
        {
            var g = new EllipseGeometry(center, r, r);
            return g;
        }

        double endAng = startAng + sweep;
        var end = new Point(center.X + r * Math.Cos(endAng), center.Y + r * Math.Sin(endAng));
        var fig = new PathFigure { StartPoint = start, IsClosed = false };
        fig.Segments.Add(new ArcSegment
        {
            Point = end,
            Size = new Size(r, r),
            IsLargeArc = fraction > 0.5,
            SweepDirection = SweepDirection.Clockwise,
        });
        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        return geo;
    }
}
