using Compositor.Core.CameraRaw;
using Compositor.Core.Geometry;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Core.Editing;

/// <summary>
/// A panel's own drag on the canvas, taking the press, drag and release instead of the tool: Camera Raw's targeted
/// adjustment and Guided Upright. Points are in document coordinates; <c>view</c> ones in view points.
/// </summary>
public interface ICanvasGesture
{
    void Begin(PointD document, PointD view);
    void Drag(PointD document, PointD view);
    void End(PointD document, PointD view);
    /// <summary>Lines to draw over the canvas, in document coordinates (the guides so far and the one being drawn).</summary>
    IReadOnlyList<(PointD Start, PointD End)> Lines { get; }
    /// <summary>A crosshair rather than the arrow.</summary>
    bool Crosshair { get; }
}

public sealed partial class EditorSession
{
    private ICanvasGesture? _canvasGesture;
    /// <summary>Set while a panel's canvas tool is armed: presses on the canvas go to it instead of the tool.</summary>
    public ICanvasGesture? CanvasGesture { get => _canvasGesture; set { _canvasGesture = value; OnPropertyChanged(); Invalidate(); } }

    /// <summary>The pointer moved over the canvas (null when it left), in document coordinates.</summary>
    public event Action<PointD?>? PointerMoved;

    public void ReportPointer(PointD? document) => PointerMoved?.Invoke(document);

    /// <summary>Changes what the Camera Raw preview shows (clipping and mask views, warnings, a range, sections off).</summary>
    public void SetCameraRawView(CameraRawView view)
    {
        if (AdjustmentEdit is not { Filter: Filters.FilterKind.CameraRaw } edit || edit.RawView == view) return;
        edit.RawView = view;
        SchedulePreview(edit);
    }

    /// <summary>
    /// The color the canvas shows at a document point (straight RGB, 0…1): the Camera Raw preview where there is one,
    /// the layer's own pixels otherwise. Null outside the layer or where it's clear.
    /// </summary>
    public (double Red, double Green, double Blue)? CameraRawColorAt(PointD point)
    {
        if (AdjustmentEdit is not { Filter: Filters.FilterKind.CameraRaw } edit) return null;
        if (edit.Preview && edit.PreviewImage is { } image && edit.PreviewTransform is { } placed)
            return ColorAt(image, placed, point);
        if (edit.Source is not { } source) return null;
        return ColorAt(source.Image, edit.Transform, point);
    }

    private static (double, double, double)? ColorAt(SKImage image, LayerTransform placed, PointD point)
    {
        var map = placed.PixelToDocument(image.Width, image.Height);
        if (!map.IsInvertible) return null;
        var pixel = map.Invert().Apply(point);
        int x = (int)Math.Floor(pixel.X), y = (int)Math.Floor(pixel.Y);
        if (x < 0 || y < 0 || x >= image.Width || y >= image.Height) return null;
        using var pixmap = image.PeekPixels();
        if (pixmap is null) return null;
        var bytes = pixmap.GetPixelSpan();
        int i = y * pixmap.RowBytes + x * 4;
        double alpha = bytes[i + 3];
        if (alpha == 0) return null;
        return (Math.Min(1, bytes[i] / alpha), Math.Min(1, bytes[i + 1] / alpha), Math.Min(1, bytes[i + 2] / alpha));
    }

    /// <summary>A document point as a fraction of the Camera Raw layer's width and height (0…1, y down).</summary>
    public PointD? CameraRawLayerPoint(PointD document)
    {
        if (AdjustmentEdit is not { Filter: Filters.FilterKind.CameraRaw, Source: { } source } edit) return null;
        var map = edit.Transform.PixelToDocument(source.Width, source.Height);
        if (!map.IsInvertible) return null;
        var pixel = map.Invert().Apply(document);
        return new PointD(pixel.X / source.Width, pixel.Y / source.Height);
    }

    /// <summary>Where a fraction of the Camera Raw layer (0…1, y down) is on the document.</summary>
    public PointD? CameraRawDocumentPoint(PointD fraction)
    {
        if (AdjustmentEdit is not { Filter: Filters.FilterKind.CameraRaw, Source: { } source } edit) return null;
        return edit.Transform.PixelToDocument(source.Width, source.Height).Apply(fraction.X * source.Width, fraction.Y * source.Height);
    }
}
