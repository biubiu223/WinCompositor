using SkiaSharp;

namespace Compositor.Core.Geometry;

/// <summary>A point in double precision. Document coordinates are pixels with a top-left origin.</summary>
public readonly record struct PointD(double X, double Y)
{
    public static readonly PointD Zero = new(0, 0);
    public static PointD operator +(PointD a, PointD b) => new(a.X + b.X, a.Y + b.Y);
    public static PointD operator -(PointD a, PointD b) => new(a.X - b.X, a.Y - b.Y);
    public static PointD operator *(PointD a, double s) => new(a.X * s, a.Y * s);
    public double Length => Math.Sqrt(X * X + Y * Y);
    public double DistanceTo(PointD other) => Math.Sqrt((X - other.X) * (X - other.X) + (Y - other.Y) * (Y - other.Y));
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y);
    public PointD Rounded() => new(Math.Round(X, MidpointRounding.AwayFromZero), Math.Round(Y, MidpointRounding.AwayFromZero));
    public SKPoint ToSK() => new((float)X, (float)Y);
    public static PointD FromSK(SKPoint p) => new(p.X, p.Y);
    public override string ToString() => $"({X:0.###}, {Y:0.###})";
}

public readonly record struct SizeD(double Width, double Height)
{
    public static readonly SizeD Zero = new(0, 0);
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>A rectangle in double precision, with a top-left origin and non-negative size.</summary>
public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public static readonly RectD Empty = new(0, 0, 0, 0);
    /// <summary>The null rectangle: the result of intersecting rectangles that do not meet.</summary>
    public static readonly RectD Null = new(double.PositiveInfinity, double.PositiveInfinity, 0, 0);

    public double Left => X;
    public double Top => Y;
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double MidX => X + Width / 2;
    public double MidY => Y + Height / 2;
    public PointD Center => new(MidX, MidY);
    public PointD Origin => new(X, Y);
    public SizeD Size => new(Width, Height);
    public bool IsNull => double.IsInfinity(X) || double.IsInfinity(Y);
    public bool IsEmpty => IsNull || Width <= 0 || Height <= 0;

    public static RectD FromLTRB(double left, double top, double right, double bottom)
        => new(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));

    public static RectD FromPoints(IEnumerable<PointD> points)
    {
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity, maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        foreach (var p in points)
        {
            minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
        }
        return double.IsInfinity(minX) ? Null : FromLTRB(minX, minY, maxX, maxY);
    }

    public bool Contains(PointD p) => !IsNull && p.X >= Left && p.X < Right && p.Y >= Top && p.Y < Bottom;
    public bool Contains(RectD r) => !IsNull && !r.IsNull && r.Left >= Left && r.Top >= Top && r.Right <= Right && r.Bottom <= Bottom;

    public bool Intersects(RectD other)
    {
        if (IsNull || other.IsNull) return false;
        return other.Left < Right && other.Right > Left && other.Top < Bottom && other.Bottom > Top;
    }

    public RectD Intersect(RectD other)
    {
        if (IsNull || other.IsNull) return Null;
        double l = Math.Max(Left, other.Left), t = Math.Max(Top, other.Top);
        double r = Math.Min(Right, other.Right), b = Math.Min(Bottom, other.Bottom);
        if (r < l || b < t) return Null;
        return FromLTRB(l, t, r, b);
    }

    public RectD Union(RectD other)
    {
        if (IsNull) return other;
        if (other.IsNull) return this;
        return FromLTRB(Math.Min(Left, other.Left), Math.Min(Top, other.Top), Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom));
    }

    public RectD Inflate(double dx, double dy)
    {
        if (IsNull) return this;
        return FromLTRB(Left - dx, Top - dy, Right + dx, Bottom + dy);
    }

    public RectD Offset(double dx, double dy) => IsNull ? this : new(X + dx, Y + dy, Width, Height);

    /// <summary>The smallest rectangle with whole-number edges that contains this one (CGRect.integral).</summary>
    public RectD Integral()
    {
        if (IsNull) return this;
        double l = Math.Floor(Left + 1e-9), t = Math.Floor(Top + 1e-9);
        double r = Math.Ceiling(Right - 1e-9), b = Math.Ceiling(Bottom - 1e-9);
        if (r < l) r = l;
        if (b < t) b = t;
        return FromLTRB(l, t, r, b);
    }

    public SKRect ToSK() => new((float)Left, (float)Top, (float)Right, (float)Bottom);
    public SKRectI ToSKI() => new((int)Math.Floor(Left), (int)Math.Floor(Top), (int)Math.Ceiling(Right), (int)Math.Ceiling(Bottom));
    public static RectD FromSK(SKRect r) => FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    public override string ToString() => IsNull ? "(null)" : $"({X:0.###}, {Y:0.###}, {Width:0.###} × {Height:0.###})";
}

/// <summary>
/// A 2D affine transform in double precision, with Core Graphics' conventions:
/// x' = A·x + C·y + Tx, y' = B·x + D·y + Ty. <see cref="Concat"/> applies this transform first, then the other.
/// </summary>
public readonly record struct Affine(double A, double B, double C, double D, double Tx, double Ty)
{
    public static readonly Affine Identity = new(1, 0, 0, 1, 0, 0);

    public static Affine Translation(double x, double y) => new(1, 0, 0, 1, x, y);
    public static Affine Scale(double sx, double sy) => new(sx, 0, 0, sy, 0, 0);
    public static Affine Rotation(double radians)
    {
        double c = Math.Cos(radians), s = Math.Sin(radians);
        return new(c, s, -s, c, 0, 0);
    }

    /// <summary>This transform, then <paramref name="other"/>.</summary>
    public Affine Concat(Affine other) => new(
        A * other.A + B * other.C,
        A * other.B + B * other.D,
        C * other.A + D * other.C,
        C * other.B + D * other.D,
        Tx * other.A + Ty * other.C + other.Tx,
        Tx * other.B + Ty * other.D + other.Ty);

    /// <summary>A translation applied before this transform (CGAffineTransform.translatedBy).</summary>
    public Affine Translated(double x, double y) => Translation(x, y).Concat(this);
    /// <summary>A rotation applied before this transform (CGAffineTransform.rotated).</summary>
    public Affine Rotated(double radians) => Rotation(radians).Concat(this);
    /// <summary>A scale applied before this transform (CGAffineTransform.scaledBy).</summary>
    public Affine Scaled(double sx, double sy) => Scale(sx, sy).Concat(this);

    public double Determinant => A * D - B * C;
    public bool IsInvertible => Math.Abs(Determinant) > 1e-15 && double.IsFinite(Determinant);

    public Affine Invert()
    {
        double det = Determinant;
        if (Math.Abs(det) < 1e-300) return this;
        double ia = D / det, ib = -B / det, ic = -C / det, id = A / det;
        return new(ia, ib, ic, id, -(Tx * ia + Ty * ic), -(Tx * ib + Ty * id));
    }

    public PointD Apply(PointD p) => new(A * p.X + C * p.Y + Tx, B * p.X + D * p.Y + Ty);
    public PointD Apply(double x, double y) => new(A * x + C * y + Tx, B * x + D * y + Ty);
    /// <summary>A vector (no translation).</summary>
    public PointD ApplyVector(double x, double y) => new(A * x + C * y, B * x + D * y);

    /// <summary>The bounding box of a rectangle's four mapped corners.</summary>
    public RectD Apply(RectD r)
    {
        if (r.IsNull) return r;
        return RectD.FromPoints(new[]
        {
            Apply(r.Left, r.Top), Apply(r.Right, r.Top), Apply(r.Right, r.Bottom), Apply(r.Left, r.Bottom)
        });
    }

    public double ScaleX => Math.Sqrt(A * A + B * B);
    public double ScaleY => Math.Sqrt(C * C + D * D);

    public SKMatrix ToSK() => new((float)A, (float)C, (float)Tx, (float)B, (float)D, (float)Ty, 0, 0, 1);
    public static Affine FromSK(SKMatrix m) => new(m.ScaleX, m.SkewY, m.SkewX, m.ScaleY, m.TransX, m.TransY);
}
