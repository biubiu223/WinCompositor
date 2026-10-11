using Compositor.Core.Geometry;

namespace Compositor.Core.Model;

public enum LayerSampling
{
    Nearest,
    Smooth,
    High,
}

public static class LayerSamplingNames
{
    public static string ToName(this LayerSampling s) => s switch
    {
        LayerSampling.Nearest => "Nearest",
        LayerSampling.Smooth => "Smooth",
        _ => "High quality",
    };

    public static LayerSampling? Parse(string? name) => name switch
    {
        "Nearest" => LayerSampling.Nearest,
        "Smooth" => LayerSampling.Smooth,
        "High quality" => LayerSampling.High,
        _ => null,
    };
}

/// <summary>
/// Unrotated bounds in document pixels; rotation is clockwise (in degrees) around their center.
/// Mirrors LayerTransform.swift.
/// </summary>
public readonly record struct LayerTransform
{
    public PointD Origin { get; init; }
    public SizeD Size { get; init; }
    public double Rotation { get; init; }
    public bool FlipX { get; init; }
    public bool FlipY { get; init; }
    public LayerSampling Sampling { get; init; }

    public LayerTransform(PointD origin, SizeD size, double rotation = 0, bool flipX = false, bool flipY = false,
                          LayerSampling sampling = LayerSampling.High)
    {
        Origin = origin; Size = size; Rotation = rotation; FlipX = flipX; FlipY = flipY; Sampling = sampling;
    }

    public LayerTransform(double x, double y, double width, double height)
        : this(new PointD(x, y), new SizeD(width, height)) { }

    public static LayerTransform Canvas(int width, int height) => new(0, 0, width, height);

    public double Width => Size.Width;
    public double Height => Size.Height;
    public PointD Center => new(Origin.X + Size.Width / 2, Origin.Y + Size.Height / 2);
    public double Radians => (Rotation % 360) * Math.PI / 180;
    public RectD Bounds => new(Origin.X, Origin.Y, Size.Width, Size.Height);

    public bool IsValid =>
        double.IsFinite(Origin.X) && double.IsFinite(Origin.Y) && double.IsFinite(Size.Width) && double.IsFinite(Size.Height)
        && double.IsFinite(Rotation)
        && Size.Width >= 1 && Size.Width <= 300_000 && Size.Height >= 1 && Size.Height <= 300_000
        && Math.Abs(Origin.X) <= 1_000_000 && Math.Abs(Origin.Y) <= 1_000_000;

    /// <summary>Where a point of the unit square (0…1, y down) lands on the document.</summary>
    public PointD Point(PointD unit)
    {
        double x = (unit.X - 0.5) * Size.Width, y = (unit.Y - 0.5) * Size.Height;
        double r = Radians, c = Center.X, d = Center.Y;
        return new PointD(c + x * Math.Cos(r) - y * Math.Sin(r), d + x * Math.Sin(r) + y * Math.Cos(r));
    }

    public PointD Point(double ux, double uy) => Point(new PointD(ux, uy));

    public bool Contains(PointD point)
    {
        double x = point.X - Center.X, y = point.Y - Center.Y, r = Radians;
        return Math.Abs(x * Math.Cos(r) + y * Math.Sin(r)) <= Size.Width / 2
            && Math.Abs(-x * Math.Sin(r) + y * Math.Cos(r)) <= Size.Height / 2;
    }

    /// <summary>The four corners, in handle order (top-left, top-right, bottom-right, bottom-left).</summary>
    public PointD[] Corners() => new[] { Point(0, 0), Point(1, 0), Point(1, 1), Point(0, 1) };

    /// <summary>The upright bounding box of the placed (possibly rotated) layer.</summary>
    public RectD BoundingBox() => RectD.FromPoints(Corners());

    public double ScalePercent(SizeD pixelSize) => Size.Width / Math.Max(1, pixelSize.Width) * 100;

    public LayerTransform ScaledToPercent(double percent, SizeD pixelSize)
    {
        var size = new SizeD(pixelSize.Width * percent / 100, pixelSize.Height * percent / 100);
        return this with { Size = size, Origin = new PointD(Center.X - size.Width / 2, Center.Y - size.Height / 2) };
    }

    /// <summary>Whole pixels and whole degrees: what dragging, scaling and rotating leave behind.</summary>
    public LayerTransform Rounded() => this with
    {
        Origin = Origin.Rounded(),
        Size = new SizeD(Math.Max(1, Math.Round(Size.Width, MidpointRounding.AwayFromZero)), Math.Max(1, Math.Round(Size.Height, MidpointRounding.AwayFromZero))),
        Rotation = Math.Round(Rotation, MidpointRounding.AwayFromZero),
    };

    /// <summary>
    /// Maps a <paramref name="width"/> × <paramref name="height"/> pixel grid (y down) onto the document where this
    /// transform places it. Mirrors BrushRaster.pixelToDocument.
    /// </summary>
    public Affine PixelToDocument(double width, double height)
    {
        var c = Center;
        return Affine.Translation(c.X, c.Y)
            .Rotated(Radians)
            .Scaled(Size.Width / width * (FlipX ? -1 : 1), Size.Height / height * (FlipY ? -1 : 1))
            .Translated(-width / 2, -height / 2);
    }

    /// <summary>The unit square (0…1, y down) mapped where this transform places a layer.</summary>
    public Affine UnitToDocument => PixelToDocument(1, 1);

    /// <summary>
    /// A transform placing the unit square as <paramref name="map"/> does — a rotated, maybe flipped rectangle
    /// (shear is dropped). Keeps this transform's sampling.
    /// </summary>
    public LayerTransform Placing(Affine map)
    {
        double sign = FlipX ? -1 : 1;
        double angle = Math.Atan2(map.B * sign, map.A * sign);
        double along = -map.C * Math.Sin(angle) + map.D * Math.Cos(angle);
        var middle = map.Apply(0.5, 0.5);
        var size = new SizeD(Math.Sqrt(map.A * map.A + map.B * map.B), Math.Abs(along));
        double degrees = angle * 180 / Math.PI;
        double rotation = degrees + Math.Round((Rotation - degrees) / 360, MidpointRounding.AwayFromZero) * 360;
        return this with
        {
            Size = size,
            Rotation = rotation,
            FlipY = along < 0,
            Origin = new PointD(middle.X - size.Width / 2, middle.Y - size.Height / 2),
        };
    }

    /// <summary>This placement carried along as a layer moves from <paramref name="old"/> to <paramref name="next"/>.</summary>
    public LayerTransform Following(LayerTransform old, LayerTransform next)
    {
        if (old == next) return this;
        if (old.Size == next.Size && old.Rotation == next.Rotation && old.FlipX == next.FlipX && old.FlipY == next.FlipY)
        {
            return this with { Origin = new PointD(Origin.X + next.Origin.X - old.Origin.X, Origin.Y + next.Origin.Y - old.Origin.Y) };
        }
        return Placing(UnitToDocument.Concat(old.UnitToDocument.Invert()).Concat(next.UnitToDocument));
    }

    /// <summary>The same place on the document, whatever the sampling.</summary>
    public bool SamePlacement(LayerTransform other) => (this with { Sampling = other.Sampling }) == other;

    /// <summary>Handle positions on the unit square: corners and edge midpoints, clockwise from top-left.</summary>
    public static readonly PointD[] Handles =
    {
        new(0, 0), new(0.5, 0), new(1, 0), new(1, 0.5), new(1, 1), new(0.5, 1), new(0, 1), new(0, 0.5),
    };
}

/// <summary>Several layers transformed together: the upright box around them when the edit began, and each one's transform then.</summary>
public sealed record TransformGroup(LayerTransform Box, IReadOnlyDictionary<Guid, LayerTransform> Originals);

public sealed record TransformEdit
{
    public required Guid LayerId { get; init; }
    public required LayerTransform Draft { get; init; }
    public bool Persistent { get; init; }
    /// <summary>Set when transforming selected pixels rather than a layer.</summary>
    public FloatingTransform? Floating { get; init; }
    /// <summary>Set once a handle is Ctrl-dragged: the four corners (document pixels) move freely.</summary>
    public PointD[]? Corners { get; init; }
    /// <summary>Set when an unlinked mask is selected: the edit places the mask alone.</summary>
    public bool Mask { get; init; }
    public TransformGroup? Group { get; init; }
    public bool FromFields { get; init; }
}

/// <summary>
/// Pixels cut from a layer by the selection and transformed on their own (Ctrl+T with a selection, or dragging
/// selected pixels). <see cref="Hole"/> is the layer with them cut out (the layer itself when duplicating).
/// </summary>
public sealed record FloatingTransform(Guid LayerId, RasterImage Pixels, LayerTransform Original, Layer LayerBefore, bool Duplicate,
                                       RasterImage Hole, DocumentSelection? SelectionBefore)
{
    public SizeD PixelSize => new(Pixels.Width, Pixels.Height);
}

public enum TransformDragMode { Move, Resize, Rotate, Distort }

public sealed class TransformDrag
{
    public LayerTransform Original { get; }
    public PointD Start { get; }
    public TransformDragMode Mode { get; }
    public int Handle { get; }
    public PointD[]? OriginalCorners { get; init; }

    public TransformDrag(LayerTransform original, PointD start, TransformDragMode mode, int handle = 0)
    {
        Original = original; Start = start; Mode = mode; Handle = handle;
    }

    /// <summary>Corners after dragging to <paramref name="point"/> for a distortion; null otherwise.</summary>
    public PointD[]? CornersTo(PointD point, bool shift = false)
    {
        if (OriginalCorners is null) return null;
        var result = (PointD[])OriginalCorners.Clone();
        double dx = point.X - Start.X, dy = point.Y - Start.Y;
        if (shift)
        {
            if (Math.Abs(dx) >= Math.Abs(dy)) dy = 0; else dx = 0;
        }
        int[] moved;
        switch (Mode)
        {
            case TransformDragMode.Distort:
                moved = Handle % 2 == 0 ? new[] { Handle / 2 } : new[] { Handle / 2, (Handle / 2 + 1) % 4 };
                break;
            case TransformDragMode.Move:
                moved = new[] { 0, 1, 2, 3 };
                break;
            default:
                return null;
        }
        foreach (var corner in moved) result[corner] = new PointD(result[corner].X + dx, result[corner].Y + dy);
        return result;
    }

    public LayerTransform UpdatedTo(PointD point, bool lockRatio, bool shift, bool fromCenter = false)
    {
        var result = Original;
        switch (Mode)
        {
            case TransformDragMode.Distort:
                break;
            case TransformDragMode.Move:
            {
                double dx = point.X - Start.X, dy = point.Y - Start.Y;
                if (shift)
                {
                    if (Math.Abs(dx) >= Math.Abs(dy)) dy = 0; else dx = 0;
                }
                result = result with { Origin = new PointD(result.Origin.X + dx, result.Origin.Y + dy) };
                break;
            }
            case TransformDragMode.Rotate:
            {
                var center = Original.Center;
                double delta = Math.Atan2(point.Y - center.Y, point.X - center.X) - Math.Atan2(Start.Y - center.Y, Start.X - center.X);
                double rotation = result.Rotation + delta * 180 / Math.PI;
                if (shift) rotation = Math.Round(rotation / 15, MidpointRounding.AwayFromZero) * 15;
                result = result with { Rotation = rotation };
                break;
            }
            case TransformDragMode.Resize:
            {
                var handle = LayerTransform.Handles[Handle];
                var anchorUnit = fromCenter ? new PointD(0.5, 0.5) : new PointD(1 - handle.X, 1 - handle.Y);
                var anchor = Original.Point(anchorUnit);
                var initialHandle = Original.Point(handle);
                double dx = initialHandle.X + point.X - Start.X - anchor.X;
                double dy = initialHandle.Y + point.Y - Start.Y - anchor.Y;
                double span = fromCenter ? 2 : 1;
                double r = Original.Radians;
                double localX = (dx * Math.Cos(r) + dy * Math.Sin(r)) * span;
                double localY = (-dx * Math.Sin(r) + dy * Math.Cos(r)) * span;
                double sx = handle.X * 2 - 1, sy = handle.Y * 2 - 1;
                double rawWidth = sx == 0 ? Original.Size.Width : localX * sx;
                double rawHeight = sy == 0 ? Original.Size.Height : localY * sy;
                bool mirroredX = rawWidth < 0, mirroredY = rawHeight < 0;
                double width = Math.Max(1, Math.Abs(rawWidth));
                double height = Math.Max(1, Math.Abs(rawHeight));
                if (lockRatio != shift)
                {
                    double factor;
                    var os = Original.Size;
                    if (sx == 0) factor = height / os.Height;
                    else if (sy == 0) factor = width / os.Width;
                    else
                    {
                        factor = Math.Max(1 / Math.Min(os.Width, os.Height),
                            (localX * sx * os.Width + localY * sy * os.Height) / (os.Width * os.Width + os.Height * os.Height));
                    }
                    width = os.Width * factor;
                    height = os.Height * factor;
                }
                bool flipX = Original.FlipX ^ mirroredX, flipY = Original.FlipY ^ mirroredY;
                double offsetX = (0.5 - anchorUnit.X) * width * (mirroredX ? -1 : 1);
                double offsetY = (0.5 - anchorUnit.Y) * height * (mirroredY ? -1 : 1);
                var center = new PointD(anchor.X + offsetX * Math.Cos(r) - offsetY * Math.Sin(r),
                                        anchor.Y + offsetX * Math.Sin(r) + offsetY * Math.Cos(r));
                result = result with
                {
                    Size = new SizeD(width, height),
                    FlipX = flipX,
                    FlipY = flipY,
                    Origin = new PointD(center.X - width / 2, center.Y - height / 2),
                };
                break;
            }
        }
        return result.IsValid ? result : Original;
    }
}

/// <summary>
/// Moving a layer snaps its edges and center to targets. The pull is a fixed distance on screen,
/// so it feels the same at any zoom.
/// </summary>
public static class TransformSnap
{
    /// <summary>How close, in screen points, a guide comes before it snaps.</summary>
    public const double Distance = 10;

    public static (double OffsetX, double OffsetY, double? X, double? Y) Offset(RectD box, IReadOnlyList<double> xs, IReadOnlyList<double> ys, double tolerance)
    {
        var h = Shift(new[] { box.Left, box.MidX, box.Right }, xs, tolerance);
        var v = Shift(new[] { box.Top, box.MidY, box.Bottom }, ys, tolerance);
        return (h.Move, v.Move, h.Target, v.Target);
    }

    private static (double Move, double? Target) Shift(double[] guides, IReadOnlyList<double> targets, double tolerance)
    {
        (double Move, double Target)? best = null;
        foreach (var guide in guides)
        {
            foreach (var target in targets)
            {
                double move = target - guide;
                if (Math.Abs(move) > tolerance) continue;
                if (best is { } current && Math.Abs(current.Move) <= Math.Abs(move)) continue;
                best = (move, target);
            }
        }
        return best is { } b ? (b.Move, b.Target) : (0, null);
    }
}
