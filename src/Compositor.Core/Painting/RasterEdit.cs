using Compositor.Core.Geometry;
using Compositor.Core.Localization;
using Compositor.Core.Model;
using Compositor.Core.Rendering;
using SkiaSharp;

namespace Compositor.Core.Painting;

/// <summary>
/// A pixel edit of one layer's image or mask, in the layer's own pixel grid ("grid coordinates": (0,0) is the image's
/// first pixel; negative values reach past it). Only a window of the grid is held in memory, grown as the edit reaches
/// further — painting past a layer's edge grows the layer, as in Photoshop. Mirrors BrushStroke's raster half.
/// </summary>
public class RasterEdit
{
    public Layer Layer { get; }
    public bool IsMask { get; }
    public int CanvasWidth { get; }
    public int CanvasHeight { get; }
    /// <summary>Where the grid sits on the document: the layer's transform, or a placed mask's own.</summary>
    public LayerTransform BaseTransform { get; }
    public int GridWidth { get; }
    public int GridHeight { get; }
    /// <summary>Grid pixels → document pixels.</summary>
    public Affine GridToDocument { get; }
    public Affine DocumentToGrid { get; }
    /// <summary>The original pixels' place in the grid.</summary>
    public RectI SourceRect { get; }
    /// <summary>How far the grid may grow.</summary>
    public RectI Extent { get; }
    /// <summary>A mask's value past its pixels (and so what new mask area starts as).</summary>
    public byte MaskBackground { get; }
    public SelectionClip? Selection { get; set; }
    public long PixelLimit { get; set; } = DocumentLimits.DocumentPixelBudget;

    private readonly RasterImage? _source;
    private readonly bool _uniformSource;
    protected int Bpp => IsMask ? 1 : 4;

    /// <summary>The part of the grid in memory.</summary>
    public RectI Window { get; private set; }
    /// <summary>The window's current pixels (premultiplied RGBA, or coverage for a mask).</summary>
    public byte[] Pixels { get; private set; } = Array.Empty<byte>();
    /// <summary>The window's pixels before the edit.</summary>
    public byte[] Base { get; private set; } = Array.Empty<byte>();
    /// <summary>The grid area changed since the edit began.</summary>
    public RectI Touched { get; protected set; }
    /// <summary>Bumped whenever the pixels change, so previews know to rebuild.</summary>
    public int Revision { get; protected set; }

    public RasterEdit(Layer layer, bool mask, int canvasWidth, int canvasHeight, bool grows = true)
    {
        Layer = layer;
        IsMask = mask;
        CanvasWidth = canvasWidth;
        CanvasHeight = canvasHeight;
        var placedMask = mask && layer.Mask?.Placement is not null ? layer.Mask : null;
        BaseTransform = placedMask?.Placement ?? layer.Transform;
        _source = mask ? layer.Mask?.Asset : layer.Asset;
        _uniformSource = _source?.IsUniform == true && mask;
        if (placedMask is not null && _uniformSource)
        {
            // A solid mask placed on its own is one pixel stretched over its place; painted, it gets one per document pixel.
            GridWidth = Math.Max(1, (int)Math.Round(BaseTransform.Size.Width));
            GridHeight = Math.Max(1, (int)Math.Round(BaseTransform.Size.Height));
        }
        else if (placedMask is not null)
        {
            GridWidth = placedMask.Asset.Width;
            GridHeight = placedMask.Asset.Height;
        }
        else
        {
            GridWidth = layer.GridWidth;
            GridHeight = layer.GridHeight;
        }
        if (GridWidth > DocumentLimits.MaxSide * 10 || GridHeight > DocumentLimits.MaxSide * 10) throw new InvalidOperationException(L.T("图层太大，无法编辑。", "The layer is too large to edit."));
        GridToDocument = BaseTransform.PixelToDocument(GridWidth, GridHeight);
        DocumentToGrid = GridToDocument.Invert();
        SourceRect = new RectI(0, 0, GridWidth, GridHeight);
        var canvasInGrid = RectI.From(DocumentToGrid.Apply(new RectD(0, 0, canvasWidth, canvasHeight)));
        Extent = grows ? SourceRect.Union(canvasInGrid) : SourceRect;
        MaskBackground = mask ? (layer.Mask is { } m ? LayerMask.Background(m.Asset) : (byte)255) : (byte)0;
        Window = new RectI(0, 0, 0, 0);
        Touched = new RectI(0, 0, 0, 0);
        // A mask is held whole from the start: previews draw it as one image.
        if (mask) EnsureWindow(SourceRect, margin: 0);
    }

    /// <summary>Makes sure <paramref name="needed"/> (grid coordinates) is in memory. False when it lies outside the extent.</summary>
    public bool EnsureWindow(RectI needed, int margin = 256)
    {
        needed = needed.Intersect(Extent);
        if (needed.IsEmpty) return false;
        if (!Window.IsEmpty && Window.Intersect(needed) == needed) return true;
        var grown = Window.Union(new RectI(needed.X - margin, needed.Y - margin, needed.Width + margin * 2, needed.Height + margin * 2)).Intersect(Extent);
        if (grown.Area > PixelLimit || grown.Area > DocumentLimits.MaxSurfacePixels)
        {
            grown = Window.Union(needed).Intersect(Extent);
            if (grown.Area > PixelLimit || grown.Area > DocumentLimits.MaxSurfacePixels)
                throw new InvalidOperationException(L.T("这次编辑超出了文档的像素上限。", "This edit exceeds the document’s pixel limit."));
        }
        Reallocate(grown);
        return true;
    }

    private void Reallocate(RectI next)
    {
        int bpp = Bpp;
        var pixels = new byte[next.Width * next.Height * bpp];
        var basePixels = new byte[pixels.Length];
        // New area starts as the original pixels where there are any, transparent (or the mask's background) elsewhere.
        if (IsMask && MaskBackground != 0) Array.Fill(basePixels, MaskBackground);
        FillFromSource(basePixels, next);
        Array.Copy(basePixels, pixels, pixels.Length);
        if (!Window.IsEmpty)
        {
            // Keep what the edit has done so far.
            var old = Window;
            for (int y = 0; y < old.Height; y++)
            {
                int dst = ((old.Y - next.Y + y) * next.Width + (old.X - next.X)) * bpp;
                Array.Copy(Pixels, y * old.Width * bpp, pixels, dst, old.Width * bpp);
                Array.Copy(Base, y * old.Width * bpp, basePixels, dst, old.Width * bpp);
            }
        }
        OnReallocate(Window, next);
        Window = next;
        Pixels = pixels;
        Base = basePixels;
        Revision++;
    }

    /// <summary>Lets subclasses move buffers kept alongside the pixels.</summary>
    protected virtual void OnReallocate(RectI old, RectI next) { }

    private void FillFromSource(byte[] target, RectI window)
    {
        if (_source is null) return;
        int bpp = Bpp;
        if (_uniformSource || (_source.IsUniform && IsMask))
        {
            using var pm = _source.PeekPixels();
            byte value = pm.GetPixelSpan()[0];
            var area = window.Intersect(SourceRect);
            for (int y = area.Y; y < area.Bottom; y++)
                Array.Fill(target, value, ((y - window.Y) * window.Width + (area.X - window.X)), area.Width);
            return;
        }
        var overlap = window.Intersect(SourceRect);
        if (overlap.IsEmpty) return;
        using var pixmap = _source.PeekPixels();
        var src = pixmap.GetPixelSpan();
        int stride = pixmap.RowBytes;
        for (int y = overlap.Y; y < overlap.Bottom; y++)
        {
            src.Slice(y * stride + overlap.X * bpp, overlap.Width * bpp)
               .CopyTo(target.AsSpan(((y - window.Y) * window.Width + (overlap.X - window.X)) * bpp));
        }
    }

    /// <summary>Marks grid area as changed.</summary>
    protected void Touch(RectI rect)
    {
        Touched = Touched.Union(rect.Intersect(Window));
        Revision++;
    }

    /// <summary>The transform placing grid rect <paramref name="rect"/> on the document.</summary>
    public LayerTransform TransformFor(RectI rect)
    {
        var center = GridToDocument.Apply(rect.X + rect.Width / 2.0, rect.Y + rect.Height / 2.0);
        var size = new SizeD(rect.Width * BaseTransform.Size.Width / GridWidth, rect.Height * BaseTransform.Size.Height / GridHeight);
        return BaseTransform with { Size = size, Origin = new PointD(center.X - size.Width / 2, center.Y - size.Height / 2) };
    }

    /// <summary>Selection coverage (0…1) at a grid pixel's center; 1 without a selection.</summary>
    protected double SelectionAt(int gx, int gy)
    {
        if (Selection is null) return 1;
        var doc = GridToDocument.Apply(gx + 0.5, gy + 0.5);
        return Selection.At(doc.X, doc.Y);
    }

    private System.Runtime.InteropServices.GCHandle _pin;
    private byte[]? _pinned;
    private SKImage? _preview;
    private int _previewRevision = -1;

    /// <summary>
    /// The window as an image over the edit's own memory (no copy: a stroke on a big layer redraws every mouse move).
    /// A new image is made for each revision, so nothing cached from an older one is shown.
    /// </summary>
    public SKImage? PreviewImage()
    {
        if (Window.IsEmpty) return null;
        if (_previewRevision == Revision && _preview is not null && ReferenceEquals(_pinned, Pixels)) return _preview;
        _preview?.Dispose();
        if (!ReferenceEquals(_pinned, Pixels))
        {
            if (_pin.IsAllocated) _pin.Free();
            _pin = System.Runtime.InteropServices.GCHandle.Alloc(Pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
            _pinned = Pixels;
        }
        var info = IsMask ? RasterImage.MaskInfo(Window.Width, Window.Height) : RasterImage.ColorInfo(Window.Width, Window.Height);
        _preview = SKImage.FromPixels(info, _pin.AddrOfPinnedObject(), Window.Width * Bpp);
        _previewRevision = Revision;
        return _preview;
    }

    /// <summary>Releases the preview and the pinned buffer once the edit is over.</summary>
    public void Release()
    {
        _preview?.Dispose();
        _preview = null;
        if (_pin.IsAllocated) _pin.Free();
        _pinned = null;
    }

    ~RasterEdit()
    {
        if (_pin.IsAllocated) _pin.Free();
    }

    /// <summary>
    /// Shows the edit on the canvas: for a layer's pixels, the original image with the window drawn over it; for a
    /// mask, the window as the whole mask.
    /// </summary>
    public void ApplyPreview(RenderOptions options)
    {
        var o = options.Edit(Layer.Id);
        var image = PreviewImage();
        if (image is null) return;
        if (IsMask)
        {
            o.Mask = image;
            var placement = TransformFor(Window);
            o.OverridesMaskPlacement = true;
            o.MaskPlacement = Layer.Mask?.Placement is null && Window == SourceRect && Layer.Mask is not null ? null : placement;
        }
        else
        {
            o.Volatile = true;
            o.PatchGrid = (GridWidth, GridHeight);
            o.Patch = image;
            o.PatchRect = Window;
            o.Transform = BaseTransform;
        }
    }

    /// <summary>
    /// The edited pixels as a new image and where it goes: a layer's pixels trimmed to what is there (never smaller
    /// than the original), a mask's whole window.
    /// </summary>
    public (RasterImage Asset, LayerTransform Transform, RectI Bounds) Commit()
    {
        if (IsMask)
        {
            var window = Window.IsEmpty ? SourceRect : Window;
            var asset = RasterImage.FromMask(Pixels, window.Width, window.Height, "Layer Mask");
            return (asset, TransformFor(window), window);
        }
        // What has pixels: the original's rect, plus whatever the edit put alpha into.
        var bounds = _source is not null ? SourceRect : new RectI(0, 0, 0, 0);
        if (!Window.IsEmpty)
        {
            var painted = PixelOps.AlphaBounds(Pixels, Window.Width, Window.Height, Window.Width * 4);
            if (!painted.IsEmpty) bounds = bounds.Union(painted.Offset(Window.X, Window.Y));
        }
        if (bounds.IsEmpty) bounds = Window.IsEmpty ? SourceRect : Window;
        var result = new byte[bounds.Width * bounds.Height * 4];
        // The original first, then the window over it.
        if (_source is not null)
        {
            var overlap = bounds.Intersect(SourceRect);
            using var pixmap = _source.PeekPixels();
            var src = pixmap.GetPixelSpan();
            for (int y = overlap.Y; y < overlap.Bottom; y++)
                src.Slice(y * pixmap.RowBytes + overlap.X * 4, overlap.Width * 4)
                   .CopyTo(result.AsSpan(((y - bounds.Y) * bounds.Width + (overlap.X - bounds.X)) * 4));
        }
        var inWindow = bounds.Intersect(Window);
        for (int y = inWindow.Y; y < inWindow.Bottom; y++)
        {
            Array.Copy(Pixels, ((y - Window.Y) * Window.Width + (inWindow.X - Window.X)) * 4,
                       result, ((y - bounds.Y) * bounds.Width + (inWindow.X - bounds.X)) * 4, inWindow.Width * 4);
        }
        return (RasterImage.FromRgba(result, bounds.Width, bounds.Height, Layer.Name), TransformFor(bounds), bounds);
    }

    /// <summary>
    /// Runs Skia drawing in document coordinates over the grid area covering <paramref name="documentArea"/>, clipped to
    /// the canvas and the selection, starting from the original pixels (fills, gradients, clearing).
    /// </summary>
    public void PaintDocument(RectD documentArea, Action<SKCanvas> draw, bool withinSource = false)
    {
        var area = documentArea.Intersect(new RectD(0, 0, CanvasWidth, CanvasHeight));
        if (Selection is { } sel)
        {
            if (sel.IsNothing) return;
            area = area.Intersect(sel.Rect.ToD());
        }
        if (area.IsNull || area.IsEmpty) return;
        var grid = RectI.From(DocumentToGrid.Apply(area));
        if (withinSource) grid = grid.Intersect(SourceRect);
        if (grid.IsEmpty || !EnsureWindow(grid, margin: 0)) return;
        grid = grid.Intersect(Window);
        // Start from the original pixels, so repeating a preview never accumulates.
        int bpp = Bpp;
        for (int y = grid.Y; y < grid.Bottom; y++)
        {
            int offset = ((y - Window.Y) * Window.Width + (grid.X - Window.X)) * bpp;
            Array.Copy(Base, offset, Pixels, offset, grid.Width * bpp);
        }
        var info = IsMask ? RasterImage.MaskInfo(Window.Width, Window.Height) : RasterImage.ColorInfo(Window.Width, Window.Height);
        unsafe
        {
            fixed (byte* p = Pixels)
            {
                using var surface = SKSurface.Create(info, (IntPtr)p, Window.Width * bpp);
                var canvas = surface.Canvas;
                canvas.ClipRect(new SKRect(grid.X - Window.X, grid.Y - Window.Y, grid.Right - Window.X, grid.Bottom - Window.Y));
                canvas.Translate(-Window.X, -Window.Y);
                canvas.Concat(DocumentToGrid.ToSK());
                canvas.ClipRect(new SKRect(0, 0, CanvasWidth, CanvasHeight));
                if (Selection is { Coverage: not null } clip)
                {
                    // Draw into a layer, keep it only where the selection is, then lay it over the original.
                    canvas.SaveLayer();
                    draw(canvas);
                    using var coverage = clip.ToImage();
                    using var dstIn = new SKPaint { BlendMode = SKBlendMode.DstIn };
                    canvas.DrawImage(coverage, new SKRect(clip.Rect.X, clip.Rect.Y, clip.Rect.Right, clip.Rect.Bottom),
                                     new SKSamplingOptions(SKFilterMode.Linear), dstIn);
                    canvas.Restore();
                }
                else
                {
                    draw(canvas);
                }
                canvas.Flush();
            }
        }
        Touch(grid);
    }

    /// <summary>
    /// Like <see cref="PaintDocument"/>, but what <paramref name="draw"/> paints is color laid over the original: on a
    /// mask, its gray (premultiplied) over the mask's gray by its alpha, as a gray-scale context composites — an
    /// alpha-only surface can't, since its "color" is its coverage.
    /// </summary>
    public void PaintOver(RectD documentArea, Action<SKCanvas> draw)
    {
        if (!IsMask) { PaintDocument(documentArea, draw); return; }
        var area = documentArea.Intersect(new RectD(0, 0, CanvasWidth, CanvasHeight));
        if (Selection is { } sel)
        {
            if (sel.IsNothing) return;
            area = area.Intersect(sel.Rect.ToD());
        }
        if (area.IsNull || area.IsEmpty) return;
        var grid = RectI.From(DocumentToGrid.Apply(area));
        if (grid.IsEmpty || !EnsureWindow(grid, margin: 0)) return;
        grid = grid.Intersect(Window);
        if (grid.IsEmpty) return;
        using var layer = new SKBitmap(RasterImage.ColorInfo(grid.Width, grid.Height));
        using (var canvas = new SKCanvas(layer))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Translate(-grid.X, -grid.Y);
            canvas.Concat(DocumentToGrid.ToSK());
            canvas.ClipRect(new SKRect(0, 0, CanvasWidth, CanvasHeight));
            draw(canvas);
            if (Selection is { Coverage: not null } clip)
            {
                using var coverage = clip.ToImage();
                using var dstIn = new SKPaint { BlendMode = SKBlendMode.DstIn };
                canvas.DrawImage(coverage, new SKRect(clip.Rect.X, clip.Rect.Y, clip.Rect.Right, clip.Rect.Bottom),
                                 new SKSamplingOptions(SKFilterMode.Linear), dstIn);
            }
            canvas.Flush();
        }
        var painted = layer.GetPixelSpan();
        int stride = layer.RowBytes;
        for (int y = 0; y < grid.Height; y++)
        {
            int row = (grid.Y + y - Window.Y) * Window.Width + (grid.X - Window.X);
            for (int x = 0; x < grid.Width; x++)
            {
                int p = y * stride + x * 4;
                int a = painted[p + 3];
                byte base_ = Base[row + x];
                // Gray over gray: the painted (premultiplied) luminance plus what its alpha leaves of the mask.
                int gray = (painted[p] * 54 + painted[p + 1] * 183 + painted[p + 2] * 19 + 128) >> 8;
                Pixels[row + x] = (byte)Math.Clamp(gray + (base_ * (255 - a) + 127) / 255, 0, 255);
            }
        }
        Touch(grid);
    }

    /// <summary>Erases the pixels inside the selection (or all of them) to transparency; a mask to black.</summary>
    public void ClearPixels(PaletteColor? maskValue = null)
    {
        var area = new RectD(0, 0, CanvasWidth, CanvasHeight);
        if (Selection is { } sel)
        {
            if (sel.IsNothing) return;
            area = area.Intersect(sel.Rect.ToD());
        }
        var grid = RectI.From(DocumentToGrid.Apply(area)).Intersect(SourceRect);
        if (grid.IsEmpty || !EnsureWindow(grid, margin: 0)) return;
        grid = grid.Intersect(Window);
        byte target = (byte)Math.Round((maskValue?.Luminance ?? 0) * 255);
        for (int y = grid.Y; y < grid.Bottom; y++)
        {
            for (int x = grid.X; x < grid.Right; x++)
            {
                double k = SelectionAt(x, y);
                if (k <= 0) continue;
                int i = (y - Window.Y) * Window.Width + (x - Window.X);
                if (IsMask)
                {
                    Pixels[i] = (byte)Math.Round(Base[i] + (target - Base[i]) * k);
                }
                else
                {
                    int p = i * 4;
                    for (int c = 0; c < 4; c++) Pixels[p + c] = (byte)Math.Round(Base[p + c] * (1 - k));
                }
            }
        }
        Touch(grid);
    }
}
