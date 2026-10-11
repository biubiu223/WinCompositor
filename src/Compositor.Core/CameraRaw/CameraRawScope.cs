using SkiaSharp;

namespace Compositor.Core.CameraRaw;

/// <summary>
/// The graded picture's RGB histogram and a hue/saturation vectorscope of the same pixels, for the panel's scope.
/// Mirrors CameraRawScope and camera_raw_scope.
/// </summary>
public sealed class CameraRawScope
{
    public const int Bins = 256, ScopeSide = 64;
    public double[] Red { get; } = new double[Bins];
    public double[] Green { get; } = new double[Bins];
    public double[] Blue { get; } = new double[Bins];
    /// <summary>Density by hue around and saturation outward, index y * ScopeSide + x.</summary>
    public double[] Vectorscope { get; } = new double[ScopeSide * ScopeSide];

    /// <summary>A sampling step that reads about 200,000 pixels however big the bitmap.</summary>
    public static int StepFor(SKBitmap bitmap) => Math.Max(1, (int)Math.Sqrt((double)bitmap.Width * bitmap.Height / 200_000));

    /// <summary>Every other pixel each way, plenty for a scope drawn to its peak. Clear pixels are skipped.</summary>
    public static unsafe CameraRawScope Make(SKBitmap bitmap, int step = 2)
    {
        var scope = new CameraRawScope();
        byte* pixels = (byte*)bitmap.GetPixels();
        int width = bitmap.Width, height = bitmap.Height, stride = bitmap.RowBytes;
        for (int y = 0; y < height; y += step)
        {
            byte* row = pixels + (long)y * stride;
            for (int x = 0; x < width; x += step)
            {
                byte* p = row + x * 4;
                double alpha = p[3];
                if (alpha == 0) continue;
                double red = Math.Min(1, p[0] / alpha), green = Math.Min(1, p[1] / alpha), blue = Math.Min(1, p[2] / alpha), w = alpha / 255;
                scope.Red[(int)Math.Round(red * 255)] += w;
                scope.Green[(int)Math.Round(green * 255)] += w;
                scope.Blue[(int)Math.Round(blue * 255)] += w;
                double top = Math.Max(red, Math.Max(green, blue)), bottom = Math.Min(red, Math.Min(green, blue)), chroma = top - bottom;
                if (chroma <= 1e-4 || top <= 1e-4) continue;
                double hue = top == red ? (green - blue) / chroma : top == green ? 2 + (blue - red) / chroma : 4 + (red - green) / chroma;
                hue /= 6;
                if (hue < 0) hue += 1;
                double angle = hue * 2 * Math.PI, saturation = chroma / top;
                int column = Math.Clamp((int)((0.5 + Math.Cos(angle) * saturation * 0.48) * ScopeSide), 0, ScopeSide - 1);
                int line = Math.Clamp((int)((0.5 + Math.Sin(angle) * saturation * 0.48) * ScopeSide), 0, ScopeSide - 1);
                scope.Vectorscope[line * ScopeSide + column] += w;
            }
        }
        return scope;
    }

    /// <summary>A shared vertical scale for the three ribbons: high enough for most bins without one spike flattening the rest.</summary>
    public double Peak
    {
        get
        {
            double Scale(double[] bins)
            {
                var sorted = bins.OrderByDescending(v => v).ToArray();
                return sorted.Length > 3 ? sorted[2] * 1.1 : sorted.FirstOrDefault();
            }
            return Math.Max(Scale(Red), Math.Max(Scale(Green), Scale(Blue)));
        }
    }
}
