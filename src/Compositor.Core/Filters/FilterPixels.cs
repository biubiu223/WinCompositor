using Compositor.Core.Adjustments;
using Compositor.Core.Localization;
using SkiaSharp;

namespace Compositor.Core.Filters;

/// <summary>
/// The Filter menu's effects on premultiplied RGBA, in place. Ports of adjust_colored_vignette, adjust_tonal_contrast
/// (AdjustPixels.c), lens_distort (LensPixels.c), dither_apply, dither_dots, scanlines_apply and dither_glow
/// (DitherPixels.c), content_fill (ContentFill.c), and Core Image's bloom.
/// </summary>
public static unsafe class FilterPixels
{
    /// <summary>
    /// Runs <paramref name="kind"/> over <paramref name="bitmap"/>. <paramref name="scale"/> is the bitmap's pixels per layer
    /// pixel (a preview made smaller blurs proportionally less). <paramref name="frame"/> is the area a vignette frames
    /// (in bitmap pixels) when it fills an empty layer; <paramref name="fill"/> is Content-Aware Fill's area (one byte a pixel).
    /// </summary>
    public static void Apply(FilterKind kind, FilterSettings settings, SKBitmap bitmap, double scale, SKRect? frame = null, byte[]? fill = null)
    {
        var s = settings.Normalized;
        switch (kind)
        {
            case FilterKind.Vignette:
                Vignette(bitmap, frame ?? new SKRect(0, 0, bitmap.Width, bitmap.Height), frame is not null, s);
                break;
            case FilterKind.BloomGlow:
                Bloom(bitmap, s.BloomRadius * scale, s.BloomAmount / 50);
                break;
            case FilterKind.TonalContrast:
                TonalContrast(bitmap, s.TonalRadius * scale, s);
                break;
            case FilterKind.LensCorrection:
                LensDistort(bitmap, s.Distortion / 100 * 0.35);
                break;
            case FilterKind.Dither:
                Dither(bitmap, s.Dither);
                break;
            case FilterKind.Scanlines:
                Scanlines(bitmap, s.Scanlines);
                break;
            case FilterKind.CameraRaw:
                CameraRaw.CameraRawProcessor.Apply(bitmap, s.CameraRaw, scale);
                break;
            case FilterKind.ContentAwareFill:
                if (fill is not null && !ContentFill(bitmap, fill)) throw new InvalidOperationException(L.T("周围没有足够的未选中不透明像素可用来填充。请选一个小一些、周围有图像的区域。",
                    "Not enough unselected, opaque image pixels to synthesize a fill. Use a smaller selection with some surrounding image."));
                break;
        }
        bitmap.NotifyPixelsChanged();
    }

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
    private static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;
    private static double Rec709(double r, double g, double b) => 0.2126 * r + 0.7152 * g + 0.0722 * b;

    private static void WritePremultiplied(byte* p, double r, double g, double b, double alpha)
    {
        p[0] = (byte)Math.Min(alpha, Math.Max(0.0, Math.Round(r * alpha)));
        p[1] = (byte)Math.Min(alpha, Math.Max(0.0, Math.Round(g * alpha)));
        p[2] = (byte)Math.Min(alpha, Math.Max(0.0, Math.Round(b * alpha)));
    }

    private delegate void Row(byte* row, int y);

    private static void Rows(SKBitmap bitmap, Row body)
    {
        byte* pixels = (byte*)bitmap.GetPixels();
        nint basePointer = (nint)pixels;
        int stride = bitmap.RowBytes;
        Parallel.For(0, bitmap.Height, y => body((byte*)basePointer + (long)y * stride, y));
    }

    // MARK: Vignette

    private static double VignetteMask(double px, double py, double width, double height, double midpoint, double roundness, double feather)
    {
        double nx = px / width * 2.0 - 1.0, ny = py / height * 2.0 - 1.0;
        double square = Math.Max(Math.Abs(nx), Math.Abs(ny));
        double circle = Math.Sqrt(nx * nx + ny * ny) / Math.Sqrt(2.0);
        double shape = (1.0 - roundness / 100.0) * 0.5;
        double dist = circle + (square - circle) * shape;
        double start = midpoint / 100.0 * 0.85;
        double soft = Math.Max(0.05, feather / 100.0);
        double t = Clamp01((dist - start) / soft);
        return t * t * (3.0 - 2.0 * t);
    }

    private static void Vignette(SKBitmap bitmap, SKRect frame, bool fillsClear, FilterSettings s)
    {
        if (s.VignetteAmount <= 0 || frame.Width <= 0 || frame.Height <= 0) return;
        double strength = Clamp01(s.VignetteAmount / 100.0);
        double red = s.VignetteColor.Red, green = s.VignetteColor.Green, blue = s.VignetteColor.Blue;
        int w = bitmap.Width;
        Rows(bitmap, (row, y) =>
        {
            for (int x = 0; x < w; x++)
            {
                byte* p = row + x * 4;
                if (p[3] == 0 && !fillsClear) continue;
                double mask = VignetteMask(x + 0.5 - frame.Left, y + 0.5 - frame.Top, frame.Width, frame.Height,
                                           s.VignetteMidpoint, s.VignetteRoundness, s.VignetteFeather);
                if (mask <= 0) continue;
                double alpha = p[3] / 255.0;
                double r = 0, g = 0, b = 0, bright = 0;
                if (p[3] != 0)
                {
                    r = Math.Min(1.0, p[0] / (double)p[3]);
                    g = Math.Min(1.0, p[1] / (double)p[3]);
                    b = Math.Min(1.0, p[2] / (double)p[3]);
                    bright = Clamp01((Rec709(r, g, b) - 0.45) / 0.55);
                }
                double effect = strength * mask * (1.0 - s.VignetteHighlights / 100.0 * bright);
                if (!fillsClear)
                {
                    // Only the pixels that are there change color; their coverage stays as it was.
                    WritePremultiplied(p, r + (red - r) * effect, g + (green - g) * effect, b + (blue - b) * effect, p[3]);
                    continue;
                }
                // The color painted over the pixel at `effect`: an opaque pixel moves toward it, a clear one takes it on.
                double output = alpha + effect * (1.0 - alpha);
                if (output <= 0) continue;
                r = (red * effect + r * alpha * (1.0 - effect)) / output;
                g = (green * effect + g * alpha * (1.0 - effect)) / output;
                b = (blue * effect + b * alpha * (1.0 - effect)) / output;
                p[3] = (byte)Math.Min(255.0, Math.Round(output * 255.0));
                WritePremultiplied(p, r, g, b, p[3]);
            }
        });
    }

    // MARK: Blur helpers

    /// <summary>A Gaussian blur of the bitmap (σ in its pixels) as a new bitmap, spreading past its edges.</summary>
    private static SKBitmap Blurred(SKBitmap bitmap, double sigma, SKShaderTileMode tile = SKShaderTileMode.Decal)
    {
        var result = new SKBitmap(bitmap.Info);
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.Transparent);
        using var image = SKImage.FromPixelCopy(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes);
        if (sigma > 0.05)
        {
            using var filter = SKImageFilter.CreateBlur((float)sigma, (float)sigma, tile);
            using var paint = new SKPaint { ImageFilter = filter };
            canvas.DrawImage(image, 0, 0, paint);
        }
        else canvas.DrawImage(image, 0, 0);
        canvas.Flush();
        return result;
    }

    // MARK: Bloom / Glow

    /// <summary>Core Image's bloom: the picture softened and added back over itself, so light areas glow past their edges.</summary>
    private static void Bloom(SKBitmap bitmap, double radius, double intensity)
    {
        if (intensity <= 0) return;
        using var soft = Blurred(bitmap, radius);
        byte* glow = (byte*)soft.GetPixels();
        nint glowBase = (nint)glow;
        int strideGlow = soft.RowBytes, w = bitmap.Width;
        float k = (float)intensity;
        Rows(bitmap, (row, y) =>
        {
            byte* light = (byte*)glowBase + (long)y * strideGlow;
            for (int x = 0; x < w * 4; x += 4)
            {
                float a = Math.Min(255, row[x + 3] + light[x + 3] * k);
                row[x + 3] = (byte)MathF.Round(a);
                for (int c = 0; c < 3; c++) row[x + c] = (byte)MathF.Min(row[x + 3], MathF.Round(row[x + c] + light[x + c] * k));
            }
        });
    }

    // MARK: Tonal Contrast

    private static double TonalSmooth(double low, double high, double value)
    {
        double t = Clamp01((value - low) / (high - low));
        return t * t * (3.0 - 2.0 * t);
    }

    private static void TonalContrast(SKBitmap bitmap, double radius, FilterSettings s)
    {
        if (s.TonalAmount <= 0 || (s.TonalShadows == 0 && s.TonalMidtones == 0 && s.TonalHighlights == 0)) return;
        using var soft = Blurred(bitmap, radius, SKShaderTileMode.Clamp);
        byte* blurred = (byte*)soft.GetPixels();
        nint blurredBase = (nint)blurred;
        int strideBlurred = soft.RowBytes, w = bitmap.Width;
        double strength = s.TonalAmount / 50.0;
        Rows(bitmap, (row, y) =>
        {
            byte* baseRow = (byte*)blurredBase + (long)y * strideBlurred;
            for (int x = 0; x < w; x++)
            {
                byte* p = row + x * 4;
                byte* b = baseRow + x * 4;
                double alpha = p[3];
                if (alpha == 0 || b[3] == 0) continue;
                double r = Math.Min(1.0, p[0] / alpha), g = Math.Min(1.0, p[1] / alpha), bl = Math.Min(1.0, p[2] / alpha);
                double lum = Rec709(r, g, bl);
                double baseLum = Rec709(Math.Min(1.0, b[0] / (double)b[3]), Math.Min(1.0, b[1] / (double)b[3]), Math.Min(1.0, b[2] / (double)b[3]));
                double shadowWeight = 1.0 - TonalSmooth(0.15, 0.5, baseLum);
                double highlightWeight = TonalSmooth(0.5, 0.85, baseLum);
                double midtoneWeight = 1.0 - shadowWeight - highlightWeight;
                double weight = (s.TonalShadows * shadowWeight + s.TonalMidtones * midtoneWeight + s.TonalHighlights * highlightWeight) / 100.0;
                double detail = lum - baseLum;
                double delta = 0.18 * Math.Tanh(detail * 6.0) * weight * strength * (4.0 * lum * (1.0 - lum));
                WritePremultiplied(p, Clamp01(r + delta), Clamp01(g + delta), Clamp01(bl + delta), alpha);
            }
        });
    }

    // MARK: Lens Correction

    /// <summary>Radial correction relative to the image's own size, so a smaller preview bends the same way.</summary>
    /// <summary>Radial lens distortion (Lens Correction's, and Camera Raw's Optics), in place.</summary>
    internal static void LensDistortion(SKBitmap bitmap, double k) => LensDistort(bitmap, k);

    private static void LensDistort(SKBitmap bitmap, double k)
    {
        if (k == 0) return;
        int width = bitmap.Width, height = bitmap.Height, stride = bitmap.RowBytes;
        var source = new byte[stride * height];
        fixed (byte* src = source)
        {
            Buffer.MemoryCopy((void*)bitmap.GetPixels(), src, source.Length, source.Length);
            nint srcBase = (nint)src;
            double cx = width * 0.5, cy = height * 0.5, halfDiagonal2 = cx * cx + cy * cy;
            Rows(bitmap, (output, y) =>
            {
                byte* from = (byte*)srcBase;
                double dy = y + 0.5 - cy;
                Span<double> sums = stackalloc double[4];
                for (int x = 0; x < width; x++)
                {
                    double dx = x + 0.5 - cx;
                    double scale = 1.0 - k * (dx * dx + dy * dy) / halfDiagonal2;
                    double sx = cx + dx * scale - 0.5, sy = cy + dy * scale - 0.5;
                    double fx0 = Math.Floor(sx), fy0 = Math.Floor(sy), fx = sx - fx0, fy = sy - fy0;
                    long x0 = (long)fx0, y0 = (long)fy0;
                    sums.Clear();
                    for (int j = 0; j < 2; j++)
                    {
                        long r = y0 + j;
                        if (r < 0 || r >= height) continue;
                        double wy = j == 1 ? fy : 1 - fy;
                        if (wy == 0) continue;
                        byte* line = from + r * stride;
                        for (int i = 0; i < 2; i++)
                        {
                            long column = x0 + i;
                            if (column < 0 || column >= width) continue;
                            double weight = wy * (i == 1 ? fx : 1 - fx);
                            if (weight == 0) continue;
                            byte* p = line + column * 4;
                            for (int c = 0; c < 4; c++) sums[c] += weight * p[c];
                        }
                    }
                    for (int c = 0; c < 4; c++) output[x * 4 + c] = (byte)Math.Round(sums[c]);
                }
            });
        }
    }

    // MARK: Dither

    private static float AdjustTone(float v, float gamma, float contrast)
    {
        v = MathF.Pow(Clamp01(v), gamma);
        return Clamp01((v - 0.5f) * contrast + 0.5f);
    }

    private static readonly (int Dx, int Dy, int Weight)[] AtkinsonTaps = { (1, 0, 1), (2, 0, 1), (-1, 1, 1), (0, 1, 1), (1, 1, 1), (0, 2, 1) };
    private static readonly (int Dx, int Dy, int Weight)[] FloydTaps = { (1, 0, 7), (-1, 1, 3), (0, 1, 5), (1, 1, 1) };

    private static float Quantize(float v, int levels)
    {
        float steps = levels - 1;
        return MathF.Round(Clamp01(v) * steps) / steps;
    }

    /// <summary>Diffuses one plane in serpentine order, so the error's drift doesn't streak to one side.</summary>
    private static void Diffuse(float[] plane, int offset, byte[] alpha, int width, int height, DitherStyle style, int levels, float diffusion)
    {
        var taps = style == DitherStyle.Atkinson ? AtkinsonTaps : FloydTaps;
        float divisor = style == DitherStyle.Atkinson ? 8 : 16;
        for (int y = 0; y < height; y++)
        {
            bool reverse = (y & 1) == 1;
            for (int i = 0; i < width; i++)
            {
                int x = reverse ? width - 1 - i : i;
                int at = y * width + x;
                if (alpha[at] == 0) continue;
                float old = plane[offset + at], q = Quantize(old, levels);
                plane[offset + at] = q;
                float error = (old - q) * diffusion / divisor;
                foreach (var (dx, dy, weight) in taps)
                {
                    int nx = x + (reverse ? -dx : dx), ny = y + dy;
                    if (nx < 0 || nx >= width || ny >= height) continue;
                    plane[offset + ny * width + nx] += error * weight;
                }
            }
        }
    }

    private static readonly byte[] Bayer8Matrix =
    {
        0, 32, 8, 40, 2, 34, 10, 42, 48, 16, 56, 24, 50, 18, 58, 26,
        12, 44, 4, 36, 14, 46, 6, 38, 60, 28, 52, 20, 62, 30, 54, 22,
        3, 35, 11, 43, 1, 33, 9, 41, 51, 19, 59, 27, 49, 17, 57, 25,
        15, 47, 7, 39, 13, 45, 5, 37, 63, 31, 55, 23, 61, 29, 53, 21,
    };
    private static readonly byte[] Bayer2Matrix = { 0, 2, 3, 1 };
    private static readonly byte[] Bayer4Matrix = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };

    private static float OrderedThreshold(DitherStyle style, int x, int y) => style switch
    {
        DitherStyle.Bayer2 => (Bayer2Matrix[(y & 1) * 2 + (x & 1)] + 0.5f) / 4,
        DitherStyle.Bayer4 => (Bayer4Matrix[(y & 3) * 4 + (x & 3)] + 0.5f) / 16,
        _ => (Bayer8Matrix[(y & 7) * 8 + (x & 7)] + 0.5f) / 64,
    };

    private static float Ordered(float v, float threshold, int levels)
    {
        float steps = levels - 1;
        float q = MathF.Floor(Clamp01(v) * steps + threshold);
        return (q > steps ? steps : q) / steps;
    }

    private static float Spot(DitherStyle style, float u, float v) => style switch
    {
        DitherStyle.Dots => MathF.PI * (u * u + v * v),
        DitherStyle.Lines => MathF.Abs(v) * 2,
        _ => MathF.Abs(u) + MathF.Abs(v),
    };

    /// <summary>Old Mac fill patterns, 8 × 8, one byte per row with the leftmost pixel in the top bit, sparsest first.</summary>
    private static readonly byte[][] Patterns =
    {
        new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 },
        new byte[] { 0x80, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00 },
        new byte[] { 0x88, 0x00, 0x22, 0x00, 0x88, 0x00, 0x22, 0x00 },
        new byte[] { 0x80, 0x40, 0x20, 0x10, 0x08, 0x04, 0x02, 0x01 },
        new byte[] { 0x88, 0x22, 0x88, 0x22, 0x88, 0x22, 0x88, 0x22 },
        new byte[] { 0x00, 0xFF, 0x00, 0x00, 0x00, 0xFF, 0x00, 0x00 },
        new byte[] { 0x11, 0x22, 0x44, 0x88, 0x11, 0x22, 0x44, 0x88 },
        new byte[] { 0xAA, 0x00, 0xAA, 0x00, 0xAA, 0x00, 0xAA, 0x00 },
        new byte[] { 0x88, 0x55, 0x22, 0x55, 0x88, 0x55, 0x22, 0x55 },
        new byte[] { 0xFF, 0x80, 0x80, 0x80, 0xFF, 0x08, 0x08, 0x08 },
        new byte[] { 0xAA, 0x55, 0xAA, 0x55, 0xAA, 0x55, 0xAA, 0x55 },
        new byte[] { 0x81, 0x42, 0x24, 0x18, 0x18, 0x24, 0x42, 0x81 },
        new byte[] { 0x77, 0xAA, 0xDD, 0xAA, 0x77, 0xAA, 0xDD, 0xAA },
        new byte[] { 0xEE, 0xDD, 0xBB, 0x77, 0xEE, 0xDD, 0xBB, 0x77 },
        new byte[] { 0x77, 0xFF, 0xDD, 0xFF, 0x77, 0xFF, 0xDD, 0xFF },
        new byte[] { 0x7F, 0xFF, 0xFF, 0xFF, 0xF7, 0xFF, 0xFF, 0xFF },
        new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF },
    };

    private static void WritePixel(byte* px, float r, float g, float b)
    {
        float a = px[3] / 255.0f;
        px[0] = (byte)MathF.Round(Clamp01(r) * a * 255.0f);
        px[1] = (byte)MathF.Round(Clamp01(g) * a * 255.0f);
        px[2] = (byte)MathF.Round(Clamp01(b) * a * 255.0f);
    }

    private static (float R, float G, float B) Rgb(AdjustmentColor c) => ((float)c.Red, (float)c.Green, (float)c.Blue);

    /// <summary>Chunky pixels: dither a copy averaged down by the pixel size, then blow it back up without smoothing.</summary>
    private static void Dither(SKBitmap bitmap, DitherSettings s)
    {
        int block = s.Style == DitherStyle.Ascii ? 1 : (int)s.PixelSize;
        if (block <= 1) { DitherInPlace(bitmap, s); return; }
        int w = (bitmap.Width + block - 1) / block, h = (bitmap.Height + block - 1) / block;
        using var small = new SKBitmap(bitmap.Info.WithSize(w, h));
        using (var canvas = new SKCanvas(small))
        using (var image = SKImage.FromPixelCopy(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes))
        using (var paint = new SKPaint { BlendMode = SKBlendMode.Src })
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawImage(image, new SKRect(0, 0, bitmap.Width / (float)block, bitmap.Height / (float)block),
                             new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
        }
        DitherInPlace(small, s);
        using (var canvas = new SKCanvas(bitmap))
        using (var image = SKImage.FromPixelCopy(small.Info, small.GetPixels(), small.RowBytes))
        using (var paint = new SKPaint { BlendMode = SKBlendMode.Src })
        {
            canvas.DrawImage(image, new SKRect(0, 0, w * block, h * block), new SKSamplingOptions(SKFilterMode.Nearest), paint);
        }
        if (s.PixelShape == DitherPixelShape.Dot)
        {
            // The gaps are the dark color: black, or the one picked.
            var gap = s.Colors == DitherColors.TwoColors ? Rgb(s.Dark) : (0f, 0f, 0f);
            Dots(bitmap, block, (byte)Math.Round(gap.Item1 * 255), (byte)Math.Round(gap.Item2 * 255), (byte)Math.Round(gap.Item3 * 255));
        }
    }

    private static void Dots(SKBitmap bitmap, int block, byte gapR, byte gapG, byte gapB)
    {
        float radius = block * 0.42f, middle = block / 2f;
        int w = bitmap.Width;
        Rows(bitmap, (row, y) =>
        {
            float dy = y % block + 0.5f - middle;
            byte* gap = stackalloc byte[] { gapR, gapG, gapB };
            for (int x = 0; x < w; x++)
            {
                byte* px = row + x * 4;
                if (px[3] == 0) continue;
                float dx = x % block + 0.5f - middle;
                float cover = Clamp01(radius - MathF.Sqrt(dx * dx + dy * dy) + 0.5f);
                if (cover >= 1) continue;
                for (int c = 0; c < 3; c++) px[c] = (byte)MathF.Round(px[c] * cover + gap[c] * px[3] / 255.0f * (1 - cover));
            }
        });
    }

    /// <summary>Each distinct character drawn into a monospaced cell, sorted from least ink to most.</summary>
    private static (byte[] Maps, float[] Coverage, int Width, int Height) Glyphs(string characters, int lineHeight)
    {
        using var typeface = SKFontManager.Default.MatchFamily("Consolas", SKFontStyle.Bold) ?? SKTypeface.Default;
        using var font = new SKFont(typeface, lineHeight / 1.2f) { Edging = SKFontEdging.Antialias };
        int height = lineHeight;
        int width = Math.Max(1, (int)Math.Round(font.MeasureText("M")));
        var metrics = font.Metrics;
        float baseline = MathF.Round((height - (metrics.Descent - metrics.Ascent)) / 2 - metrics.Ascent);
        var drawn = new List<(byte[] Map, float Coverage)>();
        var seen = new HashSet<string>();
        var info = new SKImageInfo(width, height, SKColorType.Alpha8, SKAlphaType.Premul);
        foreach (var rune in characters.EnumerateRunes())
        {
            var text = rune.ToString();
            if (!seen.Add(text)) continue;
            var map = new byte[width * height];
            fixed (byte* p = map)
            {
                using var surface = SKSurface.Create(info, (IntPtr)p, width);
                surface.Canvas.Clear(SKColors.Transparent);
                using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
                float advance = font.MeasureText(text);
                surface.Canvas.DrawText(text, MathF.Round((width - advance) / 2), baseline, SKTextAlign.Left, font, paint);
                surface.Canvas.Flush();
            }
            drawn.Add((map, map.Sum(b => (float)b) / (255f * width * height)));
        }
        drawn.Sort((a, b) => a.Coverage.CompareTo(b.Coverage));
        return (drawn.SelectMany(d => d.Map).ToArray(), drawn.Select(d => d.Coverage).ToArray(), width, height);
    }

    private static void DitherInPlace(SKBitmap bitmap, DitherSettings s)
    {
        int width = bitmap.Width, height = bitmap.Height, count = width * height, stride = bitmap.RowBytes;
        if (count == 0) return;
        bool original = s.Colors == DitherColors.Original;
        int planes = original ? 3 : 1;
        var tone = new float[count * planes];
        var alpha = new byte[count];
        var source = original ? new float[count * 3] : null;
        float gamma = MathF.Pow(2, (float)(s.Density / 100) * 1.5f);
        float contrastAmount = (float)(s.Contrast / 100);
        float contrast = contrastAmount >= 0 ? 1.0f / (1.0f - 0.95f * contrastAmount) : 1.0f + contrastAmount;
        Rows(bitmap, (row, y) =>
        {
            for (int x = 0; x < width; x++)
            {
                byte* px = row + x * 4;
                int at = y * width + x;
                alpha[at] = px[3];
                float r = 0, g = 0, b = 0;
                if (px[3] != 0) { float scale = 1.0f / px[3]; r = px[0] * scale; g = px[1] * scale; b = px[2] * scale; }
                if (original)
                {
                    tone[at] = AdjustTone(r, gamma, contrast);
                    tone[count + at] = AdjustTone(g, gamma, contrast);
                    tone[2 * count + at] = AdjustTone(b, gamma, contrast);
                    source![at * 3] = r; source[at * 3 + 1] = g; source[at * 3 + 2] = b;
                }
                else tone[at] = AdjustTone(0.2126f * r + 0.7152f * g + 0.0722f * b, gamma, contrast);
            }
        });

        var (darkR, darkG, darkB) = s.Colors == DitherColors.TwoColors ? Rgb(s.Dark) : (0f, 0f, 0f);
        var (lightR, lightG, lightB) = s.Colors == DitherColors.TwoColors ? Rgb(s.Light) : (1f, 1f, 1f);
        var style = s.Style;
        int levels = Math.Clamp((int)s.Levels, 2, 16);
        byte* pixels = (byte*)bitmap.GetPixels();

        if (style.HasTones())
        {
            // Diffusion and ordered dithering: each plane quantized to `levels` tones, then mapped to colors.
            if (style.Diffuses())
                for (int c = 0; c < planes; c++) Diffuse(tone, c * count, alpha, width, height, style, levels, (float)(s.Diffusion / 100));
            else
                for (int c = 0; c < planes; c++)
                    for (int y = 0; y < height; y++)
                        for (int x = 0; x < width; x++)
                        {
                            int at = y * width + x;
                            if (alpha[at] != 0) tone[c * count + at] = Ordered(tone[c * count + at], OrderedThreshold(style, x, y), levels);
                        }
            for (int y = 0; y < height; y++)
            {
                byte* row = pixels + (long)y * stride;
                for (int x = 0; x < width; x++)
                {
                    int at = y * width + x;
                    if (alpha[at] == 0) continue;
                    if (original) WritePixel(row + x * 4, tone[at], tone[count + at], tone[2 * count + at]);
                    else
                    {
                        float t = tone[at];
                        WritePixel(row + x * 4, darkR + (lightR - darkR) * t, darkG + (lightG - darkG) * t, darkB + (lightB - darkB) * t);
                    }
                }
            }
            return;
        }

        // Marks (halftone shapes, patterns, glyphs) cover as much of each spot as the tone calls for. On light they
        // stand for darkness, in the dark color; light on dark, the reverse.
        var marks = original ? new float[count] : tone;
        if (original) for (int i = 0; i < count; i++) marks[i] = 0.2126f * tone[i] + 0.7152f * tone[count + i] + 0.0722f * tone[2 * count + i];
        int cell = Math.Max(2, (int)s.CellSize);
        float cosA = MathF.Cos((float)(s.Angle * Math.PI / 180)), sinA = MathF.Sin((float)(s.Angle * Math.PI / 180));
        bool lightOnDark = s.LightOnDark;
        var (inkR, inkG, inkB) = lightOnDark ? (lightR, lightG, lightB) : (darkR, darkG, darkB);
        var (paperR, paperG, paperB) = lightOnDark ? (darkR, darkG, darkB) : (lightR, lightG, lightB);
        int[]? picked = null;
        int gw = 1, gh = 1, columns = 1;
        byte[]? glyphMaps = null;
        if (style == DitherStyle.Ascii)
        {
            var (maps, coverage, glyphWidth, glyphHeight) = Glyphs(s.Characters.Length == 0 ? DitherSettings.DefaultCharacters : s.Characters, (int)s.TextSize);
            glyphMaps = maps;
            gw = glyphWidth; gh = glyphHeight;
            columns = (width + gw - 1) / gw;
            int cellRows = (height + gh - 1) / gh;
            if (coverage.Length > 0)
            {
                picked = new int[columns * cellRows];
                for (int row = 0; row < cellRows; row++)
                    for (int column = 0; column < columns; column++)
                    {
                        float sum = 0;
                        int n = 0;
                        for (int yy = row * gh; yy < (row + 1) * gh && yy < height; yy++)
                            for (int xx = column * gw; xx < (column + 1) * gw && xx < width; xx++)
                            {
                                int i = yy * width + xx;
                                if (alpha[i] != 0) { sum += marks[i]; n++; }
                            }
                        float t = n > 0 ? sum / n : 1;
                        float wanted = (lightOnDark ? t : 1 - t) * coverage[^1];
                        int best = 0;
                        float bestDistance = 2;
                        for (int g = 0; g < coverage.Length; g++)
                        {
                            float d = MathF.Abs(coverage[g] - wanted);
                            if (d < bestDistance) { bestDistance = d; best = g; }
                        }
                        picked[row * columns + column] = best;
                    }
            }
        }
        float paperOriginal = lightOnDark ? 0f : 1f;
        for (int y = 0; y < height; y++)
        {
            byte* row = pixels + (long)y * stride;
            for (int x = 0; x < width; x++)
            {
                int at = y * width + x;
                if (alpha[at] == 0) continue;
                float amount;
                if (picked is not null)
                {
                    int glyph = picked[(y / gh) * columns + x / gw];
                    amount = glyphMaps![glyph * gw * gh + (y % gh) * gw + x % gw] / 255.0f;
                }
                else if (style == DitherStyle.Patterns)
                {
                    float t = marks[at];
                    float cover = lightOnDark ? t : 1 - t;
                    int index = (int)MathF.Round(cover * (Patterns.Length - 1));
                    amount = (Patterns[index][y & 7] >> (7 - (x & 7))) & 1;
                }
                else if (style == DitherStyle.Ascii) amount = 0;
                else
                {
                    float fx = x + 0.5f, fy = y + 0.5f;
                    float u = (fx * cosA + fy * sinA) / cell, v = (-fx * sinA + fy * cosA) / cell;
                    u -= MathF.Floor(u) + 0.5f;
                    v -= MathF.Floor(v) + 0.5f;
                    float t = marks[at];
                    amount = (lightOnDark ? t : 1 - t) > Spot(style, u, v) ? 1 : 0;
                }
                if (original)
                {
                    float sr = source![at * 3], sg = source[at * 3 + 1], sb = source[at * 3 + 2];
                    WritePixel(row + x * 4, paperOriginal + (sr - paperOriginal) * amount, paperOriginal + (sg - paperOriginal) * amount,
                               paperOriginal + (sb - paperOriginal) * amount);
                }
                else WritePixel(row + x * 4, paperR + (inkR - paperR) * amount, paperG + (inkG - paperG) * amount, paperB + (inkB - paperB) * amount);
            }
        }
    }

    // MARK: Scanlines

    private static void Scanlines(SKBitmap bitmap, ScanlinesSettings s)
    {
        int width = bitmap.Width, height = bitmap.Height, count = width * height, stride = bitmap.RowBytes;
        if (count == 0) return;
        int spacing = Math.Max(2, (int)s.LineSpacing);
        int lines = (height + spacing - 1) / spacing;
        bool original = s.Colors == DitherColors.Original;
        int planes = original ? 3 : 1;
        var tone = new float[count * planes];
        var alpha = new byte[count];
        var scan = new float[lines * width * planes];
        float gamma = MathF.Pow(2, (float)(s.Density / 100) * 1.5f);
        float contrastAmount = (float)(s.Contrast / 100);
        float contrast = contrastAmount >= 0 ? 1.0f / (1.0f - 0.95f * contrastAmount) : 1.0f + contrastAmount;
        Rows(bitmap, (row, y) =>
        {
            for (int x = 0; x < width; x++)
            {
                byte* px = row + x * 4;
                int at = y * width + x;
                alpha[at] = px[3];
                float r = 0, g = 0, b = 0;
                if (px[3] != 0) { float scale = 1.0f / px[3]; r = px[0] * scale; g = px[1] * scale; b = px[2] * scale; }
                if (original)
                {
                    tone[at] = AdjustTone(r, gamma, contrast);
                    tone[count + at] = AdjustTone(g, gamma, contrast);
                    tone[2 * count + at] = AdjustTone(b, gamma, contrast);
                }
                else tone[at] = AdjustTone(0.2126f * r + 0.7152f * g + 0.0722f * b, gamma, contrast);
            }
        });
        float wobble = (float)s.Wobble;
        Parallel.For(0, lines, line =>
        {
            int top = line * spacing, bottom = Math.Min(height, top + spacing);
            // A slow wave down the screen with a quicker one over it, as a CRT's picture wavers when its sync drifts.
            float wave = MathF.Sin(line * 0.45f) * 0.7f + MathF.Sin(line * 1.7f + 1.3f) * 0.3f;
            int shift = (int)MathF.Round(wobble * wave);
            Span<float> sum = stackalloc float[3];
            for (int x = 0; x < width; x++)
            {
                sum.Clear();
                int n = 0, sx = x - shift;
                if (sx >= 0 && sx < width)
                    for (int y = top; y < bottom; y++)
                    {
                        int at = y * width + sx;
                        if (alpha[at] == 0) continue;
                        for (int c = 0; c < planes; c++) sum[c] += tone[c * count + at];
                        n++;
                    }
                for (int c = 0; c < planes; c++) scan[(c * lines + line) * width + x] = n > 0 ? sum[c] / n : 0;
            }
        });
        DrawLines(bitmap, s, scan, alpha, lines, spacing, original);

        // Color split: red one way, blue the other, for colored fringes on the lines' edges.
        int split = (int)s.Split;
        if (split > 0)
        {
            Rows(bitmap, (row, y) =>
            {
                var copy = new byte[width * 4];
                new ReadOnlySpan<byte>(row, width * 4).CopyTo(copy);
                for (int x = 0; x < width; x++)
                {
                    int from = x - split, to = x + split;
                    byte a = row[x * 4 + 3];
                    byte r = from >= 0 ? copy[from * 4] : (byte)0, b = to < width ? copy[to * 4 + 2] : (byte)0;
                    row[x * 4] = r > a ? a : r;
                    row[x * 4 + 2] = b > a ? a : b;
                }
            });
        }
        if (s.Glow > 0) Glow(bitmap, spacing, (float)(s.Glow / 100 * 2.5));
    }

    /// <summary>
    /// The lines over the scanned tones, a column at a time: displaced, a landscape seen from the front (after the
    /// Rutt-Etra synthesizer), each line lifted by the picture's brightness and hiding what lies behind it.
    /// </summary>
    private static void DrawLines(SKBitmap bitmap, ScanlinesSettings s, float[] scan, byte[] alpha, int lines, int spacing, bool original)
    {
        int width = bitmap.Width, height = bitmap.Height, stride = bitmap.RowBytes, plane = lines * width;
        var tone = new float[plane];
        var lift = new float[plane];
        for (int at = 0; at < plane; at++)
            tone[at] = original ? 0.2126f * scan[at] + 0.7152f * scan[plane + at] + 0.0722f * scan[2 * plane + at] : scan[at];
        float smoothness = Clamp01((float)(s.Smoothness / 100));
        int radius = (int)MathF.Round(smoothness * spacing * 2);
        Array.Copy(tone, lift, plane);
        float displace = (float)s.Displace;
        if (radius > 0 && displace != 0)
        {
            // Brightness blurred along each line (three box passes) and then across its neighbors.
            Parallel.For(0, lines, line =>
            {
                var copy = new float[width];
                int rowStart = line * width;
                for (int pass = 0; pass < 3; pass++)
                {
                    Array.Copy(lift, rowStart, copy, 0, width);
                    float sum = 0;
                    for (int x = -radius; x <= radius; x++) sum += copy[Math.Clamp(x, 0, width - 1)];
                    for (int x = 0; x < width; x++)
                    {
                        lift[rowStart + x] = sum / (2 * radius + 1);
                        int outIndex = x - radius, inIndex = x + radius + 1;
                        sum += copy[Math.Min(width - 1, inIndex)] - copy[Math.Max(0, outIndex)];
                    }
                }
            });
            int across = (int)MathF.Round(smoothness * 2);
            var spread = new float[plane];
            for (int step = 0; step < across; step++)
            {
                Parallel.For(0, lines, line =>
                {
                    int above = Math.Max(0, line - 1) * width, here = line * width, below = Math.Min(lines - 1, line + 1) * width;
                    for (int x = 0; x < width; x++) spread[here + x] = (lift[above + x] + 2 * lift[here + x] + lift[below + x]) / 4;
                });
                Array.Copy(spread, lift, plane);
            }
        }

        float middle = spacing / 2f, dots = Clamp01((float)(s.Dots / 100)), threshold = Clamp01((float)(s.Threshold / 100));
        float thickness = Clamp01((float)(s.Thickness / 100)), blackLevel = Clamp01((float)(s.BlackLevel / 100));
        bool rising = displace >= 0;
        var dark = s.Colors == DitherColors.TwoColors ? Rgb(s.Dark) : (0f, 0f, 0f);
        var light = s.Colors == DitherColors.TwoColors ? Rgb(s.Light) : (1f, 1f, 1f);
        if (original) dark = (0, 0, 0);
        float[] screen = { dark.Item1, dark.Item2, dark.Item3 }, phosphor = { light.Item1, light.Item2, light.Item3 };
        byte* pixels = (byte*)bitmap.GetPixels();
        nint basePointer = (nint)pixels;
        Parallel.For(0, width, () => new float[height * 4], (x, _, cover) =>
        {
            Array.Clear(cover, 0, height);
            int colorOffset = height;
            float along = (x + 0.5f) % spacing - middle;
            float horizon = rising ? float.PositiveInfinity : float.NegativeInfinity;
            for (int step = 0; step < lines; step++)
            {
                int line = rising ? lines - 1 - step : step;
                float beading = dots > 0 ? Clamp01((dots - tone[line * width + x]) / 0.2f) : 0, acrossBead = along * beading;
                int centered = (int)MathF.Round(x - acrossBead);
                int at = Math.Clamp(centered, 0, width - 1);
                int i = line * width + at;
                float t = tone[i];
                float lit = threshold > 0 ? Clamp01((t - threshold) / 0.04f) : 1;
                if (lit <= 0) continue;
                float baseY = line * (float)spacing + middle;
                float here = baseY - displace * lift[i], before = at > 0 ? baseY - displace * lift[i - 1] : here;
                float lo = MathF.Min(here, before), hi = MathF.Max(here, before);
                float level = blackLevel + (1 - blackLevel) * t;
                float c0, c1, c2;
                if (original)
                {
                    c0 = blackLevel + (1 - blackLevel) * scan[i];
                    c1 = blackLevel + (1 - blackLevel) * scan[plane + i];
                    c2 = blackLevel + (1 - blackLevel) * scan[2 * plane + i];
                }
                else
                {
                    c0 = screen[0] + (phosphor[0] - screen[0]) * level;
                    c1 = screen[1] + (phosphor[1] - screen[1]) * level;
                    c2 = screen[2] + (phosphor[2] - screen[2]) * level;
                }
                float beam = middle * thickness * (0.29f + 0.71f * MathF.Sqrt(Clamp01(level)));
                int top = (int)MathF.Floor(rising ? lo - beam - 1 : MathF.Max(lo - beam - 1, horizon - 1));
                int bottom = (int)MathF.Ceiling(rising ? MathF.Min(hi + beam + 1, horizon + 1) : hi + beam + 1);
                top = Math.Max(0, top);
                bottom = Math.Min(height, bottom);
                for (int y = top; y < bottom; y++)
                {
                    float yy = y + 0.5f;
                    float off = yy < lo ? lo - yy : yy > hi ? yy - hi : 0;
                    float distance = MathF.Sqrt(off * off + acrossBead * acrossBead);
                    float hidden = rising ? Clamp01(horizon - yy + 0.5f) : Clamp01(yy - horizon + 0.5f);
                    float shown = Clamp01(beam - distance + 0.5f) * lit * hidden;
                    if (shown > cover[y])
                    {
                        cover[y] = shown;
                        cover[colorOffset + y * 3] = c0; cover[colorOffset + y * 3 + 1] = c1; cover[colorOffset + y * 3 + 2] = c2;
                    }
                }
                if (lit > 0.5f) horizon = rising ? MathF.Min(horizon, lo - beam) : MathF.Max(horizon, hi + beam);
            }
            for (int y = 0; y < height; y++)
            {
                if (alpha[y * width + x] == 0) continue;
                float shown = cover[y];
                WritePixel((byte*)basePointer + (long)y * stride + x * 4,
                    screen[0] + (cover[colorOffset + y * 3] * 1.35f - screen[0]) * shown,
                    screen[1] + (cover[colorOffset + y * 3 + 1] * 1.35f - screen[1]) * shown,
                    screen[2] + (cover[colorOffset + y * 3 + 2] * 1.35f - screen[2]) * shown);
            }
            return cover;
        }, _ => { });
    }

    /// <summary>The lines' light blurred across a few spacings and eased in over them, as a CRT's phosphors bloom.</summary>
    private static void Glow(SKBitmap bitmap, int spacing, float amount)
    {
        double sigma = spacing * 3 + 3;
        using var bloom = Blurred(bitmap, sigma, SKShaderTileMode.Clamp);
        byte* glow = (byte*)bloom.GetPixels();
        nint glowBase = (nint)glow;
        int glowStride = bloom.RowBytes, w = bitmap.Width;
        Rows(bitmap, (row, y) =>
        {
            byte* light = (byte*)glowBase + (long)y * glowStride;
            for (int x = 0; x < w * 4; x += 4)
            {
                float a = row[x + 3];
                for (int c = 0; c < 3; c++)
                {
                    float v = row[x + c], added = light[x + c] * amount * a / 255.0f, room = (a - v) * 0.7f;
                    if (room > 0) v += room * (1 - MathF.Exp(-added / room));
                    row[x + c] = (byte)MathF.Round(v > a ? a : v);
                }
            }
        });
    }

    // MARK: Content-Aware Fill

    private static uint NextRandom(ref uint state) => state = state * 1664525u + 1013904223u;

    private static double Match(byte* pixels, int stride, byte[] known, int w, int h, int p, int q, int radius)
    {
        int px = p % w, py = p / w, qx = q % w, qy = q / w, count = 0;
        double sum = 0;
        for (int dy = -radius; dy <= radius; dy++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                int x = px + dx, y = py + dy, sx = qx + dx, sy = qy + dy;
                if (x < 0 || y < 0 || x >= w || y >= h || sx < 0 || sy < 0 || sx >= w || sy >= h || known[y * w + x] == 0) continue;
                byte* a = pixels + y * stride + x * 4, b = pixels + sy * stride + sx * 4;
                for (int c = 0; c < 4; c++) { int d = a[c] - b[c]; sum += d * d; }
                count++;
            }
        return count > 0 ? sum / count : double.MaxValue;
    }

    /// <summary>
    /// Fills the masked pixels from the rest of the image, growing in from the edges: each pixel copies the best match
    /// found by propagating its neighbors' sources and a randomized patch search. False when there is nothing to copy from.
    /// </summary>
    public static bool ContentFill(SKBitmap bitmap, byte[] mask)
    {
        int w = bitmap.Width, h = bitmap.Height, n = w * h, stride = bitmap.RowBytes;
        byte* pixels = (byte*)bitmap.GetPixels();
        var known = new byte[n];
        var target = new byte[n];
        var valid = new byte[n];
        var queued = new byte[n];
        var donors = new int[n];
        var queue = new int[n];
        var chosen = new int[n];
        int radius = w >= 5 && h >= 5 ? 2 : 0;
        int missing = 0, donorCount = 0, head = 0, tail = 0, scan = 0;
        // Selected pixels are filled; unselected opaque ones are the image to match and copy from.
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int p = y * w + x;
                target[p] = (byte)(mask[p] != 0 ? 1 : 0);
                known[p] = (byte)(target[p] == 0 && pixels[y * stride + x * 4 + 3] == 255 ? 1 : 0);
                chosen[p] = -1;
                if (target[p] != 0) missing++;
            }
        if (missing == 0) return true;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int p = y * w + x;
                if (known[p] == 0) continue;
                bool ok = true;
                for (int dy = -radius; dy <= radius && ok; dy++)
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        int sx = x + dx, sy = y + dy;
                        if (sx < 0 || sy < 0 || sx >= w || sy >= h || known[sy * w + sx] == 0) { ok = false; break; }
                    }
                if (ok) { valid[p] = 1; donors[donorCount++] = p; }
            }
        if (donorCount == 0) return false;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int p = y * w + x;
                if (target[p] != 0 && ((x > 0 && known[p - 1] != 0) || (x + 1 < w && known[p + 1] != 0) || (y > 0 && known[p - w] != 0) || (y + 1 < h && known[p + w] != 0)))
                {
                    queue[tail++] = p;
                    queued[p] = 1;
                }
            }
        uint seed = 0x6d2b79f5;
        Span<int> neighbors = stackalloc int[4];
        for (;;)
        {
            while (head < tail)
            {
                int p = queue[head++], x = p % w, y = p / w, best = -1;
                double score = double.MaxValue;
                neighbors[0] = x > 0 ? p - 1 : -1;
                neighbors[1] = x + 1 < w ? p + 1 : -1;
                neighbors[2] = y > 0 ? p - w : -1;
                neighbors[3] = y + 1 < h ? p + w : -1;
                // Propagate coherent source offsets, then refine with a randomized patch search.
                for (int k = 0; k < 28; k++)
                {
                    int q;
                    if (k < 4)
                    {
                        int t = neighbors[k];
                        q = t >= 0 ? (chosen[t] >= 0 ? chosen[t] : t) + (p - t) : -1;
                    }
                    else q = donors[NextRandom(ref seed) % (uint)donorCount];
                    if (q < 0 || q >= n || valid[q] == 0) continue;
                    double s = Match(pixels, stride, known, w, h, p, q, radius);
                    if (best < 0 || s < score) { score = s; best = q; }
                }
                if (best < 0) best = donors[0];
                for (int r = 64; r >= 1; r /= 2)
                {
                    int qx = best % w + (int)(NextRandom(ref seed) % (uint)(2 * r + 1)) - r;
                    int qy = best / w + (int)(NextRandom(ref seed) % (uint)(2 * r + 1)) - r;
                    if (qx < 0 || qy < 0 || qx >= w || qy >= h || valid[qy * w + qx] == 0) continue;
                    int q = qy * w + qx;
                    double s = Match(pixels, stride, known, w, h, p, q, radius);
                    if (s < score) { score = s; best = q; }
                }
                Buffer.MemoryCopy(pixels + (best / w) * stride + (best % w) * 4, pixels + y * stride + x * 4, 4, 4);
                known[p] = 1;
                chosen[p] = best;
                for (int k = 0; k < 4; k++)
                {
                    int q = neighbors[k];
                    if (q >= 0 && target[q] != 0 && known[q] == 0 && queued[q] == 0) { queued[q] = 1; queue[tail++] = q; }
                }
            }
            // A selected area that only transparency touches starts from a donor, then spreads.
            while (scan < n && (target[scan] == 0 || known[scan] != 0)) scan++;
            if (scan >= n) break;
            queue[tail++] = scan;
            queued[scan] = 1;
        }
        return true;
    }
}
