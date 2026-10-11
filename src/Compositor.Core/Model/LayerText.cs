using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Compositor.Core.Adjustments;
using Compositor.Core.Geometry;
using Compositor.Core.Localization;

namespace Compositor.Core.Model;

public enum TextAlign { Left, Center, Right }

/// <summary>Letters painted in a color other than the text's own, in UTF-16 offsets.</summary>
public sealed record TextColorRun(int Location, int Length, double Red, double Green, double Blue)
{
    public PaletteColor Color => new(Red, Green, Blue);
}

/// <summary>Letters set in a face other than the text's own, in UTF-16 offsets.</summary>
public sealed record TextFontRun(int Location, int Length, string FontName);

/// <summary>
/// A text layer's source: what it says and how, kept so it can be edited again. Its pixels are an ordinary raster drawn
/// from this; any other pixel edit drops it and leaves plain pixels. Immutable. Mirrors LayerTextStyle in TypeTool.swift
/// (offsets in UTF-16 units, as Foundation's are — C# strings are UTF-16 too).
/// </summary>
public sealed record LayerText
{
    /// <summary>The gap between the text and its box, in layer pixels, so the box's edges are easy to grab.</summary>
    public const double Padding = 12;
    public const int MaxLength = 100_000;

    public string Content { get; init; } = "Text";
    public string FontName { get; init; } = "Helvetica";
    public double FontSize { get; init; } = 72;
    public double Red { get; init; }
    public double Green { get; init; }
    public double Blue { get; init; }
    public TextAlign Alignment { get; init; } = TextAlign.Left;
    /// <summary>Extra room after each letter, in layer pixels.</summary>
    public double Tracking { get; init; }
    /// <summary>Baseline to baseline in layer pixels; 0 is Auto (120% of the size).</summary>
    public double Leading { get; init; }
    /// <summary>A paragraph's fixed box in layer pixels; null for point text, whose size follows what it says.</summary>
    public SizeD? BoxSize { get; init; }
    public ImmutableList<TextColorRun>? ColorRuns { get; init; }
    public ImmutableList<TextFontRun>? FontRuns { get; init; }

    public PaletteColor Color => new(Red, Green, Blue);
    public double LineHeight => Leading > 0 ? Leading : FontSize * 1.2;

    public bool BoxIsValid => BoxSize is not { } box
        || (double.IsFinite(box.Width) && double.IsFinite(box.Height) && box.Width >= 16 && box.Height >= 16
            && box.Width <= DocumentLimits.MaxSide && box.Height <= DocumentLimits.MaxSide);

    public bool IsValid =>
        Content.Length <= MaxLength && BoxIsValid
        && double.IsFinite(FontSize) && FontSize is >= 1 and <= 2000
        && new[] { Red, Green, Blue }.All(v => double.IsFinite(v) && v is >= 0 and <= 1)
        && double.IsFinite(Tracking) && Tracking is >= -100 and <= 1000
        && double.IsFinite(Leading) && Leading is >= 0 and <= 5000
        && RunsAreValid(ColorRuns, r => (r.Location, r.Length), r => new[] { r.Red, r.Green, r.Blue }.All(v => double.IsFinite(v) && v is >= 0 and <= 1))
        && RunsAreValid(FontRuns, r => (r.Location, r.Length), r => r.FontName.Length is > 0 and <= 200 && !r.FontName.Contains('\n'));

    private bool RunsAreValid<T>(ImmutableList<T>? runs, Func<T, (int Location, int Length)> span, Func<T, bool> ok)
    {
        if (runs is null) return true;
        int end = 0;
        foreach (var run in runs)
        {
            var (location, length) = span(run);
            if (location < end || length <= 0 || location > int.MaxValue - length || !ok(run)) return false;
            end = location + length;
        }
        return runs.Count > 0 && end <= Content.Length;
    }

    // MARK: Per-letter color and face

    public PaletteColor ColorAt(int index) =>
        ColorRuns?.FirstOrDefault(r => r.Location <= index && index < r.Location + r.Length)?.Color ?? Color;

    public string FontAt(int index) =>
        FontRuns?.FirstOrDefault(r => r.Location <= index && index < r.Location + r.Length)?.FontName ?? FontName;

    private PaletteColor[] UnitColors()
    {
        var colors = Enumerable.Repeat(Color, Content.Length).ToArray();
        foreach (var run in ColorRuns ?? ImmutableList<TextColorRun>.Empty)
            for (int i = Math.Max(0, run.Location); i < Math.Min(colors.Length, run.Location + run.Length); i++) colors[i] = run.Color;
        return colors;
    }

    private LayerText WithUnitColors(IReadOnlyList<PaletteColor> colors)
    {
        var runs = ImmutableList.CreateBuilder<TextColorRun>();
        for (int i = 0; i < colors.Count; i++)
        {
            if (colors[i] == Color) continue;
            if (runs.Count > 0 && runs[^1] is var last && last.Location + last.Length == i && last.Color == colors[i])
                runs[^1] = last with { Length = last.Length + 1 };
            else runs.Add(new TextColorRun(i, 1, colors[i].Red, colors[i].Green, colors[i].Blue));
        }
        return this with { ColorRuns = runs.Count == 0 ? null : runs.ToImmutable() };
    }

    private string[] UnitFonts()
    {
        var fonts = Enumerable.Repeat(FontName, Content.Length).ToArray();
        foreach (var run in FontRuns ?? ImmutableList<TextFontRun>.Empty)
            for (int i = Math.Max(0, run.Location); i < Math.Min(fonts.Length, run.Location + run.Length); i++) fonts[i] = run.FontName;
        return fonts;
    }

    private LayerText WithUnitFonts(IReadOnlyList<string> fonts)
    {
        if (fonts.Count > 0 && fonts.All(f => f == fonts[0])) return this with { FontName = fonts[0], FontRuns = null };
        var runs = ImmutableList.CreateBuilder<TextFontRun>();
        for (int i = 0; i < fonts.Count; i++)
        {
            if (fonts[i] == FontName) continue;
            if (runs.Count > 0 && runs[^1] is var last && last.Location + last.Length == i && last.FontName == fonts[i])
                runs[^1] = last with { Length = last.Length + 1 };
            else runs.Add(new TextFontRun(i, 1, fonts[i]));
        }
        return this with { FontRuns = runs.Count == 0 ? null : runs.ToImmutable() };
    }

    private (int Start, int End) Clamp(int location, int length)
    {
        int start = Math.Clamp(location, 0, Content.Length);
        return (start, Math.Clamp(location + length, start, Content.Length));
    }

    /// <summary>Paints a range in <paramref name="color"/>; an empty range, or the whole text, recolors all of it.</summary>
    public LayerText SettingColor(PaletteColor color, int location, int length)
    {
        var (start, end) = Clamp(location, length);
        if (start == end || (start == 0 && end == Content.Length))
            return this with { Red = color.Red, Green = color.Green, Blue = color.Blue, ColorRuns = null };
        var colors = UnitColors();
        for (int i = start; i < end; i++) colors[i] = color;
        return WithUnitColors(colors);
    }

    /// <summary>Sets a range's face; an empty range, or the whole text, changes all of it.</summary>
    public LayerText SettingFont(string name, int location, int length)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 200 || name.Contains('\n')) return this;
        var (start, end) = Clamp(location, length);
        if (start == end || (start == 0 && end == Content.Length)) return this with { FontName = name, FontRuns = null };
        var fonts = UnitFonts();
        for (int i = start; i < end; i++) fonts[i] = name;
        return WithUnitFonts(fonts);
    }

    /// <summary>The one face covering a range, or null when the range is empty or uses more than one.</summary>
    public string? UniformFont(int location, int length)
    {
        var (start, end) = Clamp(location, length);
        if (end <= start) return null;
        var fonts = UnitFonts();
        for (int i = start + 1; i < end; i++) if (fonts[i] != fonts[start]) return null;
        return fonts[start];
    }

    /// <summary>
    /// The text with a range replaced by <paramref name="replacement"/>: the new letters take the color and face of the
    /// letter before them, as typing does.
    /// </summary>
    public LayerText Replacing(int location, int length, string replacement)
    {
        var (start, end) = Clamp(location, length);
        var content = Content[..start] + replacement + Content[end..];
        var next = this with { Content = content, ColorRuns = null, FontRuns = null };
        if (ColorRuns is not null)
        {
            var colors = UnitColors().ToList();
            var inherited = start > 0 ? colors[start - 1] : end > start ? colors[start] : colors.FirstOrDefault(Color);
            colors.RemoveRange(start, end - start);
            colors.InsertRange(start, Enumerable.Repeat(inherited, replacement.Length));
            next = next.WithUnitColors(colors);
        }
        if (FontRuns is not null)
        {
            var fonts = UnitFonts().ToList();
            var inherited = start > 0 ? fonts[start - 1] : end > start ? fonts[start] : fonts.FirstOrDefault(FontName);
            fonts.RemoveRange(start, end - start);
            fonts.InsertRange(start, Enumerable.Repeat(inherited, replacement.Length));
            next = next.WithUnitFonts(fonts);
        }
        return next;
    }

    /// <summary>A text layer's name: its first words on one line.</summary>
    public static string LayerName(string content)
    {
        var flat = string.Join(' ', content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length == 0 ? L.T("文字", "Text") : flat.Length > 40 ? flat[..40] : flat;
    }

    // MARK: JSON, as the Mac app's Codable writes it

    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["alignment"] = Alignment.ToString(),
            ["blue"] = Blue,
            ["content"] = Content,
            ["fontName"] = FontName,
            ["fontSize"] = FontSize,
            ["green"] = Green,
            ["leading"] = Leading,
            ["red"] = Red,
            ["tracking"] = Tracking,
        };
        if (BoxSize is { } box) o["boxSize"] = new JsonArray(box.Width, box.Height);
        if (ColorRuns is { } colors)
            o["colorRuns"] = new JsonArray(colors.Select(r => (JsonNode)new JsonObject
            {
                ["blue"] = r.Blue, ["green"] = r.Green, ["length"] = r.Length, ["location"] = r.Location, ["red"] = r.Red,
            }).ToArray());
        if (FontRuns is { } fonts)
            o["fontRuns"] = new JsonArray(fonts.Select(r => (JsonNode)new JsonObject
            {
                ["fontName"] = r.FontName, ["length"] = r.Length, ["location"] = r.Location,
            }).ToArray());
        return o;
    }

    public static LayerText? FromJson(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        var text = new LayerText
        {
            Content = Json.Str(o, "content") ?? "",
            FontName = Json.Str(o, "fontName") ?? "Helvetica",
            FontSize = Json.Num(o, "fontSize") ?? 72,
            Red = Json.Num(o, "red") ?? 0,
            Green = Json.Num(o, "green") ?? 0,
            Blue = Json.Num(o, "blue") ?? 0,
            Alignment = Json.Str(o, "alignment") switch { "Center" => TextAlign.Center, "Right" => TextAlign.Right, _ => TextAlign.Left },
            Tracking = Json.Num(o, "tracking") ?? 0,
            Leading = Json.Num(o, "leading") ?? 0,
            BoxSize = o["boxSize"] is JsonArray { Count: 2 } box && Json.NumOf(box[0]) is { } w && Json.NumOf(box[1]) is { } h ? new SizeD(w, h) : null,
        };
        if (o["colorRuns"] is JsonArray colorRuns)
            text = text with
            {
                ColorRuns = colorRuns.OfType<JsonObject>().Select(r => new TextColorRun(
                    (int)(Json.Num(r, "location") ?? -1), (int)(Json.Num(r, "length") ?? 0),
                    Json.Num(r, "red") ?? 0, Json.Num(r, "green") ?? 0, Json.Num(r, "blue") ?? 0)).ToImmutableList(),
            };
        if (o["fontRuns"] is JsonArray fontRuns)
            text = text with
            {
                FontRuns = fontRuns.OfType<JsonObject>().Select(r => new TextFontRun(
                    (int)(Json.Num(r, "location") ?? -1), (int)(Json.Num(r, "length") ?? 0), Json.Str(r, "fontName") ?? "")).ToImmutableList(),
            };
        return text;
    }
}
