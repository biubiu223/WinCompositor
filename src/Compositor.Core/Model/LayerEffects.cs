using System.Text.Json.Nodes;
using Compositor.Core.Localization;

namespace Compositor.Core.Model;

/// <summary>The effects a layer can draw around itself. Mirrors LayerEffectKind in LayerEffects.swift.</summary>
public enum LayerEffectKind { Stroke, Shadow, ColorOverlay, InnerShadow, OuterGlow, InnerGlow }

public static class LayerEffectKinds
{
    /// <summary>In the order the panels list them.</summary>
    public static readonly LayerEffectKind[] All =
    {
        LayerEffectKind.Stroke, LayerEffectKind.InnerShadow, LayerEffectKind.InnerGlow, LayerEffectKind.ColorOverlay,
        LayerEffectKind.OuterGlow, LayerEffectKind.Shadow,
    };

    public static string ToName(this LayerEffectKind kind) => kind switch
    {
        LayerEffectKind.Stroke => "Stroke",
        LayerEffectKind.Shadow => "Drop Shadow",
        LayerEffectKind.ColorOverlay => "Color Overlay",
        LayerEffectKind.InnerShadow => "Inner Shadow",
        LayerEffectKind.OuterGlow => "Outer Glow",
        _ => "Inner Glow",
    };

    public static string ToDisplayName(this LayerEffectKind kind) => kind switch
    {
        LayerEffectKind.Stroke => L.T("描边", "Stroke"),
        LayerEffectKind.Shadow => L.T("投影", "Drop Shadow"),
        LayerEffectKind.ColorOverlay => L.T("颜色叠加", "Color Overlay"),
        LayerEffectKind.InnerShadow => L.T("内阴影", "Inner Shadow"),
        LayerEffectKind.OuterGlow => L.T("外发光", "Outer Glow"),
        _ => L.T("内发光", "Inner Glow"),
    };

    public static LayerEffectKind? Parse(string? name) => All.Cast<LayerEffectKind?>().FirstOrDefault(k => k!.Value.ToName() == name);
}

/// <summary>
/// One effect's settings. Sizes, distances and blurs are in the layer's own pixels; colors are sRGB 0–1; opacity
/// 0–1. <see cref="Enabled"/> is null in older projects, which means shown. Mirrors the effect structs in
/// LayerEffects.swift; fields a kind doesn't use stay at their defaults and aren't written.
/// </summary>
public sealed record LayerEffect(LayerEffectKind Kind)
{
    public bool? Enabled { get; init; }
    public bool IsEnabled => Enabled ?? true;
    public double Size { get; init; }
    public double Angle { get; init; } = 90;
    public double Distance { get; init; }
    public double Blur { get; init; }
    public double Red { get; init; }
    public double Green { get; init; }
    public double Blue { get; init; }
    public double Opacity { get; init; } = 1;
    public bool Inside { get; init; }

    public PaletteColor Color => new(Red, Green, Blue);
    public LayerEffect WithColor(PaletteColor c) => this with { Red = c.Red, Green = c.Green, Blue = c.Blue };

    public const double MaxSize = 500, MaxDistance = 5000;

    /// <summary>A new effect as Photoshop starts it (a stroke or overlay takes <paramref name="color"/>).</summary>
    public static LayerEffect Default(LayerEffectKind kind, PaletteColor color) => kind switch
    {
        LayerEffectKind.Stroke => new LayerEffect(kind) { Size = 4 }.WithColor(color),
        LayerEffectKind.Shadow => new LayerEffect(kind) { Angle = 90, Distance = 20, Blur = 20, Opacity = 0.5 },
        LayerEffectKind.ColorOverlay => new LayerEffect(kind).WithColor(color),
        LayerEffectKind.InnerShadow => new LayerEffect(kind) { Angle = 90, Distance = 10, Blur = 10, Opacity = 0.5 },
        LayerEffectKind.OuterGlow => new LayerEffect(kind) { Size = 20, Opacity = 0.75 }.WithColor(PaletteColor.White),
        _ => new LayerEffect(kind) { Size = 10, Opacity = 0.75 }.WithColor(PaletteColor.White),
    };

    private static bool Unit(double v) => double.IsFinite(v) && v >= 0 && v <= 1;

    public bool IsValid => Unit(Red) && Unit(Green) && Unit(Blue) && Unit(Opacity) && Kind switch
    {
        LayerEffectKind.Stroke or LayerEffectKind.OuterGlow or LayerEffectKind.InnerGlow => double.IsFinite(Size) && Size is >= 0 and <= MaxSize,
        LayerEffectKind.Shadow or LayerEffectKind.InnerShadow =>
            double.IsFinite(Angle) && Angle is >= -360 and <= 360 && double.IsFinite(Distance) && Distance is >= 0 and <= MaxDistance
            && double.IsFinite(Blur) && Blur is >= 0 and <= MaxSize,
        _ => true,
    };

    /// <summary>Where a shadow falls, in layer pixels (y down): away from the light at <see cref="Angle"/>.</summary>
    public (double X, double Y) Offset
    {
        get
        {
            double radians = Angle * Math.PI / 180;
            return (-Math.Cos(radians) * Distance, Math.Sin(radians) * Distance);
        }
    }

    public LayerEffect Scaled(double factor) => this with
    {
        Size = Math.Min(MaxSize, Size * factor),
        Distance = Math.Min(MaxDistance, Distance * factor),
        Blur = Math.Min(MaxSize, Blur * factor),
    };

    /// <summary>Every key the Mac app's decoder requires for this kind, plus "enabled" when set.</summary>
    public JsonObject ToJson()
    {
        var o = new JsonObject();
        if (Enabled is { } enabled) o["enabled"] = enabled;
        switch (Kind)
        {
            case LayerEffectKind.Stroke:
                o["size"] = Size; o["inside"] = Inside;
                break;
            case LayerEffectKind.Shadow:
            case LayerEffectKind.InnerShadow:
                o["angle"] = Angle; o["distance"] = Distance; o["blur"] = Blur;
                break;
            case LayerEffectKind.OuterGlow:
            case LayerEffectKind.InnerGlow:
                o["size"] = Size;
                break;
        }
        o["red"] = Red; o["green"] = Green; o["blue"] = Blue; o["opacity"] = Opacity;
        return o;
    }

    public static LayerEffect? FromJson(LayerEffectKind kind, JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        var fallback = Default(kind, PaletteColor.Black);
        double Num(string key, double value) => Adjustments.Json.Num(o, key) ?? value;
        return fallback with
        {
            Enabled = Adjustments.Json.Bool(o, "enabled"),
            Size = Num("size", fallback.Size),
            Angle = Num("angle", fallback.Angle),
            Distance = Num("distance", fallback.Distance),
            Blur = Num("blur", fallback.Blur),
            Red = Num("red", fallback.Red),
            Green = Num("green", fallback.Green),
            Blue = Num("blue", fallback.Blue),
            Opacity = Num("opacity", fallback.Opacity),
            Inside = Adjustments.Json.Bool(o, "inside") ?? false,
        };
    }
}

/// <summary>
/// What a layer draws around itself: at most one effect of each kind. Kept with the layer, so it follows every edit
/// and can be changed or removed at any time; the pixels are never touched. Immutable. Mirrors LayerEffects.
/// </summary>
public sealed class LayerEffects
{
    private readonly Dictionary<LayerEffectKind, LayerEffect> _effects;

    public static readonly LayerEffects Empty = new(Array.Empty<LayerEffect>());

    public LayerEffects(IEnumerable<LayerEffect> effects) => _effects = effects.GroupBy(e => e.Kind).ToDictionary(g => g.Key, g => g.Last());

    public LayerEffect? this[LayerEffectKind kind] => _effects.TryGetValue(kind, out var e) ? e : null;
    public bool IsEmpty => _effects.Count == 0;
    public bool Contains(LayerEffectKind kind) => _effects.ContainsKey(kind);
    public bool IsEnabled(LayerEffectKind kind) => this[kind]?.IsEnabled == true;
    public IEnumerable<LayerEffect> All => LayerEffectKinds.All.Where(_effects.ContainsKey).Select(k => _effects[k]);
    public bool IsValid => _effects.Values.All(e => e.IsValid);

    /// <summary>Only the effects that are shown.</summary>
    public LayerEffects Visible => _effects.Values.All(e => e.IsEnabled) ? this : new LayerEffects(_effects.Values.Where(e => e.IsEnabled));

    public LayerEffects With(LayerEffect effect) => new(_effects.Values.Where(e => e.Kind != effect.Kind).Append(effect));
    public LayerEffects Without(LayerEffectKind kind) => new(_effects.Values.Where(e => e.Kind != kind));
    public LayerEffects Scaled(double factor) => new(_effects.Values.Select(e => e.Scaled(factor)));

    /// <summary>How far, in layer pixels, the effects reach beyond the layer's pixels.</summary>
    public double Margin
    {
        get
        {
            double margin = 0;
            foreach (var e in Visible.All)
            {
                switch (e.Kind)
                {
                    case LayerEffectKind.Stroke when !e.Inside: margin = Math.Max(margin, e.Size); break;
                    case LayerEffectKind.Shadow: margin = Math.Max(margin, e.Distance + e.Blur * 3); break;
                    case LayerEffectKind.OuterGlow: margin = Math.Max(margin, e.Size * 3); break;
                }
            }
            return Math.Ceiling(margin) + 2;
        }
    }

    public bool ContentEquals(LayerEffects? other) =>
        other is not null && other._effects.Count == _effects.Count && _effects.All(p => other[p.Key] == p.Value);

    private static string Key(LayerEffectKind kind) => kind switch
    {
        LayerEffectKind.Stroke => "stroke",
        LayerEffectKind.Shadow => "shadow",
        LayerEffectKind.ColorOverlay => "colorOverlay",
        LayerEffectKind.InnerShadow => "innerShadow",
        LayerEffectKind.OuterGlow => "outerGlow",
        _ => "innerGlow",
    };

    public JsonObject ToJson()
    {
        var o = new JsonObject();
        foreach (var kind in LayerEffectKinds.All.OrderBy(Key, StringComparer.Ordinal))
            if (this[kind] is { } e) o[Key(kind)] = e.ToJson();
        return o;
    }

    /// <summary>Null when the value isn't an effects object; effects it can't read are left out.</summary>
    public static LayerEffects? FromJson(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        var effects = new List<LayerEffect>();
        foreach (var kind in LayerEffectKinds.All)
            if (LayerEffect.FromJson(kind, o[Key(kind)]) is { } e) effects.Add(e);
        return new LayerEffects(effects);
    }
}
