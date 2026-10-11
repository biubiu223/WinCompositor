using Compositor.Core.Adjustments;
using Compositor.Core.Localization;

namespace Compositor.Core.CameraRaw;

public enum CameraRawWhiteBalance { Custom, Auto }

/// <summary>Glow's three looks. Warmth tints Diffusion and Bloom from cool to warm; Halation's fringe stays red.</summary>
public enum CameraRawGlowStyle { Diffusion, Bloom, Halation }

/// <summary>Post-crop vignette. Highlight Priority is the style whose Highlights slider protects bright pixels.</summary>
public enum CameraRawVignetteStyle { HighlightPriority, ColorPriority, PaintOverlay }

public enum CameraRawProjection { Perspective, Rectilinear }

/// <summary>The panel's sections, for their eyes: a hidden one's adjustments are left out of the preview (OK keeps them).</summary>
[Flags]
public enum CameraRawSection
{
    None = 0, Basic = 1, Curve = 2, Detail = 4, Mixer = 8, Grading = 16, Optics = 32, Geometry = 64, Effects = 128, Calibration = 256,
}

public static class CameraRawNames
{
    public static string ToDisplayName(this CameraRawGlowStyle s) => s switch
    {
        CameraRawGlowStyle.Diffusion => L.T("柔光扩散", "Diffusion"), CameraRawGlowStyle.Bloom => L.T("泛光", "Bloom"), _ => L.T("光晕（红边）", "Halation"),
    };

    public static string ToDisplayName(this CameraRawVignetteStyle s) => s switch
    {
        CameraRawVignetteStyle.HighlightPriority => L.T("高光优先", "Highlight Priority"), CameraRawVignetteStyle.ColorPriority => L.T("颜色优先", "Color Priority"),
        _ => L.T("绘画叠加", "Paint Overlay"),
    };
}

internal static class Clamps
{
    public static double C(double v, double lo, double hi, double fallback) => double.IsFinite(v) ? Math.Clamp(v, lo, hi) : fallback;
}

/// <summary>Parametric regions and point curves. Amounts −100…100; points 0…1 on both axes. Mirrors CameraRawCurveSettings.</summary>
public sealed record CameraRawCurveSettings
{
    public double Shadows { get; init; }
    public double Darks { get; init; }
    public double Lights { get; init; }
    public double Highlights { get; init; }
    /// <summary>Dividers, 0…100, kept in order: where each parametric slider hands off to the next.</summary>
    public double ShadowSplit { get; init; } = 25;
    public double DarkSplit { get; init; } = 50;
    public double LightSplit { get; init; } = 75;
    public CurvePoint[] Rgb { get; init; } = Linear;
    public CurvePoint[] Red { get; init; } = Linear;
    public CurvePoint[] Green { get; init; } = Linear;
    public CurvePoint[] Blue { get; init; } = Linear;
    /// <summary>How much the composite curve also changes saturation, −100…100.</summary>
    public double RefineSaturation { get; init; }

    public static readonly CurvePoint[] Linear = { new(0, 0), new(1, 1) };
    public static readonly CurvePoint[] MediumContrast = { new(0, 0), new(0.25, 0.18), new(0.75, 0.82), new(1, 1) };
    public static readonly CurvePoint[] StrongContrast = { new(0, 0), new(0.25, 0.10), new(0.75, 0.90), new(1, 1) };

    public bool Adjusts => Shadows != 0 || Darks != 0 || Lights != 0 || Highlights != 0 || RefineSaturation != 0
                           || !IsLinear(Rgb) || !IsLinear(Red) || !IsLinear(Green) || !IsLinear(Blue);

    public static bool IsLinear(CurvePoint[] p) => p.Length == 2 && p[0] == new CurvePoint(0, 0) && p[1] == new CurvePoint(1, 1);

    /// <summary>
    /// Camera Raw's parametric curve, matched to Photoshop's: Darks bends the whole range below the middle divider and
    /// Lights the range above it, Shadows and Highlights just past the outer dividers; each bend a gamma curve.
    /// </summary>
    public double Parametric(double tone)
    {
        if (Shadows == 0 && Darks == 0 && Lights == 0 && Highlights == 0) return tone;
        var anchors = Enumerable.Range(0, 33).Select(i =>
        {
            double x = i / 32.0;
            return new CurvePoint(x, Bend(Bend(x, ShadowSplit / 100, Shadows, LightSplit / 100, Highlights), DarkSplit / 100, Darks, DarkSplit / 100, Lights));
        }).ToArray();
        return Point(tone, anchors);
    }

    private static double Bend(double tone, double lower, double low, double upper, double high)
    {
        const double strength = 1.66;
        if (tone < lower && lower > 0) return lower * Math.Pow(tone / lower, Math.Pow(2, -low / 100 * strength));
        if (tone > upper && upper < 1)
        {
            double rest = 1 - upper;
            return 1 - rest * Math.Pow((1 - tone) / rest, Math.Pow(2, high / 100 * strength));
        }
        return tone;
    }

    /// <summary>Photoshop's measured tables draw the parametric curve while the dividers are where they were measured.</summary>
    public bool HasMeasuredDividers => ShadowSplit == 25 && DarkSplit == 50 && LightSplit == 75 && RefineSaturation == 0;

    public float[] ToneTable() => Enumerable.Range(0, 256).Select(i => (float)Point(HasMeasuredDividers ? i / 255.0 : Parametric(i / 255.0), Rgb)).ToArray();
    public static float[] ChannelTable(CurvePoint[] points) => Enumerable.Range(0, 256).Select(i => (float)Point(i / 255.0, points)).ToArray();

    private static double Point(double x, CurvePoint[] points)
    {
        if (points.Length < 2) return x;
        var curve = new CurvesSettings();
        curve.Channels[0] = points.Select(p => new CurvePoint(p.X * 255, p.Y * 255)).ToList();
        return curve.Value(x * 255, 0) / 255;
    }

    public CameraRawCurveSettings Normalized
    {
        get
        {
            double shadowSplit = Clamps.C(ShadowSplit, 5, 90, 25);
            double darkSplit = Clamps.C(DarkSplit, shadowSplit + 2, 95, 50);
            return this with
            {
                Shadows = Clamps.C(Shadows, -100, 100, 0), Darks = Clamps.C(Darks, -100, 100, 0),
                Lights = Clamps.C(Lights, -100, 100, 0), Highlights = Clamps.C(Highlights, -100, 100, 0),
                RefineSaturation = Clamps.C(RefineSaturation, -100, 100, 0),
                ShadowSplit = shadowSplit, DarkSplit = darkSplit, LightSplit = Clamps.C(LightSplit, darkSplit + 2, 98, 75),
                Rgb = Repair(Rgb), Red = Repair(Red), Green = Repair(Green), Blue = Repair(Blue),
            };
        }
    }

    private static CurvePoint[] Repair(CurvePoint[] points)
    {
        var sorted = points.Where(p => double.IsFinite(p.X) && double.IsFinite(p.Y)).OrderBy(p => p.X).ToList();
        if (sorted.Count < 2) return Linear;
        sorted[0] = new CurvePoint(0, Math.Clamp(sorted[0].Y, 0, 1));
        sorted[^1] = new CurvePoint(1, Math.Clamp(sorted[^1].Y, 0, 1));
        var kept = new List<CurvePoint> { sorted[0] };
        foreach (var p in sorted.Skip(1).Take(sorted.Count - 2))
        {
            double x = Math.Clamp(p.X, 0.01, 0.99);
            if (x <= kept[^1].X + 0.01) continue;
            kept.Add(new CurvePoint(x, Math.Clamp(p.Y, 0, 1)));
        }
        kept.Add(sorted[^1]);
        return kept.ToArray();
    }

    public bool ContentEquals(CameraRawCurveSettings o) =>
        Shadows == o.Shadows && Darks == o.Darks && Lights == o.Lights && Highlights == o.Highlights && ShadowSplit == o.ShadowSplit
        && DarkSplit == o.DarkSplit && LightSplit == o.LightSplit && RefineSaturation == o.RefineSaturation
        && Rgb.SequenceEqual(o.Rgb) && Red.SequenceEqual(o.Red) && Green.SequenceEqual(o.Green) && Blue.SequenceEqual(o.Blue);
}

/// <summary>Eight color families, each with hue, saturation and luminance shifts of −100…100. Mirrors CameraRawMixerSettings.</summary>
public sealed record CameraRawMixerSettings
{
    public static readonly string[] Names =
    {
        L.T("红色", "Reds"), L.T("橙色", "Oranges"), L.T("黄色", "Yellows"), L.T("绿色", "Greens"),
        L.T("浅绿色", "Aquas"), L.T("蓝色", "Blues"), L.T("紫色", "Purples"), L.T("洋红", "Magentas"),
    };
    public static readonly double[] Centers = { 0, 30, 60, 120, 180, 240, 270, 300 };
    public double[] Hue { get; init; } = new double[8];
    public double[] Saturation { get; init; } = new double[8];
    public double[] Luminance { get; init; } = new double[8];
    /// <summary>Colors picked from the picture (up to eight), each with its own shifts and reach.</summary>
    public CameraRawPointColor[] Points { get; init; } = Array.Empty<CameraRawPointColor>();

    public bool Adjusts => Hue.Any(v => v != 0) || Saturation.Any(v => v != 0) || Luminance.Any(v => v != 0)
                           || Points.Any(p => p.HueShift != 0 || p.SaturationShift != 0 || p.LuminanceShift != 0);

    /// <summary>How much each family shares a hue, in degrees. Neighbors overlap.</summary>
    public static double[] Weights(double degrees) => Centers.Select(center =>
    {
        double distance = Math.Abs(degrees - center);
        if (distance > 180) distance = 360 - distance;
        return Math.Max(0, 1 - distance / 40);
    }).ToArray();

    public CameraRawMixerSettings Normalized => this with
    {
        Hue = Fix(Hue), Saturation = Fix(Saturation), Luminance = Fix(Luminance),
        Points = Points.Take(8).Select(p => p.Normalized).ToArray(),
    };

    private static double[] Fix(double[] values) => Enumerable.Range(0, 8).Select(i => i < values.Length ? Clamps.C(values[i], -100, 100, 0) : 0).ToArray();
}

/// <summary>One picked color (hue in degrees, saturation and luminance 0…1, as HSL) and how far its adjustment reaches. Mirrors CameraRawPointColor.</summary>
public sealed record CameraRawPointColor(double Hue = 0, double Saturation = 0, double Luminance = 0,
                                         double HueShift = 0, double SaturationShift = 0, double LuminanceShift = 0,
                                         double HueRange = 30, double SaturationRange = 0.4, double LuminanceRange = 0.4, bool Visualize = false)
{
    public CameraRawPointColor Normalized => this with
    {
        Hue = Clamps.C(Hue, 0, 360, 0), Saturation = Clamps.C(Saturation, 0, 1, 0), Luminance = Clamps.C(Luminance, 0, 1, 0),
        HueShift = Clamps.C(HueShift, -100, 100, 0), SaturationShift = Clamps.C(SaturationShift, -100, 100, 0),
        LuminanceShift = Clamps.C(LuminanceShift, -100, 100, 0), HueRange = Clamps.C(HueRange, 5, 180, 30),
        SaturationRange = Clamps.C(SaturationRange, 0.05, 1, 0.4), LuminanceRange = Clamps.C(LuminanceRange, 0.05, 1, 0.4),
    };

    /// <summary>The kernel's nine values: hue, saturation, luminance (0…1), the three shifts (−1…1), the three reaches.</summary>
    internal float[] Floats() => new[]
    {
        (float)(Hue / 360), (float)Saturation, (float)Luminance, (float)(HueShift / 100), (float)(SaturationShift / 100),
        (float)(LuminanceShift / 100), (float)(HueRange / 360), (float)SaturationRange, (float)LuminanceRange,
    };
}

public sealed record CameraRawGradeWheel(double Hue = 0, double Saturation = 0, double Luminance = 0)
{
    public CameraRawGradeWheel Normalized => new(Clamps.C(Hue, 0, 360, 0), Clamps.C(Saturation, 0, 100, 0), Clamps.C(Luminance, -100, 100, 0));
}

/// <summary>Four color wheels plus how the three tonal wheels overlap and which end they favor. Mirrors CameraRawGradingSettings.</summary>
public sealed record CameraRawGradingSettings
{
    public CameraRawGradeWheel Shadows { get; init; } = new();
    public CameraRawGradeWheel Midtones { get; init; } = new();
    public CameraRawGradeWheel Highlights { get; init; } = new();
    public CameraRawGradeWheel Global { get; init; } = new();
    public double Blending { get; init; } = 50;
    public double Balance { get; init; }

    public bool Adjusts => new[] { Shadows, Midtones, Highlights, Global }.Any(w => w.Saturation != 0 || w.Luminance != 0);

    public CameraRawGradingSettings Normalized => this with
    {
        Shadows = Shadows.Normalized, Midtones = Midtones.Normalized, Highlights = Highlights.Normalized, Global = Global.Normalized,
        Blending = Clamps.C(Blending, 0, 100, 50), Balance = Clamps.C(Balance, -100, 100, 0),
    };
}

/// <summary>Sharpening and manual noise reduction. Amount is 0…150; the rest 0…100. Mirrors CameraRawDetailSettings.</summary>
public sealed record CameraRawDetailSettings
{
    public double SharpenAmount { get; init; }
    public double SharpenRadius { get; init; } = 10;
    public double SharpenDetail { get; init; } = 25;
    public double SharpenMasking { get; init; }
    public double NoiseLuminance { get; init; }
    public double NoiseLuminanceDetail { get; init; } = 50;
    public double NoiseLuminanceContrast { get; init; }
    public double NoiseColor { get; init; }
    public double NoiseColorDetail { get; init; } = 50;
    public double NoiseColorSmoothness { get; init; } = 50;

    public bool Adjusts => SharpenAmount != 0 || NoiseLuminance != 0 || NoiseColor != 0;

    public CameraRawDetailSettings Normalized => this with
    {
        SharpenAmount = Clamps.C(SharpenAmount, 0, 150, 0), SharpenRadius = Clamps.C(SharpenRadius, 0, 100, 10),
        SharpenDetail = Clamps.C(SharpenDetail, 0, 100, 25), SharpenMasking = Clamps.C(SharpenMasking, 0, 100, 0),
        NoiseLuminance = Clamps.C(NoiseLuminance, 0, 100, 0), NoiseLuminanceDetail = Clamps.C(NoiseLuminanceDetail, 0, 100, 50),
        NoiseLuminanceContrast = Clamps.C(NoiseLuminanceContrast, 0, 100, 0), NoiseColor = Clamps.C(NoiseColor, 0, 100, 0),
        NoiseColorDetail = Clamps.C(NoiseColorDetail, 0, 100, 50), NoiseColorSmoothness = Clamps.C(NoiseColorSmoothness, 0, 100, 50),
    };
}

/// <summary>
/// Lens corrections: chromatic aberration, a generic profile, manual distortion, defringe and lens vignetting. A
/// rendered layer has no lens metadata, so the profile only scales generic strengths. Mirrors CameraRawOpticsSettings.
/// </summary>
public sealed record CameraRawOpticsSettings
{
    public bool RemoveChromaticAberration { get; init; }
    public bool EnableLensProfile { get; init; }
    public double ProfileDistortion { get; init; } = 100;
    public double ProfileVignetting { get; init; } = 100;
    public double Distortion { get; init; }
    public double PurpleAmount { get; init; }
    public double PurpleHueLow { get; init; } = 270;
    public double PurpleHueHigh { get; init; } = 310;
    public double GreenAmount { get; init; }
    public double GreenHueLow { get; init; } = 60;
    public double GreenHueHigh { get; init; } = 120;
    public double VignetteAmount { get; init; }
    public double VignetteMidpoint { get; init; } = 50;

    public bool Adjusts => RemoveChromaticAberration || EnableLensProfile || Distortion != 0 || PurpleAmount != 0 || GreenAmount != 0 || VignetteAmount != 0;

    public CameraRawOpticsSettings Normalized
    {
        get
        {
            double pl = Clamps.C(PurpleHueLow, 0, 360, 270), ph = Clamps.C(PurpleHueHigh, 0, 360, 310);
            double gl = Clamps.C(GreenHueLow, 0, 360, 60), gh = Clamps.C(GreenHueHigh, 0, 360, 120);
            return this with
            {
                ProfileDistortion = Clamps.C(ProfileDistortion, 0, 100, 100), ProfileVignetting = Clamps.C(ProfileVignetting, 0, 100, 100),
                Distortion = Clamps.C(Distortion, -100, 100, 0), PurpleAmount = Clamps.C(PurpleAmount, 0, 100, 0),
                GreenAmount = Clamps.C(GreenAmount, 0, 100, 0), VignetteAmount = Clamps.C(VignetteAmount, -100, 100, 0),
                VignetteMidpoint = Clamps.C(VignetteMidpoint, 0, 100, 50),
                PurpleHueLow = Math.Min(pl, ph), PurpleHueHigh = Math.Max(pl, ph), GreenHueLow = Math.Min(gl, gh), GreenHueHigh = Math.Max(gl, gh),
            };
        }
    }

    /// <summary>The Lens Correction filter's strength per unit of distortion.</summary>
    public const double LensStrength = 0.35;

    public double DistortionK => Distortion / 100 * LensStrength + (EnableLensProfile ? ProfileDistortion / 100 * LensStrength : 0);
}

/// <summary>Manual perspective and transform on the pixel grid. Mirrors CameraRawGeometrySettings (Guided upright aside).</summary>
public sealed record CameraRawGeometrySettings
{
    public CameraRawProjection Projection { get; init; } = CameraRawProjection.Perspective;
    public double Vertical { get; init; }
    public double Horizontal { get; init; }
    public double Rotate { get; init; }
    public double Aspect { get; init; }
    public double Scale { get; init; }
    public double OffsetX { get; init; }
    public double OffsetY { get; init; }
    public bool ConstrainCrop { get; init; }
    /// <summary>Guided Upright: lines drawn along what should be level or plumb, in the layer's own pixels as fractions (0…1, y down).</summary>
    public CameraRawGuide[] Guides { get; init; } = Array.Empty<CameraRawGuide>();

    public bool Adjusts => Vertical != 0 || Horizontal != 0 || Rotate != 0 || Aspect != 0 || Scale != 0 || OffsetX != 0 || OffsetY != 0 || Guides.Length > 0;

    /// <summary>
    /// The corrections the guides call for on a <paramref name="width"/> × <paramref name="height"/> picture, added to
    /// the sliders': each level or plumb line straightens the picture (their mean), and two plumb lines that converge
    /// widen the narrower end until they're parallel.
    /// </summary>
    public (double Vertical, double Horizontal, double Rotate) Effective(int width, int height)
    {
        if (Guides.Length == 0 || width <= 0 || height <= 0) return (Vertical, Horizontal, Rotate);
        var tilts = new List<double>();
        var plumb = new List<(double Lean, double MidX)>();
        foreach (var guide in Guides)
        {
            double dx = (guide.EndX - guide.StartX) * width, dy = (guide.EndY - guide.StartY) * height;
            if (Math.Sqrt(dx * dx + dy * dy) < 4) continue;
            if (Math.Abs(dy) > Math.Abs(dx))
            {
                if (dy > 0) { dx = -dx; dy = -dy; } // bottom to top
                double lean = dx / -dy;            // positive: the top leans right
                plumb.Add((lean, (guide.StartX + guide.EndX) / 2));
                tilts.Add(Math.Atan(lean) * 180 / Math.PI);
            }
            else
            {
                if (dx < 0) { dx = -dx; dy = -dy; }
                tilts.Add(Math.Atan(dy / dx) * 180 / Math.PI); // positive: the right end lower
            }
        }
        double rotate = Rotate, vertical = Vertical;
        if (plumb.Count >= 2)
        {
            var ordered = plumb.OrderBy(p => p.MidX).ToList();
            double convergence = ordered[0].Lean - ordered[^1].Lean; // positive: closer together at the top
            vertical = Math.Clamp(Vertical + convergence * height / (2 * width * 0.18) * 100, -100, 100);
            rotate = Math.Clamp(Rotate + ordered.Average(p => Math.Atan(p.Lean) * 180 / Math.PI), -45, 45);
        }
        else if (tilts.Count > 0) rotate = Math.Clamp(Rotate + tilts.Average(), -45, 45);
        return (vertical, Horizontal, rotate);
    }

    public CameraRawGeometrySettings Normalized => this with
    {
        Vertical = Clamps.C(Vertical, -100, 100, 0), Horizontal = Clamps.C(Horizontal, -100, 100, 0), Rotate = Clamps.C(Rotate, -45, 45, 0),
        Aspect = Clamps.C(Aspect, -100, 100, 0), Scale = Clamps.C(Scale, -100, 100, 0),
        OffsetX = Clamps.C(OffsetX, -100, 100, 0), OffsetY = Clamps.C(OffsetY, -100, 100, 0),
    };
}

/// <summary>One Guided Upright line, as fractions of the layer's width and height (y down).</summary>
public sealed record CameraRawGuide(double StartX, double StartY, double EndX, double EndY);

/// <summary>Camera calibration: shadow tint and the primaries' hue and saturation. Mirrors CameraRawCalibrationSettings.</summary>
public sealed record CameraRawCalibrationSettings
{
    /// <summary>Process version 1–6 (6 is current).</summary>
    public int Process { get; init; } = 6;
    public double ShadowTint { get; init; }
    public double RedHue { get; init; }
    public double RedSaturation { get; init; }
    public double GreenHue { get; init; }
    public double GreenSaturation { get; init; }
    public double BlueHue { get; init; }
    public double BlueSaturation { get; init; }

    public bool Adjusts => ShadowTint != 0 || RedHue != 0 || RedSaturation != 0 || GreenHue != 0 || GreenSaturation != 0 || BlueHue != 0 || BlueSaturation != 0;

    public CameraRawCalibrationSettings Normalized => this with
    {
        Process = Math.Clamp(Process, 1, 6), ShadowTint = Clamps.C(ShadowTint, -100, 100, 0),
        RedHue = Clamps.C(RedHue, -100, 100, 0), RedSaturation = Clamps.C(RedSaturation, -100, 100, 0),
        GreenHue = Clamps.C(GreenHue, -100, 100, 0), GreenSaturation = Clamps.C(GreenSaturation, -100, 100, 0),
        BlueHue = Clamps.C(BlueHue, -100, 100, 0), BlueSaturation = Clamps.C(BlueSaturation, -100, 100, 0),
    };
}

/// <summary>The Camera Raw Filter's settings. Defaults leave the image unchanged. Mirrors CameraRawSettings.</summary>
public sealed record CameraRawSettings
{
    public CameraRawWhiteBalance WhiteBalance { get; init; }
    /// <summary>Relative cool-to-warm, −100…100. Positive is warmer.</summary>
    public double Temperature { get; init; }
    /// <summary>Green-to-magenta, −100…100. Positive is magenta.</summary>
    public double Tint { get; init; }
    /// <summary>Stops, −5…5.</summary>
    public double Exposure { get; init; }
    public double Contrast { get; init; }
    public double Highlights { get; init; }
    public double Shadows { get; init; }
    public double Whites { get; init; }
    public double Blacks { get; init; }
    public double Vibrance { get; init; }
    public double Saturation { get; init; }
    public double Texture { get; init; }
    public double Clarity { get; init; }
    public double Dehaze { get; init; }
    public double Glow { get; init; }
    public CameraRawGlowStyle GlowStyle { get; init; }
    public double GlowRange { get; init; }
    public double GlowSpread { get; init; }
    public double GlowWarmth { get; init; }
    public double VignetteAmount { get; init; }
    public CameraRawVignetteStyle VignetteStyle { get; init; }
    public double VignetteMidpoint { get; init; } = 50;
    public double VignetteRoundness { get; init; }
    public double VignetteFeather { get; init; } = 50;
    public double VignetteHighlights { get; init; }
    public double GrainAmount { get; init; }
    public double GrainSize { get; init; } = 25;
    public double GrainRoughness { get; init; } = 50;
    public CameraRawCurveSettings Curve { get; init; } = new();
    public CameraRawMixerSettings Mixer { get; init; } = new();
    public CameraRawGradingSettings Grading { get; init; } = new();
    public CameraRawDetailSettings Detail { get; init; } = new();
    public CameraRawOpticsSettings Optics { get; init; } = new();
    public CameraRawGeometrySettings Geometry { get; init; } = new();
    public CameraRawCalibrationSettings Calibration { get; init; } = new();

    public bool AdjustsLight => Exposure != 0 || Contrast != 0 || Highlights != 0 || Shadows != 0 || Whites != 0 || Blacks != 0;
    public bool AdjustsColor => Temperature != 0 || Tint != 0 || Vibrance != 0 || Saturation != 0;
    public bool AdjustsEffects => Texture != 0 || Clarity != 0 || Dehaze != 0 || Glow != 0 || VignetteAmount != 0 || GrainAmount != 0;

    public bool IsIdentity => !AdjustsLight && !AdjustsColor && !AdjustsEffects && !Curve.Adjusts && !Mixer.Adjusts && !Grading.Adjusts
                              && !Detail.Adjusts && !Optics.Adjusts && !Geometry.Adjusts && !Calibration.Adjusts;

    /// <summary>The grade with hidden sections' amounts at zero and the rest as they are. Mirrors CameraRawSettings.applying.</summary>
    public CameraRawSettings Showing(CameraRawSection hidden)
    {
        if (hidden == CameraRawSection.None) return this;
        var s = this;
        if (hidden.HasFlag(CameraRawSection.Basic))
            s = s with
            {
                Exposure = 0, Contrast = 0, Highlights = 0, Shadows = 0, Whites = 0, Blacks = 0, Temperature = 0, Tint = 0,
                Vibrance = 0, Saturation = 0, Texture = 0, Clarity = 0, Dehaze = 0,
            };
        if (hidden.HasFlag(CameraRawSection.Effects)) s = s with { Glow = 0, VignetteAmount = 0, GrainAmount = 0 };
        if (hidden.HasFlag(CameraRawSection.Curve)) s = s with { Curve = new CameraRawCurveSettings() };
        if (hidden.HasFlag(CameraRawSection.Mixer)) s = s with { Mixer = new CameraRawMixerSettings() };
        if (hidden.HasFlag(CameraRawSection.Grading)) s = s with { Grading = new CameraRawGradingSettings() };
        if (hidden.HasFlag(CameraRawSection.Detail)) s = s with { Detail = new CameraRawDetailSettings() };
        if (hidden.HasFlag(CameraRawSection.Optics)) s = s with { Optics = new CameraRawOpticsSettings() };
        if (hidden.HasFlag(CameraRawSection.Geometry)) s = s with { Geometry = new CameraRawGeometrySettings() };
        if (hidden.HasFlag(CameraRawSection.Calibration)) s = s with { Calibration = new CameraRawCalibrationSettings() };
        return s;
    }

    /// <summary>Whether a section's adjustments change anything (its eye shows only then).</summary>
    public bool Adjusts(CameraRawSection section) => section switch
    {
        CameraRawSection.Basic => AdjustsLight || AdjustsColor || Texture != 0 || Clarity != 0 || Dehaze != 0,
        CameraRawSection.Effects => Glow != 0 || VignetteAmount != 0 || GrainAmount != 0,
        CameraRawSection.Curve => Curve.Adjusts,
        CameraRawSection.Mixer => Mixer.Adjusts,
        CameraRawSection.Grading => Grading.Adjusts,
        CameraRawSection.Detail => Detail.Adjusts,
        CameraRawSection.Optics => Optics.Adjusts,
        CameraRawSection.Geometry => Geometry.Adjusts,
        CameraRawSection.Calibration => Calibration.Adjusts,
        _ => false,
    };

    /// <summary>Camera Raw's 0…100 grain size in the grain kernel's pixel scale.</summary>
    public double GrainKernelSize => 0.5 + GrainSize / 100 * 19.5;

    /// <summary>Effects that reach past a pixel (a blur's or a warp's room): previews made smaller still look the same.</summary>
    public CameraRawSettings Normalized => this with
    {
        Exposure = Clamps.C(Exposure, -5, 5, 0),
        Contrast = Clamps.C(Contrast, -100, 100, 0), Highlights = Clamps.C(Highlights, -100, 100, 0), Shadows = Clamps.C(Shadows, -100, 100, 0),
        Whites = Clamps.C(Whites, -100, 100, 0), Blacks = Clamps.C(Blacks, -100, 100, 0),
        Temperature = Clamps.C(Temperature, -100, 100, 0), Tint = Clamps.C(Tint, -100, 100, 0),
        Vibrance = Clamps.C(Vibrance, -100, 100, 0), Saturation = Clamps.C(Saturation, -100, 100, 0),
        Texture = Clamps.C(Texture, -100, 100, 0), Clarity = Clamps.C(Clarity, -100, 100, 0), Dehaze = Clamps.C(Dehaze, -100, 100, 0),
        Glow = Clamps.C(Glow, 0, 100, 0), GlowRange = Clamps.C(GlowRange, -100, 100, 0), GlowSpread = Clamps.C(GlowSpread, -100, 100, 0),
        GlowWarmth = Clamps.C(GlowWarmth, -100, 100, 0),
        VignetteAmount = Clamps.C(VignetteAmount, -100, 100, 0), VignetteMidpoint = Clamps.C(VignetteMidpoint, 0, 100, 50),
        VignetteRoundness = Clamps.C(VignetteRoundness, -100, 100, 0), VignetteFeather = Clamps.C(VignetteFeather, 0, 100, 50),
        VignetteHighlights = Clamps.C(VignetteHighlights, 0, 100, 0),
        GrainAmount = Clamps.C(GrainAmount, 0, 100, 0), GrainSize = Clamps.C(GrainSize, 0, 100, 25), GrainRoughness = Clamps.C(GrainRoughness, 0, 100, 50),
        Curve = Curve.Normalized, Mixer = Mixer.Normalized, Grading = Grading.Normalized, Detail = Detail.Normalized,
        Optics = Optics.Normalized, Geometry = Geometry.Normalized, Calibration = Calibration.Normalized,
    };
}
