using Compositor.Core.Model;
using Compositor.Core.Rendering;
using SkiaSharp;

namespace Compositor.Core.Adjustments;

/// <summary>Where a bitmap's pixels sit in the document, so patterns (grain, noise) stay fixed as the view changes.</summary>
public readonly record struct AdjustmentPlacement(double Scale, double OriginX, double OriginY, long NoiseOriginX, long NoiseOriginY)
{
    /// <summary>A layer's own pixels at one per document unit.</summary>
    public static readonly AdjustmentPlacement Identity = new(1, 0, 0, 0, 0);
}

/// <summary>
/// Color and pixel adjustments on premultiplied RGBA. Ports of LevelsPixels.c, AdjustPixels.c and NoisePixels.c,
/// split across cores by rows.
/// </summary>
public static unsafe class AdjustmentPixels
{
    private delegate void RowAction(byte* row, int width);

    private static void Rows(SKBitmap bitmap, RowAction action)
    {
        byte* pixels = (byte*)bitmap.GetPixels();
        int width = bitmap.Width, height = bitmap.Height, stride = bitmap.RowBytes;
        nint basePointer = (nint)pixels;
        if ((long)width * height < 60_000)
        {
            for (int y = 0; y < height; y++) action(pixels + (long)y * stride, width);
        }
        else
        {
            Parallel.For(0, height, y => action((byte*)basePointer + (long)y * stride, width));
        }
        bitmap.NotifyPixelsChanged();
    }

    private delegate void IndexedRowAction(byte* row, int width, int y);

    private static void RowsIndexed(SKBitmap bitmap, IndexedRowAction action)
    {
        byte* pixels = (byte*)bitmap.GetPixels();
        int width = bitmap.Width, height = bitmap.Height, stride = bitmap.RowBytes;
        nint basePointer = (nint)pixels;
        Parallel.For(0, height, y => action((byte*)basePointer + (long)y * stride, width, y));
        bitmap.NotifyPixelsChanged();
    }

    /// <summary>Per-channel lookup (3 × 256 entries, 0–1) on unpremultiplied colors, interpolating between entries. levels_apply.</summary>
    public static void Tables(SKBitmap bitmap, float[] tables)
    {
        fixed (float* t = tables)
        {
            nint tp = (nint)t;
            Rows(bitmap, (row, width) =>
            {
                float* table = (float*)tp;
                for (int x = 0; x < width; x++)
                {
                    byte* p = row + x * 4;
                    float alpha = p[3];
                    if (alpha == 0) continue;
                    for (int c = 0; c < 3; c++)
                    {
                        float v = MathF.Min(255, p[c] * 255f / alpha);
                        int lo = (int)v, hi = lo < 255 ? lo + 1 : 255;
                        float* channel = table + c * 256;
                        float result = channel[lo] + (channel[hi] - channel[lo]) * (v - lo);
                        p[c] = (byte)MathF.Min(alpha, MathF.Max(0, MathF.Round(result * alpha)));
                    }
                }
            });
        }
    }

    /// <summary>Luminance picks a color from a 256 × 3 table. adjust_gradient_map.</summary>
    public static void GradientMap(SKBitmap bitmap, byte[] table)
    {
        Rows(bitmap, (row, width) =>
        {
            for (int x = 0; x < width; x++)
            {
                byte* p = row + x * 4;
                uint a = p[3];
                if (a == 0) continue;
                uint r = p[0], g = p[1], b = p[2];
                if (a < 255)
                {
                    r = Math.Min(255u, (r * 255u + a / 2) / a);
                    g = Math.Min(255u, (g * 255u + a / 2) / a);
                    b = Math.Min(255u, (b * 255u + a / 2) / a);
                }
                uint level = Math.Min(255u, (2126u * r + 7152u * g + 722u * b + 5000u) / 10000u);
                int i = (int)level * 3;
                p[0] = (byte)((table[i] * a + 127u) / 255u);
                p[1] = (byte)((table[i + 1] * a + 127u) / 255u);
                p[2] = (byte)((table[i + 2] * a + 127u) / 255u);
            }
        });
    }

    private static uint Mix32(uint x)
    {
        x ^= x >> 16; x *= 0x7feb352dU;
        x ^= x >> 15; x *= 0x846ca68bU;
        x ^= x >> 16;
        return x;
    }

    private static float Lattice(long ix, long iy, uint seed)
    {
        uint h = Mix32(unchecked((uint)ix * 0x9E3779B1U) ^ Mix32(unchecked((uint)iy * 0x85EBCA77U) ^ seed));
        return (h & 0xFFFFU) / 65535.0f + (h >> 16) / 65535.0f - 1.0f;
    }

    private static float GrainField(double u, double v, double scale, uint seed)
    {
        double cellX = Math.Floor(u / scale), cellY = Math.Floor(v / scale);
        float tx = (float)(u / scale - cellX), ty = (float)(v / scale - cellY);
        tx = tx * tx * (3f - 2f * tx);
        ty = ty * ty * (3f - 2f * ty);
        long ix = (long)cellX, iy = (long)cellY;
        float n00 = Lattice(ix, iy, seed), n10 = Lattice(ix + 1, iy, seed);
        float n01 = Lattice(ix, iy + 1, seed), n11 = Lattice(ix + 1, iy + 1, seed);
        float top = n00 + (n10 - n00) * tx, bottom = n01 + (n11 - n01) * tx;
        return (top + (bottom - top) * ty) * 1.6f;
    }

    /// <summary>Film grain, fixed in document space by position and seed. adjust_grain.</summary>
    public static void Grain(SKBitmap bitmap, GrainSettings settings, uint seed, double originX, double originY, double unitsPerPixel)
    {
        if (!(settings.Amount > 0) || !(unitsPerPixel > 0)) return;
        double size = settings.Size > 0 ? settings.Size : 1;
        float strength = (float)Math.Min(1, settings.Amount / 100) * 0.35f * 255f;
        float rough = (float)Math.Clamp(settings.Roughness / 100, 0, 1);
        uint fineSeed = Mix32(seed ^ 0xA511E9B3U);
        double detailSize = Math.Max(0.5, size * 0.35);
        RowsIndexed(bitmap, (row, width, y) =>
        {
            double v = originY + (y + 0.5) * unitsPerPixel;
            for (int x = 0; x < width; x++)
            {
                byte* p = row + x * 4;
                uint a = p[3];
                if (a == 0) continue;
                double u = originX + (x + 0.5) * unitsPerPixel;
                float smooth = GrainField(u, v, size, seed);
                float fine = GrainField(u, v, detailSize, fineSeed);
                float noise = smooth + (fine - smooth) * rough;
                float unpremultiply = a == 255 ? 1f : 255f / a;
                float r = p[0] * unpremultiply, g = p[1] * unpremultiply, b = p[2] * unpremultiply;
                float level = MathF.Min(1, (0.2126f * r + 0.7152f * g + 0.0722f * b) / 255f);
                float delta = noise * strength * (0.4f + 2.4f * level * (1f - level));
                float coverage = a / 255f;
                p[0] = (byte)(Clamp255(r + delta) * coverage + 0.5f);
                p[1] = (byte)(Clamp255(g + delta) * coverage + 0.5f);
                p[2] = (byte)(Clamp255(b + delta) * coverage + 0.5f);
            }
        });
    }

    private static float Clamp255(float v) => v < 0 ? 0 : v > 255 ? 255 : v;

    /// <summary>Black & White with six family weights and an optional tint. adjust_black_white.</summary>
    public static void BlackWhite(SKBitmap bitmap, BlackWhiteSettings settings)
    {
        var weights = settings.Weights;
        bool tint = settings.Tint;
        double tintHue = settings.TintHue, tintSaturation = settings.TintSaturation / 100;
        Rows(bitmap, (row, width) =>
        {
            for (int x = 0; x < width; x++)
            {
                byte* p = row + x * 4;
                float alpha = p[3];
                if (alpha == 0) continue;
                float r = MathF.Min(255f, p[0] * 255f / alpha) / 255f, g = MathF.Min(255f, p[1] * 255f / alpha) / 255f, b = MathF.Min(255f, p[2] * 255f / alpha) / 255f;
                float mx = MathF.Max(r, MathF.Max(g, b)), mn = MathF.Min(r, MathF.Min(g, b));
                float md = r + g + b - mx - mn;
                int primary, secondary;
                if (mx == r) { primary = 0; secondary = g >= b ? 1 : 5; }
                else if (mx == g) { primary = 2; secondary = r >= b ? 1 : 3; }
                else { primary = 4; secondary = g >= r ? 3 : 5; }
                float gray = Math.Clamp(mn + (md - mn) * weights[secondary] + (mx - md) * weights[primary], 0f, 1f);
                float outR = gray, outG = gray, outB = gray;
                if (tint && tintSaturation > 0)
                {
                    double c = (1.0 - Math.Abs(2.0 * gray - 1.0)) * tintSaturation;
                    double hp = (tintHue % 360.0) / 60.0;
                    double xx = c * (1.0 - Math.Abs(hp % 2.0 - 1.0));
                    double r1 = 0, g1 = 0, b1 = 0;
                    if (hp < 1) { r1 = c; g1 = xx; }
                    else if (hp < 2) { r1 = xx; g1 = c; }
                    else if (hp < 3) { g1 = c; b1 = xx; }
                    else if (hp < 4) { g1 = xx; b1 = c; }
                    else if (hp < 5) { r1 = xx; b1 = c; }
                    else { r1 = c; b1 = xx; }
                    double m = gray - c / 2.0;
                    outR = (float)Math.Clamp(r1 + m, 0, 1);
                    outG = (float)Math.Clamp(g1 + m, 0, 1);
                    outB = (float)Math.Clamp(b1 + m, 0, 1);
                }
                p[0] = (byte)MathF.Min(alpha, MathF.Max(0, MathF.Round(outR * alpha)));
                p[1] = (byte)MathF.Min(alpha, MathF.Max(0, MathF.Round(outG * alpha)));
                p[2] = (byte)MathF.Min(alpha, MathF.Max(0, MathF.Round(outB * alpha)));
            }
        });
    }

    private static void TonalWeights(float v, out float shadow, out float mid, out float highlight)
    {
        const float a = 0.25f, b = 0.333f, scale = 0.7f;
        float s = Math.Clamp((v - b) / -a + 0.5f, 0f, 1f);
        float h = Math.Clamp((v + b - 1f) / a + 0.5f, 0f, 1f);
        float m1 = Math.Clamp((v - b) / a + 0.5f, 0f, 1f);
        float m2 = Math.Clamp((v + b - 1f) / -a + 0.5f, 0f, 1f);
        shadow = s * scale;
        mid = m1 * m2 * scale;
        highlight = h * scale;
    }

    /// <summary>Color Balance per tonal range. adjust_color_balance.</summary>
    public static void ColorBalance(SKBitmap bitmap, ColorBalanceSettings s)
    {
        if (s.IsIdentity) return;
        float[] shadows = { (float)(s.ShadowCyanRed / 100), (float)(s.ShadowMagentaGreen / 100), (float)(s.ShadowYellowBlue / 100) };
        float[] mids = { (float)(s.MidCyanRed / 100), (float)(s.MidMagentaGreen / 100), (float)(s.MidYellowBlue / 100) };
        float[] highs = { (float)(s.HighlightCyanRed / 100), (float)(s.HighlightMagentaGreen / 100), (float)(s.HighlightYellowBlue / 100) };
        bool preserve = s.PreserveLuminosity;
        Rows(bitmap, (row, width) =>
        {
            Span<float> c = stackalloc float[3];
            for (int x = 0; x < width; x++)
            {
                byte* p = row + x * 4;
                float alpha = p[3];
                if (alpha == 0) continue;
                for (int i = 0; i < 3; i++) c[i] = MathF.Min(255f, p[i] * 255f / alpha) / 255f;
                float before = 0.299f * c[0] + 0.587f * c[1] + 0.114f * c[2];
                for (int i = 0; i < 3; i++)
                {
                    TonalWeights(c[i], out float sw, out float mw, out float hw);
                    c[i] = Math.Clamp(c[i] + shadows[i] * sw + mids[i] * mw + highs[i] * hw, 0f, 1f);
                }
                if (preserve)
                {
                    float after = 0.299f * c[0] + 0.587f * c[1] + 0.114f * c[2];
                    if (after > 0.0001f)
                    {
                        float ratio = before / after;
                        for (int i = 0; i < 3; i++) c[i] = Math.Clamp(c[i] * ratio, 0f, 1f);
                    }
                }
                for (int i = 0; i < 3; i++) p[i] = (byte)MathF.Min(alpha, MathF.Max(0, MathF.Round(c[i] * alpha)));
            }
        });
    }

    /// <summary>A color cube lookup (dimension³ RGBA floats, red fastest), trilinear. cube_apply.</summary>
    public static void Cube(SKBitmap bitmap, float[] cube, int dimension)
    {
        fixed (float* cp = cube)
        {
            nint cubePointer = (nint)cp;
            float scale = (dimension - 1) / 255f;
            int dy = dimension, dz = dimension * dimension;
            Rows(bitmap, (row, width) =>
            {
                float* cubeBase = (float*)cubePointer;
                for (int x = 0; x < width; x++)
                {
                    byte* p = row + x * 4;
                    float alpha = p[3];
                    if (alpha == 0) continue;
                    float f0, f1, f2;
                    int l0, l1, l2;
                    {
                        float pos = MathF.Min(255, p[0] * 255f / alpha) * scale; l0 = Math.Min((int)pos, dimension - 2); f0 = pos - l0;
                        pos = MathF.Min(255, p[1] * 255f / alpha) * scale; l1 = Math.Min((int)pos, dimension - 2); f1 = pos - l1;
                        pos = MathF.Min(255, p[2] * 255f / alpha) * scale; l2 = Math.Min((int)pos, dimension - 2); f2 = pos - l2;
                    }
                    float* b = cubeBase + ((long)l0 + l1 * dy + (long)l2 * dz) * 4;
                    int sx = 4, sy = dy * 4, sz = dz * 4;
                    for (int c = 0; c < 3; c++)
                    {
                        float* q = b + c;
                        float x00 = q[0] + (q[sx] - q[0]) * f0;
                        float x10 = q[sy] + (q[sy + sx] - q[sy]) * f0;
                        float x01 = q[sz] + (q[sz + sx] - q[sz]) * f0;
                        float x11 = q[sz + sy] + (q[sz + sy + sx] - q[sz + sy]) * f0;
                        float y0 = x00 + (x10 - x00) * f1;
                        float y1 = x01 + (x11 - x01) * f1;
                        float result = y0 + (y1 - y0) * f2;
                        p[c] = (byte)MathF.Min(alpha, MathF.Max(0, MathF.Round(result * alpha)));
                    }
                }
            });
        }
    }

    private static uint NoiseHash(uint x)
    {
        x ^= x >> 16; x *= 0x7feb352dU;
        x ^= x >> 15; x *= 0x846ca68bU;
        x ^= x >> 16;
        return x;
    }

    private static float NoiseUnit(uint key) => (NoiseHash(key) >> 8) * (1.0f / 16777216.0f);

    /// <summary>Uniform or Gaussian noise, colored or monochromatic, fixed by position and seed. noise_add_at.</summary>
    public static void AddNoise(SKBitmap bitmap, double amount, bool gaussian, bool monochromatic, uint seed, long originX, long originY)
    {
        float spread = (float)(amount / 100.0 * 127.5);
        RowsIndexed(bitmap, (row, width, y) =>
        {
            uint py = unchecked((uint)(originY + y));
            for (int x = 0; x < width; x++)
            {
                byte* p = row + x * 4;
                uint alpha = p[3];
                if (alpha == 0) continue;
                uint px = unchecked((uint)(originX + x));
                uint baseKey = NoiseHash(seed ^ NoiseHash(unchecked(px * 0x9e3779b9U) ^ NoiseHash(unchecked(py * 0x85ebca6bU))));
                for (int c = 0; c < 3; c++)
                {
                    uint key = monochromatic ? baseKey : unchecked(baseKey + (uint)c * 0x9e3779b9U);
                    float n;
                    if (gaussian)
                    {
                        float u1 = NoiseUnit(key), u2 = NoiseUnit(key ^ 0x68e31da4U);
                        n = MathF.Sqrt(-2f * MathF.Log(1f - u1)) * MathF.Cos(6.2831853f * u2) * spread * (2f / 3f);
                    }
                    else n = (NoiseUnit(key) * 2f - 1f) * spread;
                    float value = Clamp255(p[c] * 255f / alpha + n);
                    p[c] = (byte)MathF.Round(value * alpha / 255f, MidpointRounding.AwayFromZero);
                }
            }
        });
    }

    /// <summary>Inverts colors, keeping alpha.</summary>
    public static void Invert(SKBitmap bitmap)
    {
        Rows(bitmap, (row, width) =>
        {
            for (int x = 0; x < width; x++)
            {
                byte* p = row + x * 4;
                byte a = p[3];
                if (a == 0) continue;
                p[0] = (byte)(a - p[0]);
                p[1] = (byte)(a - p[1]);
                p[2] = (byte)(a - p[2]);
            }
        });
    }

    // MARK: Hue/Saturation

    private const int CubeDimension = 33;
    private static readonly object CubeLock = new();
    private static readonly List<(string Key, float[] Cube)> Cubes = new();

    public static float[] HueSaturationCube(HueSaturationSettings settings)
    {
        string key = settings.Key();
        lock (CubeLock)
        {
            foreach (var entry in Cubes) if (entry.Key == key) return entry.Cube;
        }
        var cube = BuildCube(settings);
        lock (CubeLock)
        {
            Cubes.Insert(0, (key, cube));
            if (Cubes.Count > 8) Cubes.RemoveAt(Cubes.Count - 1);
        }
        return cube;
    }

    /// <summary>Hue shift, saturation and lightness amounts for each whole degree of hue (0–360).</summary>
    public static (double Shift, double Saturation, double Lightness)[] HueResponse(HueSaturationSettings settings)
    {
        var response = new (double, double, double)[361];
        for (int degree = 0; degree <= 360; degree++)
        {
            double shift = 0, saturation = 0, lightness = 0;
            foreach (var (range, adjustment) in settings.Adjustments)
            {
                if (adjustment.IsZero) continue;
                double weight = settings.Weight(range, degree);
                if (weight <= 0) continue;
                shift += adjustment.Hue * weight;
                saturation += adjustment.Saturation * weight;
                lightness += adjustment.Lightness * weight;
            }
            response[degree] = (shift, saturation, lightness);
        }
        return response;
    }

    private static float[] BuildCube(HueSaturationSettings settings)
    {
        var response = HueResponse(settings);
        int d = CubeDimension;
        var values = new float[d * d * d * 4];
        int index = 0;
        double step = d - 1;
        for (int blue = 0; blue < d; blue++)
            for (int green = 0; green < d; green++)
                for (int red = 0; red < d; red++)
                {
                    var (r, g, b) = AdjustColor(red / step, green / step, blue / step, settings, response);
                    values[index] = (float)r;
                    values[index + 1] = (float)g;
                    values[index + 2] = (float)b;
                    values[index + 3] = 1;
                    index += 4;
                }
        return values;
    }

    public static (double R, double G, double B) AdjustColor(double red, double green, double blue, HueSaturationSettings settings,
                                                             (double Shift, double Saturation, double Lightness)[]? response = null)
    {
        var (hue, saturation, lightness) = ToHsl(red, green, blue);
        double lightnessAmount;
        if (settings.Colorize)
        {
            var current = settings.Current;
            hue = current.Hue % 360;
            if (hue < 0) hue += 360;
            saturation = Math.Clamp(current.Saturation / 100, 0, 1);
            lightnessAmount = current.Lightness / 100;
        }
        else
        {
            var table = response ?? HueResponse(settings);
            var sampled = table[Math.Clamp((int)Math.Round(hue, MidpointRounding.AwayFromZero), 0, table.Length - 1)];
            lightnessAmount = sampled.Lightness / 100;
            hue = (hue + sampled.Shift) % 360;
            if (hue < 0) hue += 360;
            saturation = AdjustedSaturation(saturation, sampled.Saturation);
        }
        double amount = Math.Clamp(lightnessAmount, -1, 1);
        lightness = amount >= 0 ? lightness + (1 - lightness) * amount : lightness * (1 + amount);
        return ToRgb(hue, saturation, Math.Clamp(lightness, 0, 1));
    }

    /// <summary>Photoshop's Saturation: scales toward gray below 0, divides by what's left above.</summary>
    public static double AdjustedSaturation(double saturation, double by)
    {
        double amount = Math.Clamp(by / 100, -1, 1);
        if (amount <= 0) return Math.Max(0, saturation * (1 + amount));
        return amount >= 1 ? (saturation > 0 ? 1 : 0) : Math.Min(1, saturation / (1 - amount));
    }

    public static (double H, double S, double L) ToHsl(double red, double green, double blue)
    {
        double high = Math.Max(red, Math.Max(green, blue)), low = Math.Min(red, Math.Min(green, blue));
        double lightness = (high + low) / 2, delta = high - low;
        if (delta <= 0) return (0, 0, lightness);
        double saturation = delta / (1 - Math.Abs(2 * lightness - 1));
        double hue;
        if (high == red) hue = (green - blue) / delta;
        else if (high == green) hue = (blue - red) / delta + 2;
        else hue = (red - green) / delta + 4;
        hue *= 60;
        if (hue < 0) hue += 360;
        return (hue, Math.Min(1, saturation), lightness);
    }

    public static (double R, double G, double B) ToRgb(double hue, double saturation, double lightness)
    {
        if (saturation <= 0) return (lightness, lightness, lightness);
        double chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        double sector = hue / 60;
        double second = chroma * (1 - Math.Abs(sector % 2 - 1));
        double baseValue = lightness - chroma / 2;
        (double r, double g, double b) = (int)sector switch
        {
            0 => (chroma, second, 0.0),
            1 => (second, chroma, 0.0),
            2 => (0.0, chroma, second),
            3 => (0.0, second, chroma),
            4 => (second, 0.0, chroma),
            _ => (chroma, 0.0, second),
        };
        return (Math.Clamp(r + baseValue, 0, 1), Math.Clamp(g + baseValue, 0, 1), Math.Clamp(b + baseValue, 0, 1));
    }

    // MARK: Blurs

    /// <summary>Gaussian blur (σ in this bitmap's pixels), spreading past the edges rather than clamping to them.</summary>
    public static void GaussianBlur(SKBitmap bitmap, double sigma)
    {
        if (sigma <= 0.05) return;
        using var source = SKImage.FromPixelCopy(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        using var filter = SKImageFilter.CreateBlur((float)sigma, (float)sigma, SKShaderTileMode.Decal);
        using var paint = new SKPaint { ImageFilter = filter };
        canvas.DrawImage(source, 0, 0, paint);
        canvas.Flush();
        bitmap.NotifyPixelsChanged();
    }

    /// <summary>
    /// Photoshop's Motion Blur: an even streak of <paramref name="distance"/> pixels at <paramref name="angle"/> degrees
    /// (counterclockwise from horizontal). The picture is turned so the streak runs along rows, averaged with running
    /// sums, and turned back.
    /// </summary>
    public static void MotionBlur(SKBitmap bitmap, double angle, double distance)
    {
        int length = Math.Max(1, (int)Math.Round(distance));
        if (length <= 1) return;
        int w = bitmap.Width, h = bitmap.Height;
        bool horizontal = Math.Abs(angle % 180) < 1e-6;
        if (horizontal)
        {
            BoxRows(bitmap, length);
            return;
        }
        // Turned by +angle, the streak's direction (cos θ, −sin θ) runs along +x.
        double radians = angle * Math.PI / 180;
        double cos = Math.Abs(Math.Cos(radians)), sin = Math.Abs(Math.Sin(radians));
        int rw = (int)Math.Ceiling(w * cos + h * sin) + length + 4, rh = (int)Math.Ceiling(w * sin + h * cos) + 4;
        using var source = SKImage.FromPixelCopy(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes);
        using var turned = new SKBitmap(new SKImageInfo(rw, rh, SKColorType.Rgba8888, SKAlphaType.Premul));
        var sampling = new SKSamplingOptions(SKFilterMode.Linear);
        using (var canvas = new SKCanvas(turned))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Translate(rw / 2f, rh / 2f);
            canvas.RotateDegrees((float)angle);
            canvas.Translate(-w / 2f, -h / 2f);
            canvas.DrawImage(source, 0, 0, sampling);
        }
        BoxRows(turned, length);
        using var streaked = SKImage.FromPixelCopy(turned.Info, turned.GetPixels(), turned.RowBytes);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Translate(w / 2f, h / 2f);
            canvas.RotateDegrees((float)-angle);
            canvas.Translate(-rw / 2f, -rh / 2f);
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
            canvas.DrawImage(streaked, 0, 0, sampling, paint);
        }
        bitmap.NotifyPixelsChanged();
    }

    /// <summary>Averages each pixel with the <paramref name="length"/> pixels centered on it along its row (premultiplied).</summary>
    private static void BoxRows(SKBitmap bitmap, int length)
    {
        int half = length / 2;
        Rows(bitmap, (row, width) =>
        {
            var copy = new byte[width * 4];
            new ReadOnlySpan<byte>(row, width * 4).CopyTo(copy);
            Span<int> sum = stackalloc int[4];
            sum.Clear();
            // Window [x − half, x − half + length).
            for (int i = -half; i < -half + length; i++)
                if (i >= 0 && i < width) for (int c = 0; c < 4; c++) sum[c] += copy[i * 4 + c];
            for (int x = 0; x < width; x++)
            {
                for (int c = 0; c < 4; c++) row[x * 4 + c] = (byte)((sum[c] + length / 2) / length);
                int leaving = x - half, entering = x - half + length;
                if (leaving >= 0 && leaving < width) for (int c = 0; c < 4; c++) sum[c] -= copy[leaving * 4 + c];
                if (entering >= 0 && entering < width) for (int c = 0; c < 4; c++) sum[c] += copy[entering * 4 + c];
            }
        });
    }

    // MARK: One adjustment

    /// <summary>Runs an adjustment over a bitmap in place.</summary>
    public static void Apply(LayerAdjustment adjustment, SKBitmap bitmap, AdjustmentPlacement placement)
    {
        switch (adjustment.Kind)
        {
            case AdjustmentKind.HueSaturation:
                var hsv = adjustment.ResolvedHsv;
                if (!hsv.IsIdentity) Cube(bitmap, HueSaturationCube(hsv), CubeDimension);
                break;
            case AdjustmentKind.Levels:
                if (!adjustment.Levels.IsIdentity) Tables(bitmap, adjustment.Levels.Tables());
                break;
            case AdjustmentKind.Curves:
                if (!adjustment.Curves.IsIdentity && adjustment.Curves.IsValid) Tables(bitmap, adjustment.Curves.Tables());
                break;
            case AdjustmentKind.Exposure:
                if (!adjustment.Exposure.IsIdentity) Tables(bitmap, adjustment.Exposure.Tables());
                break;
            case AdjustmentKind.GradientMap:
                GradientMap(bitmap, adjustment.GradientMap.Table());
                break;
            case AdjustmentKind.Grain:
                Grain(bitmap, adjustment.Grain, adjustment.Grain.Seed, placement.OriginX, placement.OriginY, 1 / placement.Scale);
                break;
            case AdjustmentKind.BlackWhite:
                BlackWhite(bitmap, adjustment.BlackWhite);
                break;
            case AdjustmentKind.ColorBalance:
                ColorBalance(bitmap, adjustment.ColorBalance);
                break;
            case AdjustmentKind.Invert:
                Invert(bitmap);
                break;
            case AdjustmentKind.GaussianBlur:
                GaussianBlur(bitmap, adjustment.GaussianRadius * placement.Scale);
                break;
            case AdjustmentKind.MotionBlur:
                MotionBlur(bitmap, adjustment.ResolvedMotionAngle, adjustment.ResolvedMotionDistance * placement.Scale);
                break;
            case AdjustmentKind.AddNoise:
                AddNoise(bitmap, adjustment.ResolvedNoiseAmount, adjustment.ResolvedNoiseGaussian, adjustment.ResolvedNoiseMonochromatic,
                         adjustment.ResolvedNoiseSeed, placement.NoiseOriginX, placement.NoiseOriginY);
                break;
        }
    }

    /// <summary>
    /// Lays <paramref name="adjusted"/> over <paramref name="original"/> (both premultiplied, same size) through
    /// <paramref name="coverage"/> (one byte a pixel; null for full) times <paramref name="opacity"/>, into <paramref name="original"/>.
    /// </summary>
    public static void Mix(SKBitmap original, SKBitmap adjusted, byte[]? coverage, double opacity)
    {
        int w = original.Width, h = original.Height, strideA = original.RowBytes, strideB = adjusted.RowBytes;
        byte* a = (byte*)original.GetPixels(), b = (byte*)adjusted.GetPixels();
        nint ap = (nint)a, bp = (nint)b;
        int scale = (int)Math.Round(Math.Clamp(opacity, 0, 1) * 255);
        Parallel.For(0, h, y =>
        {
            byte* rowA = (byte*)ap + (long)y * strideA, rowB = (byte*)bp + (long)y * strideB;
            for (int x = 0; x < w; x++)
            {
                int k = coverage is null ? scale : coverage[y * w + x] * scale / 255;
                if (k == 0) continue;
                byte* pa = rowA + x * 4, pb = rowB + x * 4;
                if (k == 255) { *(uint*)pa = *(uint*)pb; continue; }
                for (int c = 0; c < 4; c++) pa[c] = (byte)(pa[c] + ((pb[c] - pa[c]) * k + 127) / 255);
            }
        });
        original.NotifyPixelsChanged();
    }

    /// <summary>
    /// Histograms of unpremultiplied red, green and blue, each pixel weighted by its alpha and its
    /// <paramref name="coverage"/> (one byte a pixel; null for full). Index 0 is the mean of the three, as on the Mac
    /// (not a luminance histogram).
    /// </summary>
    public static double[][] Histogram(SKBitmap bitmap, byte[]? coverage)
    {
        var bins = new double[4][];
        for (int c = 0; c < 4; c++) bins[c] = new double[256];
        int w = bitmap.Width, h = bitmap.Height, stride = bitmap.RowBytes;
        byte* pixels = (byte*)bitmap.GetPixels();
        for (int y = 0; y < h; y++)
        {
            byte* row = pixels + (long)y * stride;
            for (int x = 0; x < w; x++)
            {
                byte* p = row + x * 4;
                int a = p[3];
                if (a == 0) continue;
                double weight = a / 255.0;
                if (coverage is not null)
                {
                    int k = coverage[y * w + x];
                    if (k == 0) continue;
                    weight *= k / 255.0;
                }
                for (int c = 0; c < 3; c++) bins[c + 1][Math.Min(255, (p[c] * 255 + a / 2) / a)] += weight;
            }
        }
        for (int i = 0; i < 256; i++) bins[0][i] = (bins[1][i] + bins[2][i] + bins[3][i]) / 3;
        return bins;
    }

    /// <summary>
    /// Blends the adjusted colors over the originals in a blend mode, keeping the original alpha (as the Mac's
    /// LiveMaskRenderer does), so a translucent edge isn't thickened by two copies.
    /// </summary>
    public static void BlendInto(SKBitmap original, SKBitmap adjusted, BlendMode mode)
    {
        using var baseCopy = original.Copy();
        using var topCopy = adjusted.Copy();
        var alpha = PixelOps.ExtractAlphaAndMakeOpaque(baseCopy);
        PixelOps.ExtractAlphaAndMakeOpaque(topCopy);
        using (var canvas = new SKCanvas(baseCopy))
        using (var image = SKImage.FromPixelCopy(topCopy.Info, topCopy.GetPixels(), topCopy.RowBytes))
        using (var paint = new SKPaint())
        {
            Blenders.Apply(paint, mode);
            canvas.DrawImage(image, 0, 0, paint);
        }
        PixelOps.RestoreAlpha(baseCopy, alpha);
        // The blended colors replace the adjusted ones.
        baseCopy.CopyTo(adjusted);
        adjusted.NotifyPixelsChanged();
    }
}
