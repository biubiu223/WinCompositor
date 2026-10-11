using System.Collections.Concurrent;
using System.Globalization;
using Compositor.Core.Geometry;
using Compositor.Core.Localization;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Core.Text;

/// <summary>
/// Lays out and draws a text layer's source, as TypeTool.swift does with NSLayoutManager: each letter in its own face
/// (falling back to a font that has it), pair kerning from the font, tracking after each letter, lines broken at line
/// feeds and — in a fixed box — wrapped at word boundaries (anywhere between CJK characters), aligned, and spaced by the
/// leading with the baseline the font's descent up from the bottom of each line. Also answers where the caret goes and
/// which letter a point falls on, for the on-canvas editor.
/// </summary>
public sealed class TextLayout
{
    /// <summary>One letter (a UTF-16 unit, or a surrogate pair) as laid out.</summary>
    private readonly record struct Cluster(int Start, int Length, int CodePoint, SKTypeface Typeface, double Advance, double Kern, PaletteColor Color, bool IsBreak, bool IsSpace, bool IsCjk);

    /// <summary>A laid-out line: the letters it holds (<see cref="End"/> excludes its line feed), where it sits, and the caret positions.</summary>
    public sealed record Line(int Start, int End, double Top, double Baseline, double X, double Width, int[] Boundaries, double[] Carets);

    public LayerText Style { get; }
    public double Width { get; }
    public double Height { get; }
    public IReadOnlyList<Line> Lines { get; }
    private readonly List<Cluster> _clusters;

    private TextLayout(LayerText style, double width, double height, List<Line> lines, List<Cluster> clusters)
    {
        Style = style;
        Width = width;
        Height = height;
        Lines = lines;
        _clusters = clusters;
    }

    public SizeD Size => new(Width, Height);

    // MARK: Fonts

    private static readonly ConcurrentDictionary<string, SKTypeface> Typefaces = new();
    private static readonly ConcurrentDictionary<(string Family, int Weight, int Slant, int CodePoint), SKTypeface> Fallbacks = new();

    /// <summary>Mac faces that Windows doesn't have, and what stands in for them.</summary>
    private static readonly Dictionary<string, string> Substitutes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Helvetica"] = "Arial", ["Helvetica Neue"] = "Arial", ["SF Pro"] = "Segoe UI", ["SF Pro Text"] = "Segoe UI",
        ["SF Pro Display"] = "Segoe UI", [".AppleSystemUIFont"] = "Segoe UI", ["San Francisco"] = "Segoe UI",
        ["Menlo"] = "Consolas", ["Monaco"] = "Consolas", ["SF Mono"] = "Consolas", ["Courier"] = "Courier New",
        ["Times"] = "Times New Roman", ["PingFang SC"] = "Microsoft YaHei", ["PingFang TC"] = "Microsoft JhengHei",
        ["Hiragino Sans GB"] = "Microsoft YaHei", ["Heiti SC"] = "Microsoft YaHei", ["STHeiti"] = "Microsoft YaHei",
        ["Songti SC"] = "SimSun", ["STSong"] = "SimSun", ["Hiragino Sans"] = "Yu Gothic", ["Apple SD Gothic Neo"] = "Malgun Gothic",
    };

    /// <summary>
    /// A face by name: a family ("Arial"), or a PostScript-style name with a style ("Helvetica-Bold",
    /// "Arial-BoldItalicMT"), as Mac projects store them.
    /// </summary>
    public static SKTypeface Typeface(string name) => Typefaces.GetOrAdd(name, Resolve);

    /// <summary>Whether <paramref name="name"/> (a family or a PostScript-style name) is an installed face rather than a stand-in.</summary>
    public static bool IsInstalled(string name) => Installed(name) is not null;

    private static SKTypeface? Installed(string name)
    {
        var manager = SKFontManager.Default;
        if (Match(manager, name, SKFontStyle.Normal) is { } exact) return exact;
        var (family, style) = Split(name);
        if (family != name && Match(manager, family, style) is { } styled) return styled;
        // PostScript names run the family's words together ("MicrosoftYaHei", "TimesNewRomanPSMT", "ArialMT").
        if (PostScriptFamily(family) is { } spelled && Match(manager, spelled, style) is { } found) return found;
        return null;
    }

    private static (string Family, SKFontStyle Style) Split(string name)
    {
        string family = name, styleName = "";
        int dash = name.LastIndexOf('-');
        if (dash > 0) { family = name[..dash]; styleName = name[(dash + 1)..]; }
        var lower = styleName.ToLowerInvariant();
        var weight = lower.Contains("black") || lower.Contains("heavy") ? SKFontStyleWeight.Black
            : lower.Contains("bold") ? (lower.Contains("semi") || lower.Contains("demi") ? SKFontStyleWeight.SemiBold : SKFontStyleWeight.Bold)
            : lower.Contains("medium") ? SKFontStyleWeight.Medium
            : lower.Contains("light") ? SKFontStyleWeight.Light
            : lower.Contains("thin") ? SKFontStyleWeight.Thin : SKFontStyleWeight.Normal;
        var slant = lower.Contains("italic") || lower.Contains("oblique") ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright;
        return (family, new SKFontStyle(weight, SKFontStyleWidth.Normal, slant));
    }

    private static Dictionary<string, string>? _squashedFamilies;

    /// <summary>The installed family a run-together PostScript family name spells, with common foundry suffixes dropped.</summary>
    private static string? PostScriptFamily(string family)
    {
        var map = _squashedFamilies ??= SKFontManager.Default.FontFamilies
            .Where(f => !f.StartsWith('@'))
            .GroupBy(f => f.Replace(" ", "").ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.First());
        var key = family.Replace(" ", "").ToLowerInvariant();
        foreach (var suffix in new[] { "", "psmt", "mt", "ps", "std", "pro", "lt" })
        {
            if (suffix.Length > 0 && !key.EndsWith(suffix)) continue;
            if (map.TryGetValue(key[..^suffix.Length], out var found)) return found;
        }
        return null;
    }

    private static SKTypeface Resolve(string name)
    {
        var manager = SKFontManager.Default;
        if (Installed(name) is { } installed) return installed;
        // "Family-Style" names.
        string family = name, styleName = "";
        int dash = name.LastIndexOf('-');
        if (dash > 0) { family = name[..dash]; styleName = name[(dash + 1)..]; }
        var lower = styleName.ToLowerInvariant();
        var weight = lower.Contains("black") || lower.Contains("heavy") ? SKFontStyleWeight.Black
            : lower.Contains("bold") ? (lower.Contains("semi") || lower.Contains("demi") ? SKFontStyleWeight.SemiBold : SKFontStyleWeight.Bold)
            : lower.Contains("medium") ? SKFontStyleWeight.Medium
            : lower.Contains("light") ? SKFontStyleWeight.Light
            : lower.Contains("thin") ? SKFontStyleWeight.Thin : SKFontStyleWeight.Normal;
        var slant = lower.Contains("italic") || lower.Contains("oblique") ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright;
        var style = new SKFontStyle(weight, SKFontStyleWidth.Normal, slant);
        if (dash > 0 && Match(manager, family, style) is { } styled) return styled;
        if (Substitutes.TryGetValue(family, out var substitute) && Match(manager, substitute, style) is { } stand) return stand;
        if (Substitutes.TryGetValue(name, out substitute) && Match(manager, substitute, SKFontStyle.Normal) is { } stand2) return stand2;
        return manager.MatchFamily("Segoe UI", style) ?? SKTypeface.Default;
    }

    private static SKTypeface? Match(SKFontManager manager, string family, SKFontStyle style)
    {
        var typeface = manager.MatchFamily(family, style);
        if (typeface is null) return null;
        return string.Equals(typeface.FamilyName, family, StringComparison.OrdinalIgnoreCase) ? typeface : null;
    }

    /// <summary>The face that draws <paramref name="codePoint"/>: the chosen one if it has the letter, else one that does.</summary>
    private static SKTypeface FaceFor(SKTypeface chosen, int codePoint)
    {
        if (codePoint < 0x20 || chosen.ContainsGlyph(codePoint)) return chosen;
        return Fallbacks.GetOrAdd((chosen.FamilyName, chosen.FontWeight, (int)chosen.FontSlant, codePoint), key =>
            SKFontManager.Default.MatchCharacter(key.Family, chosen.FontStyle, new[] { CultureInfo.CurrentUICulture.Name, "zh-CN", "ja-JP" }, codePoint) ?? chosen);
    }

    /// <summary>Names of the installed font families, for the font menu.</summary>
    public static IReadOnlyList<string> Families() =>
        SKFontManager.Default.FontFamilies.Where(f => !f.StartsWith('@')).Distinct().OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>How far the primary face reaches below the baseline at the text's size.</summary>
    public static double Descent(LayerText style)
    {
        using var font = new SKFont(Typeface(style.FontName), (float)style.FontSize);
        return Math.Abs(font.Metrics.Descent);
    }

    private static bool IsCjk(int cp) =>
        cp is >= 0x2E80 and <= 0x9FFF or >= 0xAC00 and <= 0xD7AF or >= 0xF900 and <= 0xFAFF or >= 0xFF00 and <= 0xFFEF
            or >= 0x20000 and <= 0x2FFFF or >= 0x3000 and <= 0x303F;

    // MARK: Layout

    public static TextLayout Make(LayerText style)
    {
        double size = style.FontSize;
        var content = style.Content;
        var clusters = new List<Cluster>(content.Length);
        var fonts = new Dictionary<SKTypeface, SKFont>();
        SKFont FontFor(SKTypeface face)
        {
            if (!fonts.TryGetValue(face, out var font))
            {
                font = new SKFont(face, (float)size) { Subpixel = true, Edging = SKFontEdging.Antialias, Hinting = SKFontHinting.None };
                fonts[face] = font;
            }
            return font;
        }
        try
        {
            ushort previousGlyph = 0;
            SKTypeface? previousFace = null;
            for (int i = 0; i < content.Length;)
            {
                int length = char.IsHighSurrogate(content[i]) && i + 1 < content.Length && char.IsLowSurrogate(content[i + 1]) ? 2 : 1;
                int cp = length == 2 ? char.ConvertToUtf32(content[i], content[i + 1]) : content[i];
                bool isBreak = cp is '\n' or '\r' or 0x2028 or 0x2029;
                if (cp == '\r' && i + 1 < content.Length && content[i + 1] == '\n') length = 2; // CR LF is one break
                var face = FaceFor(Typeface(style.FontAt(i)), cp);
                double advance = 0, kern = 0;
                if (!isBreak)
                {
                    var font = FontFor(face);
                    ushort glyph = font.GetGlyph(cp);
                    advance = font.GetGlyphWidths(new[] { glyph }, null)[0];
                    if (cp == '\t') advance = font.GetGlyphWidths(new[] { font.GetGlyph(' ') }, null)[0] * 4;
                    if (ReferenceEquals(previousFace, face) && previousGlyph != 0 && glyph != 0)
                    {
                        var adjustments = face.GetKerningPairAdjustments(new[] { previousGlyph, glyph });
                        if (adjustments.Length > 0 && face.UnitsPerEm > 0) kern = adjustments[0] * size / face.UnitsPerEm;
                    }
                    previousGlyph = glyph;
                    previousFace = face;
                }
                else { previousGlyph = 0; previousFace = null; }
                clusters.Add(new Cluster(i, length, cp, face, advance, kern, style.ColorAt(i), isBreak,
                    cp is ' ' or '\t' or 0x3000, IsCjk(cp)));
                i += length;
            }
        }
        finally
        {
            foreach (var font in fonts.Values) font.Dispose();
        }

        double padding = LayerText.Padding, lineHeight = style.LineHeight;
        double tracking = style.Tracking;
        double wrap = style.BoxSize is { } box ? Math.Max(1, box.Width - 2 * padding) : double.PositiveInfinity;

        // Break into lines: (first cluster, past-the-last cluster, whether it ends with a line feed).
        var spans = new List<(int From, int To, bool HardBreak)>();
        int from = 0;
        while (true)
        {
            int to = from, lastBreak = -1;
            double width = 0;
            bool hard = false;
            while (to < clusters.Count)
            {
                var c = clusters[to];
                if (c.IsBreak) { hard = true; break; }
                double step = c.Advance + c.Kern + tracking;
                // Spaces may hang past the edge; anything else that doesn't fit starts a new line.
                if (!c.IsSpace && width + c.Kern + c.Advance > wrap + 0.01 && to > from)
                {
                    if (lastBreak > from) to = lastBreak;
                    break;
                }
                width += step;
                to++;
                // Break opportunities: after a space, and around CJK letters.
                if (c.IsSpace || c.IsCjk || (to < clusters.Count && clusters[to].IsCjk)) lastBreak = to;
            }
            spans.Add((from, to, hard));
            if (hard) { from = to + 1; continue; }
            if (to >= clusters.Count) break;
            from = to;
            // A wrapped line's leading spaces belong to the line before.
            while (from < clusters.Count && clusters[from].IsSpace && !clusters[from].IsBreak) from++;
            if (from >= clusters.Count) { spans.Add((from, from, false)); break; }
        }

        double descent = Descent(style);
        var measured = new List<(int From, int To, double Width, int[] Bounds, double[] Xs)>();
        foreach (var (start, end, _) in spans)
        {
            var bounds = new List<int>();
            var xs = new List<double>();
            double x = 0, visible = 0;
            for (int k = start; k < end; k++)
            {
                var c = clusters[k];
                x += c.Kern;
                bounds.Add(c.Start);
                xs.Add(x);
                x += c.Advance + tracking;
                if (!c.IsSpace) visible = x - tracking;
            }
            int endIndex = end < clusters.Count ? clusters[end].Start : content.Length;
            if (start == end && start < clusters.Count) endIndex = clusters[start].Start;
            bounds.Add(endIndex);
            xs.Add(x);
            measured.Add((start, end, visible, bounds.ToArray(), xs.ToArray()));
        }

        double widest = measured.Count == 0 ? 0 : measured.Max(m => m.Width);
        double boxWidth = style.BoxSize?.Width ?? Math.Max(16, Math.Ceiling(widest + padding * 2 + style.FontSize * 0.1));
        double boxHeight = style.BoxSize?.Height ?? Math.Max(16, Math.Ceiling(Math.Max(measured.Count * lineHeight, lineHeight) + padding * 2));
        double available = style.BoxSize is not null ? boxWidth - 2 * padding : widest;
        var lines = new List<Line>();
        for (int n = 0; n < measured.Count; n++)
        {
            var m = measured[n];
            double top = padding + n * lineHeight;
            double offset = style.Alignment switch
            {
                TextAlign.Center => (available - m.Width) / 2,
                TextAlign.Right => available - m.Width,
                _ => 0,
            };
            double x = padding + offset;
            int start = m.Bounds[0], end = m.Bounds[^1];
            lines.Add(new Line(start, end, top, top + lineHeight - descent, x, m.Width, m.Bounds, m.Xs.Select(v => x + v).ToArray()));
        }
        if (lines.Count == 0) lines.Add(new Line(0, 0, padding, padding + lineHeight - descent, padding, 0, new[] { 0 }, new[] { padding }));
        return new TextLayout(style, boxWidth, boxHeight, lines, clusters);
    }

    // MARK: Drawing

    /// <summary>Draws the letters in layer pixels (the box's top-left at the origin).</summary>
    public void Draw(SKCanvas canvas)
    {
        var fonts = new Dictionary<SKTypeface, SKFont>();
        using var paint = new SKPaint { IsAntialias = true };
        try
        {
            int clusterIndex = 0;
            foreach (var line in Lines)
            {
                for (int b = 0; b < line.Boundaries.Length - 1; b++)
                {
                    int start = line.Boundaries[b];
                    while (clusterIndex < _clusters.Count && _clusters[clusterIndex].Start < start) clusterIndex++;
                    if (clusterIndex >= _clusters.Count) break;
                    var c = _clusters[clusterIndex];
                    if (c.IsBreak || c.IsSpace || c.CodePoint < 0x20) continue;
                    if (!fonts.TryGetValue(c.Typeface, out var font))
                    {
                        font = new SKFont(c.Typeface, (float)Style.FontSize) { Subpixel = true, Edging = SKFontEdging.Antialias, Hinting = SKFontHinting.None };
                        fonts[c.Typeface] = font;
                    }
                    paint.Color = c.Color.ToSK();
                    canvas.DrawText(Style.Content.Substring(c.Start, c.Length), (float)line.Carets[b], (float)line.Baseline, SKTextAlign.Left, font, paint);
                }
            }
        }
        finally
        {
            foreach (var font in fonts.Values) font.Dispose();
        }
    }

    /// <summary>The text as a layer's pixels: the whole box, transparent where there are no letters.</summary>
    public RasterImage Render() => Render(L.T("文字", "Text"));

    /// <inheritdoc cref="Render()"/>
    public RasterImage Render(string name)
    {
        int w = Math.Max(1, (int)Math.Ceiling(Width)), h = Math.Max(1, (int)Math.Ceiling(Height));
        if ((long)w * h > DocumentLimits.MaxSurfacePixels || w > DocumentLimits.MaxSide || h > DocumentLimits.MaxSide)
            throw new InvalidOperationException(L.T("文字框太大。", "The text box is too large."));
        using var bitmap = new SKBitmap(RasterImage.ColorInfo(w, h));
        if (bitmap.GetPixels() == IntPtr.Zero) throw new InvalidOperationException(L.T("内存不足。", "Not enough memory."));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            Draw(canvas);
        }
        return RasterImage.FromBitmap(bitmap, name: name);
    }

    // MARK: Caret and hit-testing

    private int LineOf(int index)
    {
        for (int n = 0; n < Lines.Count; n++)
        {
            var line = Lines[n];
            if (index <= line.End && (n == Lines.Count - 1 || index < Lines[n + 1].Start)) return n;
        }
        return Lines.Count - 1;
    }

    /// <summary>Where the caret before UTF-16 offset <paramref name="index"/> stands: x, the line's top, and its height.</summary>
    public (double X, double Top, double Height) Caret(int index)
    {
        var line = Lines[LineOf(index)];
        int b = Array.FindLastIndex(line.Boundaries, v => v <= index);
        double x = b >= 0 ? line.Carets[b] : line.X;
        return (x, line.Top, Style.LineHeight);
    }

    /// <summary>The UTF-16 offset of the caret position nearest a point in layer pixels.</summary>
    public int IndexAt(PointD point)
    {
        int n = Math.Clamp((int)Math.Floor((point.Y - LayerText.Padding) / Style.LineHeight), 0, Lines.Count - 1);
        var line = Lines[n];
        int best = line.Boundaries[0];
        double distance = double.MaxValue;
        for (int b = 0; b < line.Boundaries.Length; b++)
        {
            double d = Math.Abs(line.Carets[b] - point.X);
            if (d < distance) { distance = d; best = line.Boundaries[b]; }
        }
        return best;
    }

    /// <summary>The rectangles a selection covers, line by line, in layer pixels.</summary>
    public IEnumerable<RectD> SelectionRects(int start, int end)
    {
        if (end <= start) yield break;
        foreach (var line in Lines)
        {
            int from = Math.Max(start, line.Start), to = Math.Min(end, line.End);
            bool coversBreak = end > line.End && start <= line.End && line != Lines[^1];
            if (from > to || (from == to && !coversBreak)) continue;
            double x0 = Caret(from).X, x1 = Caret(to).X + (coversBreak ? Style.FontSize * 0.25 : 0);
            yield return new RectD(x0, line.Top, Math.Max(1, x1 - x0), Style.LineHeight);
        }
    }

    /// <summary>The caret one line up or down from <paramref name="index"/>, keeping its x.</summary>
    public int Vertical(int index, int lines)
    {
        int n = Math.Clamp(LineOf(index) + lines, 0, Lines.Count - 1);
        var (x, _, _) = Caret(index);
        return IndexAt(new PointD(x, Lines[n].Top + Style.LineHeight / 2));
    }
}
