using System.Windows;
using System.Windows.Input;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace Compositor.App.Drawing;

/// <summary>Zoom and pan shared by Content-Aware Fill's two views, so they always show the same part of the picture.</summary>
public sealed class FillViewport
{
    public double Zoom { get; private set; } = 1;
    /// <summary>The picture point at the middle of the views.</summary>
    public double CenterX { get; private set; }
    public double CenterY { get; private set; }
    public event Action? Changed;

    public void Set(double zoom, double centerX, double centerY)
    {
        Zoom = Math.Clamp(zoom, 0.01, 32);
        CenterX = centerX;
        CenterY = centerY;
        Changed?.Invoke();
    }
}

public enum StrokePhase { Down, Move, Up }

/// <summary>
/// One of Content-Aware Fill's views: the picture, the sampling area in translucent green, the fill area's dashed
/// outline, a brush outline under the pointer, and a busy note. The wheel zooms, the middle button (or Space) pans; the
/// left button paints, reported in picture pixels.
/// </summary>
public sealed class FillView : SKElement
{
    private Point? _pointer;
    private Point _panFrom;
    private bool _panning;

    public FillView(FillViewport viewport)
    {
        Viewport = viewport;
        viewport.Changed += InvalidateVisual;
        Focusable = false;
        ClipToBounds = true;
        SizeChanged += (_, _) => InvalidateVisual();
    }

    public FillViewport Viewport { get; }
    public SKImage? Image { get; set; }
    /// <summary>The sampling area (Alpha8), drawn green.</summary>
    public SKBitmap? Overlay { get; set; }
    public bool ShowsOverlay { get; set; }
    /// <summary>Bumped whenever <see cref="Overlay"/>'s pixels change, so its drawn copy is remade.</summary>
    public int OverlayVersion { get; set; }
    private SKImage? _overlayImage;
    private int _overlayImageVersion = -1;
    public byte OverlayAlpha { get; set; } = 110;
    /// <summary>The fill area's outline, in picture pixels.</summary>
    public SKPath? Outline { get; set; }
    /// <summary>A stroke being painted on the fill area, shown until it's applied.</summary>
    public SKPath? LiveStroke { get; set; }
    public bool LiveStrokeRemoves { get; set; }
    /// <summary>The brush's radius in picture pixels; 0 for no brush outline.</summary>
    public double BrushRadius { get; set; }
    /// <summary>A crosshair rather than the brush (picking an object).</summary>
    public bool Crosshair { get; set; }
    public string? Busy { get; set; }
    /// <summary>Space is held: the left button pans.</summary>
    public bool SpaceHeld { get; set; }
    public bool Paints { get; init; }

    public event Action<StrokePhase, SKPoint>? Stroke;

    public SKPoint ToPicture(Point view) => new(
        (float)((view.X - ActualWidth / 2) / Viewport.Zoom + Viewport.CenterX),
        (float)((view.Y - ActualHeight / 2) / Viewport.Zoom + Viewport.CenterY));

    /// <summary>The zoom that fits the whole picture in this view.</summary>
    public double FitZoom => Image is null || ActualWidth < 10 ? 1
        : Math.Min((ActualWidth - 16) / Image.Width, (ActualHeight - 16) / Image.Height);

    protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(new SKColor(30, 30, 30));
        if (Image is not { } image || ActualWidth <= 0) return;
        float dpi = (float)(e.Info.Width / ActualWidth);
        double zoom = Viewport.Zoom;
        canvas.Save();
        canvas.Scale(dpi);
        canvas.Translate((float)(ActualWidth / 2), (float)(ActualHeight / 2));
        canvas.Scale((float)zoom);
        canvas.Translate((float)-Viewport.CenterX, (float)-Viewport.CenterY);
        var bounds = SKRect.Create(image.Width, image.Height);
        using (var board = new SKPaint { Color = new SKColor(70, 70, 70) }) canvas.DrawRect(bounds, board);
        var sampling = zoom >= 2 ? new SKSamplingOptions(SKFilterMode.Nearest) : new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
        canvas.DrawImage(image, bounds, sampling);
        if (ShowsOverlay && Overlay is { } overlay)
        {
            using var green = new SKPaint { Color = new SKColor(52, 199, 89, OverlayAlpha) };
            if (_overlayImage is null || _overlayImageVersion != OverlayVersion)
            {
                _overlayImage?.Dispose();
                _overlayImage = SKImage.FromBitmap(overlay);
                _overlayImageVersion = OverlayVersion;
            }
            canvas.DrawImage(_overlayImage, bounds, sampling, green);
        }
        float hairline = (float)(1 / zoom);
        if (LiveStroke is { } live)
        {
            using var paint = new SKPaint { Color = LiveStrokeRemoves ? new SKColor(255, 80, 80, 90) : new SKColor(80, 160, 255, 110), IsAntialias = true };
            canvas.DrawPath(live, paint);
        }
        if (Outline is { } outline)
        {
            using var dark = new SKPaint { Color = new SKColor(0, 0, 0, 200), Style = SKPaintStyle.Stroke, StrokeWidth = hairline * 1.5f, IsAntialias = true };
            using var light = new SKPaint
            {
                Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = hairline * 1.5f, IsAntialias = true,
                PathEffect = SKPathEffect.CreateDash(new[] { 5 * hairline, 4 * hairline }, 0),
            };
            canvas.DrawPath(outline, dark);
            canvas.DrawPath(outline, light);
        }
        canvas.Restore();
        canvas.Scale(dpi);
        if (_pointer is { } p && Paints && !SpaceHeld && !_panning)
        {
            using var dark = new SKPaint { Color = new SKColor(0, 0, 0, 170), Style = SKPaintStyle.Stroke, StrokeWidth = 2.5f, IsAntialias = true };
            using var light = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f, IsAntialias = true };
            if (Crosshair)
            {
                float x = (float)p.X, y = (float)p.Y;
                foreach (var paint in new[] { dark, light })
                {
                    canvas.DrawLine(x - 9, y, x + 9, y, paint);
                    canvas.DrawLine(x, y - 9, x, y + 9, paint);
                }
            }
            else if (BrushRadius > 0)
            {
                float r = (float)Math.Max(2, BrushRadius * zoom);
                canvas.DrawCircle((float)p.X, (float)p.Y, r, dark);
                canvas.DrawCircle((float)p.X, (float)p.Y, r, light);
            }
        }
        if (Busy is { Length: > 0 } busy)
        {
            using var font = new SKFont(SKTypeface.FromFamilyName("Microsoft YaHei"), 13);
            using var text = new SKPaint { Color = SKColors.White, IsAntialias = true };
            using var back = new SKPaint { Color = new SKColor(0, 0, 0, 170), IsAntialias = true };
            float width = font.MeasureText(busy);
            canvas.DrawRoundRect(SKRect.Create(10, 10, width + 20, 26), 6, 6, back);
            canvas.DrawText(busy, 20, 28, SKTextAlign.Left, font, text);
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (Image is null) return;
        // Zoom about the pointer: the picture point under it stays put.
        var view = e.GetPosition(this);
        var before = ToPicture(view);
        double zoom = Math.Clamp(Viewport.Zoom * Math.Pow(1.2, e.Delta / 120.0), 0.02, 32);
        double cx = before.X - (view.X - ActualWidth / 2) / zoom, cy = before.Y - (view.Y - ActualHeight / 2) / zoom;
        Viewport.Set(zoom, cx, cy);
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (Image is null) return;
        var view = e.GetPosition(this);
        if (e.ChangedButton == MouseButton.Middle || (e.ChangedButton == MouseButton.Left && (SpaceHeld || !Paints)))
        {
            _panning = true;
            _panFrom = view;
            CaptureMouse();
            Cursor = Cursors.ScrollAll;
            e.Handled = true;
            return;
        }
        if (e.ChangedButton != MouseButton.Left) return;
        CaptureMouse();
        Stroke?.Invoke(StrokePhase.Down, ToPicture(view));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var view = e.GetPosition(this);
        _pointer = view;
        if (_panning)
        {
            double dx = (view.X - _panFrom.X) / Viewport.Zoom, dy = (view.Y - _panFrom.Y) / Viewport.Zoom;
            _panFrom = view;
            Viewport.Set(Viewport.Zoom, Viewport.CenterX - dx, Viewport.CenterY - dy);
            return;
        }
        if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed) Stroke?.Invoke(StrokePhase.Move, ToPicture(view));
        UpdateCursor();
        InvalidateVisual();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        var view = e.GetPosition(this);
        if (_panning)
        {
            _panning = false;
            ReleaseMouseCapture();
            UpdateCursor();
            return;
        }
        if (e.ChangedButton != MouseButton.Left || !IsMouseCaptured) return;
        ReleaseMouseCapture();
        Stroke?.Invoke(StrokePhase.Up, ToPicture(view));
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _pointer = null;
        InvalidateVisual();
    }

    /// <summary>Lets go of everything drawn (the window is closing).</summary>
    public void Release()
    {
        Image = null;
        Overlay = null;
        _overlayImage?.Dispose();
        _overlayImage = null;
        Outline?.Dispose();
        Outline = null;
    }

    public void UpdateCursor() => Cursor = SpaceHeld || !Paints ? Cursors.Hand : Cursors.None;
}
