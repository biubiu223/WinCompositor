using Compositor.Core.Adjustments;
using Compositor.Core.Geometry;
using Compositor.Core.Localization;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Core.Rendering;

/// <summary>Replaces what a layer shows while an edit is in progress (a stroke, a transform, a preview).</summary>
public sealed class LayerOverride
{
    /// <summary>Pixels to draw instead of the layer's own (in their own grid).</summary>
    public SKImage? Image { get; set; }
    /// <summary>Where the pixels (override or own) are placed.</summary>
    public LayerTransform? Transform { get; set; }
    /// <summary>Mask coverage (alpha-only) to use instead of the layer's mask.</summary>
    public SKImage? Mask { get; set; }
    /// <summary>Set when <see cref="MaskPlacement"/> is meant, even when null (mask covers the layer).</summary>
    public bool OverridesMaskPlacement { get; set; }
    public LayerTransform? MaskPlacement { get; set; }
    public BlendMode? BlendMode { get; set; }
    public double? Opacity { get; set; }
    public bool Hidden { get; set; }
    /// <summary>Use fast sampling (no mipmaps) for pixels that change every frame.</summary>
    public bool Volatile { get; set; }
    /// <summary>Drawn right after the layer, in document coordinates (floating pixels, a shape draft).</summary>
    public Action<SKCanvas>? DrawAbove { get; set; }
    /// <summary>Pixels replacing part of the layer's grid while it is painted (see RasterEdit).</summary>
    public SKImage? Patch { get; set; }
    /// <summary>Where <see cref="Patch"/> goes, in grid pixels (may reach past the image).</summary>
    public RectI PatchRect { get; set; }
    /// <summary>The grid the transform places, when it differs from the image's size (a blank layer being painted).</summary>
    public (int Width, int Height)? PatchGrid { get; set; }
}

/// <summary>A bitmap being composited into, readable by adjustment layers, and the document → bitmap pixel matrix.</summary>
internal sealed record AdjustmentSurface(SKBitmap? Bitmap, SKMatrix Matrix)
{
    /// <summary>Compositing straight onto the canvas: adjustment layers are skipped.</summary>
    public static readonly AdjustmentSurface Unavailable = new(null, SKMatrix.Identity);
}

public sealed class RenderOptions
{
    public Dictionary<Guid, LayerOverride> Overrides { get; } = new();
    /// <summary>Nearest-neighbor everywhere: the canvas from 200%, where document pixels show hard-edged.</summary>
    public bool Crisp { get; set; }
    /// <summary>Layers to leave out (merging a subset, sampling one layer).</summary>
    public Func<Layer, bool>? Include { get; set; }
    /// <summary>For the screen: zoomed-out layers sample one mipmap level rather than blending two (about half the cost).</summary>
    public bool Preview { get; set; }

    /// <summary>Set while compositing onto a readable bitmap, so adjustment layers can change what is below them.</summary>
    internal AdjustmentSurface? Surface { get; set; }

    public LayerOverride? For(Guid id) => Overrides.TryGetValue(id, out var o) ? o : null;

    public LayerOverride Edit(Guid id)
    {
        if (!Overrides.TryGetValue(id, out var o)) Overrides[id] = o = new LayerOverride();
        return o;
    }
}

/// <summary>
/// Composites a document onto a Skia canvas whose matrix maps document pixels to device pixels. Shared by the
/// canvas, export and merging. Layers are drawn bottom to top; folders are pass-through (their opacity and masks apply
/// to each layer inside); a clipping stack composites as one unit in its base's blend mode.
/// </summary>
public static class DocumentRenderer
{
    private static readonly SKSamplingOptions Nearest = new(SKFilterMode.Nearest, SKMipmapMode.None);
    private static readonly SKSamplingOptions Linear = new(SKFilterMode.Linear, SKMipmapMode.None);
    private static readonly SKSamplingOptions Mipmapped = new(SKFilterMode.Linear, SKMipmapMode.Linear);
    private static readonly SKSamplingOptions MipmappedFast = new(SKFilterMode.Linear, SKMipmapMode.Nearest);
    private static readonly SKSamplingOptions Cubic = new(SKCubicResampler.Mitchell);

    /// <summary>Draws the layers. The caller provides a transparent layer to composite into (see <see cref="RenderToImage"/>).</summary>
    public static void Render(SKCanvas canvas, CanvasDocument document, RenderOptions? options = null)
    {
        options ??= new RenderOptions();
        var byId = document.Layers.ToDictionary(l => l.Id);
        var drawn = document.RenderLayers().Where(l => options.Include?.Invoke(l) ?? true).ToList();
        var folderMasks = new Dictionary<Guid, FolderMask?>();

        // Adjustment layers change what is below them, so the layers are composited where the pixels can be read back.
        if (options.Surface is null && drawn.Any(l => l.Adjustment is { IsIdentity: false } && options.For(l.Id)?.Hidden != true))
        {
            RenderOnSurface(canvas, document, options, drawn);
            return;
        }

        // Clipping stacks: a base followed by the layers clipped to it (same folder, consecutive).
        var stacked = new HashSet<Guid>();
        var stacks = new Dictionary<Guid, List<Layer>>();
        for (int i = 0; i < drawn.Count; i++)
        {
            var b = drawn[i];
            if (b.MaskSourceId is not null || b.IsAdjustment) continue;
            var children = new List<Layer>();
            for (int j = i + 1; j < drawn.Count; j++)
            {
                var child = drawn[j];
                if (child.MaskSourceId != b.Id || child.ParentId != b.ParentId) break;
                children.Add(child);
            }
            if (children.Count == 0) continue;
            stacks[b.Id] = children;
            foreach (var c in children) stacked.Add(c.Id);
        }

        foreach (var layer in drawn)
        {
            if (stacked.Contains(layer.Id)) continue;
            if (layer.IsAdjustment)
            {
                if (options.Surface is { } surface)
                {
                    canvas.Flush();
                    ApplyAdjustment(surface, document, layer, FolderMasksFor(layer, byId, folderMasks, options), options);
                }
                continue;
            }
            var masks = FolderMasksFor(layer, byId, folderMasks, options);
            if (stacks.TryGetValue(layer.Id, out var children))
            {
                DrawClippingStack(canvas, document, layer, children, byId, folderMasks, options);
            }
            else if (layer.MaskSourceId is { } source && byId.TryGetValue(source, out var sourceLayer))
            {
                // Clipped to a layer that isn't directly below it: limited by that layer's coverage.
                DrawClippedByCoverage(canvas, document, layer, sourceLayer, masks, options);
            }
            else
            {
                DrawLayer(canvas, document, layer, masks, options, document.EffectiveOpacity(layer), BlendFor(layer, options));
            }
            options.For(layer.Id)?.DrawAbove?.Invoke(canvas);
        }
    }

    /// <summary>The flattened document at its own size (or <paramref name="scale"/> of it), premultiplied RGBA.</summary>
    public static SKImage RenderToImage(CanvasDocument document, RenderOptions? options = null, double scale = 1, SKColor? background = null)
    {
        int w = Math.Max(1, (int)Math.Round(document.Width * scale)), h = Math.Max(1, (int)Math.Round(document.Height * scale));
        using var surface = SKSurface.Create(RasterImage.ColorInfo(w, h)) ?? throw new InvalidOperationException(L.T("内存不足。", "Not enough memory."));
        var canvas = surface.Canvas;
        canvas.Clear(background ?? SKColors.Transparent);
        if (background is not null)
        {
            canvas.SaveLayer();
            canvas.Clear(SKColors.Transparent);
        }
        canvas.Scale((float)(w / (double)document.Width), (float)(h / (double)document.Height));
        Render(canvas, document, options);
        if (background is not null) canvas.Restore();
        canvas.Flush();
        return surface.Snapshot();
    }

    // MARK: Adjustment layers

    /// <summary>
    /// Composites the layers into a bitmap covering what the canvas shows (plus the room blurs read beyond it), so each
    /// adjustment layer can change everything below it, then draws the bitmap onto the canvas. Mirrors AdjustmentSurface.
    /// </summary>
    private static void RenderOnSurface(SKCanvas canvas, CanvasDocument document, RenderOptions options, List<Layer> drawn)
    {
        var matrix = canvas.TotalMatrix;
        double scale = Math.Sqrt(Math.Abs(matrix.ScaleX * matrix.ScaleY - matrix.SkewX * matrix.SkewY));
        double margin = drawn.Where(l => l.Adjustment is not null).Select(l => l.Adjustment!.SamplingMargin).DefaultIfEmpty(0).Max() * scale;
        int m = (int)Math.Ceiling(margin);
        var device = canvas.DeviceClipBounds;
        var documentDevice = Affine.FromSK(matrix).Apply(document.Bounds).Inflate(m + 2, m + 2).ToSKI();
        var region = SKRectI.Intersect(new SKRectI(device.Left - m, device.Top - m, device.Right + m, device.Bottom + m), documentDevice);
        if (region.Width <= 0 || region.Height <= 0) return;
        if ((long)region.Width * region.Height > DocumentLimits.MaxSurfacePixels)
        {
            // Too big to read back: draw without the adjustments rather than not at all.
            options.Surface = AdjustmentSurface.Unavailable;
            try { Render(canvas, document, options); } finally { options.Surface = null; }
            return;
        }
        using var bitmap = new SKBitmap(RasterImage.ColorInfo(region.Width, region.Height));
        using (var surfaceCanvas = new SKCanvas(bitmap))
        {
            surfaceCanvas.Clear(SKColors.Transparent);
            var toBitmap = SKMatrix.CreateTranslation(-region.Left, -region.Top).PreConcat(matrix);
            surfaceCanvas.SetMatrix(toBitmap);
            options.Surface = new AdjustmentSurface(bitmap, toBitmap);
            try { Render(surfaceCanvas, document, options); }
            finally { options.Surface = null; }
            surfaceCanvas.Flush();
        }
        using var image = SKImage.FromPixels(bitmap.PeekPixels());
        canvas.Save();
        canvas.ResetMatrix();
        canvas.DrawImage(image, region.Left, region.Top, Nearest);
        canvas.Restore();
    }

    /// <summary>
    /// Runs an adjustment layer over what has been composited so far: through its own mask and its folders' masks, at
    /// its opacity, in its blend mode.
    /// </summary>
    private static void ApplyAdjustment(AdjustmentSurface surface, CanvasDocument document, Layer layer, List<FolderMask> folderMasks, RenderOptions options)
    {
        if (surface.Bitmap is not { } bitmap || layer.Adjustment is not { } adjustment || adjustment.IsIdentity) return;
        var o = options.For(layer.Id);
        if (o?.Hidden == true) return;
        double opacity = o?.Opacity ?? document.EffectiveOpacity(layer);
        if (opacity <= 0) return;
        var m = surface.Matrix;
        double scale = Math.Sqrt(Math.Abs(m.ScaleX * m.ScaleY - m.SkewX * m.SkewY));
        var toDocument = Affine.FromSK(m).Invert();
        var corner = toDocument.Apply(0, 0);
        var documentOrigin = Affine.FromSK(m).Apply(0, 0);
        var placement = new AdjustmentPlacement(scale, corner.X, corner.Y, (long)Math.Floor(-documentOrigin.X), (long)Math.Floor(-documentOrigin.Y));
        var mode = BlendFor(layer, options);
        var coverage = AdjustmentCoverage(layer, folderMasks, bitmap.Width, bitmap.Height, m, options);
        if (coverage is null && opacity >= 1 && mode == BlendMode.Normal)
        {
            // Everywhere, fully: straight onto what is there.
            Adjustments.AdjustmentPixels.Apply(adjustment, bitmap, placement);
            return;
        }
        using var adjusted = bitmap.Copy();
        Adjustments.AdjustmentPixels.Apply(adjustment, adjusted, placement);
        if (mode != BlendMode.Normal) Adjustments.AdjustmentPixels.BlendInto(bitmap, adjusted, mode);
        Adjustments.AdjustmentPixels.Mix(bitmap, adjusted, coverage, opacity);
    }

    /// <summary>An adjustment layer's mask times its folders' masks, one byte per bitmap pixel; null when it has none.</summary>
    private static byte[]? AdjustmentCoverage(Layer layer, List<FolderMask> folderMasks, int width, int height, SKMatrix matrix, RenderOptions options)
    {
        var content = Resolve(layer, options);
        if (content.Mask is null && folderMasks.Count == 0) return null;
        var bytes = new byte[width * height];
        unsafe
        {
            fixed (byte* p = bytes)
            {
                using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Alpha8, SKAlphaType.Premul), (IntPtr)p, width);
                var canvas = surface.Canvas;
                canvas.Clear(SKColors.White);
                canvas.SetMatrix(matrix);
                if (content.Mask is not null)
                {
                    byte background = content.MaskPlacement is not null && layer.Mask is { } owned ? LayerMask.Background(owned.Asset) : (byte)255;
                    ApplyMask(canvas, content.Mask, content.MaskPlacement ?? content.Transform, background, content.Transform.Sampling, options.Crisp);
                }
                foreach (var folder in folderMasks) ApplyMask(canvas, folder.Image, folder.Transform, 0, folder.Transform.Sampling, options.Crisp);
                canvas.Flush();
            }
        }
        return bytes;
    }

    // MARK: Layers

    private static BlendMode BlendFor(Layer layer, RenderOptions options) => options.For(layer.Id)?.BlendMode ?? layer.BlendMode;

    private sealed record FolderMask(SKImage Image, LayerTransform Transform);

    private static List<FolderMask> FolderMasksFor(Layer layer, Dictionary<Guid, Layer> byId, Dictionary<Guid, FolderMask?> cache, RenderOptions options)
    {
        var result = new List<FolderMask>();
        var parent = layer.ParentId;
        for (int depth = 0; parent is { } id && depth < 64; depth++)
        {
            if (!cache.TryGetValue(id, out var mask))
            {
                mask = null;
                if (byId.TryGetValue(id, out var folder) && folder.Mask is { IsEnabled: true } owned)
                {
                    var o = options.For(folder.Id);
                    var image = o?.Mask ?? owned.Asset.Image;
                    var placement = (o?.OverridesMaskPlacement ?? false) ? o!.MaskPlacement : owned.Placement;
                    mask = new FolderMask(image, placement ?? o?.Transform ?? folder.Transform);
                }
                cache[id] = mask;
            }
            if (mask is not null) result.Add(mask);
            parent = byId.TryGetValue(id, out var node) ? node.ParentId : null;
        }
        return result;
    }

    /// <summary>What a layer shows right now: its pixels (and any patch over them), where, and its mask.</summary>
    private sealed record Content(SKImage? Image, SKImage? Patch, RectI PatchRect, int GridWidth, int GridHeight,
                                  LayerTransform Transform, SKImage? Mask, LayerTransform? MaskPlacement, bool Volatile)
    {
        public bool HasPixels => Image is not null || Patch is not null;
    }

    private static Content Resolve(Layer layer, RenderOptions options)
    {
        var o = options.For(layer.Id);
        var image = o?.Image ?? layer.Asset?.Image;
        var transform = o?.Transform ?? layer.Transform;
        int gridWidth = o?.PatchGrid?.Width ?? image?.Width ?? layer.GridWidth;
        int gridHeight = o?.PatchGrid?.Height ?? image?.Height ?? layer.GridHeight;
        SKImage? mask = null;
        LayerTransform? placement = null;
        if (layer.Mask is { IsEnabled: true } owned)
        {
            mask = o?.Mask ?? owned.Asset.Image;
            placement = (o?.OverridesMaskPlacement ?? false) ? o!.MaskPlacement : owned.Placement;
        }
        else if (o?.Mask is { } painted && layer.Mask is null)
        {
            mask = painted;
            placement = o.OverridesMaskPlacement ? o.MaskPlacement : null;
        }
        return new Content(image, o?.Patch, o?.PatchRect ?? default, gridWidth, gridHeight, transform, mask, placement, o?.Volatile ?? false);
    }

    private static SKSamplingOptions Sampling(SKCanvas canvas, Content content, bool crisp)
    {
        var transform = content.Transform;
        if (crisp || transform.Sampling == LayerSampling.Nearest) return Nearest;
        var m = canvas.TotalMatrix;
        double sx = Math.Sqrt(m.ScaleX * m.ScaleX + m.SkewY * m.SkewY) * transform.Size.Width / Math.Max(1, content.GridWidth);
        double sy = Math.Sqrt(m.SkewX * m.SkewX + m.ScaleY * m.ScaleY) * transform.Size.Height / Math.Max(1, content.GridHeight);
        double scale = Math.Min(Math.Abs(sx), Math.Abs(sy));
        bool upright = transform.Rotation % 90 == 0 && Math.Abs(m.SkewX) < 1e-6 && Math.Abs(m.SkewY) < 1e-6;
        if (upright && Math.Abs(sx - 1) < 0.001 && Math.Abs(sy - 1) < 0.001) return Nearest; // pixel for pixel
        if (scale < 0.999) return content.Volatile ? Linear : Mipmapped;
        return transform.Sampling == LayerSampling.High && !content.Volatile ? Cubic : Linear;
    }

    /// <summary>Draws a layer's pixels in document coordinates: the image over its grid, then any patch replacing part of it.</summary>
    private static void DrawPixels(SKCanvas canvas, Content content, SKSamplingOptions sampling, bool antialias)
    {
        canvas.Save();
        canvas.Concat(content.Transform.PixelToDocument(content.GridWidth, content.GridHeight).ToSK());
        using var paint = new SKPaint { IsAntialias = antialias };
        if (content.Image is { } image)
            canvas.DrawImage(image, new SKRect(0, 0, content.GridWidth, content.GridHeight), sampling, paint);
        if (content.Patch is { } patch)
        {
            using var replace = new SKPaint { BlendMode = SKBlendMode.Src, IsAntialias = false };
            var r = content.PatchRect;
            canvas.DrawImage(patch, new SKRect(r.X, r.Y, r.Right, r.Bottom), sampling, replace);
        }
        canvas.Restore();
    }

    /// <summary>
    /// Draws one layer with its opacity, blend mode, own mask and enclosing folders' masks. <paramref name="clip"/>
    /// further limits it (a clipping source's coverage).
    /// </summary>
    private static void DrawLayer(SKCanvas canvas, CanvasDocument document, Layer layer, List<FolderMask> folderMasks,
                                  RenderOptions options, double opacity, BlendMode mode, Action<SKCanvas>? clip = null,
                                  EffectParts effectParts = EffectParts.All)
    {
        var o = options.For(layer.Id);
        if (o?.Hidden == true) return;
        var content = Resolve(layer, options);
        if (!content.HasPixels) return;
        opacity = o?.Opacity ?? opacity;
        if (opacity <= 0) return;

        var transform = content.Transform;
        var sampling = Sampling(canvas, content, options.Crisp);
        if (options.Preview && sampling.Equals(Mipmapped)) sampling = MipmappedFast;
        bool antialias = transform.Sampling != LayerSampling.Nearest && !options.Crisp;
        var visibleEffects = layer.VisibleEffects;
        bool styled = visibleEffects is not null && EffectRaster.HasWork(visibleEffects, effectParts);
        bool needsLayer = mode != BlendMode.Normal || opacity < 1 || content.Mask is not null || folderMasks.Count > 0
                          || clip is not null || content.Patch is not null || styled;
        if (!needsLayer)
        {
            DrawPixels(canvas, content, sampling, antialias);
            return;
        }

        using var layerPaint = new SKPaint { Color = SKColors.Black.WithAlpha((byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255)) };
        Blenders.Apply(layerPaint, mode);
        var map = transform.PixelToDocument(content.GridWidth, content.GridHeight);
        var placed = content.Patch is not null
            ? map.Apply(content.PatchRect.ToD()).Union(transform.BoundingBox())
            : transform.BoundingBox();
        if (styled)
        {
            // Room for what the effects draw beyond the pixels.
            double m = EffectRaster.Margin(visibleEffects!, effectParts);
            placed = placed.Union(map.Apply(new RectD(-m, -m, content.GridWidth + 2 * m, content.GridHeight + 2 * m)));
        }
        var bounds = placed.Intersect(document.Bounds.Inflate(2, 2));
        if (bounds.IsNull || bounds.IsEmpty) return;
        canvas.SaveLayer(bounds.Inflate(1, 1).ToSK(), layerPaint);
        // The layer as it shows: its pixels through its own mask.
        void DrawShown(SKCanvas target, SKSamplingOptions how)
        {
            DrawPixels(target, content, how, antialias);
            if (content.Mask is not null)
            {
                byte background = content.MaskPlacement is not null && layer.Mask is { } m ? LayerMask.Background(m.Asset) : (byte)255;
                ApplyMask(target, content.Mask, content.MaskPlacement ?? transform, background, transform.Sampling, options.Crisp);
            }
        }
        if (styled)
        {
            // Effects follow what shows; the result is cached unless the pixels can change under the same identity.
            object? key = content.Patch is null && !content.Volatile
                ? (layer.Id, content.Image, content.Mask, content.MaskPlacement, transform, layer.Mask?.Asset, options.Crisp)
                : null;
            EffectRaster.Draw(canvas, visibleEffects!, effectParts, map, content.GridWidth, content.GridHeight, key,
                target => DrawShown(target, Sampling(target, content, options.Crisp)));
        }
        else DrawShown(canvas, sampling);
        foreach (var folder in folderMasks)
        {
            ApplyMask(canvas, folder.Image, folder.Transform, 0, folder.Transform.Sampling, options.Crisp);
        }
        clip?.Invoke(canvas);
        canvas.Restore();
    }

    /// <summary>
    /// Multiplies what has been drawn into the current layer by a mask placed at <paramref name="placement"/>.
    /// Beyond the mask's rectangle, <paramref name="background"/> (255 keeps, 0 hides) applies.
    /// </summary>
    private static void ApplyMask(SKCanvas canvas, SKImage mask, LayerTransform placement, byte background, LayerSampling sampling, bool crisp)
    {
        bool uniform = mask.Width <= 2 && mask.Height <= 2;
        canvas.Save();
        canvas.Concat(placement.PixelToDocument(mask.Width, mask.Height).ToSK());
        var rect = new SKRect(0, 0, mask.Width, mask.Height);
        if (background == 0)
        {
            canvas.Save();
            canvas.ClipRect(rect, SKClipOperation.Difference, false);
            canvas.Clear(SKColors.Transparent);
            canvas.Restore();
        }
        using var paint = new SKPaint { BlendMode = SKBlendMode.DstIn, IsAntialias = false };
        var samplingOptions = crisp || sampling == LayerSampling.Nearest || uniform ? Nearest : Linear;
        canvas.DrawImage(mask, rect, samplingOptions, paint);
        canvas.Restore();
    }

    // MARK: Clipping

    /// <summary>
    /// A base and the layers clipped to it: the base's alpha is kept aside, its colors made opaque, the clipped layers
    /// drawn over them in their own modes, and the base's alpha put back — then the result composites in the base's
    /// mode. Mirrors LiveMaskRenderer.drawComposite.
    /// </summary>
    private static void DrawClippingStack(SKCanvas canvas, CanvasDocument document, Layer baseLayer, List<Layer> children,
                                          Dictionary<Guid, Layer> byId, Dictionary<Guid, FolderMask?> folderCache, RenderOptions options)
    {
        if (options.For(baseLayer.Id)?.Hidden == true) return;
        var content = Resolve(baseLayer, options);
        if (!content.HasPixels) return;
        var device = canvas.DeviceClipBounds;
        var matrix = canvas.TotalMatrix;
        var baseMap = content.Transform.PixelToDocument(content.GridWidth, content.GridHeight);
        var placed = content.Patch is not null
            ? baseMap.Apply(content.PatchRect.ToD()).Union(content.Transform.BoundingBox())
            : content.Transform.BoundingBox();
        // The base's shadow, glow and outside stroke come from the whole stack's shape, drawn around it at the end.
        var behind = baseLayer.VisibleEffects is { } baseEffects && EffectRaster.HasWork(baseEffects, EffectParts.Behind) ? baseEffects : null;
        if (behind is not null)
        {
            double margin = EffectRaster.Margin(behind, EffectParts.Behind);
            placed = placed.Union(baseMap.Apply(new RectD(-margin, -margin, content.GridWidth + 2 * margin, content.GridHeight + 2 * margin)));
        }
        var baseDevice = Affine.FromSK(matrix).Apply(placed).Inflate(2, 2).ToSKI();
        if (!baseDevice.IntersectsWith(device)) return;
        var region = SKRectI.Intersect(device, baseDevice);
        if (region.Width <= 0 || region.Height <= 0) return;
        if ((long)region.Width * region.Height > DocumentLimits.MaxSurfacePixels)
        {
            DrawLayer(canvas, document, baseLayer, FolderMasksFor(baseLayer, byId, folderCache, options), options,
                document.EffectiveOpacity(baseLayer), BlendFor(baseLayer, options));
            return;
        }

        using var bitmap = new SKBitmap(RasterImage.ColorInfo(region.Width, region.Height));
        using (var surfaceCanvas = new SKCanvas(bitmap))
        {
            surfaceCanvas.Clear(SKColors.Transparent);
            surfaceCanvas.SetMatrix(SKMatrix.CreateTranslation(-region.Left, -region.Top).PreConcat(matrix));
            DrawLayer(surfaceCanvas, document, baseLayer, FolderMasksFor(baseLayer, byId, folderCache, options), options,
                document.EffectiveOpacity(baseLayer), BlendMode.Normal, effectParts: EffectParts.OnPixels);
            surfaceCanvas.Flush();

            var alpha = PixelOps.ExtractAlphaAndMakeOpaque(bitmap);
            var stackSurface = new AdjustmentSurface(bitmap, surfaceCanvas.TotalMatrix);
            foreach (var child in children)
            {
                if (child.IsAdjustment)
                {
                    // An adjustment clipped to the base changes only the stack.
                    surfaceCanvas.Flush();
                    ApplyAdjustment(stackSurface, document, child, FolderMasksFor(child, byId, folderCache, options), options);
                    continue;
                }
                DrawLayer(surfaceCanvas, document, child, FolderMasksFor(child, byId, folderCache, options), options,
                    document.EffectiveOpacity(child), BlendFor(child, options));
                options.For(child.Id)?.DrawAbove?.Invoke(surfaceCanvas);
            }
            surfaceCanvas.Flush();
            PixelOps.RestoreAlpha(bitmap, alpha);
        }
        using var result = SKImage.FromBitmap(bitmap);
        canvas.Save();
        using var paint = new SKPaint();
        Blenders.Apply(paint, BlendFor(baseLayer, options));
        if (behind is null)
        {
            canvas.ResetMatrix();
            canvas.DrawImage(result, region.Left, region.Top, Nearest, paint);
        }
        else
        {
            // The base's shadow, glow and outside stroke made from the finished stack's shape, in the base's pixel space.
            canvas.SaveLayer(paint);
            var deviceToDocument = matrix.TryInvert(out var inverse) ? inverse : SKMatrix.Identity;
            EffectRaster.Draw(canvas, behind, EffectParts.Behind, baseMap, content.GridWidth, content.GridHeight, null, target =>
            {
                target.Save();
                target.Concat(deviceToDocument);
                target.DrawImage(result, region.Left, region.Top, Linear);
                target.Restore();
            });
            canvas.Restore();
        }
        canvas.Restore();
    }

    /// <summary>A layer clipped to a source that is not its stack's base: limited by the source's coverage.</summary>
    private static void DrawClippedByCoverage(SKCanvas canvas, CanvasDocument document, Layer layer, Layer source,
                                              List<FolderMask> masks, RenderOptions options)
    {
        DrawLayer(canvas, document, layer, masks, options, document.EffectiveOpacity(layer), BlendFor(layer, options), clip: c =>
        {
            var content = Resolve(source, options);
            if (!content.HasPixels)
            {
                c.Clear(SKColors.Transparent);
                return;
            }
            // Dst-In by the source's pixels (with its opacity and mask) as one coverage layer.
            using var coveragePaint = new SKPaint { BlendMode = SKBlendMode.DstIn };
            c.SaveLayer(coveragePaint);
            c.Clear(SKColors.Transparent);
            using (var opacity = new SKPaint { Color = SKColors.Black.WithAlpha((byte)Math.Round(source.Opacity * 255)) })
            {
                c.SaveLayer(opacity);
                DrawPixels(c, content, Linear, true);
                c.Restore();
            }
            if (content.Mask is not null) ApplyMask(c, content.Mask, content.MaskPlacement ?? content.Transform, 255, content.Transform.Sampling, options.Crisp);
            c.Restore();
        });
    }
}
