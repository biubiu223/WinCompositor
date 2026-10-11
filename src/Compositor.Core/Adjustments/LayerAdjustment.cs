using System.Text.Json;
using System.Text.Json.Nodes;
using Compositor.Core.Localization;
using Compositor.Core.Model;

namespace Compositor.Core.Adjustments;

/// <summary>Adjustment kinds, with the names the project format stores. Mirrors AdjustmentKind in LayerAdjustment.swift.</summary>
public enum AdjustmentKind
{
    HueSaturation, Levels, Curves, Exposure, GradientMap, Grain, AddNoise, GaussianBlur, MotionBlur, Invert, BlackWhite, ColorBalance,
}

public static class AdjustmentKinds
{
    public static readonly AdjustmentKind[] All = Enum.GetValues<AdjustmentKind>();

    public static string ToName(this AdjustmentKind kind) => kind switch
    {
        AdjustmentKind.HueSaturation => "Hue/Saturation",
        AdjustmentKind.Levels => "Levels",
        AdjustmentKind.Curves => "Curves",
        AdjustmentKind.Exposure => "Exposure",
        AdjustmentKind.GradientMap => "Gradient Map",
        AdjustmentKind.Grain => "Grain",
        AdjustmentKind.AddNoise => "Add Noise",
        AdjustmentKind.GaussianBlur => "Gaussian Blur",
        AdjustmentKind.MotionBlur => "Motion Blur",
        AdjustmentKind.Invert => "Invert",
        AdjustmentKind.BlackWhite => "Black & White",
        _ => "Color Balance",
    };

    public static string ToDisplayName(this AdjustmentKind kind) => kind switch
    {
        AdjustmentKind.HueSaturation => L.T("色相/饱和度", "Hue/Saturation"),
        AdjustmentKind.Levels => L.T("色阶", "Levels"),
        AdjustmentKind.Curves => L.T("曲线", "Curves"),
        AdjustmentKind.Exposure => L.T("曝光度", "Exposure"),
        AdjustmentKind.GradientMap => L.T("渐变映射", "Gradient Map"),
        AdjustmentKind.Grain => L.T("颗粒", "Grain"),
        AdjustmentKind.AddNoise => L.T("添加杂色", "Add Noise"),
        AdjustmentKind.GaussianBlur => L.T("高斯模糊", "Gaussian Blur"),
        AdjustmentKind.MotionBlur => L.T("动感模糊", "Motion Blur"),
        AdjustmentKind.Invert => L.T("反相", "Invert"),
        AdjustmentKind.BlackWhite => L.T("黑白", "Black & White"),
        _ => L.T("色彩平衡", "Color Balance"),
    };

    public static AdjustmentKind? Parse(string? name) => All.Cast<AdjustmentKind?>().FirstOrDefault(k => k!.Value.ToName() == name);

    /// <summary>Invert has nothing to set, so it applies without an editor.</summary>
    public static bool IsEditable(this AdjustmentKind kind) => kind != AdjustmentKind.Invert;

    /// <summary>Kinds that read neighboring pixels, added in format version 9.</summary>
    public static bool SamplesNeighbors(this AdjustmentKind kind) =>
        kind is AdjustmentKind.GaussianBlur or AdjustmentKind.MotionBlur or AdjustmentKind.AddNoise;
}

/// <summary>A straight sRGB color stored with an adjustment, 0–1 per channel.</summary>
public readonly record struct AdjustmentColor(double Red, double Green, double Blue)
{
    public static AdjustmentColor From(PaletteColor c) => new(c.Red, c.Green, c.Blue);
    public PaletteColor ToPalette() => new(Red, Green, Blue);
    public bool IsValid => IsUnit(Red) && IsUnit(Green) && IsUnit(Blue);
    private static bool IsUnit(double v) => double.IsFinite(v) && v >= 0 && v <= 1;
    public AdjustmentColor Clamped => new(Math.Clamp(Red, 0, 1), Math.Clamp(Green, 0, 1), Math.Clamp(Blue, 0, 1));
}

/// <summary>One channel's Levels: input black/white points and gamma, output black/white. Mirrors LevelRange.</summary>
public sealed record LevelRange(double Black = 0, double Gamma = 1, double White = 255, double OutputBlack = 0, double OutputWhite = 255)
{
    public LevelRange Normalized
    {
        get
        {
            double black = Clamp(Black, 0, 254, 0);
            return new LevelRange(black, Clamp(Gamma, 0.1, 9.99, 1), Clamp(White, black + 1, 255, 255),
                                  Clamp(OutputBlack, 0, 255, 0), Clamp(OutputWhite, 0, 255, 255));
        }
    }

    public bool IsIdentity => Normalized == new LevelRange();

    public double Apply(double value)
    {
        var s = Normalized;
        double input = Math.Clamp((value * 255 - s.Black) / (s.White - s.Black), 0, 1);
        return (s.OutputBlack + Math.Pow(input, 1 / s.Gamma) * (s.OutputWhite - s.OutputBlack)) / 255;
    }

    internal static double Clamp(double v, double lo, double hi, double fallback) => double.IsFinite(v) ? Math.Clamp(v, lo, hi) : fallback;
}

public enum LevelsChannel { Rgb, Red, Green, Blue }

/// <summary>Levels: the composite RGB range and one per channel. Mirrors LevelsSettings.</summary>
public sealed class LevelsSettings
{
    public LevelsChannel Channel { get; set; } = LevelsChannel.Rgb;
    public LevelRange[] Ranges { get; set; } = { new(), new(), new(), new() };

    public LevelRange Current
    {
        get => Ranges[(int)Channel];
        set => Ranges[(int)Channel] = value.Normalized;
    }

    public bool IsIdentity => Ranges.All(r => r.IsIdentity);

    /// <summary>The channel's own range, then the composite RGB one.</summary>
    public double Apply(double value, int channel) => Ranges[0].Apply(Ranges[channel].Apply(value));

    public LevelsSettings Clone() => new() { Channel = Channel, Ranges = (LevelRange[])Ranges.Clone() };

    /// <summary>Output (0–1) for each input byte of red, green and blue, 3 × 256.</summary>
    public float[] Tables()
    {
        var tables = new float[768];
        for (int c = 0; c < 3; c++)
            for (int i = 0; i < 256; i++) tables[c * 256 + i] = (float)Apply(i / 255.0, c + 1);
        return tables;
    }
}

public readonly record struct CurvePoint(double X, double Y);

/// <summary>Curves: four point lists (RGB, red, green, blue), monotone cubic between points. Mirrors CurvesSettings.</summary>
public sealed class CurvesSettings
{
    public LevelsChannel Channel { get; set; } = LevelsChannel.Rgb;
    public List<CurvePoint>[] Channels { get; set; } =
    {
        Identity(), Identity(), Identity(), Identity(),
    };

    public static List<CurvePoint> Identity() => new() { new(0, 0), new(255, 255) };

    public bool IsValid => Channels.Length == 4 && Channels.All(points =>
        points.Count is >= 2 and <= 32 && points[0].X == 0 && points[^1].X == 255
        && points.All(p => double.IsFinite(p.X) && double.IsFinite(p.Y) && p.X is >= 0 and <= 255 && p.Y is >= 0 and <= 255)
        && points.Zip(points.Skip(1)).All(pair => pair.First.X < pair.Second.X));

    public bool IsIdentity => Channels.All(points => points.All(p => p.X == p.Y) && points.Count >= 2);

    public CurvesSettings Clone() => new() { Channel = Channel, Channels = Channels.Select(c => new List<CurvePoint>(c)).ToArray() };

    /// <summary>Shape-preserving cubic Hermite interpolation, which never overshoots between handles.</summary>
    public double Value(double x, int channel)
    {
        var p = Channels[channel];
        int i = 0;
        for (int k = 0; k < p.Count; k++) if (p[k].X <= x) i = k;
        i = Math.Clamp(i, 0, p.Count - 2);
        var d = new double[p.Count - 1];
        for (int k = 0; k < d.Length; k++) d[k] = (p[k + 1].Y - p[k].Y) / (p[k + 1].X - p[k].X);
        double Slope(int j)
        {
            if (j == 0) return d[0];
            if (j == p.Count - 1) return d[^1];
            if (d[j - 1] * d[j] <= 0) return 0;
            return 2 / (1 / d[j - 1] + 1 / d[j]);
        }
        double h = p[i + 1].X - p[i].X, t = Math.Clamp((x - p[i].X) / h, 0, 1);
        double y = (2 * t * t * t - 3 * t * t + 1) * p[i].Y + (t * t * t - 2 * t * t + t) * h * Slope(i)
                   + (-2 * t * t * t + 3 * t * t) * p[i + 1].Y + (t * t * t - t * t) * h * Slope(i + 1);
        return Math.Clamp(y, 0, 255);
    }

    /// <summary>Each channel's curve, then the RGB curve, as 3 × 256 lookup tables (0–1).</summary>
    public float[] Tables()
    {
        var tables = new float[768];
        for (int c = 1; c <= 3; c++)
            for (int i = 0; i < 256; i++) tables[(c - 1) * 256 + i] = (float)(Value(Value(i, c), 0) / 255);
        return tables;
    }
}

/// <summary>Photoshop's Exposure: stops of linear light, an offset and gamma. Mirrors ExposureSettings.</summary>
public sealed record ExposureSettings(double Exposure = 0, double Offset = 0, double Gamma = 1)
{
    public bool IsValid => Exposure is >= -20 and <= 20 && Offset is >= -0.5 and <= 0.5 && Gamma is >= 0.01 and <= 9.99;
    public bool IsIdentity => Exposure == 0 && Offset == 0 && Gamma == 1;

    public float[] Tables()
    {
        double scale = Math.Pow(2, Exposure);
        var table = new float[768];
        for (int index = 0; index < 256; index++)
        {
            double encoded = index / 255.0;
            double linear = encoded <= 0.04045 ? encoded / 12.92 : Math.Pow((encoded + 0.055) / 1.055, 2.4);
            linear = Math.Pow(Math.Max(0, linear * scale + Offset), 1 / Gamma);
            double output = linear <= 0.0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
            float v = (float)Math.Clamp(output, 0, 1);
            table[index] = table[256 + index] = table[512 + index] = v;
        }
        return table;
    }
}

/// <summary>Gradient Map: brightness picks a color between shadows and highlights. Mirrors GradientMapSettings.</summary>
public sealed record GradientMapSettings(AdjustmentColor Shadows, AdjustmentColor Highlights, bool Reversed = false)
{
    public GradientMapSettings() : this(new AdjustmentColor(0, 0, 0), new AdjustmentColor(1, 1, 1)) { }
    public bool IsValid => Shadows.IsValid && Highlights.IsValid;
    public (AdjustmentColor Dark, AdjustmentColor Light) Ends => Reversed ? (Highlights, Shadows) : (Shadows, Highlights);

    public byte[] Table()
    {
        var (dark, light) = Ends;
        var table = new byte[768];
        static byte Channel(double from, double to, double t) => (byte)Math.Clamp(Math.Round((from + (to - from) * t) * 255, MidpointRounding.AwayFromZero), 0, 255);
        for (int i = 0; i < 256; i++)
        {
            double t = i / 255.0;
            table[i * 3] = Channel(dark.Red, light.Red, t);
            table[i * 3 + 1] = Channel(dark.Green, light.Green, t);
            table[i * 3 + 2] = Channel(dark.Blue, light.Blue, t);
        }
        return table;
    }
}

/// <summary>Film grain: brightness noise, strongest in the midtones, fixed in document space. Mirrors GrainSettings.</summary>
public sealed record GrainSettings(double Amount = 25, double Size = 1.5, double Roughness = 50, uint Seed = 0)
{
    public bool IsValid => Amount is >= 0 and <= 100 && Size is >= 0.5 and <= 20 && Roughness is >= 0 and <= 100;
}

/// <summary>Black & White with Photoshop's six color-family weights and an optional tint. Mirrors BlackWhiteSettings.</summary>
public sealed record BlackWhiteSettings(double Reds = 40, double Yellows = 60, double Greens = 40, double Cyans = 60,
                                        double Blues = 20, double Magentas = 80, bool Tint = false, double TintHue = 40,
                                        double TintSaturation = 20)
{
    public bool IsValid => new[] { Reds, Yellows, Greens, Cyans, Blues, Magentas }.All(v => double.IsFinite(v) && v is >= -200 and <= 300)
                           && TintHue is >= 0 and <= 360 && TintSaturation is >= 0 and <= 100;
    public float[] Weights => new[] { Reds, Yellows, Greens, Cyans, Blues, Magentas }.Select(v => (float)(v / 100)).ToArray();
}

/// <summary>Color Balance per tonal range, with Preserve Luminosity. Mirrors ColorBalanceSettings.</summary>
public sealed record ColorBalanceSettings(
    double ShadowCyanRed = 0, double ShadowMagentaGreen = 0, double ShadowYellowBlue = 0,
    double MidCyanRed = 0, double MidMagentaGreen = 0, double MidYellowBlue = 0,
    double HighlightCyanRed = 0, double HighlightMagentaGreen = 0, double HighlightYellowBlue = 0,
    bool PreserveLuminosity = true)
{
    private double[] AllValues => new[] { ShadowCyanRed, ShadowMagentaGreen, ShadowYellowBlue, MidCyanRed, MidMagentaGreen, MidYellowBlue,
                                          HighlightCyanRed, HighlightMagentaGreen, HighlightYellowBlue };
    public bool IsValid => AllValues.All(v => double.IsFinite(v) && v is >= -100 and <= 100);
    public bool IsIdentity => AllValues.All(v => v == 0);
}

// MARK: Hue/Saturation

public enum ColorRange { Master, Reds, Yellows, Greens, Cyans, Blues, Magentas }

public static class ColorRanges
{
    public static readonly ColorRange[] All = Enum.GetValues<ColorRange>();
    public static readonly ColorRange[] Colors = All.Where(r => r != ColorRange.Master).ToArray();

    public static string ToName(this ColorRange r) => r.ToString();
    public static ColorRange? Parse(string? name) => Enum.TryParse<ColorRange>(name, out var r) ? r : null;

    public static string ToDisplayName(this ColorRange r) => r switch
    {
        ColorRange.Master => L.T("全图", "Master"),
        ColorRange.Reds => L.T("红色", "Reds"),
        ColorRange.Yellows => L.T("黄色", "Yellows"),
        ColorRange.Greens => L.T("绿色", "Greens"),
        ColorRange.Cyans => L.T("青色", "Cyans"),
        ColorRange.Blues => L.T("蓝色", "Blues"),
        _ => L.T("洋红", "Magentas"),
    };

    public static HueBand DefaultBand(this ColorRange r) => r switch
    {
        ColorRange.Master => new HueBand(0, 0, 360, 360),
        ColorRange.Reds => new HueBand(315, 345, 15, 45),
        ColorRange.Yellows => new HueBand(15, 45, 75, 105),
        ColorRange.Greens => new HueBand(75, 105, 135, 165),
        ColorRange.Cyans => new HueBand(135, 165, 195, 225),
        ColorRange.Blues => new HueBand(195, 225, 255, 285),
        _ => new HueBand(255, 285, 315, 345),
    };
}

/// <summary>A hue band in degrees, wrapping at 360: full strength from RangeStart to RangeEnd, fading to the falloffs. Mirrors HueBand.</summary>
public readonly record struct HueBand(double FalloffStart, double RangeStart, double RangeEnd, double FalloffEnd)
{
    public static double Forward(double from, double to)
    {
        double delta = (to - from) % 360;
        return delta < 0 ? delta + 360 : delta;
    }

    public double Weight(double hue)
    {
        double span = Forward(FalloffStart, FalloffEnd);
        if (span <= 0) return 1;
        double position = Forward(FalloffStart, hue);
        if (position > span) return 0;
        double rampIn = Forward(FalloffStart, RangeStart);
        double plateauEnd = Forward(FalloffStart, RangeEnd);
        if (position < rampIn) return rampIn > 0 ? position / rampIn : 1;
        if (position <= plateauEnd) return 1;
        double rampOut = span - plateauEnd;
        return rampOut > 0 ? (span - position) / rampOut : 1;
    }

    private static double Wrap(double v) { double r = v % 360; return r < 0 ? r + 360 : r; }

    public HueBand CenteredOn(double hue)
    {
        double core = Forward(RangeStart, RangeEnd), leading = Forward(FalloffStart, RangeStart), trailing = Forward(RangeEnd, FalloffEnd);
        double start = Wrap(hue - core / 2);
        return new HueBand(Wrap(start - leading), start, Wrap(start + core), Wrap(start + core + trailing));
    }

    public HueBand Including(double hue)
    {
        if (Weight(hue) >= 1) return this;
        double shoulderIn = Forward(FalloffStart, RangeStart), shoulderOut = Forward(RangeEnd, FalloffEnd);
        var result = Forward(hue, RangeStart) <= Forward(RangeEnd, hue)
            ? this with { RangeStart = hue, FalloffStart = hue - shoulderIn }
            : this with { RangeEnd = hue, FalloffEnd = hue + shoulderOut };
        return result.Normalized();
    }

    public HueBand Excluding(double hue)
    {
        if (Weight(hue) <= 0) return this;
        double shoulderIn = Forward(FalloffStart, RangeStart), shoulderOut = Forward(RangeEnd, FalloffEnd);
        var result = Forward(FalloffStart, hue) <= Forward(hue, FalloffEnd)
            ? this with { FalloffStart = hue + 1, RangeStart = hue + 1 + shoulderIn }
            : this with { FalloffEnd = hue - 1, RangeEnd = hue - 1 - shoulderOut };
        return result.Normalized();
    }

    private HueBand Normalized()
    {
        var r = new HueBand(Wrap(FalloffStart), Wrap(RangeStart), Wrap(RangeEnd), Wrap(FalloffEnd));
        if (Forward(r.FalloffStart, r.FalloffEnd) > 350) r = r with { FalloffEnd = Wrap(r.FalloffStart + 350) };
        return r;
    }

    public double Handle(int index) => index switch { 0 => FalloffStart, 1 => RangeStart, 2 => RangeEnd, _ => FalloffEnd };

    /// <summary>Moves one handle, keeping the four in order and the band under a full circle; unchanged if it can't.</summary>
    public HueBand WithHandle(int index, double degrees)
    {
        double value = ((degrees % 360) + 360) % 360;
        var updated = index switch
        {
            0 => this with { FalloffStart = value },
            1 => this with { RangeStart = value },
            2 => this with { RangeEnd = value },
            _ => this with { FalloffEnd = value },
        };
        double span = Forward(updated.FalloffStart, updated.FalloffEnd);
        double toStart = Forward(updated.FalloffStart, updated.RangeStart), toEnd = Forward(updated.FalloffStart, updated.RangeEnd);
        return span > 1 && span <= 350 && toStart <= toEnd && toEnd <= span ? updated : this;
    }
}

public readonly record struct RangeAdjustment(double Hue = 0, double Saturation = 0, double Lightness = 0)
{
    public bool IsZero => Hue == 0 && Saturation == 0 && Lightness == 0;
}

/// <summary>Hue/Saturation with per-range values and adjustable bands. Mirrors HueSaturationSettings.</summary>
public sealed class HueSaturationSettings
{
    public ColorRange Range { get; set; } = ColorRange.Master;
    public bool Colorize { get; set; }
    public bool InvertRange { get; set; }
    public Dictionary<ColorRange, RangeAdjustment> Adjustments { get; set; } = new();
    public Dictionary<ColorRange, HueBand> Bands { get; set; } = ColorRanges.All.ToDictionary(r => r, r => r.DefaultBand());

    public HueSaturationSettings() { }

    public HueSaturationSettings(double hue, double saturation, double lightness, bool colorize, ColorRange range = ColorRange.Master)
    {
        Range = range;
        Colorize = colorize;
        Adjustments[range] = new RangeAdjustment(hue, saturation, lightness);
    }

    public RangeAdjustment Current
    {
        get => Adjustments.TryGetValue(Range, out var a) ? a : default;
        set => Adjustments[Range] = value;
    }

    public HueBand Band
    {
        get => Bands.TryGetValue(Range, out var b) ? b : Range.DefaultBand();
        set => Bands[Range] = value;
    }

    public bool IsIdentity => !Colorize && Adjustments.Values.All(a => a.IsZero);

    public double Weight(ColorRange colorRange, double hue)
    {
        if (colorRange == ColorRange.Master) return 1;
        double weight = (Bands.TryGetValue(colorRange, out var b) ? b : colorRange.DefaultBand()).Weight(hue);
        return InvertRange && colorRange == Range ? 1 - weight : weight;
    }

    public HueSaturationSettings Clone() => new()
    {
        Range = Range, Colorize = Colorize, InvertRange = InvertRange,
        Adjustments = new Dictionary<ColorRange, RangeAdjustment>(Adjustments),
        Bands = new Dictionary<ColorRange, HueBand>(Bands),
    };

    /// <summary>A key that changes whenever the result would (for caching the color cube).</summary>
    public string Key()
    {
        var parts = new List<string> { Colorize.ToString(), InvertRange.ToString(), Range.ToString() };
        foreach (var (k, v) in Adjustments.OrderBy(p => p.Key)) parts.Add($"{k}:{v.Hue},{v.Saturation},{v.Lightness}");
        foreach (var (k, v) in Bands.OrderBy(p => p.Key)) parts.Add($"{k}:{v.FalloffStart},{v.RangeStart},{v.RangeEnd},{v.FalloffEnd}");
        return string.Join("|", parts);
    }
}

/// <summary>
/// An adjustment layer's settings: its kind and every kind's settings (as the Mac app stores them, so a layer can
/// change kind and the format stays the same). Treated as immutable once in a document: edits work on a clone.
/// Mirrors LayerAdjustment.swift.
/// </summary>
public sealed class LayerAdjustment
{
    public AdjustmentKind Kind { get; set; }
    public double Hue { get; set; }
    public double Saturation { get; set; }
    public double Lightness { get; set; }
    public bool Colorize { get; set; }
    public HueSaturationSettings? HsvSettings { get; set; }
    public LevelsSettings Levels { get; set; } = new();
    public CurvesSettings Curves { get; set; } = new();
    public ExposureSettings? ExposureSettings { get; set; }
    public GradientMapSettings? GradientMapSettings { get; set; }
    public GrainSettings? GrainSettings { get; set; }
    public BlackWhiteSettings? BlackWhiteSettings { get; set; }
    public ColorBalanceSettings? ColorBalanceSettings { get; set; }
    public double? BlurRadius { get; set; }
    public double? MotionAngle { get; set; }
    public double? MotionDistance { get; set; }
    public double? NoiseAmount { get; set; }
    public bool? NoiseGaussian { get; set; }
    public bool? NoiseMonochromatic { get; set; }
    public uint? NoiseSeed { get; set; }

    public LayerAdjustment(AdjustmentKind kind) => Kind = kind;

    public HueSaturationSettings ResolvedHsv => HsvSettings ?? new HueSaturationSettings(Hue, Saturation, Lightness, Colorize);
    public ExposureSettings Exposure => ExposureSettings ?? new ExposureSettings();
    public GradientMapSettings GradientMap => GradientMapSettings ?? new GradientMapSettings();
    public GrainSettings Grain => GrainSettings ?? new GrainSettings();
    public BlackWhiteSettings BlackWhite => BlackWhiteSettings ?? new BlackWhiteSettings();
    public ColorBalanceSettings ColorBalance => ColorBalanceSettings ?? new ColorBalanceSettings();
    public double GaussianRadius => BlurRadius ?? 10;
    public double ResolvedMotionAngle => MotionAngle ?? 0;
    public double ResolvedMotionDistance => MotionDistance ?? 10;
    public double ResolvedNoiseAmount => NoiseAmount ?? 10;
    public bool ResolvedNoiseGaussian => NoiseGaussian ?? false;
    public bool ResolvedNoiseMonochromatic => NoiseMonochromatic ?? false;
    public uint ResolvedNoiseSeed => NoiseSeed ?? 0;

    /// <summary>Document pixels a partial redraw must read beyond what it shows.</summary>
    public double SamplingMargin => Kind switch
    {
        AdjustmentKind.GaussianBlur => GaussianRadius * 3 + 2,
        AdjustmentKind.MotionBlur => ResolvedMotionDistance / 2 + 2,
        _ => 0,
    };

    public bool IsValid =>
        double.IsFinite(Hue) && double.IsFinite(Saturation) && double.IsFinite(Lightness)
        && Math.Abs(Hue) <= 360 && Math.Abs(Saturation) <= 100 && Math.Abs(Lightness) <= 100
        && ResolvedHsv.Adjustments.Values.All(a => double.IsFinite(a.Hue) && Math.Abs(a.Hue) <= 360 && Math.Abs(a.Saturation) <= 100 && Math.Abs(a.Lightness) <= 100)
        && Levels.Ranges.Length == 4 && Curves.IsValid && Exposure.IsValid && GradientMap.IsValid && Grain.IsValid
        && BlackWhite.IsValid && ColorBalance.IsValid
        && GaussianRadius is >= 0.1 and <= 250 && ResolvedMotionAngle is >= -90 and <= 90
        && ResolvedMotionDistance is >= 1 and <= 2000 && ResolvedNoiseAmount is >= 0.1 and <= 400;

    public LayerAdjustment Clone() => new(Kind)
    {
        Hue = Hue, Saturation = Saturation, Lightness = Lightness, Colorize = Colorize,
        HsvSettings = HsvSettings?.Clone(), Levels = Levels.Clone(), Curves = Curves.Clone(),
        ExposureSettings = ExposureSettings, GradientMapSettings = GradientMapSettings, GrainSettings = GrainSettings,
        BlackWhiteSettings = BlackWhiteSettings, ColorBalanceSettings = ColorBalanceSettings,
        BlurRadius = BlurRadius, MotionAngle = MotionAngle, MotionDistance = MotionDistance,
        NoiseAmount = NoiseAmount, NoiseGaussian = NoiseGaussian, NoiseMonochromatic = NoiseMonochromatic, NoiseSeed = NoiseSeed,
    };

    /// <summary>Whether running it would change nothing (so it can be skipped).</summary>
    public bool IsIdentity => Kind switch
    {
        AdjustmentKind.HueSaturation => ResolvedHsv.IsIdentity,
        AdjustmentKind.Levels => Levels.IsIdentity,
        AdjustmentKind.Curves => Curves.IsIdentity,
        AdjustmentKind.Exposure => Exposure.IsIdentity,
        AdjustmentKind.Grain => Grain.Amount <= 0,
        AdjustmentKind.ColorBalance => ColorBalance.IsIdentity,
        _ => false,
    };

    // MARK: JSON, exactly as the Mac app's Codable writes and reads it

    public static LayerAdjustment? FromJson(JsonNode? node)
    {
        if (node is not JsonObject o || AdjustmentKinds.Parse(Json.Str(o, "kind")) is not { } kind) return null;
        var a = new LayerAdjustment(kind)
        {
            Hue = Json.Num(o, "hue") ?? 0,
            Saturation = Json.Num(o, "saturation") ?? 0,
            Lightness = Json.Num(o, "lightness") ?? 0,
            Colorize = Json.Bool(o, "colorize") ?? false,
            BlurRadius = Json.Num(o, "blurRadius"),
            MotionAngle = Json.Num(o, "motionAngle"),
            MotionDistance = Json.Num(o, "motionDistance"),
            NoiseAmount = Json.Num(o, "noiseAmount"),
            NoiseGaussian = Json.Bool(o, "noiseGaussian"),
            NoiseMonochromatic = Json.Bool(o, "noiseMonochromatic"),
            NoiseSeed = Json.Num(o, "noiseSeed") is { } seed ? (uint)Math.Clamp(seed, 0, uint.MaxValue) : null,
        };
        if (o["levels"] is JsonObject levels)
        {
            a.Levels.Channel = ParseChannel(Json.Str(levels, "channel"));
            if (levels["ranges"] is JsonArray ranges && ranges.Count == 4)
                a.Levels.Ranges = ranges.Select(r => r is JsonObject ro ? new LevelRange(
                    Json.Num(ro, "black") ?? 0, Json.Num(ro, "gamma") ?? 1, Json.Num(ro, "white") ?? 255,
                    Json.Num(ro, "outputBlack") ?? 0, Json.Num(ro, "outputWhite") ?? 255) : new LevelRange()).ToArray();
        }
        if (o["curves"] is JsonObject curves)
        {
            a.Curves.Channel = ParseChannel(Json.Str(curves, "channel"));
            if (curves["channels"] is JsonArray channels && channels.Count == 4)
                a.Curves.Channels = channels.Select(c => c is JsonArray points
                    ? points.OfType<JsonObject>().Select(p => new CurvePoint(Json.Num(p, "x") ?? 0, Json.Num(p, "y") ?? 0)).ToList()
                    : CurvesSettings.Identity()).ToArray();
        }
        if (o["hsvSettings"] is JsonObject hsv)
        {
            var settings = new HueSaturationSettings
            {
                Range = ColorRanges.Parse(Json.Str(hsv, "range")) ?? ColorRange.Master,
                Colorize = Json.Bool(hsv, "colorize") ?? false,
                InvertRange = Json.Bool(hsv, "invertRange") ?? false,
            };
            foreach (var (key, value) in Json.Pairs(hsv["adjustments"]))
                if (ColorRanges.Parse(key) is { } range && value is JsonObject v)
                    settings.Adjustments[range] = new RangeAdjustment(Json.Num(v, "hue") ?? 0, Json.Num(v, "saturation") ?? 0, Json.Num(v, "lightness") ?? 0);
            foreach (var (key, value) in Json.Pairs(hsv["bands"]))
                if (ColorRanges.Parse(key) is { } range && value is JsonObject v)
                    settings.Bands[range] = new HueBand(Json.Num(v, "falloffStart") ?? 0, Json.Num(v, "rangeStart") ?? 0,
                                                        Json.Num(v, "rangeEnd") ?? 0, Json.Num(v, "falloffEnd") ?? 0);
            a.HsvSettings = settings;
        }
        if (o["exposureSettings"] is JsonObject e)
            a.ExposureSettings = new ExposureSettings(Json.Num(e, "exposure") ?? 0, Json.Num(e, "offset") ?? 0, Json.Num(e, "gamma") ?? 1);
        if (o["gradientMapSettings"] is JsonObject g)
            a.GradientMapSettings = new GradientMapSettings(Color(g["shadows"], new(0, 0, 0)), Color(g["highlights"], new(1, 1, 1)), Json.Bool(g, "reversed") ?? false);
        if (o["grainSettings"] is JsonObject gr)
            a.GrainSettings = new GrainSettings(Json.Num(gr, "amount") ?? 25, Json.Num(gr, "size") ?? 1.5, Json.Num(gr, "roughness") ?? 50,
                                                (uint)Math.Clamp(Json.Num(gr, "seed") ?? 0, 0, uint.MaxValue));
        if (o["blackWhiteSettings"] is JsonObject bw)
            a.BlackWhiteSettings = new BlackWhiteSettings(Json.Num(bw, "reds") ?? 40, Json.Num(bw, "yellows") ?? 60, Json.Num(bw, "greens") ?? 40,
                Json.Num(bw, "cyans") ?? 60, Json.Num(bw, "blues") ?? 20, Json.Num(bw, "magentas") ?? 80, Json.Bool(bw, "tint") ?? false,
                Json.Num(bw, "tintHue") ?? 40, Json.Num(bw, "tintSaturation") ?? 20);
        if (o["colorBalanceSettings"] is JsonObject cb)
            a.ColorBalanceSettings = new ColorBalanceSettings(
                Json.Num(cb, "shadowCyanRed") ?? 0, Json.Num(cb, "shadowMagentaGreen") ?? 0, Json.Num(cb, "shadowYellowBlue") ?? 0,
                Json.Num(cb, "midCyanRed") ?? 0, Json.Num(cb, "midMagentaGreen") ?? 0, Json.Num(cb, "midYellowBlue") ?? 0,
                Json.Num(cb, "highlightCyanRed") ?? 0, Json.Num(cb, "highlightMagentaGreen") ?? 0, Json.Num(cb, "highlightYellowBlue") ?? 0,
                Json.Bool(cb, "preserveLuminosity") ?? true);
        return a;
    }

    private static LevelsChannel ParseChannel(string? name) => name switch
    {
        "Red" => LevelsChannel.Red,
        "Green" => LevelsChannel.Green,
        "Blue" => LevelsChannel.Blue,
        _ => LevelsChannel.Rgb,
    };

    private static string ChannelName(LevelsChannel c) => c switch
    {
        LevelsChannel.Red => "Red",
        LevelsChannel.Green => "Green",
        LevelsChannel.Blue => "Blue",
        _ => "RGB",
    };

    private static AdjustmentColor Color(JsonNode? node, AdjustmentColor fallback) =>
        node is JsonObject c ? new AdjustmentColor(Json.Num(c, "red") ?? fallback.Red, Json.Num(c, "green") ?? fallback.Green, Json.Num(c, "blue") ?? fallback.Blue) : fallback;

    private static JsonObject ColorJson(AdjustmentColor c) => new() { ["blue"] = c.Blue, ["green"] = c.Green, ["red"] = c.Red };

    /// <summary>Every key the Mac app's decoder requires, plus the optional ones that are set.</summary>
    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["kind"] = Kind.ToName(),
            ["hue"] = Hue,
            ["saturation"] = Saturation,
            ["lightness"] = Lightness,
            ["colorize"] = Colorize,
            ["levels"] = new JsonObject
            {
                ["channel"] = ChannelName(Levels.Channel),
                ["ranges"] = new JsonArray(Levels.Ranges.Select(r => (JsonNode)new JsonObject
                {
                    ["black"] = r.Black, ["gamma"] = r.Gamma, ["outputBlack"] = r.OutputBlack, ["outputWhite"] = r.OutputWhite, ["white"] = r.White,
                }).ToArray()),
            },
            ["curves"] = new JsonObject
            {
                ["channel"] = ChannelName(Curves.Channel),
                ["channels"] = new JsonArray(Curves.Channels.Select(c => (JsonNode)new JsonArray(
                    c.Select(p => (JsonNode)new JsonObject { ["x"] = p.X, ["y"] = p.Y }).ToArray())).ToArray()),
            },
        };
        if (HsvSettings is { } hsv)
        {
            var adjustments = new JsonArray();
            foreach (var (range, value) in hsv.Adjustments)
            {
                adjustments.Add(range.ToName());
                adjustments.Add(new JsonObject { ["hue"] = value.Hue, ["lightness"] = value.Lightness, ["saturation"] = value.Saturation });
            }
            var bands = new JsonArray();
            foreach (var (range, band) in hsv.Bands)
            {
                bands.Add(range.ToName());
                bands.Add(new JsonObject
                {
                    ["falloffEnd"] = band.FalloffEnd, ["falloffStart"] = band.FalloffStart, ["rangeEnd"] = band.RangeEnd, ["rangeStart"] = band.RangeStart,
                });
            }
            o["hsvSettings"] = new JsonObject
            {
                ["adjustments"] = adjustments, ["bands"] = bands, ["colorize"] = hsv.Colorize, ["invertRange"] = hsv.InvertRange, ["range"] = hsv.Range.ToName(),
            };
        }
        if (ExposureSettings is { } e) o["exposureSettings"] = new JsonObject { ["exposure"] = e.Exposure, ["gamma"] = e.Gamma, ["offset"] = e.Offset };
        if (GradientMapSettings is { } g) o["gradientMapSettings"] = new JsonObject { ["highlights"] = ColorJson(g.Highlights), ["reversed"] = g.Reversed, ["shadows"] = ColorJson(g.Shadows) };
        if (GrainSettings is { } gr) o["grainSettings"] = new JsonObject { ["amount"] = gr.Amount, ["roughness"] = gr.Roughness, ["seed"] = gr.Seed, ["size"] = gr.Size };
        if (BlackWhiteSettings is { } bw)
            o["blackWhiteSettings"] = new JsonObject
            {
                ["blues"] = bw.Blues, ["cyans"] = bw.Cyans, ["greens"] = bw.Greens, ["magentas"] = bw.Magentas, ["reds"] = bw.Reds,
                ["tint"] = bw.Tint, ["tintHue"] = bw.TintHue, ["tintSaturation"] = bw.TintSaturation, ["yellows"] = bw.Yellows,
            };
        if (ColorBalanceSettings is { } cb)
            o["colorBalanceSettings"] = new JsonObject
            {
                ["highlightCyanRed"] = cb.HighlightCyanRed, ["highlightMagentaGreen"] = cb.HighlightMagentaGreen, ["highlightYellowBlue"] = cb.HighlightYellowBlue,
                ["midCyanRed"] = cb.MidCyanRed, ["midMagentaGreen"] = cb.MidMagentaGreen, ["midYellowBlue"] = cb.MidYellowBlue,
                ["preserveLuminosity"] = cb.PreserveLuminosity,
                ["shadowCyanRed"] = cb.ShadowCyanRed, ["shadowMagentaGreen"] = cb.ShadowMagentaGreen, ["shadowYellowBlue"] = cb.ShadowYellowBlue,
            };
        if (BlurRadius is { } radius) o["blurRadius"] = radius;
        if (MotionAngle is { } angle) o["motionAngle"] = angle;
        if (MotionDistance is { } distance) o["motionDistance"] = distance;
        if (NoiseAmount is { } amount) o["noiseAmount"] = amount;
        if (NoiseGaussian is { } gaussian) o["noiseGaussian"] = gaussian;
        if (NoiseMonochromatic is { } mono) o["noiseMonochromatic"] = mono;
        if (NoiseSeed is { } seed) o["noiseSeed"] = seed;
        return o;
    }
}

/// <summary>Lenient readers for JSON written by the Mac app or by hand.</summary>
internal static class Json
{
    public static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    public static bool? Bool(JsonObject o, string key) => o[key] is JsonValue v ? v.GetValueKind() switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    } : null;

    public static double? Num(JsonObject o, string key) => NumOf(o[key]);

    public static double? NumOf(JsonNode? node)
    {
        if (node is not JsonValue v || v.GetValueKind() != JsonValueKind.Number) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<uint>(out var u)) return u;
        if (v.TryGetValue<float>(out var f)) return f;
        if (v.TryGetValue<decimal>(out var m)) return (double)m;
        return null;
    }

    /// <summary>
    /// A dictionary as Swift's Codable writes one with non-String keys — an array alternating key and value — or as
    /// an ordinary object.
    /// </summary>
    public static IEnumerable<(string Key, JsonNode? Value)> Pairs(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            for (int i = 0; i + 1 < array.Count; i += 2)
                if (array[i] is JsonValue k && k.GetValueKind() == JsonValueKind.String) yield return (k.GetValue<string>(), array[i + 1]);
        }
        else if (node is JsonObject obj)
        {
            foreach (var (k, v) in obj) yield return (k, v);
        }
    }
}
