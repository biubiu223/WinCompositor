using Compositor.Core.Localization;
using Compositor.Core.Model;
using Compositor.Core.Rendering;
using SkiaSharp;

namespace Compositor.Core.Geometry;

/// <summary>
/// Free distortion (Ctrl-drag a transform handle): the layer's four corners move independently. Layer transforms are
/// affine, so a distortion is previewed live and, on Apply, the pixels (and mask) are resampled into the new shape —
/// as Photoshop does for pixel layers — leaving an ordinary upright layer over the shape's bounds. Mirrors DistortWarp
/// in Distort.swift.
/// </summary>
public static class DistortWarp
{
    /// <summary>The transform's corners in handle order: top-left, top-right, bottom-right, bottom-left.</summary>
    public static PointD[] Corners(LayerTransform transform) => transform.Corners();

    /// <summary>
    /// Four finite corners with some area to them. A convex shape is warped in perspective; anything else — a corner
    /// pulled past its neighbours, which folds the shape over — is warped as two triangles instead.
    /// </summary>
    public static bool IsUsable(IReadOnlyList<PointD>? corners)
    {
        if (corners is not { Count: 4 } || corners.Any(c => !double.IsFinite(c.X) || !double.IsFinite(c.Y) || Math.Abs(c.X) > 1_000_000 || Math.Abs(c.Y) > 1_000_000))
            return false;
        // Both halves need area, or one of them has nothing to draw.
        return Math.Abs(Area(corners[0], corners[1], corners[2])) > 0.01 && Math.Abs(Area(corners[0], corners[2], corners[3])) > 0.01;
    }

    private static double Area(PointD a, PointD b, PointD c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    /// <summary>A shape a perspective warp can take: convex, wound consistently either way (so a mirrored one counts).</summary>
    public static bool IsConvex(IReadOnlyList<PointD> corners)
    {
        if (!IsUsable(corners)) return false;
        double sign = 0;
        for (int i = 0; i < 4; i++)
        {
            PointD a = corners[i], b = corners[(i + 1) % 4], c = corners[(i + 2) % 4];
            double cross = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
            if (Math.Abs(cross) <= 0.01) return false;
            if (sign == 0) sign = cross < 0 ? -1 : 1;
            else if ((cross < 0) != (sign < 0)) return false;
        }
        return true;
    }

    /// <summary>The affine map taking three source points to three destination points.</summary>
    private static Affine? AffineMap((PointD, PointD, PointD) source, (PointD, PointD, PointD) target)
    {
        var u = new PointD(source.Item2.X - source.Item1.X, source.Item2.Y - source.Item1.Y);
        var v = new PointD(source.Item3.X - source.Item1.X, source.Item3.Y - source.Item1.Y);
        var uu = new PointD(target.Item2.X - target.Item1.X, target.Item2.Y - target.Item1.Y);
        var vv = new PointD(target.Item3.X - target.Item1.X, target.Item3.Y - target.Item1.Y);
        double det = u.X * v.Y - v.X * u.Y;
        if (Math.Abs(det) <= 1e-9) return null;
        double a = (uu.X * v.Y - vv.X * u.Y) / det, c = (vv.X * u.X - uu.X * v.X) / det;
        double b = (uu.Y * v.Y - vv.Y * u.Y) / det, d = (vv.Y * u.X - uu.Y * v.X) / det;
        return new Affine(a, b, c, d,
            target.Item1.X - (a * source.Item1.X + c * source.Item1.Y),
            target.Item1.Y - (b * source.Item1.X + d * source.Item1.Y));
    }

    /// <summary>The perspective mapping of the unit square (corners in <see cref="Corners"/> order) onto <paramref name="c"/>.</summary>
    public static SKMatrix Homography(IReadOnlyList<PointD> c)
    {
        double sx = c[0].X - c[1].X + c[2].X - c[3].X, sy = c[0].Y - c[1].Y + c[2].Y - c[3].Y;
        double g = 0, h = 0;
        if (Math.Abs(sx) > 1e-9 || Math.Abs(sy) > 1e-9)
        {
            double dx1 = c[1].X - c[2].X, dx2 = c[3].X - c[2].X, dy1 = c[1].Y - c[2].Y, dy2 = c[3].Y - c[2].Y;
            double den = dx1 * dy2 - dx2 * dy1;
            if (Math.Abs(den) > 1e-12)
            {
                g = (sx * dy2 - dx2 * sy) / den;
                h = (dx1 * sy - sx * dy1) / den;
            }
        }
        double a = c[1].X - c[0].X + g * c[1].X, b = c[3].X - c[0].X + h * c[3].X, x0 = c[0].X;
        double d = c[1].Y - c[0].Y + g * c[1].Y, e = c[3].Y - c[0].Y + h * c[3].Y, y0 = c[0].Y;
        return new SKMatrix((float)a, (float)b, (float)x0, (float)d, (float)e, (float)y0, (float)g, (float)h, 1);
    }

    /// <summary>Applies <see cref="Homography"/> to a point of the unit square, in double precision.</summary>
    public static PointD Map(IReadOnlyList<PointD> c, PointD p)
    {
        var m = Homography(c);
        double w = m.Persp0 * p.X + m.Persp1 * p.Y + 1;
        return new PointD((m.ScaleX * p.X + m.SkewX * p.Y + m.TransX) / w, (m.SkewY * p.X + m.ScaleY * p.Y + m.TransY) / w);
    }

    /// <summary>
    /// Where each corner of the image's own pixels lands: a flipped layer shows its pixels mirrored, so they go to the
    /// opposite corners of the shape. Returned in top-left, top-right, bottom-right, bottom-left order.
    /// </summary>
    public static PointD[] ImageCorners(IReadOnlyList<PointD> corners, bool flipX, bool flipY)
    {
        PointD Corner(int x, int y)
        {
            int u = flipX ? 1 - x : x, v = flipY ? 1 - y : y;
            return corners[new[] { 0, 1, 3, 2 }[v * 2 + u]];
        }
        return new[] { Corner(0, 0), Corner(1, 0), Corner(1, 1), Corner(0, 1) };
    }

    /// <summary>
    /// <paramref name="image"/>, shown through <paramref name="transform"/>, resampled so its corners land on
    /// <paramref name="corners"/>. Returns the warped pixels over the shape's whole-pixel bounds and the upright
    /// transform for them. <paramref name="limit"/> caps the longest side for previews.
    /// </summary>
    public static (RasterImage Image, LayerTransform Transform) Warp(RasterImage image, LayerTransform transform, IReadOnlyList<PointD> corners,
                                                                     double? limit = null)
    {
        if (!IsUsable(corners)) throw new ArgumentException("The shape has no area.");
        double minX = Math.Floor(corners.Min(c => c.X)), minY = Math.Floor(corners.Min(c => c.Y));
        var bounds = new RectD(minX, minY, Math.Ceiling(corners.Max(c => c.X)) - minX, Math.Ceiling(corners.Max(c => c.Y)) - minY);
        if (bounds.Width < 1 || bounds.Height < 1 || bounds.Width > 300_000 || bounds.Height > 300_000
            || bounds.Width * bounds.Height > DocumentLimits.MaxSurfacePixels)
            throw new InvalidOperationException(L.T("扭曲后的图像太大。", "The distorted image is too large."));
        var placed = new LayerTransform(bounds.Origin, bounds.Size, sampling: transform.Sampling);
        // A uniform mask already covers any shape.
        if (image.IsMask && image.IsUniform) return (image, placed);
        double factor = limit is { } l ? Math.Min(1, l / Math.Max(bounds.Width, bounds.Height)) : 1;
        int width = Math.Max(1, (int)Math.Ceiling(bounds.Width * factor));
        int height = Math.Max(1, (int)Math.Ceiling(bounds.Height * factor));
        var target = ImageCorners(corners, transform.FlipX, transform.FlipY)
            .Select(p => new PointD((p.X - bounds.X) * factor, (p.Y - bounds.Y) * factor)).ToArray();
        var info = image.IsMask
            ? new SKImageInfo(width, height, SKColorType.Alpha8, SKAlphaType.Premul)
            : new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        bitmap.Erase(SKColors.Transparent);
        using (var canvas = new SKCanvas(bitmap))
        {
            var sampling = transform.Sampling == LayerSampling.Nearest
                ? new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None)
                : new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
            using var paint = new SKPaint { IsAntialias = true };
            var source = new SKRect(0, 0, image.Width, image.Height);
            if (IsConvex(corners))
            {
                var map = Homography(target).PreConcat(SKMatrix.CreateScale(1f / image.Width, 1f / image.Height));
                canvas.SetMatrix(map);
                canvas.DrawImage(image.Image, source, source, sampling, paint);
            }
            else
            {
                // A folded shape (a corner dragged past its neighbours) has no perspective that takes the image to it,
                // so each half is taken there on its own, as two triangles meeting along the shape's diagonal.
                PointD tl = new(0, 0), tr = new(image.Width, 0), br = new(image.Width, image.Height), bl = new(0, image.Height);
                var halves = new[]
                {
                    ((tl, tr, br), (target[0], target[1], target[2])),
                    ((tl, br, bl), (target[0], target[2], target[3])),
                };
                foreach (var (from, to) in halves)
                {
                    if (AffineMap(from, to) is not { } map) continue;
                    canvas.Save();
                    using var triangle = new SKPath();
                    triangle.MoveTo(to.Item1.ToSK()); triangle.LineTo(to.Item2.ToSK()); triangle.LineTo(to.Item3.ToSK()); triangle.Close();
                    // Hard edges along the shared diagonal, so the two halves meet exactly instead of blending twice.
                    canvas.ClipPath(triangle, antialias: false);
                    canvas.Concat(map.ToSK());
                    canvas.DrawImage(image.Image, source, source, sampling, paint);
                    canvas.Restore();
                }
            }
        }
        return (RasterImage.FromBitmap(bitmap, image.IsMask, image.Name), placed);
    }

    /// <summary>
    /// A full-resolution warp cropped to its visible pixels: a distorted shape rarely fills its bounding box, so the
    /// layer (and its handles) should hug what is there. <c>Crop</c> is in the warp's pixels, for cropping a mask to match.
    /// </summary>
    public static (RasterImage Image, LayerTransform Transform, RectI Crop) WarpTrimmed(RasterImage image, LayerTransform transform, IReadOnlyList<PointD> corners)
    {
        var (warped, placed) = Warp(image, transform, corners);
        var full = new RectI(0, 0, warped.Width, warped.Height);
        var crop = PixelOps.AlphaBounds(warped);
        if (crop.IsEmpty || crop == full) return (warped, placed, full);
        var cropped = PixelOps.Crop(warped, crop, image.Name);
        placed = placed with
        {
            Origin = new PointD(placed.Origin.X + crop.X, placed.Origin.Y + crop.Y),
            Size = new SizeD(crop.Width, crop.Height),
        };
        return (cropped, placed, crop);
    }

    /// <summary>
    /// Where <paramref name="placement"/>'s corners (handle order) land when the perspective taking
    /// <paramref name="transform"/>'s corners to <paramref name="corners"/> is applied around it too — how a linked mask
    /// placed apart from its layer, or a layer of a group, distorts with the box.
    /// </summary>
    public static PointD[] Carried(LayerTransform placement, LayerTransform transform, IReadOnlyList<PointD> corners)
    {
        // Document → the box's unit square (unflipped), then the perspective.
        var toUnit = Affine.Translation(-transform.Center.X, -transform.Center.Y)
            .Concat(Affine.Rotation(-transform.Radians))
            .Concat(Affine.Scale(1 / transform.Size.Width, 1 / transform.Size.Height))
            .Concat(Affine.Translation(0.5, 0.5));
        return placement.Corners().Select(p => Map(corners, toUnit.Apply(p))).ToArray();
    }

    /// <summary>
    /// A mask warped like <see cref="Warp"/>, but <paramref name="background"/> (its tone past its pixels) outside the
    /// shape instead of black — for masks placed apart from their layers, which show beyond their own bounds.
    /// </summary>
    public static (RasterImage Image, LayerTransform Transform) WarpMask(RasterImage mask, LayerTransform transform, IReadOnlyList<PointD> corners,
                                                                         byte background, double? limit = null)
    {
        var (warped, placed) = Warp(mask, transform, corners, limit);
        if (background == 0 || ReferenceEquals(warped, mask)) return (warped, placed);
        int width = warped.Width, height = warped.Height;
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Alpha8, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(0, 0, 0, background));
            double sx = width / placed.Size.Width, sy = height / placed.Size.Height;
            using var shape = new SKPath();
            for (int i = 0; i < 4; i++)
            {
                var p = new SKPoint((float)((corners[i].X - placed.Origin.X) * sx), (float)((corners[i].Y - placed.Origin.Y) * sy));
                if (i == 0) shape.MoveTo(p); else shape.LineTo(p);
            }
            shape.Close();
            canvas.ClipPath(shape, antialias: true);
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
            canvas.DrawImage(warped.Image, 0, 0, paint);
        }
        return (RasterImage.FromBitmap(bitmap, true, mask.Name), placed);
    }

    /// <summary>
    /// Carries an outline drawn over the original pixels (placed by <paramref name="pixelToDocument"/>) into the distorted
    /// shape, so a transformed selection keeps matching its pixels. Null when the shape is folded.
    /// </summary>
    public static SKPath? MapPath(SKPath path, Affine pixelToDocument, SizeD pixelSize, LayerTransform transform, IReadOnlyList<PointD> corners)
    {
        if (!IsConvex(corners) || pixelSize.Width <= 0 || pixelSize.Height <= 0 || !pixelToDocument.IsInvertible) return null;
        var toPixels = pixelToDocument.Invert();
        var map = Homography(corners);
        var result = new SKPath();
        using var iterator = path.CreateRawIterator();
        var points = new SKPoint[4];
        SKPoint Carry(SKPoint point)
        {
            var pixel = toPixels.Apply(point.X, point.Y);
            double u = pixel.X / pixelSize.Width, v = pixel.Y / pixelSize.Height;
            if (transform.FlipX) u = 1 - u;
            if (transform.FlipY) v = 1 - v;
            return map.MapPoint((float)u, (float)v);
        }
        SKPathVerb verb;
        while ((verb = iterator.Next(points)) != SKPathVerb.Done)
        {
            switch (verb)
            {
                case SKPathVerb.Move: result.MoveTo(Carry(points[0])); break;
                case SKPathVerb.Line: result.LineTo(Carry(points[1])); break;
                case SKPathVerb.Quad: result.QuadTo(Carry(points[1]), Carry(points[2])); break;
                case SKPathVerb.Conic: result.ConicTo(Carry(points[1]), Carry(points[2]), iterator.ConicWeight()); break;
                case SKPathVerb.Cubic: result.CubicTo(Carry(points[1]), Carry(points[2]), Carry(points[3])); break;
                case SKPathVerb.Close: result.Close(); break;
            }
        }
        return result;
    }
}
