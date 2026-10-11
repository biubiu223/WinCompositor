using Compositor.Core.Adjustments;
using Compositor.Core.Geometry;
using SkiaSharp;

namespace Compositor.Core.CameraRaw;

/// <summary>
/// The Camera Raw Filter over a bitmap, in Camera Raw's order: geometry, calibration, then Light and Color (and, while
/// it folds in, the curve, mixer and grading) through one composed table, then the curve pass, effects, grain, optics
/// and detail. Mirrors CameraRawSettings.apply.
/// </summary>
public static class CameraRawProcessor
{
    /// <summary>
    /// Grades <paramref name="bitmap"/> (premultiplied RGBA) in place. <paramref name="scale"/> is its pixels per layer
    /// pixel; <paramref name="clipping"/> 1 or 2 draws the clipping view instead of the grade; <paramref name="sharpenMask"/>
    /// draws the sharpening mask; <paramref name="known"/> is the picture's brightness when measured already.
    /// </summary>
    public static void Apply(SKBitmap bitmap, CameraRawSettings settings, double scale = 1, uint seed = 0, int clipping = 0,
                             bool sharpenMask = false, CameraRawTables.Brightness? known = null, int visualizePoint = -1)
    {
        var s = settings.Normalized;
        if (visualizePoint >= s.Mixer.Points.Length) visualizePoint = -1;
        if (s.IsIdentity && clipping == 0 && !sharpenMask && visualizePoint < 0) return;
        double pixelScale = scale > 0 ? scale : 1;
        bool foldsColor = clipping == 0 && !sharpenMask && visualizePoint < 0 && s.Curve.RefineSaturation == 0 && s.Mixer.Points.Length == 0;
        bool colorAdjusts = s.Curve.Adjusts || s.Mixer.Adjusts || s.Grading.Adjusts;
        bool paintColor = !foldsColor && clipping == 0 && !sharpenMask && (colorAdjusts || visualizePoint >= 0);
        bool paintEffects = clipping == 0 && !sharpenMask && s.AdjustsEffects;
        bool paintDetailOptics = clipping == 0 && (s.Detail.Adjusts || s.Optics.Adjusts || sharpenMask);

        if (clipping == 0 && !sharpenMask && visualizePoint < 0 && s.Geometry.Adjusts) Geometry(bitmap, s.Geometry);
        bool adaptive = s.Contrast != 0 || s.Highlights != 0 || s.Shadows != 0;
        var brightness = adaptive ? known ?? CameraRawPixels.Statistics(bitmap) : new CameraRawTables.Brightness();
        if (clipping == 0 && !sharpenMask && s.Calibration.ShadowTint != 0)
            CameraRawPixels.ShadowTint(bitmap, s.Calibration.ShadowTint, s.Calibration.Process);
        var table = CameraRawTables.Compose(CameraRawTables.Stages(s, brightness));
        if (foldsColor && colorAdjusts) table = FoldingColor(s, table);
        if (table is not null || clipping != 0) CameraRawPixels.Apply(bitmap, table, CameraRawTables.Grid, clipping);
        if (paintColor) CurveColor(bitmap, s, visualizePoint);
        if (paintEffects)
        {
            CameraRawPixels.Effects(bitmap, s, pixelScale);
            if (s.GrainAmount > 0)
                AdjustmentPixels.Grain(bitmap, new GrainSettings(s.GrainAmount, s.GrainKernelSize, s.GrainRoughness), seed, 0, 0, 1 / pixelScale);
        }
        if (paintDetailOptics)
        {
            if (sharpenMask) { CameraRawPixels.SharpenMaskOverlay(bitmap, s.Detail, pixelScale); }
            else
            {
                if (s.Optics.Adjusts) CameraRawPixels.Optics(bitmap, s.Optics, CameraRawTables.Data);
                if (s.Detail.Adjusts) CameraRawPixels.Detail(bitmap, s.Detail, pixelScale);
            }
        }
        bitmap.NotifyPixelsChanged();
    }

    /// <summary>Curve, Color Mixer and Color Grading run over a composed table, so they cost a pixel nothing more than its lookup.</summary>
    private static float[]? FoldingColor(CameraRawSettings s, float[]? composed)
    {
        var table = composed ?? CameraRawTables.Identity();
        if (table is null || CameraRawTables.Data is not { } data) return composed;
        var curve = s.Curve;
        if (curve.Adjusts)
            CameraRawPixels.TableCurves(table, curve.ToneTable(), CameraRawCurveSettings.ChannelTable(curve.Red),
                CameraRawCurveSettings.ChannelTable(curve.Green), CameraRawCurveSettings.ChannelTable(curve.Blue));
        var stages = CameraRawTables.MixerStages(s.Mixer);
        stages.AddRange(CameraRawTables.GradingStages(s.Grading));
        CameraRawPixels.ComposeOnto(table, data, stages, CameraRawTables.Size);
        return table;
    }

    /// <summary>The curve pass for when it can't fold into the table (Refine Saturation works on the whole curve).</summary>
    private static void CurveColor(SKBitmap bitmap, CameraRawSettings s, int visualizePoint)
    {
        var points = s.Mixer.Points.SelectMany(p => p.Floats()).ToArray();
        var curve = s.Curve;
        var mixer = CameraRawTables.Compose(CameraRawTables.MixerStages(s.Mixer));
        var grade = CameraRawTables.Compose(CameraRawTables.GradingStages(s.Grading));
        CameraRawPixels.CurveColor(bitmap, curve.ToneTable(), CameraRawCurveSettings.ChannelTable(curve.Red), CameraRawCurveSettings.ChannelTable(curve.Green),
            CameraRawCurveSettings.ChannelTable(curve.Blue), curve.RefineSaturation / 100, mixer, grade, CameraRawTables.Grid, points, visualizePoint);
    }

    /// <summary>Perspective and transform on the pixel grid; the output keeps the input's size. Mirrors CameraRawGeometrySettings.apply.</summary>
    private static void Geometry(SKBitmap bitmap, CameraRawGeometrySettings g)
    {
        int width = bitmap.Width, height = bitmap.Height;
        var (vertical, horizontal, rotate) = g.Effective(width, height);
        var corners = OutputCorners(g with { Vertical = vertical, Horizontal = horizontal, Rotate = rotate }, width, height);
        if (!DistortWarp.IsUsable(corners)) return;
        using var source = SKImage.FromBitmap(bitmap.Copy());
        using var surface = new SKCanvas(bitmap);
        surface.Clear(SKColors.Transparent);
        var map = DistortWarp.Homography(corners).PreConcat(SKMatrix.CreateScale(1f / width, 1f / height));
        surface.SetMatrix(map);
        using var paint = new SKPaint { IsAntialias = true };
        surface.DrawImage(source, new SKRect(0, 0, width, height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
        surface.Flush();
        if (!g.ConstrainCrop) return;
        // Constrain Crop: the empty edges trimmed off, the rest scaled back up to fill the frame.
        using var warped = bitmap.Copy();
        var bounds = AlphaBounds(warped);
        if (bounds.Width < 1 || bounds.Height < 1 || (bounds.Width >= width && bounds.Height >= height)) return;
        double fit = Math.Min((double)width / bounds.Width, (double)height / bounds.Height);
        float w = (float)(bounds.Width * fit), h = (float)(bounds.Height * fit);
        using var image = SKImage.FromBitmap(warped);
        surface.ResetMatrix();
        surface.Clear(SKColors.Transparent);
        surface.DrawImage(image, SKRect.Create(bounds.Left, bounds.Top, bounds.Width, bounds.Height),
            SKRect.Create((width - w) / 2, (height - h) / 2, w, h), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
    }

    private static unsafe SKRectI AlphaBounds(SKBitmap bitmap)
    {
        int minX = bitmap.Width, minY = bitmap.Height, maxX = -1, maxY = -1;
        byte* pixels = (byte*)bitmap.GetPixels();
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                // Mostly covered: a soft warp edge doesn't count as content.
                if (pixels[(long)y * bitmap.RowBytes + x * 4 + 3] < 250) continue;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
        return maxX < 0 ? SKRectI.Empty : new SKRectI(minX, minY, maxX + 1, maxY + 1);
    }

    /// <summary>Where the image's corners go (top-left, top-right, bottom-right, bottom-left, y down).</summary>
    private static PointD[] OutputCorners(CameraRawGeometrySettings g, int width, int height)
    {
        double w = width, h = height;
        double strength = g.Projection == CameraRawProjection.Perspective ? 1 : 0.55;
        double v = g.Vertical / 100 * w * 0.18 * strength, hz = g.Horizontal / 100 * h * 0.18 * strength;
        double aspectScale = 1 + g.Aspect / 200, zoom = 1 + g.Scale / 100;
        double shiftX = g.OffsetX / 100 * w * 0.15, shiftY = g.OffsetY / 100 * h * 0.15;
        // As the Mac app places them with y up, then turned over: Vertical widens the top, Horizontal the bottom.
        var points = new[]
        {
            new PointD(-v + shiftX, h + shiftY), new PointD(w + v + shiftX, h + shiftY),
            new PointD(w + hz + shiftX, -shiftY), new PointD(-hz + shiftX, -shiftY),
        };
        var center = new PointD(w / 2 + shiftX, h / 2 + shiftY);
        double radians = g.Rotate * Math.PI / 180, cos = Math.Cos(radians), sin = Math.Sin(radians);
        for (int i = 0; i < 4; i++)
        {
            double dx = points[i].X - center.X, dy = points[i].Y - center.Y;
            var p = new PointD(center.X + dx * cos - dy * sin, center.Y + dx * sin + dy * cos);
            p = new PointD(center.X + (p.X - center.X) * aspectScale, center.Y + (p.Y - center.Y) / aspectScale);
            p = new PointD(center.X + (p.X - center.X) * zoom, center.Y + (p.Y - center.Y) * zoom);
            points[i] = new PointD(p.X, h - p.Y);
        }
        return points;
    }

    /// <summary>Gray-world white balance of the opaque pixels: the Temperature and Tint that neutralize their average.</summary>
    public static unsafe (double Temperature, double Tint)? AutoBalance(SKBitmap bitmap)
    {
        double r = 0, g = 0, b = 0, count = 0;
        byte* pixels = (byte*)bitmap.GetPixels();
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                byte* p = pixels + (long)y * bitmap.RowBytes + x * 4;
                double alpha = p[3];
                if (alpha == 0) continue;
                r += CameraRawPixels.Decode(Math.Min(1, p[0] / alpha));
                g += CameraRawPixels.Decode(Math.Min(1, p[1] / alpha));
                b += CameraRawPixels.Decode(Math.Min(1, p[2] / alpha));
                count++;
            }
        if (count == 0) return null;
        return CameraRawTables.Neutralize(CameraRawPixels.Encode(r / count), CameraRawPixels.Encode(g / count), CameraRawPixels.Encode(b / count));
    }
}
