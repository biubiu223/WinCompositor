using Compositor.Core.Adjustments;
using Compositor.Core.Localization;

namespace Compositor.Core.Filters;

/// <summary>The Filter menu's own effects (the blurs and noise are adjustments; see <see cref="AdjustmentKind"/>).</summary>
public enum FilterKind { Vignette, BloomGlow, TonalContrast, LensCorrection, Dither, Scanlines, ContentAwareFill, CameraRaw, RemoveBackground }

public static class FilterKinds
{
    public static string ToName(this FilterKind kind) => kind switch
    {
        FilterKind.Vignette => "Vignette",
        FilterKind.BloomGlow => "Bloom / Glow",
        FilterKind.TonalContrast => "Tonal Contrast",
        FilterKind.LensCorrection => "Lens Correction",
        FilterKind.Dither => "Dither",
        FilterKind.Scanlines => "Scanlines",
        FilterKind.CameraRaw => "Camera Raw Filter",
        FilterKind.RemoveBackground => "Remove Background",
        _ => "Content-Aware Fill",
    };

    public static string ToDisplayName(this FilterKind kind) => kind switch
    {
        FilterKind.Vignette => L.T("暗角", "Vignette"),
        FilterKind.BloomGlow => L.T("泛光", "Bloom / Glow"),
        FilterKind.TonalContrast => L.T("色调对比", "Tonal Contrast"),
        FilterKind.LensCorrection => L.T("镜头校正", "Lens Correction"),
        FilterKind.Dither => L.T("仿色", "Dither"),
        FilterKind.Scanlines => L.T("扫描线", "Scanlines"),
        FilterKind.CameraRaw => L.T("Camera Raw 滤镜", "Camera Raw Filter"),
        FilterKind.RemoveBackground => L.T("移除背景", "Remove Background"),
        _ => L.T("内容识别填充", "Content-Aware Fill"),
    };

    public static FilterKind? Parse(string? name) => Enum.GetValues<FilterKind>().Cast<FilterKind?>().FirstOrDefault(k => k!.Value.ToName() == name);

    /// <summary>Content-Aware Fill fills a selection rather than reworking the layer, and has nothing to set.</summary>
    public static bool IsAutomatic(this FilterKind kind) => kind == FilterKind.ContentAwareFill;

    /// <summary>Filters whose look depends on single pixels: previewed at full size, never on a smaller copy.</summary>
    public static bool PreviewsFullSize(this FilterKind kind) => kind is FilterKind.Dither or FilterKind.Scanlines or FilterKind.ContentAwareFill;
}

public enum DitherStyle { Atkinson, FloydSteinberg, Bayer2, Bayer4, Bayer8, Dots, Lines, Diamonds, Patterns, Ascii }

public enum DitherColors { BlackWhite, TwoColors, Original }

public enum DitherPixelShape { Square, Dot }

public static class DitherNames
{
    public static string ToDisplayName(this DitherStyle s) => s switch
    {
        DitherStyle.Atkinson => L.T("Atkinson（经典 Mac）", "Atkinson (Classic Mac)"),
        DitherStyle.FloydSteinberg => "Floyd–Steinberg",
        DitherStyle.Bayer2 => "Bayer 2 × 2",
        DitherStyle.Bayer4 => "Bayer 4 × 4",
        DitherStyle.Bayer8 => "Bayer 8 × 8",
        DitherStyle.Dots => L.T("半调网点", "Halftone Dots"),
        DitherStyle.Lines => L.T("半调线条", "Halftone Lines"),
        DitherStyle.Diamonds => L.T("半调菱形", "Halftone Diamonds"),
        DitherStyle.Patterns => L.T("Mac 图案", "Mac Patterns"),
        _ => L.T("ASCII 字符", "ASCII"),
    };

    public static string ToDisplayName(this DitherColors c) => c switch
    {
        DitherColors.BlackWhite => L.T("黑白", "Black & White"),
        DitherColors.TwoColors => L.T("双色", "Two Colors"),
        _ => L.T("原色", "Original"),
    };

    /// <summary>Error diffusion and ordered styles quantize to a number of tones; the rest draw marks in two.</summary>
    public static bool HasTones(this DitherStyle s) => s <= DitherStyle.Bayer8;
    public static bool Diffuses(this DitherStyle s) => s <= DitherStyle.FloydSteinberg;
    public static bool IsHalftone(this DitherStyle s) => s is DitherStyle.Dots or DitherStyle.Lines or DitherStyle.Diamonds;
}

/// <summary>Filter ▸ Dither's settings. Mirrors DitherSettings in Dither.swift.</summary>
public sealed record DitherSettings
{
    public const string DefaultCharacters = " .:-=+*#%@";
    public DitherStyle Style { get; init; } = DitherStyle.Atkinson;
    /// <summary>Each dithered pixel covers this many layer pixels on a side (1–32).</summary>
    public double PixelSize { get; init; } = 2;
    public DitherPixelShape PixelShape { get; init; } = DitherPixelShape.Square;
    /// <summary>Halftone screen cells, in dithered pixels (4–64).</summary>
    public double CellSize { get; init; } = 8;
    /// <summary>ASCII's line height in layer pixels (6–64).</summary>
    public double TextSize { get; init; } = 14;
    public double Angle { get; init; } = 45;
    /// <summary>Tones per channel for diffusion and ordered styles (2–8); 2 is 1-bit.</summary>
    public double Levels { get; init; } = 2;
    public double Diffusion { get; init; } = 100;
    public double Density { get; init; }
    public double Contrast { get; init; }
    public DitherColors Colors { get; init; } = DitherColors.BlackWhite;
    public AdjustmentColor Dark { get; init; } = new(0, 0, 0);
    public AdjustmentColor Light { get; init; } = new(1, 1, 1);
    public bool LightOnDark { get; init; } = true;
    public string Characters { get; init; } = DefaultCharacters;

    public DitherSettings Normalized => this with
    {
        PixelSize = Math.Round(Clamp(PixelSize, 1, 32, 2)),
        CellSize = Math.Round(Clamp(CellSize, 4, 64, 8)),
        TextSize = Math.Round(Clamp(TextSize, 6, 64, 14)),
        Angle = Clamp(Angle, -90, 90, 45),
        Levels = Math.Round(Clamp(Levels, 2, 8, 2)),
        Diffusion = Clamp(Diffusion, 0, 100, 100),
        Density = Clamp(Density, -100, 100, 0),
        Contrast = Clamp(Contrast, -100, 100, 0),
        Dark = Dark.Clamped,
        Light = Light.Clamped,
        Characters = new string(Characters.Where(c => c != '\n' && c != '\r').Take(64).ToArray()),
    };

    internal static double Clamp(double v, double lo, double hi, double fallback) => double.IsFinite(v) ? Math.Clamp(v, lo, hi) : fallback;
}

/// <summary>Filter ▸ Scanlines' settings: the picture as a CRT draws it. Mirrors ScanlinesSettings.</summary>
public sealed record ScanlinesSettings
{
    public double LineSpacing { get; init; } = 4;
    public double Thickness { get; init; } = 70;
    public double Glow { get; init; }
    public double Dots { get; init; }
    public double Wobble { get; init; }
    public double Displace { get; init; }
    public double Smoothness { get; init; } = 50;
    public double Threshold { get; init; }
    public double Split { get; init; }
    public double Density { get; init; }
    public double Contrast { get; init; }
    public double BlackLevel { get; init; }
    public DitherColors Colors { get; init; } = DitherColors.BlackWhite;
    public AdjustmentColor Dark { get; init; } = new(0, 0, 0);
    public AdjustmentColor Light { get; init; } = new(1, 1, 1);

    public ScanlinesSettings Normalized => this with
    {
        LineSpacing = Math.Round(DitherSettings.Clamp(LineSpacing, 2, 32, 4)),
        Thickness = DitherSettings.Clamp(Thickness, 5, 100, 70),
        Glow = DitherSettings.Clamp(Glow, 0, 100, 0),
        Dots = DitherSettings.Clamp(Dots, 0, 100, 0),
        Wobble = DitherSettings.Clamp(Wobble, 0, 64, 0),
        Displace = DitherSettings.Clamp(Displace, -100, 100, 0),
        Smoothness = DitherSettings.Clamp(Smoothness, 0, 100, 50),
        Threshold = DitherSettings.Clamp(Threshold, 0, 100, 0),
        Split = Math.Round(DitherSettings.Clamp(Split, 0, 16, 0)),
        Density = DitherSettings.Clamp(Density, -100, 100, 0),
        Contrast = DitherSettings.Clamp(Contrast, -100, 100, 0),
        BlackLevel = DitherSettings.Clamp(BlackLevel, 0, 100, 0),
        Dark = Dark.Clamped,
        Light = Light.Clamped,
    };
}

/// <summary>Every Filter-menu effect's settings; each reads only its own. Mirrors FilterSettings in Filters.swift.</summary>
public sealed record FilterSettings
{
    public double VignetteAmount { get; init; } = 35;
    public AdjustmentColor VignetteColor { get; init; } = new(0, 0, 0);
    public double VignetteMidpoint { get; init; } = 50;
    public double VignetteRoundness { get; init; } = 100;
    public double VignetteFeather { get; init; } = 60;
    public double VignetteHighlights { get; init; } = 25;
    public double BloomAmount { get; init; } = 40;
    public double BloomRadius { get; init; } = 24;
    public double TonalAmount { get; init; } = 50;
    public double TonalRadius { get; init; } = 16;
    public double TonalShadows { get; init; } = 40;
    public double TonalMidtones { get; init; } = 60;
    public double TonalHighlights { get; init; } = 30;
    /// <summary>Lens Correction's Remove Distortion, −100–100: positive straightens barrel distortion.</summary>
    public double Distortion { get; init; }
    public DitherSettings Dither { get; init; } = new();
    public ScanlinesSettings Scanlines { get; init; } = new();
    public CameraRaw.CameraRawSettings CameraRaw { get; init; } = new();
    public Segmentation.RemoveBackgroundSettings RemoveBackground { get; init; } = new();

    private static double C(double v, double lo, double hi, double fallback) => DitherSettings.Clamp(v, lo, hi, fallback);

    public FilterSettings Normalized => this with
    {
        VignetteAmount = C(VignetteAmount, 0, 100, 35),
        VignetteColor = VignetteColor.Clamped,
        VignetteMidpoint = C(VignetteMidpoint, 0, 100, 50),
        VignetteRoundness = C(VignetteRoundness, -100, 100, 100),
        VignetteFeather = C(VignetteFeather, 0, 100, 60),
        VignetteHighlights = C(VignetteHighlights, 0, 100, 25),
        BloomAmount = C(BloomAmount, 0, 100, 40),
        BloomRadius = C(BloomRadius, 1, 150, 24),
        TonalAmount = C(TonalAmount, 0, 100, 50),
        TonalRadius = C(TonalRadius, 1, 100, 16),
        TonalShadows = C(TonalShadows, -100, 100, 40),
        TonalMidtones = C(TonalMidtones, -100, 100, 60),
        TonalHighlights = C(TonalHighlights, -100, 100, 30),
        Distortion = C(Distortion, -100, 100, 0),
        Dither = Dither.Normalized,
        Scanlines = Scanlines.Normalized,
        CameraRaw = CameraRaw.Normalized,
        RemoveBackground = RemoveBackground.Normalized,
    };

    /// <summary>Whether applying <paramref name="kind"/> with these settings changes nothing (OK then closes as Cancel does).</summary>
    public bool DoesNothing(FilterKind kind) => kind switch
    {
        FilterKind.Vignette => VignetteAmount <= 0,
        FilterKind.BloomGlow => BloomAmount <= 0,
        FilterKind.TonalContrast => TonalAmount <= 0 || (TonalShadows == 0 && TonalMidtones == 0 && TonalHighlights == 0),
        FilterKind.LensCorrection => Distortion == 0,
        FilterKind.CameraRaw => CameraRaw.IsIdentity,
        _ => false,
    };

    /// <summary>The room a filter needs around the layer to spread into, in layer pixels.</summary>
    public double Margin(FilterKind kind) => kind == FilterKind.BloomGlow ? BloomRadius * 3 + 2 : 0;
}
