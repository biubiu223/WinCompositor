using Compositor.Core.Model;

namespace Compositor.Core.IO;

/// <summary>
/// The choices a camera RAW still leaves open, spent at import since layers are 8-bit. Exposure in stops; Temperature
/// and Tint relative to the camera's own reading (−100…100); Boost is how much of the codec's contrast to keep (1 all
/// of it, 0 a flat, neutral rendering). Mirrors RawDevelopSettings.
/// </summary>
public sealed record RawDevelopSettings(double Exposure = 0, double Temperature = 0, double Tint = 0, double Boost = 1)
{
    public bool IsAsShot => Exposure == 0 && Temperature == 0 && Tint == 0 && Boost == 1;

    public RawDevelopSettings Normalized => new(
        double.IsFinite(Exposure) ? Math.Clamp(Exposure, -3, 3) : 0,
        double.IsFinite(Temperature) ? Math.Clamp(Temperature, -100, 100) : 0,
        double.IsFinite(Tint) ? Math.Clamp(Tint, -100, 100) : 0,
        double.IsFinite(Boost) ? Math.Clamp(Boost, 0, 1) : 1);
}

/// <summary>
/// Develops a RAW frame decoded at 16 bits a channel: white balance and exposure in linear light, then the contrast
/// kept, then down to 8 bits with a little noise rather than straight rounding, so a smooth sky doesn't come out in
/// steps (the Mac app's dither_quantize16). Mirrors RawImporter.develop.
/// </summary>
public static class RawDevelop
{
    private static readonly Lazy<float[]> Linear = new(() =>
        Enumerable.Range(0, 65536).Select(i =>
        {
            double e = i / 65535.0;
            return (float)(e <= 0.04045 ? e / 12.92 : Math.Pow((e + 0.055) / 1.055, 2.4));
        }).ToArray());

    private static double Encode(double l) => l <= 0 ? 0 : l >= 1 ? 1 : l <= 0.0031308 ? l * 12.92 : 1.055 * Math.Pow(l, 1 / 2.4) - 0.055;

    /// <summary>
    /// The developed pixels, premultiplied RGBA 8-bit, from straight RGBA 16-bit (<paramref name="rgba16"/>, 4 values a
    /// pixel). <paramref name="seed"/> fixes the dither so a preview doesn't shimmer as sliders move.
    /// </summary>
    public static byte[] Apply(ushort[] rgba16, int width, int height, RawDevelopSettings settings, uint seed = 1)
    {
        var s = settings.Normalized;
        var linear = Linear.Value;
        double gain = Math.Pow(2, s.Exposure);
        // White balance as channel gains in linear light, keeping brightness: warmer lifts red and lowers blue; a
        // positive Tint is magenta (less green).
        double rGain = Math.Pow(2, s.Temperature / 100 * 0.5), bGain = Math.Pow(2, -s.Temperature / 100 * 0.5), gGain = Math.Pow(2, -s.Tint / 100 * 0.35);
        double norm = 0.2126 * rGain + 0.7152 * gGain + 0.0722 * bGain;
        rGain *= gain / norm; gGain *= gain / norm; bGain *= gain / norm;
        double flatten = 1 - s.Boost;
        var output = new byte[width * height * 4];
        Parallel.For(0, height, y =>
        {
            uint state = seed * 0x9E3779B9u ^ (uint)(y * 0x85EBCA6B) | 1;
            double Noise()
            {
                state ^= state << 13; state ^= state >> 17; state ^= state << 5;
                return (state & 0xFFFFFF) / (double)0x1000000;
            }
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                double r = linear[rgba16[i]] * rGain, g = linear[rgba16[i + 1]] * gGain, b = linear[rgba16[i + 2]] * bGain;
                // A soft shoulder for what exposure pushed past white, rather than a hard clip.
                r = Shoulder(r); g = Shoulder(g); b = Shoulder(b);
                double er = Encode(r), eg = Encode(g), eb = Encode(b);
                if (flatten > 0)
                {
                    er = Flat(er, flatten); eg = Flat(eg, flatten); eb = Flat(eb, flatten);
                }
                double alpha = rgba16[i + 3] / 65535.0;
                byte a = (byte)Math.Round(alpha * 255);
                output[i] = Quantize(er * alpha, Noise());
                output[i + 1] = Quantize(eg * alpha, Noise());
                output[i + 2] = Quantize(eb * alpha, Noise());
                output[i + 3] = a;
                output[i] = Math.Min(output[i], a); output[i + 1] = Math.Min(output[i + 1], a); output[i + 2] = Math.Min(output[i + 2], a);
            }
        });
        return output;
    }

    private static double Shoulder(double v) => v <= 0.8 ? Math.Max(0, v) : 0.8 + 0.2 * (1 - Math.Exp(-(v - 0.8) / 0.2));

    /// <summary>Less of the rendering's contrast: an inverse S around the middle, lifting shadows and holding highlights.</summary>
    private static double Flat(double v, double amount)
    {
        double flat = 0.5 + Math.Atan((v - 0.5) * 2.2) / Math.Atan(1.1) * 0.5 * 0.82 + 0.03;
        return Math.Clamp(v + (flat - v) * amount, 0, 1);
    }

    private static byte Quantize(double v, double noise) => (byte)Math.Clamp(Math.Floor(v * 255 + noise), 0, 255);

    public static RasterImage ToImage(byte[] premultiplied, int width, int height, string name) =>
        RasterImage.FromRgba(premultiplied, width, height, name);
}
