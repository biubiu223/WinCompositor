using Compositor.Core.Geometry;
using Compositor.Core.Localization;
using Compositor.Core.Model;
using Compositor.Core.Painting;
using Compositor.Core.Pixels;
using Compositor.Core.Rendering;
using SkiaSharp;

namespace Compositor.Core.Editing;

/// <summary>Clone Stamp's options-bar settings. Mirrors CloneSettings.</summary>
public sealed record CloneSettings(bool Aligned = true, bool SampleAllLayers = false);

public sealed partial class EditorSession
{
    private SpotHealingMode _spotHealingMode = SpotHealingMode.ContentAware;
    public SpotHealingMode SpotHealingMode { get => _spotHealingMode; set => SetField(ref _spotHealingMode, value); }

    private CloneSettings _cloneSettings = new();
    public CloneSettings CloneSettings { get => _cloneSettings; set { if (SetField(ref _cloneSettings, value)) CloneOffset = null; } }

    private SmearMode _smearMode = SmearMode.Liquify;
    public SmearMode SmearMode { get => _smearMode; set => SetField(ref _smearMode, value); }

    private double _blurRadius = 5;
    /// <summary>How far Blur softens, in canvas pixels, whatever the brush's size (0.5–50).</summary>
    public double BlurRadius { get => _blurRadius; set => SetField(ref _blurRadius, Math.Clamp(value, 0.5, 50)); }

    private PointD? _cloneSource;
    /// <summary>Where Clone Stamp copies from (Alt-click sets it).</summary>
    public PointD? CloneSource { get => _cloneSource; private set { _cloneSource = value; OnPropertyChanged(); Invalidate(); } }

    /// <summary>Aligned cloning: the offset the first stroke fixed, kept for the strokes after it.</summary>
    public (double X, double Y)? CloneOffset { get; private set; }

    private WarpStroke? _warpStroke;
    /// <summary>A Liquify or Smudge stroke in progress (the canvas shows its working copy in place of the layer).</summary>
    public WarpStroke? WarpStroke { get => _warpStroke; private set { _warpStroke = value; Invalidate(); } }

    /// <summary>Alt-click with Clone Stamp: where it copies from. A new source starts a new alignment.</summary>
    public void SetCloneSource(PointD point)
    {
        if (!point.IsFinite) return;
        CloneSource = point;
        CloneOffset = null;
    }

    /// <summary>The whole-pixel offset a stroke starting at <paramref name="point"/> copies with; null without a source.</summary>
    public (double X, double Y)? CloneStrokeOffset(PointD point)
    {
        if (CloneSource is not { } source) return null;
        return (CloneSettings.Aligned ? CloneOffset : null) ?? (Math.Round(source.X - point.X), Math.Round(source.Y - point.Y));
    }

    /// <summary>Where the source sits for a brush at <paramref name="point"/>, for the canvas's crosshair.</summary>
    public PointD? CloneSamplePoint(PointD point)
    {
        if (CloneSource is not { } source) return null;
        if (CloneOffset is not { } offset || !(CloneSettings.Aligned || BrushStroke is not null)) return source;
        return new PointD(point.X + offset.X, point.Y + offset.Y);
    }

    /// <summary>What a Clone Stamp stroke copies: the layer's own pixels, or the canvas as it shows every layer.</summary>
    private BrushSource? CloneSourceFor(BrushStroke stroke, (double X, double Y) offset)
    {
        if (Document is not { } document) return null;
        if (CloneSettings.SampleAllLayers)
        {
            using var composite = DocumentRenderer.RenderToImage(document);
            var raster = RasterImage.FromImage(composite);
            return new DocumentSource(raster.CopyPixels(), document.Width, document.Height, stroke.GridToDocument, new PointD(offset.X, offset.Y));
        }
        if (stroke.Layer.Asset is not { } asset) return null;
        // The document offset in the layer's own pixels.
        var map = stroke.GridToDocument;
        if (!map.IsInvertible) return null;
        var inverse = map.Invert();
        var grid = inverse.ApplyVector(offset.X, offset.Y);
        return new GridSource(asset.CopyPixels(), asset.Width, asset.Height, 4, 0, 0, grid.X, grid.Y);
    }

    /// <summary>
    /// What a Blur stroke paints: the layer's pixels (or its mask) softened by the Blur radius, measured on the canvas,
    /// taken when the stroke starts — so going over an area again in a new stroke softens it further, as in Photoshop.
    /// </summary>
    private BrushSource? BlurSourceFor(BrushStroke stroke)
    {
        var image = stroke.IsMask ? stroke.Layer.Mask?.Asset : stroke.Layer.Asset;
        if (image is null) return null;
        var map = stroke.GridToDocument;
        double perPixel = Math.Max(1e-6, Math.Sqrt(Math.Abs(map.Determinant)));
        double sigma = Math.Min(BlurRadius / perPixel, Math.Max(image.Width, image.Height) / 2.0);
        int margin = (int)Math.Ceiling(3 * sigma);
        int w = image.Width + 2 * margin, h = image.Height + 2 * margin;
        if ((long)w * h > DocumentLimits.MaxSurfacePixels) return null;
        bool mask = stroke.IsMask;
        var info = mask ? RasterImage.MaskInfo(w, h) : RasterImage.ColorInfo(w, h);
        using var sharp = new SKBitmap(info);
        using (var canvas = new SKCanvas(sharp))
        {
            // Past its pixels a mask keeps its edge tone, so blurring near its edge doesn't pull in the wrong one.
            if (mask && stroke.Layer.Mask is { } owned) canvas.Clear(SKColors.Black.WithAlpha(LayerMask.Background(owned.Asset)));
            else canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
            canvas.DrawImage(image.Image, margin, margin, paint);
        }
        using var soft = new SKBitmap(info);
        using (var canvas = new SKCanvas(soft))
        using (var source = SKImage.FromBitmap(sharp))
        using (var filter = SKImageFilter.CreateBlur((float)sigma, (float)sigma, mask ? SKShaderTileMode.Clamp : SKShaderTileMode.Decal))
        using (var paint = new SKPaint { ImageFilter = filter, BlendMode = SKBlendMode.Src })
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawImage(source, 0, 0, paint);
        }
        var bytes = new byte[w * h * (mask ? 1 : 4)];
        var span = soft.GetPixelSpan();
        int row = w * (mask ? 1 : 4);
        for (int y = 0; y < h; y++) span.Slice(y * soft.RowBytes, row).CopyTo(bytes.AsSpan(y * row));
        return new GridSource(bytes, w, h, mask ? 1 : 4, -margin, -margin) { Replaces = true };
    }

    /// <summary>Spot Healing, Clone Stamp and Blur work on image pixels; healing and cloning have nothing to do on a mask.</summary>
    private string? RetouchRefusal(Tool tool)
    {
        if (IsMaskSelected && tool is Tool.SpotHealing or Tool.CloneStamp or Tool.Remove)
            return L.T("污点修复、仿制图章和抹除工具作用于图层的像素，不能用于蒙版。", "Spot Healing, Clone Stamp and the Remove tool work on a layer’s pixels, not its mask.");
        if (IsMaskSelected && tool == Tool.Smear && SmearMode != SmearMode.Blur)
            return L.T("液化和涂抹作用于图层的像素，不能用于蒙版。", "Liquify and Smudge work on a layer’s pixels, not its mask.");
        if (tool != Tool.Brush && !IsMaskSelected && ActiveLayer?.Asset is null)
            return L.T("这个图层还没有像素可以修饰。", "This layer has no pixels to retouch yet.");
        return null;
    }

    // MARK: Liquify and Smudge

    private void BeginWarp(PointD point)
    {
        if (ActiveLayer is not { Asset: { } asset } layer || Document is not { } document) return;
        FinishOpacityEdit();
        try
        {
            var stroke = new WarpStroke(layer, asset, layer.Transform, document.Width, document.Height, SmearMode, BrushSettings);
            stroke.Append(point);
            WarpStroke = stroke;
        }
        catch (Exception error) { ShowError(L.T("无法涂抹", "Couldn’t smudge"), error.Message); }
    }

    public void CancelWarp()
    {
        _warpStroke?.Dispose();
        WarpStroke = null;
    }

    /// <summary>Paints the finished Smudge or Liquify result into the layer's pixels along the stroke, as one undo step.</summary>
    private void FinishWarp()
    {
        if (WarpStroke is not { } warp || Document is not { } document) return;
        try
        {
            if (warp.Points.Count == 0 || document.Find(warp.Layer.Id) is not { } current
                || !ReferenceEquals(current.Asset, warp.Layer.Asset) || current.Transform != warp.Layer.Transform) return;
            // A hard tip a little wider than the brush covers everything the stroke moved.
            var settings = BrushSettings with { Diameter = Math.Min(BrushSettings.MaxDiameter, warp.Diameter + 4), Hardness = 1, Opacity = 1, Erasing = false };
            var stroke = (BrushStroke)MakeRasterEdit(current, () => new BrushStroke(current, false, settings, document.Width, document.Height));
            stroke.Source = new DocumentSource(warp.Result, warp.Width, warp.Height, stroke.GridToDocument, new PointD(0, 0)) { Replaces = true };
            double spacing = Math.Max(1, warp.Diameter * 0.05);
            PointD? kept = null;
            for (int i = 0; i < warp.Points.Count; i++)
            {
                var point = warp.Points[i];
                if (kept is { } k && i < warp.Points.Count - 1 && point.DistanceTo(k) < spacing) continue;
                stroke.Append(point);
                kept = point;
            }
            stroke.Flush();
            if (stroke.HasPainted) CommitRasterEdit(stroke, warp.Mode.ToName());
            stroke.Release();
        }
        catch (Exception error) { ShowError(L.T("无法涂抹", "Couldn’t smudge"), error.Message); }
        finally { CancelWarp(); }
    }

    /// <summary>Shows a Liquify or Smudge stroke's working copy in place of the layer while it is being made.</summary>
    public void ApplyWarpPreview(RenderOptions options)
    {
        if (WarpStroke is not { } warp || Document?.Find(warp.Layer.Id) is not { } layer) return;
        var o = options.Edit(layer.Id);
        o.Image = warp.Preview;
        o.Transform = LayerTransform.Canvas(warp.Width, warp.Height);
        o.Volatile = true;
        if (layer.Mask is not null)
        {
            o.OverridesMaskPlacement = true;
            o.MaskPlacement = layer.MaskTransform;
        }
    }
}
