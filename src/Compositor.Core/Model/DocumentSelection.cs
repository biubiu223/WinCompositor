using Compositor.Core.Geometry;
using SkiaSharp;

namespace Compositor.Core.Model;

/// <summary>
/// A document-space selection outline. Null on the document means no selection; a selection whose path is empty is an
/// explicit empty selection, which later edits must treat as "touch nothing". Mirrors Selection.swift.
/// The path is never mutated once a selection holds it.
/// </summary>
public sealed class DocumentSelection
{
    public SKPath Path { get; }
    public bool Antialiased { get; }
    /// <summary>How far the edge fades, in document pixels. 0 is a hard edge.</summary>
    public double Feather { get; }

    public DocumentSelection(SKPath path, bool antialiased = true, double feather = 0)
    {
        Path = path;
        Path.FillType = SKPathFillType.Winding;
        Antialiased = antialiased;
        Feather = feather;
    }

    public static DocumentSelection Rectangle(RectD rect, bool antialiased = true)
    {
        var path = new SKPath();
        path.AddRect(rect.ToSK());
        return new DocumentSelection(path, antialiased);
    }

    public bool IsEmpty
    {
        get
        {
            if (Path.IsEmpty) return true;
            var b = Path.Bounds;
            return b.Width <= 0 || b.Height <= 0;
        }
    }

    public RectD Bounds => IsEmpty ? RectD.Null : RectD.FromSK(Path.Bounds);

    /// <summary>The outline's bounds widened by the feather's visible falloff.</summary>
    public RectD CoverageBounds => Bounds.Inflate(Math.Ceiling(Feather * 2), Math.Ceiling(Feather * 2));

    public bool Contains(PointD point) => !IsEmpty && Path.Contains((float)point.X, (float)point.Y);

    public DocumentSelection WithPath(SKPath path) => new(path, Antialiased, Feather);
    public DocumentSelection WithFeather(double feather) => new(Path, Antialiased, feather);

    public DocumentSelection Offset(double dx, double dy)
    {
        var path = new SKPath(Path);
        path.Transform(SKMatrix.CreateTranslation((float)dx, (float)dy));
        return new DocumentSelection(path, Antialiased, Feather);
    }

    /// <summary>
    /// 8-bit coverage (255 = selected) for <paramref name="region"/> of the document, one byte per pixel, rows top
    /// down. Soft edges come from the feather, blurred either side of the outline as Photoshop's are.
    /// </summary>
    public byte[] Coverage(RectI region)
    {
        var info = new SKImageInfo(region.Width, region.Height, SKColorType.Alpha8, SKAlphaType.Premul);
        var bytes = new byte[region.Width * region.Height];
        unsafe
        {
            fixed (byte* p = bytes)
            {
                using var surface = SKSurface.Create(info, (IntPtr)p, region.Width);
                var canvas = surface.Canvas;
                canvas.Clear(SKColors.Transparent);
                canvas.Translate(-region.X, -region.Y);
                using var paint = new SKPaint
                {
                    IsAntialias = Antialiased || Feather > 0,
                    Color = SKColors.White,
                    Style = SKPaintStyle.Fill,
                };
                if (Feather > 0)
                {
                    paint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, (float)(Feather / 2));
                }
                canvas.DrawPath(Path, paint);
                canvas.Flush();
            }
        }
        return bytes;
    }

    /// <summary>Coverage for just the selected region of the canvas, ready to clip edits.</summary>
    public SelectionClip Clip(int canvasWidth, int canvasHeight)
    {
        if (IsEmpty) return SelectionClip.Nothing;
        var region = CoverageBounds.Inflate(1, 1).Integral().Intersect(new RectD(0, 0, canvasWidth, canvasHeight));
        if (region.IsNull || region.Width < 1 || region.Height < 1) return SelectionClip.Nothing;
        var r = RectI.From(region);
        return new SelectionClip(r, Coverage(r));
    }

    // MARK: Boolean operations

    public static SKPath Union(SKPath a, SKPath b) => a.Op(b, SKPathOp.Union) ?? new SKPath(a);
    public static SKPath Intersect(SKPath a, SKPath b) => a.Op(b, SKPathOp.Intersect) ?? new SKPath();
    public static SKPath Subtract(SKPath a, SKPath b) => a.Op(b, SKPathOp.Difference) ?? new SKPath(a);

    public static SKPath CanvasPath(int width, int height)
    {
        var path = new SKPath();
        path.AddRect(new SKRect(0, 0, width, height));
        return path;
    }

    /// <summary>A band <paramref name="width"/> wide centered on the outline, with round joins (Expand/Contract).</summary>
    public static SKPath StrokeBand(SKPath path, double width)
    {
        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = (float)width,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            StrokeMiter = 10,
        };
        var result = new SKPath();
        paint.GetFillPath(path, result);
        return result;
    }
}

/// <summary>Selection coverage for one region of the document. An empty selection clips everything away.</summary>
public sealed class SelectionClip
{
    public static readonly SelectionClip Nothing = new(new RectI(0, 0, 0, 0), null);

    public RectI Rect { get; }
    /// <summary>One byte per pixel of <see cref="Rect"/>; null when nothing is selected.</summary>
    public byte[]? Coverage { get; }

    public SelectionClip(RectI rect, byte[]? coverage)
    {
        Rect = rect;
        Coverage = coverage;
    }

    public bool IsNothing => Coverage is null || Rect.Width <= 0 || Rect.Height <= 0;

    /// <summary>Coverage (0…255) at a document pixel, 0 outside.</summary>
    public byte At(int x, int y)
    {
        if (Coverage is null) return 0;
        int lx = x - Rect.X, ly = y - Rect.Y;
        if ((uint)lx >= (uint)Rect.Width || (uint)ly >= (uint)Rect.Height) return 0;
        return Coverage[ly * Rect.Width + lx];
    }

    /// <summary>Coverage (0…1) at a document position, sampled at the pixel it falls in.</summary>
    public double At(double x, double y) => At((int)Math.Floor(x), (int)Math.Floor(y)) / 255.0;

    /// <summary>The coverage as an alpha-only image placed at <see cref="Rect"/> (for clipping draws).</summary>
    public SKImage? ToImage()
    {
        if (Coverage is null) return null;
        var info = new SKImageInfo(Rect.Width, Rect.Height, SKColorType.Alpha8, SKAlphaType.Premul);
        return SKImage.FromPixelCopy(info, Coverage, Rect.Width);
    }
}

/// <summary>An integer rectangle.</summary>
public readonly record struct RectI(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public long Area => (long)Width * Height;

    public static RectI From(RectD r)
    {
        var i = r.Integral();
        return new RectI((int)i.X, (int)i.Y, (int)i.Width, (int)i.Height);
    }

    public RectD ToD() => new(X, Y, Width, Height);
    public SKRectI ToSK() => new(X, Y, Right, Bottom);

    public RectI Intersect(RectI o)
    {
        int l = Math.Max(X, o.X), t = Math.Max(Y, o.Y), r = Math.Min(Right, o.Right), b = Math.Min(Bottom, o.Bottom);
        return r <= l || b <= t ? new RectI(l, t, 0, 0) : new RectI(l, t, r - l, b - t);
    }

    public RectI Union(RectI o)
    {
        if (IsEmpty) return o;
        if (o.IsEmpty) return this;
        int l = Math.Min(X, o.X), t = Math.Min(Y, o.Y), r = Math.Max(Right, o.Right), b = Math.Max(Bottom, o.Bottom);
        return new RectI(l, t, r - l, b - t);
    }

    public RectI Offset(int dx, int dy) => new(X + dx, Y + dy, Width, Height);
}
