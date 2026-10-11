using System.Text.Json.Nodes;
using Compositor.Core.Geometry;
using Compositor.Core.Localization;
using SkiaSharp;

namespace Compositor.Core.Model;

public enum ShapeKind { Rectangle, Ellipse, Line }

public static class ShapeKinds
{
    public static string ToName(this ShapeKind kind) => kind.ToString();
    public static ShapeKind? Parse(string? name) => Enum.TryParse<ShapeKind>(name, out var k) && Enum.IsDefined(k) ? k : null;
    public static string ToDisplayName(this ShapeKind kind) => kind switch
    {
        ShapeKind.Rectangle => L.T("矩形", "Rectangle"),
        ShapeKind.Ellipse => L.T("椭圆", "Ellipse"),
        _ => L.T("直线", "Line"),
    };
}

/// <summary>
/// What a shape layer draws, kept so the shape can be drawn again at a new size (a rounded corner keeps its radius).
/// The layer's pixels are an ordinary raster; any other pixel edit drops this and leaves plain pixels. Mirrors
/// LayerShapeStyle in ShapeTool.swift.
/// </summary>
public sealed record LayerShape(ShapeKind Kind, double Red, double Green, double Blue, double CornerRadius = 0,
                                double? LineWidth = null, PointD? Start = null, PointD? End = null)
{
    public PaletteColor Color => new(Red, Green, Blue);

    public bool IsValid => new[] { Red, Green, Blue }.All(v => double.IsFinite(v) && v is >= 0 and <= 1)
                           && double.IsFinite(CornerRadius) && CornerRadius >= 0
                           && (LineWidth is null || (double.IsFinite(LineWidth.Value) && LineWidth > 0));

    /// <summary>The shape filling a box of <paramref name="width"/> × <paramref name="height"/> pixels.</summary>
    public RasterImage Render(int width, int height, string name)
    {
        using var bitmap = new SKBitmap(RasterImage.ColorInfo(width, height));
        if (bitmap.GetPixels() == IntPtr.Zero) throw new InvalidOperationException(L.T("内存不足。", "Not enough memory."));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            Draw(canvas, width, height, Color, Kind, CornerRadius, LineWidth ?? 0, Start, End);
        }
        return RasterImage.FromBitmap(bitmap, name: name);
    }

    public static void Draw(SKCanvas canvas, double width, double height, PaletteColor color, ShapeKind kind, double cornerRadius,
                            double lineWidth, PointD? start, PointD? end)
    {
        using var paint = new SKPaint { Color = color.ToSK(), IsAntialias = true };
        var bounds = new SKRect(0, 0, (float)width, (float)height);
        switch (kind)
        {
            case ShapeKind.Ellipse:
                canvas.DrawOval(bounds, paint);
                break;
            case ShapeKind.Line:
            {
                // The ends sit where they were dragged, as fractions of the box; older lines ran corner to corner,
                // inset by half their thickness.
                double thickness = Math.Max(1, lineWidth);
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = (float)thickness;
                paint.StrokeCap = SKStrokeCap.Round;
                double insetX = Math.Min(thickness, width) / 2, insetY = Math.Min(thickness, height) / 2;
                var from = start is { } s ? new SKPoint((float)(s.X * width), (float)(s.Y * height)) : new SKPoint((float)insetX, (float)insetY);
                var to = end is { } e ? new SKPoint((float)(e.X * width), (float)(e.Y * height)) : new SKPoint((float)(width - insetX), (float)(height - insetY));
                canvas.DrawLine(from, to, paint);
                break;
            }
            default:
            {
                float radius = (float)Math.Min(Math.Max(0, cornerRadius), Math.Min(width, height) / 2);
                if (radius > 0) canvas.DrawRoundRect(bounds, radius, radius, paint);
                else canvas.DrawRect(bounds, paint);
                break;
            }
        }
    }

    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["blue"] = Blue, ["cornerRadius"] = CornerRadius, ["green"] = Green, ["kind"] = Kind.ToName(), ["red"] = Red,
        };
        if (LineWidth is { } width) o["lineWidth"] = width;
        if (Start is { } start) o["start"] = new JsonArray(start.X, start.Y);
        if (End is { } end) o["end"] = new JsonArray(end.X, end.Y);
        return o;
    }

    public static LayerShape? FromJson(JsonNode? node)
    {
        if (node is not JsonObject o || ShapeKinds.Parse(Adjustments.Json.Str(o, "kind")) is not { } kind) return null;
        static PointD? Point(JsonNode? n) => n is JsonArray { Count: 2 } a && Adjustments.Json.NumOf(a[0]) is { } x && Adjustments.Json.NumOf(a[1]) is { } y
            ? new PointD(x, y) : null;
        var shape = new LayerShape(kind, Adjustments.Json.Num(o, "red") ?? 0, Adjustments.Json.Num(o, "green") ?? 0, Adjustments.Json.Num(o, "blue") ?? 0,
            Adjustments.Json.Num(o, "cornerRadius") ?? 0, Adjustments.Json.Num(o, "lineWidth"), Point(o["start"]), Point(o["end"]));
        return shape.IsValid ? shape : null;
    }
}
