using Compositor.Core.Localization;
using Compositor.Core.Model;

namespace Compositor.Core.IO.Psd;

/// <summary>
/// Reads a layer's Photoshop layer style (<c>lfx2</c>, or <c>lmfx</c> with several of a kind) into the effects the editor
/// draws: drop and inner shadow, outer and inner glow, color overlay and stroke, with their color, opacity, angle,
/// distance and size (scaled by the style's scale). What the editor can't draw — bevel and emboss, satin, gradient and
/// pattern overlays, other blend modes — is listed in the import report.
/// </summary>
public static class PsdEffects
{
    public sealed record Result(LayerEffects Effects, IReadOnlyList<string> Notes);

    /// <summary>Photoshop's global light when the file doesn't say.</summary>
    public const double DefaultGlobalAngle = 120;

    private static readonly (string Key, string Multi, LayerEffectKind Kind, string Mode)[] Kinds =
    {
        ("DrSh", "dropShadowMulti", LayerEffectKind.Shadow, "Mltp"),
        ("IrSh", "innerShadowMulti", LayerEffectKind.InnerShadow, "Mltp"),
        ("OrGl", "", LayerEffectKind.OuterGlow, "Scrn"),
        ("IrGl", "", LayerEffectKind.InnerGlow, "Scrn"),
        ("SoFi", "solidFillMulti", LayerEffectKind.ColorOverlay, "Nrml"),
        ("FrFX", "frameFXMulti", LayerEffectKind.Stroke, "Nrml"),
    };

    private static readonly (string Key, string Multi, string Name)[] Unsupported =
    {
        ("ebbl", "", L.T("斜面和浮雕", "Bevel & Emboss")), ("ChFX", "", L.T("光泽", "Satin")),
        ("GrFl", "gradientFillMulti", L.T("渐变叠加", "Gradient Overlay")), ("patternFill", "", L.T("图案叠加", "Pattern Overlay")),
    };

    public static Result? Parse(IReadOnlyDictionary<string, byte[]> extra, double globalAngle = DefaultGlobalAngle)
    {
        if (!(extra.TryGetValue("lmfx", out var data) || extra.TryGetValue("lfx2", out data)) || data.Length > 8_000_000) return null;
        var reader = new PsdDescriptorReader(data);
        if (reader.U32() is not 0 || reader.Descriptor(versioned: true) is not { } style) return null;
        double scale = (style.Number("Scl ") ?? 100) / 100;
        if (!double.IsFinite(scale) || scale <= 0) scale = 1;
        bool master = style.Bool("masterFXSwitch") ?? true;

        var effects = new List<LayerEffect>();
        var notes = new List<string>();
        bool dropped = false, modes = false, extras = false;
        foreach (var (key, multi, kind, mode) in Kinds)
        {
            var found = Instances(style, key, multi);
            if (found.Count == 0) continue;
            if (found.Count(Present) > 1) dropped = true;
            var item = found.FirstOrDefault(i => Enabled(i)) ?? found[0];
            if (!Present(item)) continue;
            var effect = Effect(kind, item, scale, globalAngle, ref extras) with { Enabled = master && Enabled(item) };
            if (!effect.IsValid) continue;
            if (item.Enum("Md  ") is { } blend && blend != mode && effect.IsEnabled) modes = true;
            effects.Add(effect);
        }
        var missing = Unsupported.Where(u => Instances(style, u.Key, u.Multi).Any(i => Present(i) && Enabled(i))).Select(u => u.Name).ToList();
        if (missing.Count > 0)
        {
            string list = string.Join(L.T("、", ", "), missing);
            notes.Add(L.T($"不支持的图层样式（{list}）已舍弃，外观可能有所不同。", $"Unsupported layer styles ({list}) were discarded, so the appearance may differ."));
        }
        if (dropped) notes.Add(L.T("同一种效果有多个时，只保留了第一个。", "Where an effect was applied more than once, only the first was kept."));
        if (modes) notes.Add(L.T("图层样式的混合模式已按各效果的默认模式绘制。", "Layer style blend modes were drawn with each effect’s default mode."));
        if (extras) notes.Add(L.T("图层样式的部分选项（扩展、杂色、等高线、渐变等）已省略。",
                                  "Some layer style options (Spread, Noise, Contour, Gradient and others) were omitted."));
        if (!master && effects.Count > 0)
            notes.Add(L.T("这个图层的样式在 Photoshop 中是隐藏的，导入后也保持隐藏。", "This layer’s style is hidden in Photoshop, and stays hidden after import."));
        return effects.Count == 0 && notes.Count == 0 ? null : new Result(new LayerEffects(effects), notes);
    }

    private static List<Dictionary<string, PsdValue>> Instances(Dictionary<string, PsdValue> style, string key, string multi)
    {
        var result = new List<Dictionary<string, PsdValue>>();
        if (multi.Length > 0 && style.List(multi) is { } list)
            result.AddRange(list.Where(v => v.Kind == PsdValue.Type.Object && v.Items is not null).Select(v => v.Items!));
        if (result.Count == 0 && style.Object(key) is { } single) result.Add(single);
        return result;
    }

    private static bool Present(Dictionary<string, PsdValue> item) => item.Bool("present") ?? true;
    private static bool Enabled(Dictionary<string, PsdValue> item) => item.Bool("enab") ?? true;

    private static LayerEffect Effect(LayerEffectKind kind, Dictionary<string, PsdValue> item, double scale, double globalAngle, ref bool extras)
    {
        var effect = new LayerEffect(kind) { Opacity = Math.Clamp((item.Number("Opct") ?? 100) / 100, 0, 1) };
        if (Color(item) is { } c) effect = effect with { Red = c.R, Green = c.G, Blue = c.B };
        else if (kind is LayerEffectKind.OuterGlow or LayerEffectKind.InnerGlow) effect = effect.WithColor(PaletteColor.White);
        double Pixels(string key) => Math.Max(0, (item.Number(key) ?? 0) * scale);
        switch (kind)
        {
            case LayerEffectKind.Shadow:
            case LayerEffectKind.InnerShadow:
            {
                double angle = item.Bool("uglg") == true ? globalAngle : item.Number("lagl") ?? globalAngle;
                if (angle > 360 || angle < -360) angle %= 360;
                effect = effect with
                {
                    Angle = angle,
                    Distance = Math.Min(LayerEffect.MaxDistance, Pixels("Dstn")),
                    Blur = Math.Min(LayerEffect.MaxSize, Pixels("blur")),
                };
                if ((item.Number("Ckmt") ?? 0) > 0 || (item.Number("Nose") ?? 0) > 0) extras = true;
                break;
            }
            case LayerEffectKind.OuterGlow:
            case LayerEffectKind.InnerGlow:
                effect = effect with { Size = Math.Min(LayerEffect.MaxSize, Pixels("blur")) };
                if ((item.Number("Ckmt") ?? 0) > 0 || (item.Number("Nose") ?? 0) > 0 || item.Object("Grad") is not null
                    || (kind == LayerEffectKind.InnerGlow && item.Enum("glwS") == "SrcC")) extras = true;
                break;
            case LayerEffectKind.Stroke:
                effect = effect with { Size = Math.Min(LayerEffect.MaxSize, Pixels("Sz  ")), Inside = item.Enum("Styl") == "InsF" };
                if (item.Enum("Styl") == "CtrF" || item.Enum("PntT") is { } paint && paint != "SClr") extras = true;
                break;
        }
        return effect;
    }

    /// <summary>An effect's color: RGB (0–255), gray, or hue/saturation/brightness.</summary>
    private static (double R, double G, double B)? Color(Dictionary<string, PsdValue> item)
    {
        if (item.Object("Clr ") is not { } color) return null;
        if (color.Number("Rd  ") is { } r && color.Number("Grn ") is { } g && color.Number("Bl  ") is { } b)
            return (Math.Clamp(r / 255, 0, 1), Math.Clamp(g / 255, 0, 1), Math.Clamp(b / 255, 0, 1));
        if (color.Number("redFloat") is { } rf && color.Number("greenFloat") is { } gf && color.Number("blueFloat") is { } bf)
            return (Math.Clamp(rf, 0, 1), Math.Clamp(gf, 0, 1), Math.Clamp(bf, 0, 1));
        if (color.Number("Gry ") is { } gray)
        {
            double v = Math.Clamp(1 - gray / 100, 0, 1);
            return (v, v, v);
        }
        if (color.Number("H   ") is { } h && color.Number("Strt") is { } s && color.Number("Brgh") is { } br)
        {
            var c = PaletteColor.FromHsb(h, s / 100, br / 100);
            return (c.Red, c.Green, c.Blue);
        }
        return null;
    }
}
