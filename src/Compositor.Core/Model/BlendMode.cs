using Compositor.Core.Localization;

namespace Compositor.Core.Model;

/// <summary>Photoshop's blend modes, in its menu order. Mirrors LayerBlendMode in LayerAppearance.swift.</summary>
public enum BlendMode
{
    Normal,
    Darken, Multiply, ColorBurn, LinearBurn,
    Lighten, Screen, ColorDodge, LinearDodge,
    Overlay, SoftLight, HardLight, VividLight, LinearLight, PinLight, HardMix,
    Difference, Exclusion, Subtract, Divide,
    Hue, Saturation, Color, Luminosity,
}

public static class BlendModes
{
    public static readonly BlendMode[] All = Enum.GetValues<BlendMode>();

    /// <summary>Photoshop's grouping; menus draw a separator between groups.</summary>
    public static readonly BlendMode[][] Groups =
    {
        new[] { BlendMode.Normal },
        new[] { BlendMode.Darken, BlendMode.Multiply, BlendMode.ColorBurn, BlendMode.LinearBurn },
        new[] { BlendMode.Lighten, BlendMode.Screen, BlendMode.ColorDodge, BlendMode.LinearDodge },
        new[] { BlendMode.Overlay, BlendMode.SoftLight, BlendMode.HardLight, BlendMode.VividLight, BlendMode.LinearLight, BlendMode.PinLight, BlendMode.HardMix },
        new[] { BlendMode.Difference, BlendMode.Exclusion, BlendMode.Subtract, BlendMode.Divide },
        new[] { BlendMode.Hue, BlendMode.Saturation, BlendMode.Color, BlendMode.Luminosity },
    };

    /// <summary>The name the project format stores (and Photoshop's English menu shows).</summary>
    public static string ToName(this BlendMode mode) => mode switch
    {
        BlendMode.Normal => "Normal",
        BlendMode.Darken => "Darken",
        BlendMode.Multiply => "Multiply",
        BlendMode.ColorBurn => "Color Burn",
        BlendMode.LinearBurn => "Linear Burn",
        BlendMode.Lighten => "Lighten",
        BlendMode.Screen => "Screen",
        BlendMode.ColorDodge => "Color Dodge",
        BlendMode.LinearDodge => "Linear Dodge (Add)",
        BlendMode.Overlay => "Overlay",
        BlendMode.SoftLight => "Soft Light",
        BlendMode.HardLight => "Hard Light",
        BlendMode.VividLight => "Vivid Light",
        BlendMode.LinearLight => "Linear Light",
        BlendMode.PinLight => "Pin Light",
        BlendMode.HardMix => "Hard Mix",
        BlendMode.Difference => "Difference",
        BlendMode.Exclusion => "Exclusion",
        BlendMode.Subtract => "Subtract",
        BlendMode.Divide => "Divide",
        BlendMode.Hue => "Hue",
        BlendMode.Saturation => "Saturation",
        BlendMode.Color => "Color",
        BlendMode.Luminosity => "Luminosity",
        _ => "Normal",
    };

    /// <summary>The name shown in the interface (Photoshop's own terms in the interface language).</summary>
    public static string ToDisplayName(this BlendMode mode) => mode switch
    {
        BlendMode.Normal => L.T("正常", "Normal"),
        BlendMode.Darken => L.T("变暗", "Darken"),
        BlendMode.Multiply => L.T("正片叠底", "Multiply"),
        BlendMode.ColorBurn => L.T("颜色加深", "Color Burn"),
        BlendMode.LinearBurn => L.T("线性加深", "Linear Burn"),
        BlendMode.Lighten => L.T("变亮", "Lighten"),
        BlendMode.Screen => L.T("滤色", "Screen"),
        BlendMode.ColorDodge => L.T("颜色减淡", "Color Dodge"),
        BlendMode.LinearDodge => L.T("线性减淡（添加）", "Linear Dodge (Add)"),
        BlendMode.Overlay => L.T("叠加", "Overlay"),
        BlendMode.SoftLight => L.T("柔光", "Soft Light"),
        BlendMode.HardLight => L.T("强光", "Hard Light"),
        BlendMode.VividLight => L.T("亮光", "Vivid Light"),
        BlendMode.LinearLight => L.T("线性光", "Linear Light"),
        BlendMode.PinLight => L.T("点光", "Pin Light"),
        BlendMode.HardMix => L.T("实色混合", "Hard Mix"),
        BlendMode.Difference => L.T("差值", "Difference"),
        BlendMode.Exclusion => L.T("排除", "Exclusion"),
        BlendMode.Subtract => L.T("减去", "Subtract"),
        BlendMode.Divide => L.T("划分", "Divide"),
        BlendMode.Hue => L.T("色相", "Hue"),
        BlendMode.Saturation => L.T("饱和度", "Saturation"),
        BlendMode.Color => L.T("颜色", "Color"),
        BlendMode.Luminosity => L.T("明度", "Luminosity"),
        _ => L.T("正常", "Normal"),
    };

    public static BlendMode? Parse(string? name)
    {
        if (name is null) return null;
        foreach (var mode in All)
        {
            if (mode.ToName() == name) return mode;
        }
        return null;
    }
}
