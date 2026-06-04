using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using Path = Microsoft.UI.Xaml.Shapes.Path;   // 消歧:隐式 using 带入了 System.IO.Path

namespace ClaudeNotch.UI;

/// <summary>进度环:底环(Ellipse 描边)+ 按百分比上色的弧(Path/ArcSegment)。固定直径,代码构建。</summary>
public sealed class RingControl : Canvas
{
    readonly double _diameter, _thickness;
    readonly Path _arc;
    int _percent;
    Color _color;

    public RingControl(double diameter, double thickness, Color color)
    {
        _diameter = diameter; _thickness = thickness; _color = color;
        Width = diameter; Height = diameter;

        var track = new Ellipse
        {
            Width = diameter - thickness,
            Height = diameter - thickness,
            Stroke = Theme.Brush(Theme.Track),
            StrokeThickness = thickness,
        };
        SetLeft(track, thickness / 2);
        SetTop(track, thickness / 2);
        Children.Add(track);

        _arc = new Path
        {
            Stroke = Theme.Brush(color),
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Stretch = Stretch.None,
        };
        SetLeft(_arc, 0);
        SetTop(_arc, 0);
        Children.Add(_arc);
    }

    public Color RingColor { get => _color; set { _color = value; _arc.Stroke = Theme.Brush(value); } }
    public int Percent { get => _percent; set { _percent = Math.Clamp(value, 0, 100); Rebuild(); } }

    void Rebuild()
    {
        double r = (_diameter - _thickness) / 2;
        double cx = _diameter / 2, cy = _diameter / 2;

        if (_percent <= 0) { _arc.Data = null; return; }
        if (_percent >= 100)
        {
            _arc.Data = new EllipseGeometry { Center = new Point(cx, cy), RadiusX = r, RadiusY = r };
            return;
        }

        double frac = _percent / 100.0;
        double start = -Math.PI / 2;
        double sweep = 2 * Math.PI * frac;
        var sp = new Point(cx + r * Math.Cos(start), cy + r * Math.Sin(start));
        double end = start + sweep;
        var ep = new Point(cx + r * Math.Cos(end), cy + r * Math.Sin(end));

        var fig = new PathFigure { StartPoint = sp, IsClosed = false };
        fig.Segments.Add(new ArcSegment
        {
            Point = ep,
            Size = new Size(r, r),
            IsLargeArc = frac > 0.5,
            SweepDirection = SweepDirection.Clockwise,
        });
        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        _arc.Data = geo;
    }
}
