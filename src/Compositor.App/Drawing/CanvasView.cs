using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Compositor.App.Services;
using Compositor.Core.Editing;
using Compositor.Core.Geometry;
using Compositor.Core.Model;
using Compositor.Core.Painting;
using Compositor.Core.Rendering;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace Compositor.App.Drawing;

/// <summary>Where the transform box's handles are on screen, and what a press there does. Mirrors TransformOverlayGeometry.</summary>
public sealed class TransformGeometry
{
    public PointD[] Handles { get; }
    public PointD RotationHandle { get; }
    public double Radians { get; }

    /// <summary>A free distortion: the corners move on their own, so the handles only distort (no rotation).</summary>
    public bool Distorting { get; }

    public TransformGeometry(LayerTransform transform, Func<PointD, PointD> toView)
    {
        Handles = LayerTransform.Handles.Select(h => toView(transform.Point(h))).ToArray();
        Radians = transform.Radians;
        RotationHandle = new PointD(Handles[1].X + Math.Sin(Radians) * 28, Handles[1].Y - Math.Cos(Radians) * 28);
    }

    /// <summary>A distortion's box: its four corners (document pixels, handle order) and the midpoints between them.</summary>
    public TransformGeometry(PointD[] corners, Func<PointD, PointD> toView)
    {
        var c = corners.Select(toView).ToArray();
        static PointD Mid(PointD a, PointD b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);
        Handles = new[] { c[0], Mid(c[0], c[1]), c[1], Mid(c[1], c[2]), c[2], Mid(c[2], c[3]), c[3], Mid(c[3], c[0]) };
        Radians = Math.Atan2(c[1].Y - c[0].Y, c[1].X - c[0].X);
        RotationHandle = new PointD(double.NaN, double.NaN);
        Distorting = true;
    }

    public (TransformDragMode Mode, int Index)? Hit(PointD point)
    {
        bool Near(PointD other) => point.DistanceTo(other) <= 10;
        var handleMode = Distorting ? TransformDragMode.Distort : TransformDragMode.Resize;
        if (!Distorting && Near(RotationHandle)) return (TransformDragMode.Rotate, 0);
        for (int i = 0; i < Handles.Length; i++) if (Near(Handles[i])) return (handleMode, i);
        foreach (var (start, end, handle) in new[] { (0, 2, 1), (2, 4, 3), (4, 6, 5), (6, 0, 7) })
        {
            var a = Handles[start]; var b = Handles[end];
            double dx = b.X - a.X, dy = b.Y - a.Y, ls = dx * dx + dy * dy;
            if (ls <= 0) continue;
            double t = ((point.X - a.X) * dx + (point.Y - a.Y) * dy) / ls;
            if (t >= 0 && t <= 1 && Math.Sqrt(Math.Pow(point.X - a.X - t * dx, 2) + Math.Pow(point.Y - a.Y - t * dy, 2)) <= 6)
                return (handleMode, handle);
        }
        if (Distorting) return null;
        // Just outside a corner rotates, as in Photoshop.
        foreach (var corner in new[] { 0, 2, 4, 6 })
        {
            double d = point.DistanceTo(Handles[corner]);
            if (d > 10 && d <= 26 && !Contains(point)) return (TransformDragMode.Rotate, 0);
        }
        return null;
    }

    public bool Contains(PointD p)
    {
        // Point in the quadrilateral of the four corners.
        var c = new[] { Handles[0], Handles[2], Handles[4], Handles[6] };
        bool inside = false;
        for (int i = 0, j = 3; i < 4; j = i++)
        {
            if ((c[i].Y > p.Y) != (c[j].Y > p.Y) && p.X < (c[j].X - c[i].X) * (p.Y - c[i].Y) / (c[j].Y - c[i].Y) + c[i].X) inside = !inside;
        }
        return inside;
    }

    public Cursor ResizeCursor(int index)
    {
        double angle = Math.Atan2(Handles[2].Y - Handles[0].Y, Handles[2].X - Handles[0].X);
        double[] offsets = { Math.PI / 4, Math.PI / 2, 3 * Math.PI / 4, 0, Math.PI / 4, Math.PI / 2, 3 * Math.PI / 4, 0 };
        int direction = ((int)Math.Round((angle + offsets[index]) / (Math.PI / 4)) % 4 + 4) % 4;
        return direction switch
        {
            0 => Cursors.SizeWE,
            1 => Cursors.SizeNWSE,
            2 => Cursors.SizeNS,
            _ => Cursors.SizeNESW,
        };
    }
}

/// <summary>
/// The canvas: draws the document with the session's previews and overlays, and turns pointer input into the
/// active tool's edits. Mirrors EditorCanvas.swift (CanvasView) and TransformOverlay.swift.
/// </summary>
public sealed partial class CanvasView : SKElement
{
    private enum Drag
    {
        None, Pan, ZoomScrub, TransformMove, TransformHandle, SelectionMove, Marquee, Lasso, Brush, Eyedropper,
        CropCreate, CropMove, CropResize, Guide, NewGuide, BrushTip,
        GradientEnd, GradientStart, Shape, TypeBox, TypeSelect, Gesture,
    }

    private EditorSession? _session;
    private double _dpi = 1;
    private Drag _drag;
    private PointD _dragStartView;
    private PointD _dragStartDocument;
    private TransformDrag? _transformDrag;
    private RectD _cropOriginal;
    private int _cropHandle;
    private Guid? _guideId;
    private GuideAxis _guideAxis;
    private double _zoomStart;
    private bool _zoomMoved;
    private (double Diameter, double Hardness) _tipStart;
    private PointD? _hover;
    private bool _spaceHeld;
    private bool _pointerInside;
    private bool _marqueeSquareArmed;

    private SKImage? _cache;
    private object? _cacheKey;
    private SKRectI _cacheRegion;
    private float _antsPhase;
    private readonly DispatcherTimer _antsTimer;
    private SKShader? _checker;
    private double _checkerDpi;

    public event EventHandler? ViewChanged;

    public CanvasView()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        AllowDrop = true;
        _antsTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(120) };
        _antsTimer.Tick += (_, _) =>
        {
            if (_session?.DisplayedSelection is { IsEmpty: false } || _session?.LassoDraft is not null)
            {
                _antsPhase = (_antsPhase + 1) % 8;
                InvalidateVisual();
            }
        };
        _antsTimer.Start();
        SizeChanged += (_, _) => { SyncViewport(); InvalidateVisual(); };
    }

    public EditorSession? Session
    {
        get => _session;
        set
        {
            if (ReferenceEquals(_session, value)) return;
            if (_session is not null)
            {
                _session.DocumentChanged -= OnSessionChanged;
                _session.CanvasInvalidated -= OnSessionChanged;
                _session.Viewport.Changed -= OnViewportChanged;
                _session.PropertyChanged -= OnSessionProperty;
            }
            _session = value;
            if (_session is not null)
            {
                _session.DocumentChanged += OnSessionChanged;
                _session.CanvasInvalidated += OnSessionChanged;
                _session.Viewport.Changed += OnViewportChanged;
                _session.PropertyChanged += OnSessionProperty;
            }
            _drag = Drag.None;
            ReleaseCache();
            SyncViewport();
            SyncTextInput();
            UpdateCursor();
            InvalidateVisual();
        }
    }

    private void OnSessionChanged(object? sender, EventArgs e) => InvalidateVisual();
    private void OnViewportChanged(object? sender, EventArgs e) { InvalidateVisual(); _textInput?.Place(); ViewChanged?.Invoke(this, EventArgs.Empty); }

    private void OnSessionProperty(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EditorSession.Tool) or nameof(EditorSession.BrushMode) or nameof(EditorSession.CanvasSampler) or nameof(EditorSession.CanvasGesture)) UpdateCursor();
        if (e.PropertyName == nameof(EditorSession.Document)) SyncViewport();
        if (e.PropertyName == nameof(EditorSession.TextDraft)) SyncTextInput();
    }

    public bool SpaceHeld
    {
        get => _spaceHeld;
        set { if (_spaceHeld == value) return; _spaceHeld = value; UpdateCursor(); InvalidateVisual(); }
    }

    private void SyncViewport()
    {
        if (_session is null || ActualWidth <= 0 || ActualHeight <= 0) return;
        var source = PresentationSource.FromVisual(this);
        double dpi = source?.CompositionTarget?.TransformToDevice.M11 ?? _dpi;
        var size = new SizeD(ActualWidth, ActualHeight);
        if (_session.Viewport.ViewSize != size || Math.Abs(_session.Viewport.BackingScale - Math.Max(1, dpi)) > 1e-6)
            _session.Viewport.Resize(size, dpi, _session.Document?.Size);
        _dpi = dpi;
    }

    private void ReleaseCache()
    {
        _cache?.Dispose();
        _cache = null;
        _cacheKey = null;
    }

    // MARK: Drawing

    private static readonly SKColor Surround = new(27, 27, 27);
    public const double CrispZoom = 2, PixelGridZoom = 8;

    protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
    {
        base.OnPaintSurface(e);
        var canvas = e.Surface.Canvas;
        var session = _session;
        _dpi = ActualWidth > 0 ? e.Info.Width / ActualWidth : 1;
        canvas.Clear(session?.CanvasOnly == true ? SKColors.Black : Surround);
        if (session?.Document is not { } document) return;
        SyncViewport();
        var viewport = session.Viewport;
        var docRect = viewport.DocumentRect(document.Size);
        var device = new RectD(docRect.X * _dpi, docRect.Y * _dpi, docRect.Width * _dpi, docRect.Height * _dpi);
        var visible = device.Intersect(new RectD(0, 0, e.Info.Width, e.Info.Height));

        // The document's shadow and transparency checkerboard.
        if (!session.CanvasOnly)
        {
            using var shadow = new SKPaint
            {
                Color = new SKColor(0, 0, 0, 90),
                MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, (float)(7 * _dpi)),
            };
            canvas.DrawRect(device.Offset(0, 3 * _dpi).ToSK(), shadow);
        }
        if (visible.IsNull || visible.IsEmpty) return;
        using (var checker = new SKPaint { Shader = Checker(device.Left, device.Top) })
            canvas.DrawRect(device.ToSK(), checker);

        // The layers, from the cache when nothing that shows them has changed. The cache reaches past the view, so
        // panning only moves it until the view runs off its edge.
        var needed = SKRectI.Ceiling(visible.ToSK());
        var key = CacheKey(session, document);
        var origin = new SKPoint((float)device.Left, (float)device.Top);
        var shifted = SKRectI.Ceiling(new SKRect(
            _cacheRegion.Left + origin.X - _cacheOrigin.X, _cacheRegion.Top + origin.Y - _cacheOrigin.Y,
            _cacheRegion.Right + origin.X - _cacheOrigin.X, _cacheRegion.Bottom + origin.Y - _cacheOrigin.Y));
        bool reusable = _cache is not null && Equals(key, _cacheKey) && shifted.Contains(needed);
        if (!reusable)
        {
            _cache?.Dispose();
            int marginX = e.Info.Width / 2, marginY = e.Info.Height / 2;
            var wide = SKRectI.Intersect(SKRectI.Ceiling(device.ToSK()),
                new SKRectI(needed.Left - marginX, needed.Top - marginY, needed.Right + marginX, needed.Bottom + marginY));
            // Only while it stays a few screens' worth; a busy edit (a stroke) redraws just what shows.
            bool busy = session.BrushStroke is not null || session.TransformEdit is not null;
            var region = busy || (long)wide.Width * wide.Height > 4L * e.Info.Width * e.Info.Height ? needed : wide;
            _cache = RenderComposite(session, document, region);
            _cacheKey = key;
            _cacheRegion = region;
            _cacheOrigin = origin;
            shifted = region;
        }
        if (_cache is not null)
        {
            using var copy = new SKPaint();
            canvas.DrawImage(_cache, _cacheRegion.Left + origin.X - _cacheOrigin.X, _cacheRegion.Top + origin.Y - _cacheOrigin.Y,
                new SKSamplingOptions(SKFilterMode.Nearest), copy);
        }

        canvas.Save();
        canvas.Scale((float)_dpi);
        using (var border = new SKPaint { Color = new SKColor(255, 255, 255, 34), Style = SKPaintStyle.Stroke, StrokeWidth = (float)(1 / _dpi) })
            canvas.DrawRect(docRect.ToSK(), border);
        DrawOverlays(canvas, session, document, docRect);
        canvas.Restore();
    }

    private SKPoint _cacheOrigin;

    private object CacheKey(EditorSession session, CanvasDocument document) => (
        document,
        session.Viewport.Zoom,
        _dpi,
        session.BrushStroke?.Revision ?? -1,
        session.BrushStroke,
        session.TransformEdit,
        session.BlendPreview,
        session.MaskAloneLayer?.Id,
        session.IsMaskSelected,
        session.ActiveLayerId,
        session.AdjustmentPreviewVersion,
        (session.GradientEdit, session.GradientEdit?.Revision),
        (session.TextDraft?.LayerId, session.TextDraft?.Revision),
        (session.WarpStroke, session.WarpStroke?.Revision));

    private SKShader Checker(double x, double y)
    {
        if (_checker is null || _checkerDpi != _dpi)
        {
            _checker?.Dispose();
            int tile = Math.Max(1, (int)Math.Round(10 * _dpi));
            using var bitmap = new SKBitmap(tile * 2, tile * 2);
            using (var c = new SKCanvas(bitmap))
            {
                c.Clear(new SKColor(77, 77, 77));
                using var light = new SKPaint { Color = new SKColor(89, 89, 89) };
                c.DrawRect(0, 0, tile, tile, light);
                c.DrawRect(tile, tile, tile, tile, light);
            }
            _checker = SKShader.CreateBitmap(bitmap, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat);
            _checkerDpi = _dpi;
        }
        return _checker.WithLocalMatrix(SKMatrix.CreateTranslation((float)x, (float)y));
    }

    private SKImage? RenderComposite(EditorSession session, CanvasDocument document, SKRectI region)
    {
        if (region.Width <= 0 || region.Height <= 0) return null;
        using var surface = SKSurface.Create(new SKImageInfo(region.Width, region.Height, SKColorType.Rgba8888, SKAlphaType.Premul, RasterImage.Srgb));
        if (surface is null) return null;
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        var toView = session.Viewport.DocumentToView(document.Size);
        var matrix = SKMatrix.CreateTranslation(-region.Left, -region.Top)
            .PreConcat(SKMatrix.CreateScale((float)_dpi, (float)_dpi))
            .PreConcat(toView.ToSK());
        canvas.SetMatrix(matrix);
        canvas.ClipRect(new SKRect(0, 0, document.Width, document.Height));

        if (session.MaskAloneLayer is { Mask: { } mask } owner)
        {
            DrawMaskAlone(canvas, session, document, owner, mask);
        }
        else
        {
            var options = new RenderOptions { Crisp = session.Viewport.Zoom >= CrispZoom, Preview = true };
            session.BrushStroke?.ApplyPreview(options);
            session.ApplyTransformPreview(options);
            session.ApplyAdjustmentPreview(options);
            session.GradientEdit?.Raster.ApplyPreview(options);
            session.ApplyTextPreview(options);
            session.ApplyWarpPreview(options);
            if (session.BlendPreview is { } preview) options.Edit(preview.LayerId).BlendMode = preview.Mode;
            canvas.SaveLayer();
            DocumentRenderer.Render(canvas, document, options);
            canvas.Restore();
        }
        canvas.Flush();
        return surface.Snapshot();
    }

    /// <summary>The mask as Photoshop shows it after an Alt-click: grayscale across the canvas.</summary>
    private static void DrawMaskAlone(SKCanvas canvas, EditorSession session, CanvasDocument document, Layer owner, LayerMask mask)
    {
        var stroke = session.BrushStroke is { IsMask: true } s && s.Layer.Id == owner.Id ? s : null;
        SKImage image = stroke?.PreviewImage() ?? mask.Asset.Image;
        var placement = stroke is not null ? stroke.TransformFor(stroke.Window) : mask.Placement ?? owner.Transform;
        byte background = LayerMask.Background(mask.Asset);
        using var fill = new SKPaint { Color = new SKColor(background, background, background) };
        canvas.DrawRect(new SKRect(0, 0, document.Width, document.Height), fill);
        canvas.Save();
        canvas.Concat(placement.PixelToDocument(image.Width, image.Height).ToSK());
        using var black = new SKPaint { Color = SKColors.Black };
        canvas.DrawRect(new SKRect(0, 0, image.Width, image.Height), black);
        using var white = new SKPaint { Color = SKColors.White };
        canvas.DrawImage(image, new SKRect(0, 0, image.Width, image.Height),
            session.Viewport.Zoom >= CrispZoom ? new SKSamplingOptions(SKFilterMode.Nearest) : new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), white);
        canvas.Restore();
    }

    private static readonly SKColor Accent = new(47, 123, 246);
    private static readonly SKColor GuideColor = new(0, 200, 255);

    private void DrawOverlays(SKCanvas canvas, EditorSession session, CanvasDocument document, RectD docRect)
    {
        var viewport = session.Viewport;
        double ppp = viewport.PointsPerPixel;
        float hairline = (float)(1 / _dpi);
        PointD V(PointD p) => viewport.ViewPoint(p, document.Size);

        // Pixel grid from 800%.
        if (session.ShowsPixelGrid && viewport.Zoom >= PixelGridZoom)
        {
            var area = docRect.Intersect(new RectD(0, 0, ActualWidth, ActualHeight));
            if (!area.IsNull)
            {
                using var grid = new SKPaint { Color = new SKColor(255, 255, 255, 28), StrokeWidth = hairline, Style = SKPaintStyle.Stroke };
                var first = viewport.DocumentPoint(area.Origin, document.Size);
                var last = viewport.DocumentPoint(new PointD(area.Right, area.Bottom), document.Size);
                for (int x = (int)Math.Ceiling(first.X); x <= (int)Math.Floor(last.X); x++)
                {
                    float vx = (float)V(new PointD(x, 0)).X;
                    canvas.DrawLine(vx, (float)area.Top, vx, (float)area.Bottom, grid);
                }
                for (int y = (int)Math.Ceiling(first.Y); y <= (int)Math.Floor(last.Y); y++)
                {
                    float vy = (float)V(new PointD(0, y)).Y;
                    canvas.DrawLine((float)area.Left, vy, (float)area.Right, vy, grid);
                }
            }
        }

        // Layout grid.
        if (session.ShowsGrid)
        {
            double step = session.GridSpacing / (double)session.GridSubdivisions;
            using var minor = new SKPaint { Color = new SKColor(180, 180, 180, 60), StrokeWidth = hairline, Style = SKPaintStyle.Stroke, PathEffect = SKPathEffect.CreateDash(new[] { 1f, 2f }, 0) };
            using var major = new SKPaint { Color = new SKColor(180, 180, 180, 130), StrokeWidth = hairline, Style = SKPaintStyle.Stroke };
            bool minorVisible = step * ppp >= 4;
            int count = (int)(document.Width / step + 0.001);
            for (int i = 0; i <= count; i++)
            {
                double x = Math.Round(i * step);
                bool isMajor = Math.Abs(x % session.GridSpacing) < 0.001;
                if (!isMajor && !minorVisible) continue;
                float vx = (float)V(new PointD(x, 0)).X;
                canvas.DrawLine(vx, (float)docRect.Top, vx, (float)docRect.Bottom, isMajor ? major : minor);
            }
            count = (int)(document.Height / step + 0.001);
            for (int i = 0; i <= count; i++)
            {
                double y = Math.Round(i * step);
                bool isMajor = Math.Abs(y % session.GridSpacing) < 0.001;
                if (!isMajor && !minorVisible) continue;
                float vy = (float)V(new PointD(0, y)).Y;
                canvas.DrawLine((float)docRect.Left, vy, (float)docRect.Right, vy, isMajor ? major : minor);
            }
        }

        // Guides, across the whole view.
        if (session.ShowsGuides)
        {
            using var guide = new SKPaint { Color = GuideColor, StrokeWidth = hairline, Style = SKPaintStyle.Stroke };
            foreach (var g in document.Guides)
            {
                if (_drag == Drag.Guide && g.Id == _guideId) continue;
                DrawGuide(canvas, g.Axis, g.Position, guide);
            }
            if ((_drag == Drag.Guide || _drag == Drag.NewGuide) && _hover is { } h)
            {
                var doc = viewport.DocumentPoint(h, document.Size);
                DrawGuide(canvas, _guideAxis, Math.Round(_guideAxis == GuideAxis.Vertical ? doc.X : doc.Y), guide);
            }
        }

        if (session.Tool == Tool.Crop) DrawCrop(canvas, session, document);
        else DrawTransformHandles(canvas, session, document);

        // Marching ants.
        if (session.DisplayedSelection is { IsEmpty: false } selection)
        {
            using var path = new SKPath(selection.Path);
            path.Transform(viewport.DocumentToView(document.Size).ToSK());
            using var white = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = hairline, IsAntialias = false };
            using var black = new SKPaint
            {
                Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = hairline, IsAntialias = false,
                PathEffect = SKPathEffect.CreateDash(new[] { 4f, 4f }, _antsPhase),
            };
            canvas.DrawPath(path, white);
            canvas.DrawPath(path, black);
        }

        DrawLassoDraft(canvas, session, document);
        DrawToolOverlays(canvas, session, document);

        // A panel tool's lines (Guided Upright's guides), dark under light so they show on anything.
        if (session.CanvasGesture is { Lines.Count: > 0 } gesture)
        {
            using var under = new SKPaint { Color = new SKColor(0, 0, 0, 150), Style = SKPaintStyle.Stroke, StrokeWidth = 3, IsAntialias = true, StrokeCap = SKStrokeCap.Round };
            using var over = new SKPaint { Color = new SKColor(255, 214, 10), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true, StrokeCap = SKStrokeCap.Round };
            using var dot = new SKPaint { Color = new SKColor(255, 214, 10), Style = SKPaintStyle.Fill, IsAntialias = true };
            foreach (var (start, end) in gesture.Lines)
            {
                var a = V(start).ToSK(); var b = V(end).ToSK();
                canvas.DrawLine(a, b, under);
                canvas.DrawLine(a, b, over);
                canvas.DrawCircle(a, 3, dot);
                canvas.DrawCircle(b, 3, dot);
            }
        }

        // Snap lines while a move is snapped.
        var snaps = session.SnapGuides;
        if ((snaps.Xs.Count > 0 || snaps.Ys.Count > 0) && _drag != Drag.None)
        {
            using var snap = new SKPaint { Color = Accent, StrokeWidth = 1, Style = SKPaintStyle.Stroke };
            foreach (var x in snaps.Xs) { var a = V(new PointD(x, 0)); var b = V(new PointD(x, document.Height)); canvas.DrawLine(a.ToSK(), b.ToSK(), snap); }
            foreach (var y in snaps.Ys) { var a = V(new PointD(0, y)); var b = V(new PointD(document.Width, y)); canvas.DrawLine(a.ToSK(), b.ToSK(), snap); }
        }

        // The brush's outline under the pointer.
        if (session.Tool.IsBrushTool() && !_spaceHeld && (_pointerInside || _drag == Drag.Brush || _drag == Drag.BrushTip) && _hover is { } pointer && !IsPicking)
        {
            var center = _drag == Drag.BrushTip ? _dragStartView : pointer;
            double radius = session.BrushSettings.Diameter / 2 * ppp;
            using var dark = new SKPaint { Color = new SKColor(0, 0, 0, 160), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
            using var light = new SKPaint { Color = new SKColor(255, 255, 255, 220), Style = SKPaintStyle.Stroke, StrokeWidth = 0.8f, IsAntialias = true };
            if (radius >= 3)
            {
                canvas.DrawCircle(center.ToSK(), (float)radius, dark);
                canvas.DrawCircle(center.ToSK(), (float)radius, light);
                if (session.BrushSettings.Hardness < 1 && radius * session.BrushSettings.Hardness >= 3)
                {
                    using var inner = new SKPaint { Color = new SKColor(255, 255, 255, 90), Style = SKPaintStyle.Stroke, StrokeWidth = 0.8f, IsAntialias = true, PathEffect = SKPathEffect.CreateDash(new[] { 2f, 3f }, 0) };
                    canvas.DrawCircle(center.ToSK(), (float)(radius * session.BrushSettings.Hardness), inner);
                }
            }
            if (radius < 6 || _drag == Drag.BrushTip)
            {
                float cx = (float)center.X, cy = (float)center.Y;
                canvas.DrawLine(cx - 6, cy, cx + 6, cy, dark);
                canvas.DrawLine(cx, cy - 6, cx, cy + 6, dark);
                canvas.DrawLine(cx - 6, cy, cx + 6, cy, light);
                canvas.DrawLine(cx, cy - 6, cx, cy + 6, light);
            }
        }
    }

    private void DrawGuide(SKCanvas canvas, GuideAxis axis, double position, SKPaint paint)
    {
        if (_session?.Document is not { } document) return;
        var v = _session.Viewport.ViewPoint(new PointD(position, position), document.Size);
        if (axis == GuideAxis.Vertical) canvas.DrawLine((float)v.X, 0, (float)v.X, (float)ActualHeight, paint);
        else canvas.DrawLine(0, (float)v.Y, (float)ActualWidth, (float)v.Y, paint);
    }

    /// <summary>The Move tool's box and handles, when the active layer (or selection of layers) can be transformed.</summary>
    public TransformGeometry? Geometry()
    {
        if (_session is not { Tool: Tool.Move, Document: { } document } session) return null;
        if (!session.ShowsTransformControls && session.TransformEdit?.Persistent != true) return null;
        if (session.TransformEdit is null && !session.CanTransform) return null;
        if (session.TransformEdit?.Corners is { } corners) return new TransformGeometry(corners, p => session.Viewport.ViewPoint(p, document.Size));
        if (session.EditedTransform() is not { } transform) return null;
        return new TransformGeometry(transform, p => session.Viewport.ViewPoint(p, document.Size));
    }

    private void DrawTransformHandles(SKCanvas canvas, EditorSession session, CanvasDocument document)
    {
        if (Geometry() is not { } geometry) return;
        using var line = new SKPaint { Color = Accent, Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
        var h = geometry.Handles;
        using var box = new SKPath();
        box.MoveTo(h[0].ToSK()); box.LineTo(h[2].ToSK()); box.LineTo(h[4].ToSK()); box.LineTo(h[6].ToSK()); box.Close();
        if (!geometry.Distorting) { box.MoveTo(h[1].ToSK()); box.LineTo(geometry.RotationHandle.ToSK()); }
        canvas.DrawPath(box, line);
        using var fill = new SKPaint { Color = SKColors.White, IsAntialias = true };
        foreach (var p in h)
        {
            var r = new SKRect((float)p.X - 3.5f, (float)p.Y - 3.5f, (float)p.X + 3.5f, (float)p.Y + 3.5f);
            canvas.DrawRect(r, fill);
            canvas.DrawRect(r, line);
        }
        if (geometry.Distorting) return;
        canvas.DrawCircle(geometry.RotationHandle.ToSK(), 4, fill);
        canvas.DrawCircle(geometry.RotationHandle.ToSK(), 4, line);
    }

    private RectD? CropViewRect()
    {
        if (_session?.CropRect is not { } rect || _session.Document is not { } document) return null;
        var origin = _session.Viewport.ViewPoint(rect.Origin, document.Size);
        double ppp = _session.Viewport.PointsPerPixel;
        return new RectD(origin.X, origin.Y, rect.Width * ppp, rect.Height * ppp);
    }

    private PointD[] CropHandles(RectD rect) =>
        LayerTransform.Handles.Select(h => new PointD(rect.Left + h.X * rect.Width, rect.Top + h.Y * rect.Height)).ToArray();

    /// <summary>The handle a press at <paramref name="view"/> grabs: a corner, or anywhere along an edge.</summary>
    private int? CropHandleAt(RectD rect, PointD view)
    {
        var handles = CropHandles(rect);
        foreach (var corner in new[] { 0, 2, 4, 6 }) if (handles[corner].DistanceTo(view) <= 10) return corner;
        const double reach = 6;
        bool withinX = view.X > rect.Left + 10 && view.X < rect.Right - 10;
        bool withinY = view.Y > rect.Top + 10 && view.Y < rect.Bottom - 10;
        if (withinX && Math.Abs(view.Y - rect.Top) <= reach) return 1;
        if (withinY && Math.Abs(view.X - rect.Right) <= reach) return 3;
        if (withinX && Math.Abs(view.Y - rect.Bottom) <= reach) return 5;
        if (withinY && Math.Abs(view.X - rect.Left) <= reach) return 7;
        return null;
    }

    private void DrawCrop(SKCanvas canvas, EditorSession session, CanvasDocument document)
    {
        if (CropViewRect() is not { } rect) return;
        using var shade = new SKPaint { Color = new SKColor(0, 0, 0, 150) };
        using var outside = new SKPath { FillType = SKPathFillType.EvenOdd };
        outside.AddRect(new SKRect(0, 0, (float)ActualWidth, (float)ActualHeight));
        outside.AddRect(rect.ToSK());
        canvas.DrawPath(outside, shade);
        using var border = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
        canvas.DrawRect(rect.ToSK(), border);
        using var thirds = new SKPaint { Color = new SKColor(255, 255, 255, 100), Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
        for (int i = 1; i <= 2; i++)
        {
            float x = (float)(rect.Left + rect.Width * i / 3), y = (float)(rect.Top + rect.Height * i / 3);
            canvas.DrawLine(x, (float)rect.Top, x, (float)rect.Bottom, thirds);
            canvas.DrawLine((float)rect.Left, y, (float)rect.Right, y, thirds);
        }
        using var fill = new SKPaint { Color = SKColors.White };
        using var edge = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
        foreach (var p in CropHandles(rect))
        {
            var r = new SKRect((float)p.X - 4, (float)p.Y - 4, (float)p.X + 4, (float)p.Y + 4);
            canvas.DrawRect(r, fill);
            canvas.DrawRect(r, edge);
        }
    }

    private void DrawLassoDraft(SKCanvas canvas, EditorSession session, CanvasDocument document)
    {
        if (session.LassoDraft is not { } draft || draft.Points.Count == 0) return;
        var points = draft.Points.Select(p => session.Viewport.ViewPoint(p, document.Size)).ToList();
        if (draft.Kind == LassoKind.Polygonal && draft.Cursor is { } cursor) points.Add(session.Viewport.ViewPoint(cursor, document.Size));
        using var path = new SKPath();
        if (draft.Kind == LassoKind.Ellipse && points.Count == 4) path.AddOval(RectD.FromPoints(points).ToSK());
        else
        {
            path.MoveTo(points[0].ToSK());
            for (int i = 1; i < points.Count; i++) path.LineTo(points[i].ToSK());
            if (draft.Kind == LassoKind.Rectangle) path.Close();
        }
        using var dark = new SKPaint { Color = new SKColor(0, 0, 0, 200), Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true };
        using var light = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
        canvas.DrawPath(path, dark);
        canvas.DrawPath(path, light);
        if (draft.Kind == LassoKind.Polygonal)
        {
            var first = points[0];
            var r = new SKRect((float)first.X - 4, (float)first.Y - 4, (float)first.X + 4, (float)first.Y + 4);
            using var fill = new SKPaint { Color = SKColors.White };
            using var edge = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
            canvas.DrawRect(r, fill);
            canvas.DrawRect(r, edge);
        }
    }

    // MARK: Pointer input

    private static bool Shift => Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
    private static bool Ctrl => Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
    private static bool Alt => Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) || Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt);

    /// <summary>Alt with the brush picks a color, as in Photoshop.</summary>
    private bool IsPicking => _session?.Tool == Tool.Eyedropper || (_session?.Tool is Tool.Brush or Tool.CloneStamp && Alt && _drag == Drag.None);

    private PointD ViewPoint(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        return new PointD(p.X, p.Y);
    }

    private PointD ToDocument(PointD view) => _session!.Viewport.DocumentPoint(view, _session.Document!.Size);

    protected override void OnMouseEnter(MouseEventArgs e) { base.OnMouseEnter(e); _pointerInside = true; UpdateCursor(); }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _pointerInside = false;
        _session?.ReportPointer(null);
        if (_session?.Tool.IsBrushTool() == true) InvalidateVisual();
        _session?.MoveLassoCursor(null);
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        Keyboard.Focus(this);
        if (_session?.Document is null) return;
        var view = ViewPoint(e);
        _hover = view;
        if (e.ChangedButton == MouseButton.Middle) { StartDrag(Drag.Pan, view); e.Handled = true; return; }
        if (e.ChangedButton == MouseButton.Right)
        {
            // Alt-right-drag sizes the brush (left-right) and softens it (up-down), as in Photoshop.
            if (_session.Tool.IsBrushTool() && Alt)
            {
                _tipStart = (_session.BrushSettings.Diameter, _session.BrushSettings.Hardness);
                StartDrag(Drag.BrushTip, view);
                e.Handled = true;
            }
            return;
        }
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2 && HandleDoubleClick(view)) { e.Handled = true; return; }
        LeftDown(view);
        e.Handled = true;
    }

    private void StartDrag(Drag drag, PointD view)
    {
        _drag = drag;
        _dragStartView = view;
        if (_session?.Document is not null) _dragStartDocument = ToDocument(view);
        CaptureMouse();
    }

    private bool HandleDoubleClick(PointD view)
    {
        var session = _session!;
        if (session.Tool == Tool.Lasso && session.LassoDraft is { Kind: LassoKind.Polygonal })
        {
            session.FinishLasso();
            return true;
        }
        if (session.Tool == Tool.Crop && CropViewRect() is { } rect && rect.Contains(view))
        {
            session.CommitCrop();
            return true;
        }
        if (session.Tool == Tool.Move && session.TransformEdit is { Persistent: true } && Geometry() is { } g && g.Contains(view))
        {
            session.CommitTransform();
            return true;
        }
        if (session.Tool == Tool.Type && TypeDoubleClick(view)) return true;
        if (session.Tool == Tool.Move && session.ActiveLayer is { Text: not null } textLayer && textLayer.Transform.Contains(ToDocument(view)))
        {
            // Double-clicking text with the Move tool opens it for typing, as Photoshop does.
            session.EditActiveText();
            return session.TextDraft is not null;
        }
        return false;
    }

    private void LeftDown(PointD view)
    {
        var session = _session!;
        var doc = ToDocument(view);
        if (_spaceHeld || session.Tool == Tool.Hand) { StartDrag(Drag.Pan, view); return; }
        // An adjustment panel's eyedropper takes the click.
        if (session.CanvasSampler is { } sampler) { sampler(doc); return; }
        // So does a panel's own canvas tool (Camera Raw's targeted adjustment, Guided Upright).
        if (session.CanvasGesture is { } gesture) { StartDrag(Drag.Gesture, view); gesture.Begin(doc, view); InvalidateVisual(); return; }
        switch (session.Tool)
        {
            case Tool.Zoom:
                _zoomStart = session.Viewport.Zoom;
                _zoomMoved = false;
                StartDrag(Drag.ZoomScrub, view);
                break;
            case Tool.Move:
                MoveDown(view, doc);
                break;
            case Tool.Marquee:
            case Tool.Lasso:
                SelectionDown(view, doc);
                break;
            case Tool.Wand:
                if (Ctrl && TryFloatingDrag(view, doc)) break;
                if (SelectionMoveDown(view, doc)) break;
                // Object mode picks the object under the click, from the subject model.
                if (session.WandMode == WandMode.Object) _ = session.SelectObjectAsync(doc, session.SelectionModeFor(Shift, Alt));
                else session.MagicWandAt(doc, session.SelectionModeFor(Shift, Alt));
                break;
            case Tool.Crop:
                CropDown(view, doc);
                break;
            case Tool.Brush:
            case Tool.SpotHealing:
            case Tool.Remove:
            case Tool.CloneStamp:
            case Tool.Smear:
                if (Alt && session.Tool == Tool.Brush) { SampleColor(doc); StartDrag(Drag.Eyedropper, view); break; }
                // Alt-click with Clone Stamp: where it copies from.
                if (Alt && session.Tool == Tool.CloneStamp) { session.SetCloneSource(doc); break; }
                session.BeginBrush(doc, straightLine: Shift);
                if (session.BrushStroke is not null || session.WarpStroke is not null) StartDrag(Drag.Brush, view);
                break;
            case Tool.Eyedropper:
                SampleColor(doc);
                StartDrag(Drag.Eyedropper, view);
                break;
            case Tool.Gradient:
                GradientDown(view, doc);
                break;
            case Tool.Shape:
                session.BeginShape(doc);
                if (session.ShapeDraft is not null) StartDrag(Drag.Shape, view);
                break;
            case Tool.Type:
                TypeDown(view, doc);
                break;
        }
        UpdateCursor(view);
    }

    private void SampleColor(PointD doc)
    {
        if (_session?.SampleColor(doc) is { } color) _session.ForegroundColor = color;
    }

    private void MoveDown(PointD view, PointD doc)
    {
        var session = _session!;
        // Guides can be dragged with the Move tool.
        if (session.ShowsGuides && !session.LocksGuides && GuideAt(view) is { } guide)
        {
            _guideId = guide.Id;
            _guideAxis = guide.Axis;
            StartDrag(Drag.Guide, view);
            return;
        }
        // A handle of the transform box.
        if (Geometry() is { } geometry && geometry.Hit(view) is { } hit)
        {
            if (session.TransformEdit is null) session.BeginTransform(persistent: false);
            if (session.TransformEdit is { } edit)
            {
                var mode = hit.Mode;
                // Ctrl-dragging a handle distorts, as in Photoshop; once distorted, handles keep distorting.
                if (mode is TransformDragMode.Resize or TransformDragMode.Distort && (Ctrl || edit.Corners is not null) && session.BeginDistort())
                    mode = TransformDragMode.Distort;
                edit = session.TransformEdit!;
                _transformDrag = new TransformDrag(edit.Draft, doc, mode, hit.Index) { OriginalCorners = mode == TransformDragMode.Distort ? edit.Corners : null };
                StartDrag(Drag.TransformHandle, view);
            }
            return;
        }
        // Pixels being transformed: dragging inside moves them.
        if (session.TransformEdit is { } pending && Geometry() is { } box && box.Contains(view))
        {
            _transformDrag = new TransformDrag(pending.Draft, doc, TransformDragMode.Move) { OriginalCorners = pending.Corners };
            StartDrag(Drag.TransformMove, view);
            return;
        }
        if (session.TransformEdit is { Persistent: true }) return;
        // With a selection, the Move tool drags the selected pixels (Alt copies them).
        if (TryFloatingDrag(view, doc)) return;
        // Ctrl-click (or Auto-Select) picks the layer under the pointer.
        if ((Ctrl || session.AutoSelect) && LayerAt(doc) is { } picked && picked != session.ActiveLayerId)
            session.SelectLayer(picked);
        if (Alt && session.CanTransform) session.DuplicateLayers();
        session.BeginTransform(persistent: false);
        if (session.TransformEdit is { } edit2)
        {
            _transformDrag = new TransformDrag(edit2.Draft, doc, TransformDragMode.Move);
            StartDrag(Drag.TransformMove, view);
        }
    }

    private bool TryFloatingDrag(PointD view, PointD doc)
    {
        var session = _session!;
        if (session.TransformEdit is not null || !session.CanTransformSelection || session.Selection?.Contains(doc) != true) return false;
        if (!session.BeginFloatingTransform(duplicate: Alt, persistent: false) || session.TransformEdit is not { } edit) return false;
        _transformDrag = new TransformDrag(edit.Draft, doc, TransformDragMode.Move);
        StartDrag(Drag.TransformMove, view);
        return true;
    }

    /// <summary>The topmost visible layer with a pixel under the point.</summary>
    private Guid? LayerAt(PointD doc)
    {
        if (_session?.Document is not { } document) return null;
        foreach (var layer in document.RenderLayers().Reverse())
        {
            if (layer.Asset is not { } asset || !layer.Transform.Contains(doc)) continue;
            var grid = layer.Transform.PixelToDocument(asset.Width, asset.Height).Invert().Apply(doc);
            int x = (int)Math.Floor(grid.X), y = (int)Math.Floor(grid.Y);
            if (x < 0 || y < 0 || x >= asset.Width || y >= asset.Height) continue;
            using var pixmap = asset.PeekPixels();
            if (pixmap.GetPixelSpan()[y * pixmap.RowBytes + x * 4 + 3] > 10) return layer.Id;
        }
        return null;
    }

    private CanvasGuide? GuideAt(PointD view)
    {
        if (_session?.Document is not { } document) return null;
        foreach (var guide in document.Guides.Reverse())
        {
            var v = _session.Viewport.ViewPoint(new PointD(guide.Position, guide.Position), document.Size);
            if (guide.Axis == GuideAxis.Vertical ? Math.Abs(v.X - view.X) <= 4 : Math.Abs(v.Y - view.Y) <= 4) return guide;
        }
        return null;
    }

    private bool SelectionMoveDown(PointD view, PointD doc)
    {
        var session = _session!;
        if (Shift || Alt || session.SelectionModeChoice != SelectionMode.Replace || !session.CanMoveSelection(doc)) return false;
        if (!session.BeginSelectionMove()) return false;
        StartDrag(Drag.SelectionMove, view);
        return true;
    }

    private void SelectionDown(PointD view, PointD doc)
    {
        var session = _session!;
        var mode = session.SelectionModeFor(Shift, Alt);
        if (session.Tool == Tool.Lasso && session.LassoKind == LassoKind.Polygonal)
        {
            if (session.LassoDraft is { } draft)
            {
                var first = session.Viewport.ViewPoint(draft.Points[0], session.Document!.Size);
                if (draft.Points.Count >= 3 && first.DistanceTo(view) <= 8) session.FinishLasso();
                else session.ExtendLasso(doc);
                return;
            }
            if (Ctrl && TryFloatingDrag(view, doc)) return;
            if (SelectionMoveDown(view, doc)) return;
            session.BeginLasso(doc, mode);
            return;
        }
        if (Ctrl && TryFloatingDrag(view, doc)) return;
        if (SelectionMoveDown(view, doc)) return;
        if (session.Tool == Tool.Marquee)
        {
            var start = session.SnappedPoint(doc, TransformSnap.Distance / session.Viewport.PointsPerPixel);
            session.BeginLasso(start, mode);
            _marqueeSquareArmed = !Shift;
            StartDrag(Drag.Marquee, view);
        }
        else
        {
            session.BeginLasso(doc, mode);
            StartDrag(Drag.Lasso, view);
        }
    }

    private void CropDown(PointD view, PointD doc)
    {
        var session = _session!;
        if (CropViewRect() is { } rect && session.CropRect is { } crop)
        {
            if (CropHandleAt(rect, view) is { } handle)
            {
                _cropOriginal = crop;
                _cropHandle = handle;
                StartDrag(Drag.CropResize, view);
                return;
            }
            // Inside a frame that has been set, a drag moves it; while it is still the whole canvas, a drag draws a new one.
            if (rect.Contains(view) && crop != session.Document!.Bounds)
            {
                _cropOriginal = crop;
                StartDrag(Drag.CropMove, view);
                return;
            }
        }
        StartDrag(Drag.CropCreate, view);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_session?.Document is null) return;
        var view = ViewPoint(e);
        var previous = _hover;
        _hover = view;
        var session = _session;
        var doc = ToDocument(view);
        session.ReportPointer(doc);
        double tolerance = TransformSnap.Distance / session.Viewport.PointsPerPixel;
        switch (_drag)
        {
            case Drag.Gesture:
                session.CanvasGesture?.Drag(doc, view);
                InvalidateVisual();
                break;
            case Drag.Pan:
                if (previous is { } p) session.Viewport.Translate(view.X - p.X, view.Y - p.Y);
                break;
            case Drag.ZoomScrub:
                double dx = view.X - _dragStartView.X;
                if (Math.Abs(dx) > 3) _zoomMoved = true;
                if (_zoomMoved) session.ZoomTo(_zoomStart * Math.Pow(2, dx / 120), _dragStartView);
                break;
            case Drag.TransformMove or Drag.TransformHandle when _transformDrag?.OriginalCorners is not null:
                if (_transformDrag.CornersTo(doc, Shift) is { } corners) session.PreviewCorners(corners);
                break;
            case Drag.TransformMove when _transformDrag is not null && session.TransformEdit is { } edit:
            {
                var draft = _transformDrag.UpdatedTo(doc, session.LocksTransformRatio, Shift).Rounded();
                var moving = session.TransformEdit.Group?.Originals.Keys.ToHashSet() ?? new HashSet<Guid> { edit.LayerId };
                session.PreviewTransform(session.SnappedMove(draft, moving, tolerance));
                break;
            }
            case Drag.TransformHandle when _transformDrag is not null:
            {
                var draft = _transformDrag.UpdatedTo(doc, session.LocksTransformRatio, Shift, fromCenter: Alt).Rounded();
                session.PreviewTransform(draft);
                break;
            }
            case Drag.SelectionMove:
            {
                double ox = doc.X - _dragStartDocument.X, oy = doc.Y - _dragStartDocument.Y;
                if (Shift) { if (Math.Abs(ox) >= Math.Abs(oy)) oy = 0; else ox = 0; }
                session.MoveSelection(ox, oy);
                break;
            }
            case Drag.Marquee:
            {
                // Shift pressed mid-drag makes a square (or circle); Alt grows it from the center.
                if (!Shift) _marqueeSquareArmed = true;
                var corner = session.SnappedPoint(doc, tolerance);
                session.DragMarquee(corner, square: Shift && _marqueeSquareArmed, fromCenter: Alt && session.LassoDraft?.Mode != SelectionMode.Subtract);
                break;
            }
            case Drag.Lasso:
                session.ExtendLasso(doc);
                break;
            case Drag.Brush:
                session.ContinueBrush(Shift ? Constrain(doc) : doc);
                break;
            case Drag.Eyedropper:
                SampleColor(doc);
                break;
            case Drag.CropCreate:
                session.CropRect = CropGeometry.Create(_dragStartDocument, doc, session.CropRatio, symmetric: Alt);
                break;
            case Drag.CropMove:
                session.CropRect = CropGeometry.Snapped(_cropOriginal.Offset(doc.X - _dragStartDocument.X, doc.Y - _dragStartDocument.Y));
                break;
            case Drag.CropResize:
            {
                var t = new LayerTransform(_cropOriginal.Origin, _cropOriginal.Size);
                var next = new TransformDrag(t, _dragStartDocument, TransformDragMode.Resize, _cropHandle)
                    .UpdatedTo(doc, session.CropRatio is not null, false, fromCenter: Alt);
                var rect = CropGeometry.Snapped(new RectD(next.Origin.X, next.Origin.Y, next.Size.Width, next.Size.Height));
                if (CropGeometry.Valid(rect)) session.CropRect = rect;
                break;
            }
            case Drag.Guide:
            case Drag.NewGuide:
                InvalidateVisual();
                break;
            case Drag.BrushTip:
            {
                double diameter = Math.Clamp(Math.Round(_tipStart.Diameter + (view.X - _dragStartView.X) * 2 / session.Viewport.PointsPerPixel), 1, 2000);
                double hardness = Math.Clamp(_tipStart.Hardness - (view.Y - _dragStartView.Y) / 200, 0, 1);
                session.BrushSettings = session.BrushSettings with { Diameter = diameter, Hardness = hardness };
                break;
            }
            case Drag.GradientEnd:
            case Drag.GradientStart:
            case Drag.Shape:
            case Drag.TypeBox:
            case Drag.TypeSelect:
                ToolDragMove(_drag, view, doc);
                break;
            default:
                if (session.LassoDraft is { Kind: LassoKind.Polygonal }) session.MoveLassoCursor(doc);
                UpdateCursor(view);
                if (session.Tool.IsBrushTool()) InvalidateVisual();
                break;
        }
    }

    /// <summary>Shift while painting keeps the stroke on the axis it started along.</summary>
    private PointD Constrain(PointD doc)
    {
        double dx = doc.X - _dragStartDocument.X, dy = doc.Y - _dragStartDocument.Y;
        return Math.Abs(dx) >= Math.Abs(dy) ? new PointD(doc.X, _dragStartDocument.Y) : new PointD(_dragStartDocument.X, doc.Y);
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_session?.Document is null) { ReleaseMouseCapture(); return; }
        var view = ViewPoint(e);
        var session = _session;
        var doc = ToDocument(view);
        var drag = _drag;
        _drag = Drag.None;
        ReleaseMouseCapture();
        switch (drag)
        {
            case Drag.Gesture:
                session.CanvasGesture?.End(doc, view);
                InvalidateVisual();
                break;
            case Drag.ZoomScrub:
                if (!_zoomMoved)
                {
                    double target = session.Viewport.KeyboardZoomTarget(Alt ? -1 : 1);
                    session.ZoomTo(target, view);
                }
                break;
            case Drag.TransformMove:
            case Drag.TransformHandle:
                _transformDrag = null;
                session.SnapGuides = (Array.Empty<double>(), Array.Empty<double>());
                if (session.TransformEdit is { Persistent: false }) session.CommitTransform();
                break;
            case Drag.SelectionMove:
                session.EndSelectionMove();
                break;
            case Drag.Marquee:
            case Drag.Lasso:
                session.SnapGuides = (Array.Empty<double>(), Array.Empty<double>());
                session.FinishLasso();
                break;
            case Drag.Brush:
                session.FinishBrush();
                break;
            case Drag.CropCreate when session.CropRect is { } created && (created.Width < 2 || created.Height < 2):
                session.CropRect = session.Document.Bounds;
                break;
            case Drag.Guide when _guideId is { } id:
            {
                double position = _guideAxis == GuideAxis.Vertical ? doc.X : doc.Y;
                bool offCanvas = view.X < 0 || view.Y < 0 || view.X > ActualWidth || view.Y > ActualHeight;
                session.MoveGuide(id, position, remove: offCanvas);
                _guideId = null;
                break;
            }
            case Drag.NewGuide:
            {
                bool inside = view.X >= 0 && view.Y >= 0 && view.X <= ActualWidth && view.Y <= ActualHeight;
                if (inside) session.AddGuide(_guideAxis, _guideAxis == GuideAxis.Vertical ? doc.X : doc.Y);
                break;
            }
            case Drag.GradientEnd:
            case Drag.GradientStart:
            case Drag.Shape:
            case Drag.TypeBox:
            case Drag.TypeSelect:
                ToolDragEnd(drag, view, doc);
                break;
        }
        UpdateCursor(view);
        InvalidateVisual();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_drag == Drag.None || _session is null) return;
        // Capture lost mid-drag (a dialog, Alt-Tab): finish what was started rather than leave it hanging.
        var drag = _drag;
        _drag = Drag.None;
        switch (drag)
        {
            case Drag.Brush: _session.FinishBrush(); break;
            case Drag.SelectionMove: _session.EndSelectionMove(); break;
            case Drag.Marquee:
            case Drag.Lasso: _session.FinishLasso(); break;
            case Drag.TransformMove:
            case Drag.TransformHandle:
                if (_session.TransformEdit is { Persistent: false }) _session.CommitTransform();
                break;
            case Drag.Shape: _session.FinishShape(); break;
            case Drag.GradientEnd:
            case Drag.GradientStart: _session.EndGradientDrag(); break;
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_session?.Document is null) return;
        var view = ViewPoint(e);
        if (Ctrl || Alt)
        {
            _session.ZoomTo(_session.Viewport.Zoom * Math.Pow(1.0018, e.Delta), view);
        }
        else if (Shift)
        {
            _session.Viewport.Translate(e.Delta * 0.6, 0);
        }
        else
        {
            _session.Viewport.Translate(0, e.Delta * 0.6);
        }
        e.Handled = true;
    }

    /// <summary>A guide dragged out of a ruler (the rulers start it, the canvas follows the pointer).</summary>
    public void BeginNewGuide(GuideAxis axis)
    {
        if (_session?.Document is null) return;
        _guideAxis = axis;
        _drag = Drag.NewGuide;
        CaptureMouse();
    }

    // MARK: Cursors

    private static Cursor? _crossCursor, _eyedropperCursor, _zoomInCursor, _zoomOutCursor, _moveCursor, _rotateCursor;

    private static Cursor Cross => _crossCursor ??= Bitmaps.MakeCursor(c =>
    {
        using var dark = new SKPaint { Color = SKColors.Black, StrokeWidth = 3, IsAntialias = true };
        using var light = new SKPaint { Color = SKColors.White, StrokeWidth = 1, IsAntialias = true };
        foreach (var p in new[] { dark, light })
        {
            c.DrawLine(16, 5, 16, 13, p); c.DrawLine(16, 19, 16, 27, p);
            c.DrawLine(5, 16, 13, 16, p); c.DrawLine(19, 16, 27, 16, p);
        }
    }, 16, 16);

    private static Cursor EyedropperCursor => _eyedropperCursor ??= Bitmaps.MakeCursor(c =>
    {
        using var path = SKPath.ParseSvgPathData("M20,6 L26,12 M22.5,9.5 L26,6 C27.2,4.8 29,6.6 27.8,7.8 L24.3,11.3 M21.8,10.2 L9,23 L6.8,23.6 L5.2,27 L8.6,25.4 L9.2,23.2");
        using var dark = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = 3.2f, IsAntialias = true, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
        using var light = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 1.4f, IsAntialias = true, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
        c.DrawPath(path, dark);
        c.DrawPath(path, light);
    }, 5, 27);

    private static Cursor ZoomCursor(bool zoomOut) => (zoomOut ? _zoomOutCursor : _zoomInCursor) ?? MakeZoom(zoomOut);

    private static Cursor MakeZoom(bool zoomOut)
    {
        var cursor = Bitmaps.MakeCursor(c =>
        {
            using var dark = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = 3.4f, IsAntialias = true, StrokeCap = SKStrokeCap.Round };
            using var light = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 1.6f, IsAntialias = true, StrokeCap = SKStrokeCap.Round };
            foreach (var p in new[] { dark, light })
            {
                c.DrawCircle(12, 12, 8, p);
                c.DrawLine(18, 18, 26, 26, p);
                c.DrawLine(8, 12, 16, 12, p);
                if (!zoomOut) c.DrawLine(12, 8, 12, 16, p);
            }
        }, 12, 12);
        if (zoomOut) _zoomOutCursor = cursor; else _zoomInCursor = cursor;
        return cursor;
    }

    private static Cursor MoveCursor => _moveCursor ??= Bitmaps.MakeCursor(c =>
    {
        using var arrow = SKPath.ParseSvgPathData("M4,3 L4,21 L8.5,16.8 L11.5,23.5 L14.2,22.3 L11.3,15.8 L17.5,15.8 Z");
        using var fill = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        using var edge = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f, IsAntialias = true };
        c.DrawPath(arrow, fill);
        c.DrawPath(arrow, edge);
        using var cross = SKPath.ParseSvgPathData("M23,14 L23,28 M16,21 L30,21 M23,14 L21,16 M23,14 L25,16 M23,28 L21,26 M23,28 L25,26 M16,21 L18,19 M16,21 L18,23 M30,21 L28,19 M30,21 L28,23");
        using var dark = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 3, IsAntialias = true, StrokeCap = SKStrokeCap.Round };
        using var light = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = 1.3f, IsAntialias = true, StrokeCap = SKStrokeCap.Round };
        c.DrawPath(cross, dark);
        c.DrawPath(cross, light);
    }, 4, 3);

    private static Cursor RotateCursor => _rotateCursor ??= Bitmaps.MakeCursor(c =>
    {
        using var arc = SKPath.ParseSvgPathData("M8,22 A11,11 0 0 1 22,8 M22,8 L18,5 M22,8 L19,12 M8,22 L5,18 M8,22 L12,25");
        using var dark = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 3.4f, IsAntialias = true, StrokeCap = SKStrokeCap.Round };
        using var light = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true, StrokeCap = SKStrokeCap.Round };
        c.DrawPath(arc, dark);
        c.DrawPath(arc, light);
    }, 15, 15);

    public void UpdateCursor(PointD? at = null)
    {
        var session = _session;
        if (session?.Document is null) { Cursor = Cursors.Arrow; return; }
        var view = at ?? _hover;
        if (_drag == Drag.Pan || _spaceHeld || session.Tool == Tool.Hand) { Cursor = _drag == Drag.Pan ? Cursors.ScrollAll : Cursors.Hand; return; }
        if (session.CanvasSampler is not null) { Cursor = EyedropperCursor; return; }
        if (session.CanvasGesture is { } gesture) { Cursor = gesture.Crosshair ? Cross : Cursors.SizeNS; return; }
        switch (session.Tool)
        {
            case Tool.Brush:
                Cursor = IsPicking ? EyedropperCursor : Cursors.None;
                return;
            case Tool.Eyedropper:
                Cursor = EyedropperCursor;
                return;
            case Tool.Gradient:
            case Tool.Shape:
                Cursor = Cross;
                return;
            case Tool.Type:
                Cursor = view is { } pointerAt && session.TextDraft is not null && !InsideTextDraft(pointerAt) ? Cursors.Arrow : Cursors.IBeam;
                return;
            case Tool.Zoom:
                Cursor = ZoomCursor(Alt);
                return;
            case Tool.Marquee:
            case Tool.Lasso:
            case Tool.Wand:
                if (view is { } p && _drag == Drag.None && session.Document is not null)
                {
                    var doc = ToDocument(p);
                    if (Ctrl && session.CanTransformSelection && session.Selection?.Contains(doc) == true) { Cursor = MoveCursor; return; }
                    if (!Shift && !Alt && session.SelectionModeChoice == SelectionMode.Replace && session.CanMoveSelection(doc) && session.LassoDraft is null)
                    { Cursor = Cursors.SizeAll; return; }
                }
                Cursor = Cross;
                return;
            case Tool.Crop:
                if (view is { } c && CropViewRect() is { } rect)
                {
                    if (CropHandleAt(rect, c) is { } i) { Cursor = i % 4 == 0 ? Cursors.SizeNWSE : i % 4 == 2 ? Cursors.SizeNESW : i % 4 == 1 ? Cursors.SizeNS : Cursors.SizeWE; return; }
                    if (rect.Contains(c) && session.CropRect != session.Document!.Bounds) { Cursor = Cursors.SizeAll; return; }
                }
                Cursor = Cross;
                return;
            case Tool.Move:
                if (view is { } m)
                {
                    if (session.ShowsGuides && !session.LocksGuides && GuideAt(m) is { } guide) { Cursor = guide.Axis == GuideAxis.Vertical ? Cursors.SizeWE : Cursors.SizeNS; return; }
                    if (Geometry() is { } geometry && geometry.Hit(m) is { } hit)
                    {
                        Cursor = hit.Mode == TransformDragMode.Rotate ? RotateCursor
                            : hit.Mode == TransformDragMode.Distort || Ctrl ? Cross : geometry.ResizeCursor(hit.Index);
                        return;
                    }
                }
                Cursor = MoveCursor;
                return;
            default:
                Cursor = Cursors.Arrow;
                return;
        }
    }
}
