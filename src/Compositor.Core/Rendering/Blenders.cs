using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Core.Rendering;

/// <summary>
/// Photoshop's blend modes on Skia. Most map to Skia's own modes (which, unlike Core Graphics, respect the source's
/// transparency in Color Burn and Color Dodge). The ones Skia lacks — and Soft Light, whose W3C formula differs from
/// Photoshop's — are SkSL blenders using the separable compositing formula on premultiplied colors.
/// </summary>
public static class Blenders
{
    private static readonly Dictionary<BlendMode, SKBlender> Cache = new();
    private static readonly object Lock = new();

    /// <summary>Skia's equivalent, when it has one that matches Photoshop.</summary>
    public static SKBlendMode? Native(BlendMode mode) => mode switch
    {
        BlendMode.Normal => SKBlendMode.SrcOver,
        BlendMode.Darken => SKBlendMode.Darken,
        BlendMode.Multiply => SKBlendMode.Multiply,
        BlendMode.ColorBurn => SKBlendMode.ColorBurn,
        BlendMode.Lighten => SKBlendMode.Lighten,
        BlendMode.Screen => SKBlendMode.Screen,
        BlendMode.ColorDodge => SKBlendMode.ColorDodge,
        BlendMode.Overlay => SKBlendMode.Overlay,
        BlendMode.HardLight => SKBlendMode.HardLight,
        BlendMode.Difference => SKBlendMode.Difference,
        BlendMode.Exclusion => SKBlendMode.Exclusion,
        BlendMode.Hue => SKBlendMode.Hue,
        BlendMode.Saturation => SKBlendMode.Saturation,
        BlendMode.Color => SKBlendMode.Color,
        BlendMode.Luminosity => SKBlendMode.Luminosity,
        _ => null,
    };

    /// <summary>Configures a paint to composite in <paramref name="mode"/>.</summary>
    public static void Apply(SKPaint paint, BlendMode mode)
    {
        if (Native(mode) is { } native)
        {
            paint.Blender = null;
            paint.BlendMode = native;
            return;
        }
        paint.BlendMode = SKBlendMode.SrcOver;
        paint.Blender = Custom(mode);
    }

    private static SKBlender Custom(BlendMode mode)
    {
        lock (Lock)
        {
            if (Cache.TryGetValue(mode, out var cached)) return cached;
            string function = mode switch
            {
                BlendMode.LinearBurn => "return max(0.0, s + d - 1.0);",
                BlendMode.LinearDodge => "return min(1.0, s + d);",
                BlendMode.VividLight =>
                    "if (s <= 0.5) { float s2 = 2.0 * s; if (d >= 1.0) return 1.0; if (s2 <= 0.0) return 0.0; return max(0.0, 1.0 - (1.0 - d) / s2); }" +
                    " float s3 = 2.0 * s - 1.0; if (d <= 0.0) return 0.0; if (s3 >= 1.0) return 1.0; return min(1.0, d / (1.0 - s3));",
                BlendMode.LinearLight => "return clamp(d + 2.0 * s - 1.0, 0.0, 1.0);",
                BlendMode.PinLight => "return s <= 0.5 ? min(d, 2.0 * s) : max(d, 2.0 * s - 1.0);",
                BlendMode.HardMix => "return (s + d >= 1.0) ? 1.0 : 0.0;",
                BlendMode.Subtract => "return max(0.0, d - s);",
                BlendMode.Divide => "if (s <= 0.0) return d <= 0.0 ? 0.0 : 1.0; return min(1.0, d / s);",
                // Photoshop's Soft Light (not the W3C one Skia implements).
                BlendMode.SoftLight =>
                    "return s <= 0.5 ? 2.0 * d * s + d * d * (1.0 - 2.0 * s) : 2.0 * d * (1.0 - s) + sqrt(d) * (2.0 * s - 1.0);",
                _ => "return s;",
            };
            string sksl = $@"
float blendChannel(float s, float d) {{ {function} }}
half4 main(half4 src, half4 dst) {{
    float sa = src.a;
    float da = dst.a;
    float3 s = sa > 0.0 ? float3(src.rgb) / sa : float3(0.0);
    float3 d = da > 0.0 ? float3(dst.rgb) / da : float3(0.0);
    s = clamp(s, 0.0, 1.0);
    d = clamp(d, 0.0, 1.0);
    float3 b = float3(blendChannel(s.r, d.r), blendChannel(s.g, d.g), blendChannel(s.b, d.b));
    float3 rgb = float3(src.rgb) * (1.0 - da) + float3(dst.rgb) * (1.0 - sa) + sa * da * clamp(b, 0.0, 1.0);
    float a = sa + da * (1.0 - sa);
    return half4(half3(rgb), half(a));
}}";
            var effect = SKRuntimeEffect.CreateBlender(sksl, out var errors)
                         ?? throw new InvalidOperationException($"Blend mode {mode} failed to compile: {errors}");
            var blender = effect.ToBlender();
            Cache[mode] = blender;
            return blender;
        }
    }

    /// <summary>
    /// Blends one straight (unpremultiplied) channel with Photoshop's formula, for CPU work such as color picking
    /// and tests. <paramref name="s"/> is the layer's color, <paramref name="d"/> the backdrop's, both 0…1.
    /// </summary>
    public static double Channel(BlendMode mode, double s, double d)
    {
        switch (mode)
        {
            case BlendMode.Normal: return s;
            case BlendMode.Darken: return Math.Min(s, d);
            case BlendMode.Multiply: return s * d;
            case BlendMode.ColorBurn: return d >= 1 ? 1 : s <= 0 ? 0 : Math.Max(0, 1 - (1 - d) / s);
            case BlendMode.LinearBurn: return Math.Max(0, s + d - 1);
            case BlendMode.Lighten: return Math.Max(s, d);
            case BlendMode.Screen: return s + d - s * d;
            case BlendMode.ColorDodge: return d <= 0 ? 0 : s >= 1 ? 1 : Math.Min(1, d / (1 - s));
            case BlendMode.LinearDodge: return Math.Min(1, s + d);
            case BlendMode.Overlay: return d <= 0.5 ? 2 * s * d : 1 - 2 * (1 - s) * (1 - d);
            case BlendMode.SoftLight:
                return s <= 0.5 ? 2 * d * s + d * d * (1 - 2 * s) : 2 * d * (1 - s) + Math.Sqrt(d) * (2 * s - 1);
            case BlendMode.HardLight: return s <= 0.5 ? 2 * s * d : 1 - 2 * (1 - s) * (1 - d);
            case BlendMode.VividLight:
                if (s <= 0.5) { double s2 = 2 * s; return d >= 1 ? 1 : s2 <= 0 ? 0 : Math.Max(0, 1 - (1 - d) / s2); }
                else { double s3 = 2 * s - 1; return d <= 0 ? 0 : s3 >= 1 ? 1 : Math.Min(1, d / (1 - s3)); }
            case BlendMode.LinearLight: return Math.Clamp(d + 2 * s - 1, 0, 1);
            case BlendMode.PinLight: return s <= 0.5 ? Math.Min(d, 2 * s) : Math.Max(d, 2 * s - 1);
            case BlendMode.HardMix: return s + d >= 1 ? 1 : 0;
            case BlendMode.Difference: return Math.Abs(s - d);
            case BlendMode.Exclusion: return s + d - 2 * s * d;
            case BlendMode.Subtract: return Math.Max(0, d - s);
            case BlendMode.Divide: return s <= 0 ? (d <= 0 ? 0 : 1) : Math.Min(1, d / s);
            default: return s;
        }
    }
}
