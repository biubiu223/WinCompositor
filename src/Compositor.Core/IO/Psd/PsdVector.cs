using System.Text;
using Compositor.Core.Geometry;
using Compositor.Core.Localization;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Core.IO.Psd;

/// <summary>
/// Photoshop vector layers: fill rectangles and ellipses become live shape layers; other vector masks ('vmsk'/'vsms')
/// are drawn into pixels with their solid fill and stroke ('SoCo', 'vstk'). Mirrors PSDVector.swift.
/// </summary>
public static class PsdVector
{
    public sealed record LiveShape(LayerShape Shape, RectI Bounds, RasterImage Image, List<string> Notes);
    public sealed record RasterShape(RasterImage Image, RectI Bounds);

    /// <summary>Shape layers bigger than this are drawn by hand rather than kept live.</summary>
    private const long MaxShapePixels = 100_000_000;

    public static LiveShape? Live(Dictionary<string, byte[]> extra, SizeD canvas, long remaining)
    {
        extra.TryGetValue("vstk", out var stroke);
        bool fillEnabled = (stroke is not null ? Bool(stroke, "fillEnabled") : null) ?? extra.ContainsKey("SoCo");
        bool strokeEnabled = (stroke is not null ? Bool(stroke, "strokeEnabled") : null) ?? false;
        if (!fillEnabled || !extra.TryGetValue("SoCo", out var soco) || Rgb(soco) is not { } fill) return null;
        var origin = Origination(extra.GetValueOrDefault("vogk")) ?? SharpRect(extra.GetValueOrDefault("vmsk") ?? extra.GetValueOrDefault("vsms"), canvas);
        if (origin is null) return null;
        var box = RectI.From(origin.Bounds);
        if (PixelSize(box.Width, box.Height, remaining) is not { } size) return null;
        box = new RectI(box.X, box.Y, size.Width, size.Height);
        var shape = new LayerShape(origin.Kind, fill.R, fill.G, fill.B, origin.CornerRadius);
        var image = shape.Render(size.Width, size.Height, "Shape");
        var notes = new List<string>();
        if (strokeEnabled) notes.Add(L.T("形状图层不支持 Photoshop 的描边，已略去。", "The Photoshop stroke isn’t supported on shape layers and was omitted."));
        notes.AddRange(origin.Notes);
        return new LiveShape(shape, box, image, notes);
    }

    public static RasterShape? Raster(Dictionary<string, byte[]> extra, SizeD canvas, long remaining)
    {
        var mask = extra.GetValueOrDefault("vmsk") ?? extra.GetValueOrDefault("vsms");
        if (mask is null || PathFrom(mask, canvas) is not { } path) return null;
        using (path)
        {
            var fill = extra.TryGetValue("SoCo", out var soco) ? Rgb(soco) : null;
            extra.TryGetValue("vstk", out var stroke);
            bool fillEnabled = (stroke is not null ? Bool(stroke, "fillEnabled") : null) ?? fill is not null;
            bool strokeEnabled = (stroke is not null ? Bool(stroke, "strokeEnabled") : null) ?? false;
            var strokeColor = stroke is not null ? Rgb(stroke) : null;
            double strokeWidth = (stroke is not null ? Unit(stroke, "strokeStyleLineWidth") : null) ?? 1;
            if (!(fillEnabled && fill is not null || strokeEnabled && strokeColor is not null)) return null;
            if (!double.IsFinite(strokeWidth)) return null;
            if (strokeEnabled && (strokeWidth < 0 || strokeWidth > 300_000)) throw ImageImportException.TooLarge();
            var bounds = path.Bounds;
            if (strokeEnabled)
            {
                float grow = (float)Math.Ceiling(strokeWidth / 2 + 1);
                bounds.Inflate(grow, grow);
            }
            var box = RectI.From(new RectD(bounds.Left, bounds.Top, bounds.Width, bounds.Height));
            if (PixelSize(box.Width, box.Height, remaining) is not { } size) return null;
            using var bitmap = new SKBitmap(RasterImage.ColorInfo(size.Width, size.Height));
            bitmap.Erase(SKColors.Transparent);
            using (var canvasSk = new SKCanvas(bitmap))
            {
                canvasSk.Translate(-box.X, -box.Y);
                if (fillEnabled && fill is { } f)
                {
                    using var paint = new SKPaint { IsAntialias = true, Color = new SKColor(Byte(f.R), Byte(f.G), Byte(f.B)) };
                    path.FillType = SKPathFillType.Winding;
                    canvasSk.DrawPath(path, paint);
                }
                if (strokeEnabled && strokeColor is { } s)
                {
                    using var paint = new SKPaint
                    {
                        IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = (float)strokeWidth,
                        StrokeJoin = SKStrokeJoin.Miter, StrokeMiter = 10, StrokeCap = SKStrokeCap.Butt,
                        Color = new SKColor(Byte(s.R), Byte(s.G), Byte(s.B)),
                    };
                    canvasSk.DrawPath(path, paint);
                }
            }
            return new RasterShape(RasterImage.FromBitmap(bitmap, name: "Shape"), new RectI(box.X, box.Y, size.Width, size.Height));
        }
    }

    private static byte Byte(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);

    /// <summary>Sizes that fit the per-side and remaining-pixel limits; too big throws, degenerate is null.</summary>
    private static (int Width, int Height)? PixelSize(double width, double height, long remaining)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height)) return null;
        if (Math.Abs(width) > 300_000 || Math.Abs(height) > 300_000) throw ImageImportException.TooLarge();
        long budget = Math.Min(MaxShapePixels, Math.Max(0, remaining));
        int w = Math.Max(1, (int)width), h = Math.Max(1, (int)height);
        if ((long)w * h > budget) throw ImageImportException.TooLarge();
        return (w, h);
    }

    private sealed record Origin(ShapeKind Kind, RectD Bounds, double CornerRadius = 0)
    {
        public List<string> Notes { get; } = new();
    }

    /// <summary>'vogk' origination: 1 or 2 a rectangle (2 rounded), 5 an ellipse.</summary>
    private static Origin? Origination(byte[]? data)
    {
        if (data is null || Int32(data, "keyOriginType") is not { } type) return null;
        ShapeKind kind;
        switch (type)
        {
            case 1: case 2: kind = ShapeKind.Rectangle; break;
            case 5: kind = ShapeKind.Ellipse; break;
            default: return null;
        }
        int from = OffsetOf("keyOriginShapeBBox", data) ?? 0;
        if (Unit(data, "Left", from) is not { } left || Unit(data, "Top ", from) is not { } top
            || Unit(data, "Rght", from) is not { } right || Unit(data, "Btom", from) is not { } bottom) return null;
        var bounds = new RectD(left, top, right - left, bottom - top);
        if (bounds.Width < 1 || bounds.Height < 1 || !double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y)) return null;
        double radius = 0;
        if (kind == ShapeKind.Rectangle && OffsetOf("keyOriginRRectRadii", data) is { } radiiAt)
        {
            var radii = new[] { "topLeft", "topRight", "bottomRight", "bottomLeft" }.Select(k => Unit(data, k, radiiAt)).ToList();
            if (radii.All(r => r is not null))
            {
                double lo = radii.Min()!.Value, hi = radii.Max()!.Value;
                if (hi - lo > 0.5) return null; // corners of different radii stay pixels
                radius = hi;
            }
        }
        return new Origin(kind, bounds, radius);
    }

    /// <summary>A vector mask of four sharp corners: a rectangle.</summary>
    private static Origin? SharpRect(byte[]? data, SizeD canvas)
    {
        if (data is null || PathFrom(data, canvas) is not { } path) return null;
        using (path)
        {
            int offset = 8, remaining = 0;
            var anchors = new List<PointD>();
            bool sharp = true;
            while (offset + 26 <= data.Length)
            {
                int type = BigEndian.I16(data, offset);
                int body = offset + 2;
                offset += 26;
                switch (type)
                {
                    case 0: case 3:
                        if (anchors.Count > 0) return null;
                        remaining = BigEndian.I16(data, body);
                        break;
                    case 1: case 2: case 4: case 5:
                        if (remaining <= 0) continue;
                        remaining--;
                        var incoming = Point(data, body, canvas);
                        var anchor = Point(data, body + 8, canvas);
                        var outgoing = Point(data, body + 16, canvas);
                        if (incoming.DistanceTo(anchor) > 0.5 || outgoing.DistanceTo(anchor) > 0.5) sharp = false;
                        anchors.Add(anchor);
                        break;
                }
            }
            if (!sharp || anchors.Count != 4) return null;
            // Axis-aligned only: a rotated four-corner path stays a path.
            var xs = anchors.Select(a => Math.Round(a.X, 1)).Distinct().Count();
            var ys = anchors.Select(a => Math.Round(a.Y, 1)).Distinct().Count();
            if (xs != 2 || ys != 2) return null;
            var box = path.Bounds;
            if (box.Width < 1 || box.Height < 1) return null;
            return new Origin(ShapeKind.Rectangle, new RectD(box.Left, box.Top, box.Width, box.Height));
        }
    }

    /// <summary>The vector mask's path records (closed and open subpaths of Bézier knots, in canvas fractions).</summary>
    public static SKPath? PathFrom(byte[] data, SizeD canvas)
    {
        if (data.Length < 8 || canvas.Width <= 0 || canvas.Height <= 0) return null;
        var path = new SKPath();
        int offset = 8, remaining = 0;
        bool closed = true, first = true;
        var previousOut = new PointD(0, 0);
        PointD firstIn = default, firstAnchor = default;
        void Close()
        {
            if (first || !closed) return;
            path.CubicTo(previousOut.ToSK(), firstIn.ToSK(), firstAnchor.ToSK());
            path.Close();
        }
        while (offset + 26 <= data.Length)
        {
            int type = BigEndian.I16(data, offset);
            int body = offset + 2;
            offset += 26;
            switch (type)
            {
                case 0: case 3:
                    Close();
                    remaining = BigEndian.I16(data, body);
                    closed = type == 0;
                    first = true;
                    break;
                case 1: case 2: case 4: case 5:
                    if (remaining <= 0) continue;
                    remaining--;
                    var incoming = Point(data, body, canvas);
                    var anchor = Point(data, body + 8, canvas);
                    var outgoing = Point(data, body + 16, canvas);
                    if (first)
                    {
                        path.MoveTo(anchor.ToSK());
                        firstIn = incoming;
                        firstAnchor = anchor;
                        first = false;
                    }
                    else path.CubicTo(previousOut.ToSK(), incoming.ToSK(), anchor.ToSK());
                    previousOut = outgoing;
                    break;
            }
        }
        Close();
        if (path.IsEmpty) { path.Dispose(); return null; }
        return path;
    }

    private static PointD Point(byte[] data, int at, SizeD canvas)
    {
        double y = BigEndian.I32(data, at) / (double)0x1000000, x = BigEndian.I32(data, at + 4) / (double)0x1000000;
        return new PointD(x * canvas.Width, y * canvas.Height);
    }

    // MARK: Descriptor values, found by key

    public static (double R, double G, double B)? Rgb(byte[] data)
    {
        if (Double(data, "Rd  ") is not { } r || Double(data, "Grn ") is not { } g || Double(data, "Bl  ") is not { } b) return null;
        static double Channel(double v) => v > 1 ? Math.Clamp(v, 0, 255) / 255 : Math.Clamp(v, 0, 1);
        return (Channel(r), Channel(g), Channel(b));
    }

    public static bool? Bool(byte[] data, string key)
    {
        if (OffsetOf(key, data) is not { } start) return null;
        int type = start + key.Length;
        if (type + 5 > data.Length || Ascii(data, type, 4) != "bool") return null;
        return data[type + 4] != 0;
    }

    public static double? Unit(byte[] data, string key, int from = 0)
    {
        if (OffsetOf(key, data, from) is not { } at || OffsetOf("UntF", data, at) is not { } unit) return null;
        return DoubleAt(data, unit + 8);
    }

    private static int? Int32(byte[] data, string key)
    {
        if (OffsetOf(key, data) is not { } start) return null;
        int type = start + key.Length;
        if (type + 8 > data.Length || Ascii(data, type, 4) != "long") return null;
        return BigEndian.I32(data, type + 4);
    }

    private static double? Double(byte[] data, string key)
    {
        if (OffsetOf(key, data) is not { } start) return null;
        int type = start + key.Length;
        if (type + 12 > data.Length || Ascii(data, type, 4) != "doub") return null;
        return DoubleAt(data, type + 4);
    }

    private static double? DoubleAt(byte[] data, int offset)
    {
        if (offset < 0 || offset + 8 > data.Length) return null;
        ulong bits = (ulong)BigEndian.U32(data, offset) << 32 | BigEndian.U32(data, offset + 4);
        return BitConverter.UInt64BitsToDouble(bits);
    }

    private static int? OffsetOf(string key, byte[] data, int from = 0)
    {
        if (from >= data.Length) return null;
        int at = data.AsSpan(from).IndexOf(Encoding.ASCII.GetBytes(key));
        return at < 0 ? null : from + at;
    }

    private static string Ascii(byte[] data, int offset, int count) => Encoding.ASCII.GetString(data, offset, count);
}
