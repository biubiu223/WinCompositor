using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Compositor.Core.CameraRaw;
using Compositor.Core.Localization;

namespace Compositor.App.Controls;

/// <summary>
/// Camera Raw's scope: the RGB histogram (where channels overlap their mix shows, gray where all three do) or a
/// hue/saturation vectorscope, with the shadow and highlight clipping triangles in the top corners. Clicking a
/// triangle turns its clipping warning on the canvas on or off. Mirrors CameraRawHistogramView.
/// </summary>
public sealed class CameraRawScopeView : FrameworkElement
{
    private const double Triangle = 14;

    public CameraRawScope? Scope { get; set; }
    public bool Vectorscope { get; set; }
    public bool ShadowWarning { get; set; }
    public bool HighlightWarning { get; set; }

    /// <summary>A triangle was clicked: which warning (false for shadows, true for highlights).</summary>
    public event Action<bool>? WarningToggled;

    public CameraRawScopeView()
    {
        ToolTip = L.T("单击左上或右上角的三角形显示阴影或高光修剪警告（U / O）；右键切换直方图与矢量示波器",
            "Click the triangle at the top left or top right to show the shadow or highlight clipping warning (U / O). Right-click to switch between the histogram and the vectorscope.");
        Cursor = Cursors.Arrow;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var p = e.GetPosition(this);
        if (p.Y > Triangle + 8 || Vectorscope) return;
        if (p.X < Triangle + 8) { WarningToggled?.Invoke(false); e.Handled = true; }
        else if (p.X > ActualWidth - Triangle - 8) { WarningToggled?.Invoke(true); e.Handled = true; }
    }

    private static readonly Brush Back = Frozen(new SolidColorBrush(Color.FromRgb(24, 24, 24)));
    private static readonly Pen Frame = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(70, 70, 70)), 1));
    private static readonly Pen Faint = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)), 1));

    private static T Frozen<T>(T freezable) where T : Freezable { freezable.Freeze(); return freezable; }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 20 || h < 20) return;
        dc.DrawRoundedRectangle(Back, Frame, new Rect(0.5, 0.5, w - 1, h - 1), 4, 4);
        if (Scope is not { } scope) return;
        if (Vectorscope) DrawVectorscope(dc, scope, w, h);
        else DrawHistogram(dc, scope, w, h);
    }

    private void DrawHistogram(DrawingContext dc, CameraRawScope scope, double w, double h)
    {
        // Thirds, faintly, as Camera Raw marks them.
        for (int i = 1; i < 4; i++) dc.DrawLine(Faint, new Point(w * i / 4, 4), new Point(w * i / 4, h - 4));
        double peak = scope.Peak;
        if (peak <= 0) return;
        double inner = h - 6, step = (w - 4) / CameraRawScope.Bins;
        // Seven layers: one channel alone, two together (their mix), all three (gray).
        var groups = new StreamGeometry[7];
        var contexts = new StreamGeometryContext[7];
        for (int i = 0; i < 7; i++) { groups[i] = new StreamGeometry(); contexts[i] = groups[i].Open(); }
        void Bar(int group, double x, double from, double to)
        {
            if (to - from < 0.01) return;
            var c = contexts[group];
            double top = h - 3 - to * inner, bottom = h - 3 - from * inner;
            c.BeginFigure(new Point(x, top), true, true);
            c.LineTo(new Point(x + step + 0.4, top), false, false);
            c.LineTo(new Point(x + step + 0.4, bottom), false, false);
            c.LineTo(new Point(x, bottom), false, false);
        }
        for (int bin = 0; bin < CameraRawScope.Bins; bin++)
        {
            double r = Math.Min(1, scope.Red[bin] / peak), g = Math.Min(1, scope.Green[bin] / peak), b = Math.Min(1, scope.Blue[bin] / peak);
            double x = 2 + bin * step;
            var heights = new[] { (Value: r, Bit: 1), (Value: g, Bit: 2), (Value: b, Bit: 4) }.OrderBy(v => v.Value).ToArray();
            // From the floor to the lowest all three overlap; then the other two; then the tallest alone.
            Bar(6, x, 0, heights[0].Value);
            int pair = heights[1].Bit | heights[2].Bit;
            Bar(pair switch { 3 => 3, 6 => 4, _ => 5 }, x, heights[0].Value, heights[1].Value);
            Bar(heights[2].Bit switch { 1 => 0, 2 => 1, _ => 2 }, x, heights[1].Value, heights[2].Value);
        }
        Color[] colors =
        {
            Color.FromRgb(220, 60, 60), Color.FromRgb(70, 200, 80), Color.FromRgb(70, 110, 240),
            Color.FromRgb(225, 215, 70), Color.FromRgb(70, 205, 215), Color.FromRgb(210, 80, 215), Color.FromRgb(190, 190, 190),
        };
        for (int i = 0; i < 7; i++)
        {
            contexts[i].Close();
            groups[i].Freeze();
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(220, colors[i].R, colors[i].G, colors[i].B)), null, groups[i]);
        }
        DrawTriangle(dc, scope, false, w);
        DrawTriangle(dc, scope, true, w);
    }

    /// <summary>A corner triangle, lit in the clipped channels' color when something clips, ringed when its warning is on.</summary>
    private void DrawTriangle(DrawingContext dc, CameraRawScope scope, bool highlights, double w)
    {
        double total = scope.Red.Sum();
        int bin = highlights ? CameraRawScope.Bins - 1 : 0;
        double limit = total * 1e-5;
        bool r = scope.Red[bin] > limit, g = scope.Green[bin] > limit, b = scope.Blue[bin] > limit;
        var fill = r || g || b
            ? Color.FromRgb((byte)(r ? 255 : 70), (byte)(g ? 255 : 70), (byte)(b ? 255 : 70))
            : Color.FromRgb(85, 85, 85);
        double left = highlights ? w - Triangle - 5 : 5, top = 5;
        var geometry = new StreamGeometry();
        using (var c = geometry.Open())
        {
            if (highlights)
            {
                c.BeginFigure(new Point(left + Triangle, top), true, true);
                c.LineTo(new Point(left + Triangle, top + Triangle), true, false);
                c.LineTo(new Point(left, top), true, false);
            }
            else
            {
                c.BeginFigure(new Point(left, top), true, true);
                c.LineTo(new Point(left + Triangle, top), true, false);
                c.LineTo(new Point(left, top + Triangle), true, false);
            }
        }
        geometry.Freeze();
        bool on = highlights ? HighlightWarning : ShadowWarning;
        var ring = on ? new Pen(new SolidColorBrush(highlights ? Color.FromRgb(255, 80, 80) : Color.FromRgb(90, 150, 255)), 2) : null;
        dc.DrawGeometry(new SolidColorBrush(fill), ring, geometry);
    }

    private static void DrawVectorscope(DrawingContext dc, CameraRawScope scope, double w, double h)
    {
        double side = h - 10, left = (w - side) / 2, top = 5, cell = side / CameraRawScope.ScopeSide;
        var center = new Point(left + side / 2, top + side / 2);
        dc.DrawEllipse(null, Faint, center, side * 0.48, side * 0.48);
        dc.DrawEllipse(null, Faint, center, side * 0.24, side * 0.24);
        // Where each primary and secondary sits on the ring.
        for (int i = 0; i < 6; i++)
        {
            double angle = i * Math.PI / 3;
            var edge = new Point(center.X + Math.Cos(angle) * side * 0.48, center.Y + Math.Sin(angle) * side * 0.48);
            dc.DrawLine(Faint, center, edge);
            dc.DrawEllipse(new SolidColorBrush(Theme.FromHsb(i * 60)), null, edge, 2.5, 2.5);
        }
        double peak = scope.Vectorscope.Max();
        if (peak <= 0) return;
        double log = Math.Log(1 + peak);
        for (int y = 0; y < CameraRawScope.ScopeSide; y++)
            for (int x = 0; x < CameraRawScope.ScopeSide; x++)
            {
                double v = scope.Vectorscope[y * CameraRawScope.ScopeSide + x];
                if (v <= 0) continue;
                double strength = Math.Log(1 + v) / log;
                double dx = (x + 0.5) / CameraRawScope.ScopeSide - 0.5, dy = (y + 0.5) / CameraRawScope.ScopeSide - 0.5;
                double hue = (Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360;
                var c = Theme.FromHsb(hue);
                byte alpha = (byte)Math.Clamp(60 + strength * 195, 0, 255);
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B)), null,
                    new Rect(left + x * cell, top + y * cell, cell + 0.3, cell + 0.3));
            }
    }
}
