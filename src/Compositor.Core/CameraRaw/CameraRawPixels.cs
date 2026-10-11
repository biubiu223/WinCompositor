using SkiaSharp;

namespace Compositor.Core.CameraRaw;

/// <summary>
/// Camera Raw's pixel work on premultiplied RGBA, in place: composing measured tables, looking colors up in them, and
/// the effects, curve, detail, optics and calibration passes. Ports of the camera_raw_* and adjust_camera_raw_*
/// functions in AdjustPixels.c.
/// </summary>
public static unsafe class CameraRawPixels
{
    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
    internal static double Decode(double e) => e <= 0.04045 ? e / 12.92 : Math.Pow((e + 0.055) / 1.055, 2.4);
    internal static double Encode(double l) => l <= 0 ? 0 : l >= 1 ? 1 : l <= 0.0031308 ? l * 12.92 : 1.055 * Math.Pow(l, 1 / 2.4) - 0.055;
    private static double Rec709(double r, double g, double b) => 0.2126 * r + 0.7152 * g + 0.0722 * b;

    /// <summary>Moves r, g, b so their luminance becomes <paramref name="target"/>, keeping the hue; black lifts to neutral.</summary>
    private static void ScaleLuminance(ref double r, ref double g, ref double b, double target)
    {
        target = Clamp01(target);
        double y = Rec709(r, g, b);
        if (Math.Abs(target - y) < 1e-8) return;
        if (y < 1e-8)
        {
            if (target > y) r = g = b = target;
            return;
        }
        double scale = target / y;
        r = Clamp01(r * scale); g = Clamp01(g * scale); b = Clamp01(b * scale);
    }

    private static void WritePremultiplied(byte* p, double r, double g, double b, double alpha)
    {
        p[0] = (byte)Math.Min(alpha, Math.Max(0, Math.Round(r * alpha)));
        p[1] = (byte)Math.Min(alpha, Math.Max(0, Math.Round(g * alpha)));
        p[2] = (byte)Math.Min(alpha, Math.Max(0, Math.Round(b * alpha)));
    }

    private delegate void PixelBody(byte* p, int x, int y);

    /// <summary>Every pixel with any coverage, rows in parallel.</summary>
    private static void EachPixel(SKBitmap bitmap, PixelBody body)
    {
        nint basePointer = bitmap.GetPixels();
        int width = bitmap.Width, stride = bitmap.RowBytes;
        Parallel.For(0, bitmap.Height, y =>
        {
            byte* row = (byte*)basePointer + (long)y * stride;
            for (int x = 0; x < width; x++)
            {
                byte* p = row + x * 4;
                if (p[3] != 0) body(p, x, y);
            }
        });
    }

    private static (double R, double G, double B) Straight(byte* p)
    {
        double a = p[3];
        return (Math.Min(1, p[0] / a), Math.Min(1, p[1] / a), Math.Min(1, p[2] / a));
    }

    // MARK: Tables

    private static void TableLookup(byte[] data, int offset, int size, ReadOnlySpan<double> input, Span<double> output)
    {
        Span<double> f = stackalloc double[3];
        Span<int> i = stackalloc int[3];
        for (int c = 0; c < 3; c++)
        {
            double x = Clamp01(input[c]) * (size - 1);
            i[c] = x >= size - 1 ? size - 2 : (int)x;
            f[c] = x - i[c];
        }
        double o0 = 0, o1 = 0, o2 = 0;
        for (int corner = 0; corner < 8; corner++)
        {
            int dr = corner >> 2, dg = corner >> 1 & 1, db = corner & 1;
            double w = (dr != 0 ? f[0] : 1 - f[0]) * (dg != 0 ? f[1] : 1 - f[1]) * (db != 0 ? f[2] : 1 - f[2]);
            if (w == 0) continue;
            int at = offset + (((i[0] + dr) * size + (i[1] + dg)) * size + (i[2] + db)) * 3;
            o0 += w * data[at]; o1 += w * data[at + 1]; o2 += w * data[at + 2];
        }
        output[0] = o0 / 255; output[1] = o1 / 255; output[2] = o2 / 255;
    }

    /// <summary>One stage's color for an sRGB color (0…1), in place.</summary>
    public static void StageColor(byte[] data, CameraRawStage stage, int size, Span<double> rgb)
    {
        double n0 = 0, n1 = 0, n2 = 0;
        Span<double> looked = stackalloc double[3];
        for (int t = 0; t < 8; t++)
        {
            double w = stage.Weights[t];
            if (w == 0) continue;
            if (stage.Tables[t] >= 0) TableLookup(data, stage.Tables[t], size, rgb, looked);
            else rgb.CopyTo(looked);
            n0 += w * looked[0]; n1 += w * looked[1]; n2 += w * looked[2];
        }
        rgb[0] = Clamp01(n0); rgb[1] = Clamp01(n1); rgb[2] = Clamp01(n2);
    }

    /// <summary>The stages run in order over a <paramref name="grid"/>³ lattice of sRGB colors (3 floats each, red slowest).</summary>
    public static float[] Compose(byte[] data, int grid, IReadOnlyList<CameraRawStage> stages, int size)
    {
        var output = new float[grid * grid * grid * 3];
        Parallel.For(0, grid, r =>
        {
            Span<double> color = stackalloc double[3];
            for (int g = 0; g < grid; g++)
                for (int b = 0; b < grid; b++)
                {
                    color[0] = (double)r / (grid - 1); color[1] = (double)g / (grid - 1); color[2] = (double)b / (grid - 1);
                    foreach (var stage in stages) StageColor(data, stage, size, color);
                    int o = ((r * grid + g) * grid + b) * 3;
                    output[o] = (float)color[0]; output[o + 1] = (float)color[1]; output[o + 2] = (float)color[2];
                }
        });
        return output;
    }

    /// <summary>The stages run over every entry of a composed table already made, in place.</summary>
    public static void ComposeOnto(float[] table, byte[] data, IReadOnlyList<CameraRawStage> stages, int size)
    {
        if (stages.Count == 0) return;
        int entries = table.Length / 3;
        Parallel.For(0, entries, i =>
        {
            Span<double> color = stackalloc double[] { table[i * 3], table[i * 3 + 1], table[i * 3 + 2] };
            foreach (var stage in stages) StageColor(data, stage, size, color);
            table[i * 3] = (float)color[0]; table[i * 3 + 1] = (float)color[1]; table[i * 3 + 2] = (float)color[2];
        });
    }

    private static void ComposedLookup(float[] table, int grid, ref double r, ref double g, ref double b)
    {
        double x0 = Clamp01(r) * (grid - 1), x1 = Clamp01(g) * (grid - 1), x2 = Clamp01(b) * (grid - 1);
        int i0 = x0 >= grid - 1 ? grid - 2 : (int)x0, i1 = x1 >= grid - 1 ? grid - 2 : (int)x1, i2 = x2 >= grid - 1 ? grid - 2 : (int)x2;
        double f0 = x0 - i0, f1 = x1 - i1, f2 = x2 - i2;
        double o0 = 0, o1 = 0, o2 = 0;
        for (int corner = 0; corner < 8; corner++)
        {
            int dr = corner >> 2, dg = corner >> 1 & 1, db = corner & 1;
            double w = (dr != 0 ? f0 : 1 - f0) * (dg != 0 ? f1 : 1 - f1) * (db != 0 ? f2 : 1 - f2);
            if (w == 0) continue;
            int at = (((i0 + dr) * grid + (i1 + dg)) * grid + (i2 + db)) * 3;
            o0 += w * table[at]; o1 += w * table[at + 1]; o2 += w * table[at + 2];
        }
        r = Clamp01(o0); g = Clamp01(o1); b = Clamp01(o2);
    }

    /// <summary>
    /// The composed table over every pixel. <paramref name="clipping"/> 1 shows clipped highlights lit on black, 2
    /// clipped shadows dark on white (Alt held on a Light slider).
    /// </summary>
    public static void Apply(SKBitmap bitmap, float[]? table, int grid, int clipping = 0)
    {
        EachPixel(bitmap, (p, _, _) =>
        {
            double alpha = p[3];
            double r = Math.Min(255, p[0] * 255.0 / alpha) / 255, g = Math.Min(255, p[1] * 255.0 / alpha) / 255, b = Math.Min(255, p[2] * 255.0 / alpha) / 255;
            if (table is not null) ComposedLookup(table, grid, ref r, ref g, ref b);
            if (clipping == 1)
            {
                r = r >= 254.5 / 255 ? 1 : 0; g = g >= 254.5 / 255 ? 1 : 0; b = b >= 254.5 / 255 ? 1 : 0;
            }
            else if (clipping == 2)
            {
                bool rc = r <= 0.5 / 255, gc = g <= 0.5 / 255, bc = b <= 0.5 / 255;
                if (rc || gc || bc) { r = rc ? 0 : 1; g = gc ? 0 : 1; b = bc ? 0 : 1; }
                else r = g = b = 1;
            }
            WritePremultiplied(p, r, g, b, alpha);
        });
    }

    /// <summary>The adaptive sliders' measures of the picture: brightest channel, luminance, log brightest, as sRGB levels.</summary>
    public static CameraRawTables.Brightness Statistics(SKBitmap bitmap)
    {
        var decoded = new double[256];
        for (int i = 0; i < 256; i++) decoded[i] = Decode(i / 255.0);
        nint basePointer = bitmap.GetPixels();
        int width = bitmap.Width, height = bitmap.Height, stride = bitmap.RowBytes;
        var sums = new double[height, 4];
        Parallel.For(0, height, y =>
        {
            byte* row = (byte*)basePointer + (long)y * stride;
            double brightest = 0, luminance = 0, logBrightest = 0, weight = 0;
            for (int x = 0; x < width; x++)
            {
                byte* p = row + x * 4;
                double alpha = p[3];
                if (alpha == 0) continue;
                double r, g, b;
                if (p[3] == 255) { r = decoded[p[0]]; g = decoded[p[1]]; b = decoded[p[2]]; }
                else { r = Decode(Math.Min(1, p[0] / alpha)); g = Decode(Math.Min(1, p[1] / alpha)); b = Decode(Math.Min(1, p[2] / alpha)); }
                double top = Math.Max(r, Math.Max(g, b)), w = alpha / 255;
                brightest += w * top;
                luminance += w * Rec709(r, g, b);
                logBrightest += w * Math.Log(top + 3e-4);
                weight += w;
            }
            sums[y, 0] = brightest; sums[y, 1] = luminance; sums[y, 2] = logBrightest; sums[y, 3] = weight;
        });
        double tb = 0, tl = 0, tlog = 0, tw = 0;
        for (int y = 0; y < height; y++) { tb += sums[y, 0]; tl += sums[y, 1]; tlog += sums[y, 2]; tw += sums[y, 3]; }
        if (tw <= 0) return new CameraRawTables.Brightness(127.5, 127.5, 127.5);
        return new CameraRawTables.Brightness(Encode(tb / tw) * 255, Encode(tl / tw) * 255, Encode(Math.Exp(tlog / tw) - 3e-4) * 255);
    }

    /// <summary>Clipped shadows painted blue and clipped highlights red, over the grade.</summary>
    public static void ClipOverlay(SKBitmap bitmap, bool shadows, bool highlights)
    {
        if (!shadows && !highlights) return;
        EachPixel(bitmap, (p, _, _) =>
        {
            var (r, g, b) = Straight(p);
            if (shadows && (r <= 0.5 / 255 || g <= 0.5 / 255 || b <= 0.5 / 255)) { r *= 0.35; g *= 0.35; b = b * 0.35 + 0.65; }
            if (highlights && (r >= 254.5 / 255 || g >= 254.5 / 255 || b >= 254.5 / 255)) { r = r * 0.35 + 0.65; g *= 0.35; b *= 0.35; }
            WritePremultiplied(p, r, g, b, p[3]);
        });
    }

    // MARK: Planes

    private static float[] Luma(SKBitmap bitmap)
    {
        int width = bitmap.Width;
        var luma = new float[width * bitmap.Height];
        nint basePointer = bitmap.GetPixels();
        int stride = bitmap.RowBytes;
        Parallel.For(0, bitmap.Height, y =>
        {
            byte* row = (byte*)basePointer + (long)y * stride;
            for (int x = 0; x < width; x++)
            {
                byte* p = row + x * 4;
                if (p[3] == 0) { luma[y * width + x] = 0; continue; }
                var (r, g, b) = Straight(p);
                luma[y * width + x] = (float)Rec709(r, g, b);
            }
        });
        return luma;
    }

    private static int Clamped(int index, int limit) => index < 0 ? 0 : index >= limit ? limit - 1 : index;

    /// <summary>Edge-clamped box blur of a plane.</summary>
    private static float[] BoxBlur(float[] source, int width, int height, int radius)
    {
        var result = new float[source.Length];
        if (radius < 1) { Array.Copy(source, result, source.Length); return result; }
        var temp = new float[source.Length];
        int window = radius * 2 + 1;
        Parallel.For(0, height, y =>
        {
            double sum = 0;
            int row = y * width;
            for (int k = -radius; k <= radius; k++) sum += source[row + Clamped(k, width)];
            for (int x = 0; x < width; x++)
            {
                temp[row + x] = (float)(sum / window);
                sum += source[row + Clamped(x + radius + 1, width)];
                sum -= source[row + Clamped(x - radius, width)];
            }
        });
        Parallel.For(0, width, x =>
        {
            double sum = 0;
            for (int k = -radius; k <= radius; k++) sum += temp[Clamped(k, height) * width + x];
            for (int y = 0; y < height; y++)
            {
                result[y * width + x] = (float)(sum / window);
                sum += temp[Clamped(y + radius + 1, height) * width + x];
                sum -= temp[Clamped(y - radius, height) * width + x];
            }
        });
        return result;
    }

    /// <summary>An edge-aware blur (a guided filter, the plane guiding itself): smooth within regions, stopping at edges.</summary>
    private static float[] GuidedFilter(float[] source, int width, int height, int radius, float epsilon)
    {
        int count = source.Length;
        var mean = BoxBlur(source, width, height, radius);
        var squares = new float[count];
        for (int i = 0; i < count; i++) squares[i] = source[i] * source[i];
        var square = BoxBlur(squares, width, height, radius);
        var a = new float[count];
        var b = new float[count];
        for (int i = 0; i < count; i++)
        {
            float variance = square[i] - mean[i] * mean[i];
            a[i] = variance / (variance + epsilon);
            b[i] = mean[i] - a[i] * mean[i];
        }
        var meanA = BoxBlur(a, width, height, radius);
        var meanB = BoxBlur(b, width, height, radius);
        var result = new float[count];
        for (int i = 0; i < count; i++) result[i] = meanA[i] * source[i] + meanB[i];
        return result;
    }

    private static int EffectsRadius(double baseRadius, double scale) => (int)Math.Round(Math.Clamp(baseRadius * (scale > 0 ? scale : 1), 1, 64));

    // MARK: Effects

    private static void Dehaze(ref double r, ref double g, ref double b, double amount, double[] haze)
    {
        double d = amount / 100;
        Span<double> c = stackalloc double[] { r, g, b };
        if (d < 0)
        {
            double veil = 0.3 * Math.Pow(-d, 1.3);
            for (int k = 0; k < 3; k++) c[k] = c[k] * (1 - veil) + haze[k] * veil;
        }
        else
        {
            double dark = 1;
            for (int k = 0; k < 3; k++) dark = Math.Min(dark, c[k] / Math.Max(haze[k], 1e-3));
            double transmission = Math.Max(0.5, 1 - 0.7 * d * dark);
            for (int k = 0; k < 3; k++) c[k] = (c[k] - haze[k]) / transmission + haze[k];
        }
        r = Clamp01(c[0]); g = Clamp01(c[1]); b = Clamp01(c[2]);
    }

    /// <summary>The picture's haze color: the mean of its brightest one percent of pixels by luminance.</summary>
    private static double[] HazeColor(SKBitmap bitmap)
    {
        var histogram = new double[256];
        int width = bitmap.Width, height = bitmap.Height, stride = bitmap.RowBytes;
        byte* basePointer = (byte*)bitmap.GetPixels();
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                byte* p = basePointer + (long)y * stride + x * 4;
                if (p[3] == 0) continue;
                var (r, g, b) = Straight(p);
                histogram[(int)Math.Round(255 * Rec709(r, g, b))] += p[3] / 255.0;
            }
        double total = histogram.Sum();
        int threshold = 255;
        for (double above = 0; threshold > 0 && above + histogram[threshold] < total * 0.01; threshold--) above += histogram[threshold];
        double s0 = 0, s1 = 0, s2 = 0, weight = 0;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                byte* p = basePointer + (long)y * stride + x * 4;
                if (p[3] == 0) continue;
                var (r, g, b) = Straight(p);
                if (Math.Round(255 * Rec709(r, g, b)) < threshold) continue;
                s0 += r * p[3]; s1 += g * p[3]; s2 += b * p[3];
                weight += p[3];
            }
        return weight > 0 ? new[] { s0 / weight, s1 / weight, s2 / weight } : new[] { 1.0, 1.0, 1.0 };
    }

    internal static double VignetteMask(double px, double py, double width, double height, double midpoint, double roundness, double feather)
    {
        double nx = px / width * 2 - 1, ny = py / height * 2 - 1;
        double square = Math.Max(Math.Abs(nx), Math.Abs(ny));
        double circle = Math.Sqrt(nx * nx + ny * ny) / Math.Sqrt(2);
        double shape = (1 - roundness / 100) * 0.5;
        double dist = circle + (square - circle) * shape;
        double start = midpoint / 100 * 0.85;
        double soft = Math.Max(0.05, feather / 100);
        double t = Clamp01((dist - start) / soft);
        return t * t * (3 - 2 * t);
    }

    private static void Vignette(ref double r, ref double g, ref double b, int x, int y, int width, int height,
                                 double amount, double midpoint, double roundness, double feather, double highlights, int style)
    {
        if (amount == 0) return;
        double mask = VignetteMask(x + 0.5, y + 0.5, width, height, midpoint + 5.9, Math.Min(100, 100 + roundness), feather);
        double effect = amount / 100 * mask;
        if (style == 0 && mask > 0)
        {
            double l0 = Decode(r), l1 = Decode(g), l2 = Decode(b);
            double bright = Clamp01((Rec709(r, g, b) - 0.45) / 0.55);
            if (amount < 0)
            {
                double ease = (1 - 0.3 * bright) * (1 - highlights / 100 * bright);
                double gain = Math.Exp(-5.8 * Math.Pow(-amount / 100, 1.43) * mask * ease);
                l0 *= gain; l1 *= gain; l2 *= gain;
            }
            else
            {
                double a = amount / 100, toward = Math.Pow(a, 0.85) * Math.Pow(mask, 1 + 1.5 * (1 - a));
                l0 += (1 - l0) * toward; l1 += (1 - l1) * toward; l2 += (1 - l2) * toward;
            }
            r = Encode(l0); g = Encode(l1); b = Encode(l2);
            return;
        }
        if (effect < 0) { double f = 1 + effect; r *= f; g *= f; b *= f; }
        else if (effect > 0) { r += (1 - r) * effect; g += (1 - g) * effect; b += (1 - b) * effect; }
        if (style == 1 && mask > 0)
        {
            double lum = Rec709(r, g, b), sat = 1 - 0.75 * mask * Math.Abs(amount / 100);
            r = Clamp01(lum + (r - lum) * sat); g = Clamp01(lum + (g - lum) * sat); b = Clamp01(lum + (b - lum) * sat);
        }
    }

    /// <summary>Texture, Clarity, Dehaze, Glow and the post-crop vignette. <paramref name="scale"/> is preview pixels per layer pixel.</summary>
    public static void Effects(SKBitmap bitmap, CameraRawSettings s, double scale)
    {
        int width = bitmap.Width, height = bitmap.Height;
        if (width == 0 || height == 0) return;
        if (s.Texture == 0 && s.Clarity == 0 && s.Dehaze == 0 && !(s.Glow > 0) && s.VignetteAmount == 0) return;
        float[]? fine = null, coarse = null, glowPlane = null;
        if (s.Texture != 0 || s.Clarity != 0 || s.Glow > 0)
        {
            var luma = Luma(bitmap);
            // Radii in proportion to the picture, fitted to Photoshop's on 1456-pixel-wide photos (8 and 32 there).
            double side = Math.Max(width, height);
            if (s.Texture != 0) fine = GuidedFilter(luma, width, height, (int)Math.Max(1, Math.Round(side * 8 / 1456)), 0.01f);
            if (s.Clarity != 0) coarse = GuidedFilter(luma, width, height, (int)Math.Max(2, Math.Round(side * 32 / 1456)), 0.01f);
            if (s.Glow > 0)
            {
                double spread = s.GlowSpread / 100, baseRadius = s.GlowStyle == CameraRawGlowStyle.Bloom ? 2.0 : 5.0;
                int radius = EffectsRadius(Math.Max(1, baseRadius * (1 + spread)), scale);
                float threshold = (float)(0.55 + 0.4 * (s.GlowRange / 100)), denominator = Math.Max(0.05f, 1 - threshold);
                var source = new float[luma.Length];
                for (int i = 0; i < luma.Length; i++) source[i] = Math.Clamp((luma[i] - threshold) / denominator, 0, 1);
                glowPlane = BoxBlur(source, width, height, radius);
            }
        }
        var haze = s.Dehaze != 0 ? HazeColor(bitmap) : new[] { 1.0, 1.0, 1.0 };
        double warmth = s.GlowWarmth / 100, glowRed, glowGreen, glowBlue, glowGain;
        if (s.GlowStyle == CameraRawGlowStyle.Halation) { glowRed = 1; glowGreen = 0.35 - 0.3 * warmth; glowBlue = 0.2 - 0.2 * warmth; glowGain = 1; }
        else
        {
            glowRed = 0.75 + 0.25 * warmth; glowGreen = 0.6 + 0.2 * warmth; glowBlue = 0.75 - 0.6 * warmth;
            glowGain = s.GlowStyle == CameraRawGlowStyle.Bloom ? 1.4 : 1;
        }
        int vignetteStyle = (int)s.VignetteStyle;
        EachPixel(bitmap, (p, x, y) =>
        {
            int index = y * width + x;
            var (r, g, b) = Straight(p);
            if (fine is not null || coarse is not null)
            {
                double tone = Rec709(r, g, b), detail = 0;
                if (fine is not null) detail += s.Texture / 100 * (s.Texture > 0 ? 0.6 : 0.5) * (tone - fine[index]);
                if (coarse is not null) detail += s.Clarity / 100 * (s.Clarity > 0 ? 0.85 : 0.5) * (tone - coarse[index]);
                if (detail != 0) ScaleLuminance(ref r, ref g, ref b, Clamp01(tone + detail));
            }
            if (s.Dehaze != 0) Dehaze(ref r, ref g, ref b, s.Dehaze, haze);
            if (glowPlane is not null)
            {
                double add = glowPlane[index] * (s.Glow / 100) * glowGain;
                r = Clamp01(r + add * glowRed); g = Clamp01(g + add * glowGreen); b = Clamp01(b + add * glowBlue);
            }
            Vignette(ref r, ref g, ref b, x, y, width, height, s.VignetteAmount, s.VignetteMidpoint, s.VignetteRoundness,
                     s.VignetteFeather, s.VignetteHighlights, vignetteStyle);
            WritePremultiplied(p, r, g, b, p[3]);
        });
    }

    // MARK: Curve and color

    private static double LutAt(float[] lut, double value)
    {
        double scaled = Clamp01(value) * 255;
        int lo = (int)scaled, hi = lo < 255 ? lo + 1 : 255;
        return lut[lo] + (lut[hi] - lut[lo]) * (scaled - lo);
    }

    /// <summary>
    /// The tone curve (on red, green and blue alike, as Photoshop's), Refine Saturation, each channel's curve, then the
    /// Color Mixer's and Color Grading's composed tables.
    /// </summary>
    private static double CircularDistance(double a, double b)
    {
        double d = Math.Abs(a - b);
        return d > 0.5 ? 1 - d : d;
    }

    /// <summary>How much a point color reaches an HSL color (0…1 each): a tent in hue, saturation and luminance.</summary>
    private static double PointWeight(double h, double s, double l, float[] points, int index)
    {
        int o = index * 9;
        double hueHalf = Math.Max(0.01, points[o + 6]), satHalf = Math.Max(0.01, points[o + 7]), lumHalf = Math.Max(0.01, points[o + 8]);
        double hueW = 1 - CircularDistance(h, points[o]) / hueHalf;
        double satW = 1 - Math.Abs(s - points[o + 1]) / satHalf;
        double lumW = 1 - Math.Abs(l - points[o + 2]) / lumHalf;
        return hueW < 0 || satW < 0 || lumW < 0 ? 0 : hueW * satW * lumW;
    }

    public static void CurveColor(SKBitmap bitmap, float[] toneLut, float[] redLut, float[] greenLut, float[] blueLut, double refineSaturation,
                                  float[]? mixerTable, float[]? gradeTable, int grid, float[]? points = null, int visualize = -1)
    {
        int pointCount = points is null ? 0 : points.Length / 9;
        bool hsl = pointCount > 0;
        EachPixel(bitmap, (p, _, _) =>
        {
            var (r, g, b) = Straight(p);
            double cr = LutAt(toneLut, r), cg = LutAt(toneLut, g), cb = LutAt(toneLut, b);
            if (refineSaturation < 0)
            {
                double br = r, bg = g, bb = b;
                ScaleLuminance(ref br, ref bg, ref bb, LutAt(toneLut, Rec709(r, g, b)));
                double k = -refineSaturation;
                cr += (br - cr) * k; cg += (bg - cg) * k; cb += (bb - cb) * k;
            }
            else if (refineSaturation > 0)
            {
                double lum = Rec709(cr, cg, cb), factor = 1 + refineSaturation;
                cr = Clamp01(lum + (cr - lum) * factor); cg = Clamp01(lum + (cg - lum) * factor); cb = Clamp01(lum + (cb - lum) * factor);
            }
            r = LutAt(redLut, cr); g = LutAt(greenLut, cg); b = LutAt(blueLut, cb);
            if (mixerTable is not null) ComposedLookup(mixerTable, grid, ref r, ref g, ref b);
            double sourceH = 0, sourceS = 0, sourceL = 0;
            if (hsl)
            {
                RgbToHsl(r, g, b, out double h, out double s, out double l);
                (sourceH, sourceS, sourceL) = (h, s, l);
                for (int i = 0; i < pointCount; i++)
                {
                    double w = PointWeight(h, s, l, points!, i);
                    if (w <= 0) continue;
                    h += points![i * 9 + 3] * w * (30.0 / 360);
                    s = Clamp01(s * (1 + points[i * 9 + 4] * w));
                    l = Clamp01(l + points[i * 9 + 5] * w * 0.25);
                }
                h -= Math.Floor(h);
                HslToRgb(h, s, l, out r, out g, out b);
            }
            if (gradeTable is not null) ComposedLookup(gradeTable, grid, ref r, ref g, ref b);
            if (visualize >= 0 && visualize < pointCount && PointWeight(sourceH, sourceS, sourceL, points!, visualize) <= 0.05)
            {
                r *= 0.35; g *= 0.35; b *= 0.35;
            }
            WritePremultiplied(p, r, g, b, p[3]);
        });
    }

    /// <summary>A composed table's colors run through the tone curve, then each channel's curve, in place.</summary>
    public static void TableCurves(float[] table, float[] toneLut, float[] redLut, float[] greenLut, float[] blueLut)
    {
        for (int i = 0; i < table.Length; i += 3)
        {
            table[i] = (float)LutAt(redLut, LutAt(toneLut, table[i]));
            table[i + 1] = (float)LutAt(greenLut, LutAt(toneLut, table[i + 1]));
            table[i + 2] = (float)LutAt(blueLut, LutAt(toneLut, table[i + 2]));
        }
    }

    // MARK: Detail

    private static double DetailRadius(double slider, double scale) => Math.Clamp((0.5 + slider / 100 * 2.5) * (scale > 0 ? scale : 1), 0.5, 64);

    private static float SharpenEdgeAt(float[] luma, int width, int height, int x, int y, int radius)
    {
        if (radius < 1) radius = 1;
        float center = luma[y * width + x], sum = 0;
        int count = 0;
        for (int dy = -radius; dy <= radius; dy += radius)
            for (int dx = -radius; dx <= radius; dx += radius)
            {
                if (dx == 0 && dy == 0) continue;
                int sx = x + dx, sy = y + dy;
                if (sx < 0 || sy < 0 || sx >= width || sy >= height) continue;
                sum += Math.Abs(luma[sy * width + sx] - center);
                count++;
            }
        return count > 0 ? sum / count : 0;
    }

    /// <summary>The sharpening's mask, in gray (Alt held on Masking): white where it sharpens.</summary>
    public static void SharpenMaskOverlay(SKBitmap bitmap, CameraRawDetailSettings d, double scale)
    {
        int width = bitmap.Width, height = bitmap.Height;
        var luma = Luma(bitmap);
        int radius = EffectsRadius(DetailRadius(d.SharpenRadius, scale), 1);
        double threshold = d.SharpenMasking / 100 * 0.35, boost = 0.5 + d.SharpenDetail / 100;
        EachPixel(bitmap, (p, x, y) =>
        {
            float edge = SharpenEdgeAt(luma, width, height, x, y, radius);
            double mask = Clamp01((edge * boost - threshold) / Math.Max(0.04, 0.35 - threshold * 0.5));
            byte gray = (byte)Math.Round(mask * p[3]);
            p[0] = p[1] = p[2] = gray;
        });
    }

    /// <summary>Noise reduction (luminance, then color) and sharpening.</summary>
    public static void Detail(SKBitmap bitmap, CameraRawDetailSettings d, double scale)
    {
        int width = bitmap.Width, height = bitmap.Height, count = width * height;
        if (count == 0 || (d.SharpenAmount == 0 && d.NoiseLuminance == 0 && d.NoiseColor == 0)) return;
        var luma = Luma(bitmap);
        if (d.NoiseLuminance > 0)
        {
            var work = BoxBlur(luma, width, height, EffectsRadius(1 + d.NoiseLuminance / 50, scale));
            double strength = d.NoiseLuminance / 100, preserve = d.NoiseLuminanceDetail / 100, contrast = d.NoiseLuminanceContrast / 100;
            var source = (float[])luma.Clone();
            EachPixel(bitmap, (p, x, y) =>
            {
                int index = y * width + x;
                float edge = SharpenEdgeAt(source, width, height, x, y, 1);
                double local = strength * (1 - preserve * Math.Min(1, edge * 6));
                float blurred = work[index];
                float target = (float)(source[index] * (1 - local) + blurred * local);
                if (contrast != 0) target = (float)(target + contrast * 0.25 * (source[index] - blurred));
                luma[index] = target;
                var (r, g, b) = Straight(p);
                ScaleLuminance(ref r, ref g, ref b, target);
                WritePremultiplied(p, r, g, b, p[3]);
            });
        }
        if (d.NoiseColor > 0)
        {
            // Each pixel keeps its brightness and takes the color around it: its red and blue differences from its
            // brightness are blurred (weighted by coverage) and put back.
            double strength = d.NoiseColor / 100;
            int radius = EffectsRadius(1.5 + strength * 4 * (0.5 + d.NoiseColorSmoothness / 100), scale);
            var red = new float[count]; var blue = new float[count]; var weight = new float[count];
            nint basePointer = bitmap.GetPixels();
            int stride = bitmap.RowBytes;
            Parallel.For(0, height, y =>
            {
                byte* row = (byte*)basePointer + (long)y * stride;
                for (int x = 0; x < width; x++)
                {
                    byte* p = row + x * 4;
                    int index = y * width + x;
                    if (p[3] == 0) continue;
                    var (r, g, b) = Straight(p);
                    double luminance = Rec709(r, g, b), coverage = p[3] / 255.0;
                    red[index] = (float)((r - luminance) * coverage);
                    blue[index] = (float)((b - luminance) * coverage);
                    weight[index] = (float)coverage;
                }
            });
            var redBlur = BoxBlur(red, width, height, radius);
            var blueBlur = BoxBlur(blue, width, height, radius);
            var weightBlur = BoxBlur(weight, width, height, radius);
            for (int i = 0; i < count; i++) if (weightBlur[i] > 1e-6f) { redBlur[i] /= weightBlur[i]; blueBlur[i] /= weightBlur[i]; }
            double preserve = d.NoiseColorDetail / 100;
            EachPixel(bitmap, (p, x, y) =>
            {
                int index = y * width + x;
                int up = Math.Max(0, y - radius), down = Math.Min(height - 1, y + radius);
                int left = Math.Max(0, x - radius), right = Math.Min(width - 1, x + radius);
                double dx = Math.Abs(redBlur[y * width + right] - redBlur[y * width + left]) + Math.Abs(blueBlur[y * width + right] - blueBlur[y * width + left]);
                double dy = Math.Abs(redBlur[down * width + x] - redBlur[up * width + x]) + Math.Abs(blueBlur[down * width + x] - blueBlur[up * width + x]);
                double local = strength * (1 - preserve * Math.Min(1, (dx + dy) * 3));
                var (r, g, b) = Straight(p);
                double luminance = Rec709(r, g, b);
                double redDiff = (r - luminance) + (redBlur[index] - (r - luminance)) * local;
                double blueDiff = (b - luminance) + (blueBlur[index] - (b - luminance)) * local;
                r = luminance + redDiff;
                b = luminance + blueDiff;
                g = (luminance - 0.2126 * r - 0.0722 * b) / 0.7152;
                WritePremultiplied(p, Clamp01(r), Clamp01(g), Clamp01(b), p[3]);
            });
        }
        if (d.SharpenAmount > 0)
        {
            luma = Luma(bitmap);
            int radius = EffectsRadius(DetailRadius(d.SharpenRadius, scale), 1);
            var work = BoxBlur(luma, width, height, radius);
            double amount = d.SharpenAmount / 100, detailMix = d.SharpenDetail / 100, threshold = d.SharpenMasking / 100 * 0.35;
            EachPixel(bitmap, (p, x, y) =>
            {
                int index = y * width + x;
                float edge = SharpenEdgeAt(luma, width, height, x, y, radius);
                // Masking at 0 sharpens everywhere; raising it keeps the sharpening to edges.
                double mask = threshold > 0 ? Clamp01((edge * (0.5 + detailMix) - threshold) / Math.Max(0.04, 0.35 - threshold * 0.5)) : 1;
                double high = luma[index] - work[index];
                double sharpened = Clamp01(luma[index] + high * amount * mask * 1.9 * (0.5 + detailMix));
                var (r, g, b) = Straight(p);
                ScaleLuminance(ref r, ref g, ref b, sharpened);
                WritePremultiplied(p, r, g, b, p[3]);
            });
        }
    }

    // MARK: Optics and calibration

    private static double HueDegrees(double r, double g, double b)
    {
        double maxc = Math.Max(r, Math.Max(g, b)), minc = Math.Min(r, Math.Min(g, b)), chroma = maxc - minc;
        if (chroma < 1e-6) return 0;
        double hue = maxc == r ? (g - b) / chroma % 6 : maxc == g ? (b - r) / chroma + 2 : (r - g) / chroma + 4;
        hue *= 60;
        return hue < 0 ? hue + 360 : hue;
    }

    public static double HueOf(double r, double g, double b) => HueDegrees(r, g, b);

    /// <summary>Hue in degrees, saturation and lightness (0…1) as the point colors measure them.</summary>
    public static (double Hue, double Saturation, double Lightness) HslOf(double r, double g, double b)
    {
        RgbToHsl(r, g, b, out double h, out double s, out double l);
        return (h * 360, s, l);
    }

    /// <summary>Rec. 709 luma of a gamma-encoded color, the tone the parametric curve's regions are split by.</summary>
    public static double ToneOf(double r, double g, double b) => Rec709(r, g, b);

    private static bool HueInRange(double hue, double low, double high) => low <= high ? hue >= low && hue <= high : hue >= low || hue <= high;

    private static void Defringe(ref double r, ref double g, ref double b, CameraRawOpticsSettings o)
    {
        double hue = HueDegrees(r, g, b), maxc = Math.Max(r, Math.Max(g, b)), chroma = maxc - Math.Min(r, Math.Min(g, b));
        if (chroma < 1e-6) return;
        double sat = chroma / maxc, reduce = 0;
        if (o.PurpleAmount > 0 && HueInRange(hue, o.PurpleHueLow, o.PurpleHueHigh)) reduce = Math.Max(reduce, o.PurpleAmount / 100);
        if (o.GreenAmount > 0 && HueInRange(hue, o.GreenHueLow, o.GreenHueHigh)) reduce = Math.Max(reduce, o.GreenAmount / 100);
        if (reduce <= 0) return;
        double lum = Rec709(r, g, b), factor = 1 - reduce * sat;
        r = Clamp01(lum + (r - lum) * factor); g = Clamp01(lum + (g - lum) * factor); b = Clamp01(lum + (b - lum) * factor);
    }

    private static void Chromatic(SKBitmap bitmap, double strength)
    {
        int width = bitmap.Width, height = bitmap.Height, stride = bitmap.RowBytes;
        var copy = new byte[height * stride];
        System.Runtime.InteropServices.Marshal.Copy(bitmap.GetPixels(), copy, 0, copy.Length);
        double cx = width * 0.5, cy = height * 0.5, maxR = Math.Sqrt(cx * cx + cy * cy);
        EachPixel(bitmap, (p, x, y) =>
        {
            double alpha = p[3];
            double dx = x + 0.5 - cx, dy = y + 0.5 - cy;
            double radial = Math.Sqrt(dx * dx + dy * dy) / maxR, shift = strength * radial * radial * 2.5;
            int rx = Clamped((int)Math.Round(x - shift), width), bx = Clamped((int)Math.Round(x + shift), width);
            int row = y * stride;
            double g = Math.Min(1, copy[row + x * 4 + 1] / alpha);
            double r = Math.Min(1, copy[row + rx * 4] / Math.Max(1.0, copy[row + rx * 4 + 3]));
            double b = Math.Min(1, copy[row + bx * 4 + 2] / Math.Max(1.0, copy[row + bx * 4 + 3]));
            WritePremultiplied(p, r, g, b, alpha);
        });
    }

    /// <summary>Lens vignette correction: Exposure through its measured tables, growing toward the corners.</summary>
    private static void VignetteCorrect(ref double r, ref double g, ref double b, int x, int y, int width, int height, double amount, double midpoint, byte[] data)
    {
        if (amount == 0) return;
        double dx = x + 0.5 - width / 2.0, dy = y + 0.5 - height / 2.0;
        double distance = Math.Sqrt(dx * dx + dy * dy) / Math.Sqrt(width * width / 4.0 + height * height / 4.0) * (1 + (50 - midpoint) / 100);
        double stops = Math.Sign(amount) * Math.Pow(Math.Abs(amount) / 100, 1.15) * 1.98 * Math.Pow(distance, 4.3);
        double position = Math.Min(20, Math.Max(0, (stops + 5) / 0.5));
        int index = position >= 20 ? 19 : (int)position;
        double fraction = position - index;
        Span<double> color = stackalloc double[] { r, g, b };
        Span<double> low = stackalloc double[3], high = stackalloc double[3];
        int baseOffset = CameraRawTables.ExposureTables * CameraRawTables.Entries;
        TableLookup(data, baseOffset + index * CameraRawTables.Entries, CameraRawTables.Size, color, low);
        TableLookup(data, baseOffset + (index + 1) * CameraRawTables.Entries, CameraRawTables.Size, color, high);
        r = Clamp01(low[0] + (high[0] - low[0]) * fraction);
        g = Clamp01(low[1] + (high[1] - low[1]) * fraction);
        b = Clamp01(low[2] + (high[2] - low[2]) * fraction);
    }

    public static void Optics(SKBitmap bitmap, CameraRawOpticsSettings o, byte[]? data)
    {
        int width = bitmap.Width, height = bitmap.Height;
        if (width == 0 || height == 0) return;
        double vignette = o.VignetteAmount + (o.EnableLensProfile ? o.ProfileVignetting / 100 : 0) * 35;
        if (o.DistortionK != 0) Filters.FilterPixels.LensDistortion(bitmap, o.DistortionK);
        if (o.RemoveChromaticAberration) Chromatic(bitmap, 0.45);
        if (o.PurpleAmount == 0 && o.GreenAmount == 0 && (vignette == 0 || data is null)) return;
        EachPixel(bitmap, (p, x, y) =>
        {
            var (r, g, b) = Straight(p);
            Defringe(ref r, ref g, ref b, o);
            if (data is not null) VignetteCorrect(ref r, ref g, ref b, x, y, width, height, vignette, o.VignetteMidpoint, data);
            WritePremultiplied(p, r, g, b, p[3]);
        });
    }

    private static void RgbToHsl(double r, double g, double b, out double h, out double s, out double l)
    {
        double maxc = Math.Max(r, Math.Max(g, b)), minc = Math.Min(r, Math.Min(g, b));
        l = (maxc + minc) * 0.5;
        double d = maxc - minc;
        if (d < 1e-6) { h = 0; s = 0; return; }
        s = d / (1 - Math.Abs(2 * l - 1));
        h = maxc == r ? (g - b) / d % 6 : maxc == g ? (b - r) / d + 2 : (r - g) / d + 4;
        h /= 6;
        if (h < 0) h += 1;
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 0.5) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }

    private static void HslToRgb(double h, double s, double l, out double r, out double g, out double b)
    {
        if (s <= 1e-6) { r = g = b = l; return; }
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
        r = HueToRgb(p, q, h + 1.0 / 3); g = HueToRgb(p, q, h); b = HueToRgb(p, q, h - 1.0 / 3);
    }

    /// <summary>Calibration's Shadows Tint (the primaries come from the measured tables with Light and Color).</summary>
    public static void ShadowTint(SKBitmap bitmap, double shadowTint, int process)
    {
        double versionScale = process <= 1 ? 0.55 : process == 2 ? 0.65 : process == 3 ? 0.75 : process == 4 ? 0.85 : process == 5 ? 0.92 : 1.0;
        double tint = shadowTint / 100 * versionScale;
        if (tint == 0) return;
        EachPixel(bitmap, (p, _, _) =>
        {
            var (r, g, b) = Straight(p);
            RgbToHsl(r, g, b, out double h, out double s, out double l);
            if (l < 0.35)
            {
                h += tint * 0.06;
                if (h < 0) h += 1;
                if (h >= 1) h -= 1;
            }
            HslToRgb(h, s, l, out r, out g, out b);
            WritePremultiplied(p, r, g, b, p[3]);
        });
    }
}
