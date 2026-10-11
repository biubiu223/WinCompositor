using Compositor.Core.Geometry;
using Compositor.Core.Model;

namespace Compositor.Core.Painting;

public enum BrushToolMode { Paint, Erase }

/// <summary>The brush's options-bar settings. Mirrors BrushSettings in BrushStroke.swift.</summary>
public record struct BrushSettings
{
    public BrushSettings() { }
    public double Diameter { get; set; } = 40;
    public double Hardness { get; set; } = 1;
    public PaletteColor Color { get; set; } = PaletteColor.Black;
    /// <summary>Caps the whole stroke, as in Photoshop: overlapping dabs never exceed it.</summary>
    public double Opacity { get; set; } = 1;
    /// <summary>0–100: the brush trails the pointer on a string this long (screen points).</summary>
    public double Smoothing { get; set; } = 0;
    public bool Erasing { get; set; } = false;

    public const double MaxDiameter = 2100;
}

/// <summary>
/// A brush stroke on a layer's pixels or mask. Dabs accumulate into a coverage buffer — soft tips build up within the
/// stroke, hard tips keep their antialiased silhouette — and each pixel is recomposed as the original plus
/// color × coverage × opacity, so overlapping dabs never exceed the stroke's opacity. Mirrors BrushStroke.swift's
/// software path.
/// </summary>
public sealed class BrushStroke : RasterEdit
{
    public BrushSettings Settings { get; }
    /// <summary>Clone Stamp, Blur, and a finished Smudge or Liquify paint pixels from here rather than a color.</summary>
    public BrushSource? Source { get; set; }
    /// <summary>Spot Healing: shows a dark wash while painting; <see cref="Heal"/> rebuilds the area when it ends.</summary>
    public bool Healing { get; set; }
    /// <summary>The Remove tool: shows a pink wash while painting; the area is filled away when it ends.</summary>
    public bool Removing { get; set; }
    /// <summary>The undo step's name, when it isn't the brush's own.</summary>
    public string? EditName { get; set; }
    private byte[] _coverage = Array.Empty<byte>();
    private readonly List<PointD> _samples = new();
    private PointD? _previous;
    private double _distanceToNext;
    private (RectI Rect, byte[] Coverage)? _tailBackup;
    private RectI _dirty;
    private readonly byte _paintR, _paintG, _paintB, _paintGray;
    private static readonly float[] Falloff = BuildFalloff();

    public BrushStroke(Layer layer, bool mask, BrushSettings settings, int canvasWidth, int canvasHeight, bool growsMask = true)
        : base(layer, mask, canvasWidth, canvasHeight, grows: !mask || growsMask)
    {
        if (!double.IsFinite(settings.Diameter) || settings.Diameter < 1 || settings.Diameter > BrushSettings.MaxDiameter
            || !double.IsFinite(settings.Hardness) || settings.Hardness < 0 || settings.Hardness > 1
            || !double.IsFinite(settings.Opacity) || settings.Opacity < 0.01 || settings.Opacity > 1)
            throw new ArgumentOutOfRangeException(nameof(settings));
        Settings = settings;
        _paintR = settings.Color.R8;
        _paintG = settings.Color.G8;
        _paintB = settings.Color.B8;
        _paintGray = (byte)Math.Round(settings.Color.Luminance * 255);
        _coverage = new byte[Window.Width * Window.Height];
    }

    public bool HasPainted => !Touched.IsEmpty;

    protected override void OnReallocate(RectI old, RectI next)
    {
        var coverage = new byte[next.Width * next.Height];
        if (!old.IsEmpty && _coverage.Length == old.Width * old.Height)
        {
            for (int y = 0; y < old.Height; y++)
                Array.Copy(_coverage, y * old.Width, coverage, (old.Y - next.Y + y) * next.Width + (old.X - next.X), old.Width);
        }
        _coverage = coverage;
    }

    /// <summary>
    /// Adds a pointer sample. Dabs follow a centripetal Catmull–Rom curve through the samples; the newest piece is first
    /// drawn as a provisional straight tail (so the stroke never trails the cursor), then replaced by the curve when the
    /// next sample arrives or on <see cref="Flush"/>.
    /// </summary>
    public void Append(PointD point)
    {
        if (!point.IsFinite || Math.Abs(point.X) > 10_000_000 || Math.Abs(point.Y) > 10_000_000) return;
        if (_samples.Count > 0 && _samples[^1] == point) return;
        _dirty = new RectI(0, 0, 0, 0);
        RemoveTail();
        _samples.Add(point);
        if (_samples.Count > 4) _samples.RemoveAt(0);
        int n = _samples.Count;
        if (n == 1) Walk(point);
        else if (n >= 3) Curve(_samples[n - 3], _samples[n - 2], _samples[Math.Max(0, n - 4)], _samples[n - 1]);
        if (n >= 2) DrawTail(_samples[n - 2], point);
        Compose(_dirty);
    }

    /// <summary>Replaces the provisional tail with the stroke's final curve piece. Safe to repeat.</summary>
    public void Flush()
    {
        _dirty = new RectI(0, 0, 0, 0);
        RemoveTail();
        int n = _samples.Count;
        if (n >= 2)
        {
            Curve(_samples[n - 2], _samples[n - 1], _samples[Math.Max(0, n - 3)], _samples[n - 1]);
            var last = _samples[n - 1];
            _samples.Clear();
            _samples.Add(last);
        }
        Compose(_dirty);
    }

    private RectI AffectedGrid(PointD a, PointD b)
    {
        double reach = Settings.Diameter / 2 + 2;
        var box = RectD.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y))
            .Inflate(reach, reach).Intersect(new RectD(0, 0, CanvasWidth, CanvasHeight));
        if (box.IsNull || box.IsEmpty) return new RectI(0, 0, 0, 0);
        return RectI.From(DocumentToGrid.Apply(box).Inflate(1, 1)).Intersect(Extent);
    }

    private void DrawTail(PointD start, PointD end)
    {
        var rect = AffectedGrid(start, end);
        if (!rect.IsEmpty && EnsureWindow(rect))
        {
            rect = rect.Intersect(Window);
            var saved = new byte[rect.Width * rect.Height];
            for (int y = 0; y < rect.Height; y++)
                Array.Copy(_coverage, (rect.Y - Window.Y + y) * Window.Width + (rect.X - Window.X), saved, y * rect.Width, rect.Width);
            _tailBackup = (rect, saved);
        }
        var state = (_previous, _distanceToNext);
        Walk(end);
        (_previous, _distanceToNext) = state;
    }

    private void RemoveTail()
    {
        if (_tailBackup is not { } backup) return;
        _tailBackup = null;
        var rect = backup.Rect.Intersect(Window);
        for (int y = 0; y < rect.Height; y++)
        {
            int src = (rect.Y - backup.Rect.Y + y) * backup.Rect.Width + (rect.X - backup.Rect.X);
            Array.Copy(backup.Coverage, src, _coverage, (rect.Y - Window.Y + y) * Window.Width + (rect.X - Window.X), rect.Width);
        }
        _dirty = _dirty.Union(rect);
    }

    /// <summary>Centripetal Catmull–Rom from <paramref name="start"/> to <paramref name="end"/>: through every sample, without loops.</summary>
    private void Curve(PointD start, PointD end, PointD before, PointD after)
    {
        static double Knot(double t, PointD a, PointD b) => t + Math.Max(0.0001, Math.Sqrt(a.DistanceTo(b)));
        static PointD Mix(PointD a, PointD b, double ta, double tb, double t)
        {
            double wa = (tb - t) / (tb - ta), wb = (t - ta) / (tb - ta);
            return new PointD(a.X * wa + b.X * wb, a.Y * wa + b.Y * wb);
        }
        double t0 = 0, t1 = Knot(t0, before, start), t2 = Knot(t1, start, end), t3 = Knot(t2, end, after);
        int pieces = Math.Max(1, (int)Math.Ceiling(start.DistanceTo(end) / 2));
        for (int i = 1; i <= pieces; i++)
        {
            double t = t1 + (t2 - t1) * i / pieces;
            var a1 = Mix(before, start, t0, t1, t);
            var a2 = Mix(start, end, t1, t2, t);
            var a3 = Mix(end, after, t2, t3, t);
            var b1 = Mix(a1, a2, t0, t2, t);
            var b2 = Mix(a2, a3, t1, t3, t);
            Walk(i == pieces ? end : Mix(b1, b2, t1, t2, t));
        }
    }

    public static double SpacingFraction(double hardness) => hardness >= 1 ? 0.015 : 0.025;

    /// <summary>Lays evenly spaced dabs along a straight run from the previous dab.</summary>
    private void Walk(PointD point)
    {
        double spacing = Math.Max(0.25, Settings.Diameter * SpacingFraction(Settings.Hardness));
        if (_previous is { } previous)
        {
            double dx = point.X - previous.X, dy = point.Y - previous.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length > 0)
            {
                double distance = _distanceToNext;
                while (distance <= length)
                {
                    Dab(new PointD(previous.X + dx * distance / length, previous.Y + dy * distance / length));
                    distance += spacing;
                }
                _distanceToNext = distance - length;
            }
        }
        else
        {
            Dab(point);
            _distanceToNext = spacing;
        }
        _previous = point;
    }

    private void Dab(PointD center)
    {
        double radius = Settings.Diameter / 2;
        var docRect = new RectD(center.X - radius, center.Y - radius, radius * 2, radius * 2).Intersect(new RectD(0, 0, CanvasWidth, CanvasHeight));
        if (docRect.IsNull || docRect.IsEmpty) return;
        var grid = RectI.From(DocumentToGrid.Apply(docRect).Inflate(1, 1)).Intersect(Extent);
        if (grid.IsEmpty || !EnsureWindow(grid)) return;
        grid = grid.Intersect(Window);
        if (grid.IsEmpty) return;

        var m = GridToDocument;
        bool hard = Settings.Hardness >= 1;
        double hardness = Settings.Hardness;
        double cx = center.X, cy = center.Y;
        int windowX = Window.X, windowY = Window.Y, windowWidth = Window.Width;
        int canvasW = CanvasWidth, canvasH = CanvasHeight;
        var coverage = _coverage;

        void Row(int gy)
        {
            double py = gy + 0.5;
            int rowOffset = (gy - windowY) * windowWidth - windowX;
            for (int gx = grid.X; gx < grid.Right; gx++)
            {
                double px = gx + 0.5;
                double docX = m.A * px + m.C * py + m.Tx;
                double docY = m.B * px + m.D * py + m.Ty;
                if (docX < 0 || docY < 0 || docX >= canvasW || docY >= canvasH) continue;
                double ddx = docX - cx, ddy = docY - cy;
                double d = Math.Sqrt(ddx * ddx + ddy * ddy);
                double v;
                if (hard)
                {
                    v = radius - d + 0.5;
                    if (v <= 0) continue;
                    if (v > 1) v = 1;
                }
                else
                {
                    double u = d / radius;
                    if (u >= 1) continue;
                    if (u <= hardness) v = 1;
                    else
                    {
                        double t = (u - hardness) / (1 - hardness);
                        v = Falloff[(int)(t * (Falloff.Length - 1))];
                        if (v <= 0) continue;
                    }
                }
                int i = rowOffset + gx;
                int current = coverage[i];
                int next = hard ? Math.Max(current, (int)(v * 255 + 0.5)) : current + (int)(v * (255 - current) + 0.5);
                if (next > 255) next = 255;
                coverage[i] = (byte)next;
            }
        }

        if (grid.Area > 60_000) Parallel.For(grid.Y, grid.Bottom, Row);
        else for (int gy = grid.Y; gy < grid.Bottom; gy++) Row(gy);
        _dirty = _dirty.Union(grid);
    }

    /// <summary>Soft-brush falloff: a normalized Gaussian that fades across the radius and reaches zero at the rim.</summary>
    private static float[] BuildFalloff()
    {
        var table = new float[1025];
        const double k = 2.5;
        for (int i = 0; i < table.Length; i++)
        {
            double u = i / (double)(table.Length - 1);
            table[i] = (float)Math.Max(0, (Math.Exp(-k * u * u) - Math.Exp(-k)) / (1 - Math.Exp(-k)));
        }
        return table;
    }

    /// <summary>Rebuilds the pixels in <paramref name="rect"/> from the original and the coverage.</summary>
    private void Compose(RectI rect)
    {
        rect = rect.Intersect(Window);
        if (rect.IsEmpty) return;
        bool removing = Removing && !IsMask;
        bool healing = (Healing || Removing) && !IsMask;
        // Healing previews as a dark wash (removing, a pink one) at a fixed strength; the area is rebuilt when the stroke ends.
        double opacity = removing ? 0.5 : healing ? 0.45 : Settings.Opacity;
        bool erase = Settings.Erasing && !IsMask;
        var pixels = Pixels;
        var basePixels = Base;
        var coverage = _coverage;
        int windowX = Window.X, windowY = Window.Y, windowWidth = Window.Width;
        bool selection = Selection is not null;
        var map = GridToDocument;
        var clip = Selection;
        var source = Source;
        byte paintR = removing ? (byte)255 : healing ? (byte)31 : _paintR, paintG = removing ? (byte)70 : healing ? (byte)31 : _paintG, paintB = removing ? (byte)150 : healing ? (byte)31 : _paintB;

        void Row(int gy)
        {
            Span<byte> sample = stackalloc byte[4];
            for (int gx = rect.X; gx < rect.Right; gx++)
            {
                int i = (gy - windowY) * windowWidth + (gx - windowX);
                int cov = coverage[i];
                double k = cov / 255.0 * opacity;
                if (selection && cov > 0)
                {
                    var doc = map.Apply(gx + 0.5, gy + 0.5);
                    k *= clip!.At(doc.X, doc.Y);
                }
                // Painting from a source: where it doesn't reach there is nothing to paint.
                if (source is not null && k > 0 && !source.Sample(gx, gy, sample)) k = 0;
                if (IsMask)
                {
                    byte target = source is not null ? sample[0] : _paintGray;
                    pixels[i] = (byte)(basePixels[i] + (target - basePixels[i]) * k + 0.5);
                    continue;
                }
                int p = i * 4;
                if (k <= 0)
                {
                    pixels[p] = basePixels[p]; pixels[p + 1] = basePixels[p + 1];
                    pixels[p + 2] = basePixels[p + 2]; pixels[p + 3] = basePixels[p + 3];
                    continue;
                }
                double keep = 1 - k;
                if (source is not null)
                {
                    if (source.Replaces)
                    {
                        // Toward the source, alpha and all (Blur, a finished Smudge or Liquify).
                        for (int c = 0; c < 4; c++) pixels[p + c] = (byte)(sample[c] * k + basePixels[p + c] * keep + 0.5);
                    }
                    else
                    {
                        // Laid over what is there (Clone Stamp): clear parts of the sample leave the pixels alone.
                        double under = 1 - sample[3] / 255.0 * k;
                        for (int c = 0; c < 4; c++) pixels[p + c] = (byte)Math.Min(255, sample[c] * k + basePixels[p + c] * under + 0.5);
                    }
                    continue;
                }
                if (erase)
                {
                    pixels[p] = (byte)(basePixels[p] * keep + 0.5);
                    pixels[p + 1] = (byte)(basePixels[p + 1] * keep + 0.5);
                    pixels[p + 2] = (byte)(basePixels[p + 2] * keep + 0.5);
                    pixels[p + 3] = (byte)(basePixels[p + 3] * keep + 0.5);
                }
                else
                {
                    pixels[p] = (byte)(paintR * k + basePixels[p] * keep + 0.5);
                    pixels[p + 1] = (byte)(paintG * k + basePixels[p + 1] * keep + 0.5);
                    pixels[p + 2] = (byte)(paintB * k + basePixels[p + 2] * keep + 0.5);
                    pixels[p + 3] = (byte)(255 * k + basePixels[p + 3] * keep + 0.5);
                }
            }
        }

        if (rect.Area > 60_000) Parallel.For(rect.Y, rect.Bottom, Row);
        else for (int gy = rect.Y; gy < rect.Bottom; gy++) Row(gy);
        Touch(rect);
    }

    /// <summary>What the stroke covered (0–255) on the layer's own <paramref name="width"/> × <paramref name="height"/> pixels, within the selection.</summary>
    public byte[] PaintedCoverage(int width, int height)
    {
        var result = new byte[width * height];
        var painted = Touched.Intersect(Window).Intersect(new RectI(0, 0, width, height));
        if (painted.IsEmpty) return result;
        var map = GridToDocument;
        for (int y = painted.Y; y < painted.Bottom; y++)
            for (int x = painted.X; x < painted.Right; x++)
            {
                int cov = _coverage[(y - Window.Y) * Window.Width + (x - Window.X)];
                if (cov == 0) continue;
                if (Selection is { } clip)
                {
                    var doc = map.Apply(x + 0.5, y + 0.5);
                    cov = (int)Math.Round(cov * clip.At(doc.X, doc.Y));
                }
                result[y * width + x] = (byte)cov;
            }
        return result;
    }

    /// <summary>
    /// Spot Healing, once the stroke ends: the painted area rebuilt from the layer's pixels around it (see
    /// <see cref="Pixels.SpotHeal"/>), written where the wash was.
    /// </summary>
    public void Heal(global::Compositor.Core.Pixels.SpotHealingMode mode, uint seed)
    {
        if (!Healing || IsMask || Layer.Asset is not { } asset || Touched.IsEmpty) return;
        // The painted area, and room around it for the patches the healing looks for (up to ~3 spots away).
        var painted = Touched.Intersect(Window);
        int reach = (int)Math.Ceiling(Math.Max(painted.Width, painted.Height) * 3.2) + 20;
        var region = new RectI(painted.X - reach, painted.Y - reach, painted.Width + 2 * reach, painted.Height + 2 * reach).Intersect(SourceRect.Union(painted));
        if (region.IsEmpty) return;
        int w = region.Width, h = region.Height, stride = w * 4;
        var rgba = new byte[stride * h];
        using (var pixmap = asset.PeekPixels())
        {
            var src = pixmap.GetPixelSpan();
            var overlap = region.Intersect(SourceRect);
            for (int y = overlap.Y; y < overlap.Bottom; y++)
                src.Slice(y * pixmap.RowBytes + overlap.X * 4, overlap.Width * 4)
                   .CopyTo(rgba.AsSpan((y - region.Y) * stride + (overlap.X - region.X) * 4));
        }
        var coverage = new byte[w * h];
        var map = GridToDocument;
        for (int y = painted.Y; y < painted.Bottom; y++)
        {
            for (int x = painted.X; x < painted.Right; x++)
            {
                int cov = _coverage[(y - Window.Y) * Window.Width + (x - Window.X)];
                if (cov == 0) continue;
                if (Selection is { } clip)
                {
                    var doc = map.Apply(x + 0.5, y + 0.5);
                    cov = (int)Math.Round(cov * clip.At(doc.X, doc.Y));
                }
                coverage[(y - region.Y) * w + (x - region.X)] = (byte)cov;
            }
        }
        global::Compositor.Core.Pixels.SpotHeal.Run(rgba, coverage, w, h, stride, Settings.Opacity, mode, seed);
        var pixels = Pixels;
        for (int y = painted.Y; y < painted.Bottom; y++)
        {
            for (int x = painted.X; x < painted.Right; x++)
            {
                int r = (y - region.Y) * w + (x - region.X);
                if (coverage[r] == 0) continue;
                int i = ((y - Window.Y) * Window.Width + (x - Window.X)) * 4;
                Array.Copy(rgba, r * 4, pixels, i, 4);
            }
        }
        Touch(painted);
    }
}

/// <summary>Where a brush stroke takes its pixels from, instead of a color.</summary>
public abstract class BrushSource
{
    /// <summary>The source replaces the pixels (with coverage as the mix) rather than being laid over them.</summary>
    public bool Replaces { get; init; }

    /// <summary>
    /// The premultiplied color (or, for a mask, the gray in [0]) at the center of grid pixel (gx, gy); false where the
    /// source doesn't reach.
    /// </summary>
    public abstract bool Sample(int gx, int gy, Span<byte> value);

    /// <summary>Bilinear sample of a raster at a position in its pixels (centers at +0.5); false outside it.</summary>
    protected static bool Bilinear(byte[] pixels, int width, int height, int bpp, double x, double y, Span<byte> value)
    {
        x -= 0.5; y -= 0.5;
        if (x < -0.5 || y < -0.5 || x > width - 0.5 || y > height - 0.5) return false;
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        double fx = x - x0, fy = y - y0;
        int x1 = Math.Min(width - 1, x0 + 1), y1 = Math.Min(height - 1, y0 + 1);
        x0 = Math.Max(0, x0); y0 = Math.Max(0, y0);
        if (fx < 1e-6 && fy < 1e-6)
        {
            int p = (y0 * width + x0) * bpp;
            for (int c = 0; c < bpp; c++) value[c] = pixels[p + c];
            return true;
        }
        int p00 = (y0 * width + x0) * bpp, p10 = (y0 * width + x1) * bpp, p01 = (y1 * width + x0) * bpp, p11 = (y1 * width + x1) * bpp;
        for (int c = 0; c < bpp; c++)
        {
            double top = pixels[p00 + c] + (pixels[p10 + c] - pixels[p00 + c]) * fx;
            double bottom = pixels[p01 + c] + (pixels[p11 + c] - pixels[p01 + c]) * fx;
            value[c] = (byte)(top + (bottom - top) * fy + 0.5);
        }
        return true;
    }
}

/// <summary>
/// A raster laid out on the stroke's own pixel grid: its pixel (0, 0) sits at grid (<paramref name="originX"/>,
/// <paramref name="originY"/>), and each grid pixel reads from (<paramref name="offsetX"/>, <paramref name="offsetY"/>)
/// grid pixels away — the layer's pixels for Clone Stamp, a blurred copy of them for Blur.
/// </summary>
public sealed class GridSource(byte[] pixels, int width, int height, int bpp, int originX, int originY, double offsetX = 0, double offsetY = 0) : BrushSource
{
    public override bool Sample(int gx, int gy, Span<byte> value) =>
        Bilinear(pixels, width, height, bpp, gx + 0.5 + offsetX - originX, gy + 0.5 + offsetY - originY, value);
}

/// <summary>
/// A document-sized raster (the canvas as shown, a Smudge's working copy), read <paramref name="offset"/> document
/// pixels from where the brush paints.
/// </summary>
public sealed class DocumentSource(byte[] pixels, int width, int height, Geometry.Affine gridToDocument, Geometry.PointD offset) : BrushSource
{
    public override bool Sample(int gx, int gy, Span<byte> value)
    {
        var doc = gridToDocument.Apply(gx + 0.5, gy + 0.5);
        return Bilinear(pixels, width, height, 4, doc.X + offset.X, doc.Y + offset.Y, value);
    }
}
