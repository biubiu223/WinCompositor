using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Compositor.Core.Editing;
using Compositor.Core.Geometry;
using Compositor.Core.Model;

namespace Compositor.App.Drawing;

/// <summary>A ruler along the canvas in document pixels; dragging out of it makes a guide. Mirrors CanvasRulers.swift.</summary>
public sealed class Ruler : FrameworkElement
{
    public const double Thickness = 20;
    public GuideAxis Axis { get; }
    public CanvasView Canvas { get; }
    private static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Brush Background = new SolidColorBrush(Color.FromRgb(38, 38, 38));
    private static readonly Pen Tick = new(new SolidColorBrush(Color.FromRgb(120, 120, 120)), 1);
    private static readonly Brush Label = new SolidColorBrush(Color.FromRgb(160, 160, 160));
    private static readonly Pen Edge = new(new SolidColorBrush(Color.FromRgb(58, 58, 58)), 1);

    /// <summary>Which edge of the canvas the ruler runs along: horizontal rulers make horizontal guides.</summary>
    public Ruler(GuideAxis axis, CanvasView canvas)
    {
        Axis = axis;
        Canvas = canvas;
        if (axis == GuideAxis.Horizontal) Height = Thickness; else Width = Thickness;
        Cursor = axis == GuideAxis.Horizontal ? Cursors.SizeNS : Cursors.SizeWE;
        canvas.ViewChanged += (_, _) => InvalidateVisual();
        Static.Freeze();
    }

    private static readonly Freezables Static = new();

    private sealed class Freezables
    {
        public void Freeze()
        {
            if (Background.IsFrozen) return;
            Background.Freeze(); Tick.Freeze(); Label.Freeze(); Edge.Freeze();
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Canvas.Session?.Document is null) return;
        // A horizontal ruler (along the top) makes horizontal guides.
        Canvas.BeginNewGuide(Axis == GuideAxis.Horizontal ? GuideAxis.Horizontal : GuideAxis.Vertical);
        e.Handled = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Background, null, new Rect(0, 0, ActualWidth, ActualHeight));
        bool horizontal = Axis == GuideAxis.Horizontal;
        if (horizontal) dc.DrawLine(Edge, new Point(0, ActualHeight - 0.5), new Point(ActualWidth, ActualHeight - 0.5));
        else dc.DrawLine(Edge, new Point(ActualWidth - 0.5, 0), new Point(ActualWidth - 0.5, ActualHeight));
        var session = Canvas.Session;
        if (session?.Document is not { } document) return;
        double ppp = session.Viewport.PointsPerPixel;
        if (ppp <= 0) return;
        // Where the canvas sits relative to this ruler: the ruler shares the canvas's other axis.
        var offset = Canvas.TranslatePoint(new Point(0, 0), this);
        var origin = session.Viewport.ViewPoint(PointD.Zero, document.Size);
        double start = horizontal ? origin.X + offset.X : origin.Y + offset.Y;
        double length = horizontal ? ActualWidth : ActualHeight;
        double[] steps = { 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000, 50000 };
        double major = steps.FirstOrDefault(s => s * ppp >= 60, 100000);
        double minor = major / (major >= 10 && (major / 10) * ppp >= 5 ? 10 : 5);
        double firstDoc = Math.Floor(-start / ppp / major) * major;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (double value = firstDoc; ; value += minor)
        {
            double pos = start + value * ppp;
            if (pos > length) break;
            if (pos < -1) continue;
            bool isMajor = Math.Abs(value / major - Math.Round(value / major)) < 1e-6;
            double tickLength = isMajor ? Thickness : Thickness * 0.3;
            double snapped = Math.Round(pos) + 0.5;
            if (horizontal) dc.DrawLine(Tick, new Point(snapped, Thickness - tickLength), new Point(snapped, Thickness));
            else dc.DrawLine(Tick, new Point(Thickness - tickLength, snapped), new Point(Thickness, snapped));
            if (isMajor)
            {
                var text = new FormattedText(value.ToString("0", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, Face, 9, Label, dpi);
                if (horizontal) dc.DrawText(text, new Point(snapped + 3, 1));
                else
                {
                    dc.PushTransform(new RotateTransform(-90, 1, snapped - 3));
                    dc.DrawText(text, new Point(1, snapped - 3));
                    dc.Pop();
                }
            }
        }
    }
}
