using System.Globalization;
using System.Text;
using Compositor.Core.Geometry;
using Compositor.Core.Localization;
using Compositor.Core.Model;
using Compositor.Core.Text;

namespace Compositor.Core.IO.Psd;

/// <summary>
/// Reads a Photoshop 6 type layer (<c>TySh</c>) into the editor's text model: the 2×3 transform, the text descriptor
/// and the warp descriptor, with the font, size, color, tracking, leading and alignment from the engine dictionary in
/// <c>EngineData</c>. What the model can't hold (vertical text, shear, uneven scale) stays pixels. Mirrors PSDText.swift.
/// </summary>
public static class PsdText
{
    /// <summary>A parsed type layer: its style and where its anchor lands on the document.</summary>
    public sealed record Source(LayerText Style, IReadOnlyList<string> Notes, PointD DocumentAnchor, double Rotation, bool FlipY, bool AnchorIsFrame);

    public static string FirstStyleNote => L.T("只保留了第一种文字样式。", "Only the first text style was kept.");
    public static string WarpNote => L.T("Photoshop 的文字变形已省略。", "The Photoshop text warp was omitted.");
    public static string FauxNote => L.T("仿粗体或仿斜体已省略。", "Faux bold or faux italic was omitted.");
    public static string JustifyNote => L.T("两端对齐已按左对齐导入。", "Full justification was imported as left alignment.");

    public static string? MissingFontNote(string name) =>
        TextLayout.IsInstalled(name) ? null
            : L.T($"没有安装字体“{name}”，文字改用其他字体显示。", $"The font “{name}” isn’t installed, so the text is shown in another font.");

    public static Source? Parse(IReadOnlyDictionary<string, byte[]> extra)
    {
        if (!(extra.TryGetValue("TySh", out var data) || extra.TryGetValue("tySh", out data)) || data.Length > 8_000_000) return null;
        var reader = new PsdDescriptorReader(data);
        if (reader.U16() != 1) return null;
        var m = new double[6];
        for (int i = 0; i < 6; i++)
        {
            if (reader.F64() is not { } v || !double.IsFinite(v)) return null;
            m[i] = v;
        }
        if (reader.U16() != 50 || reader.Descriptor(versioned: true) is not { } text) return null;
        if (text.Enum("Ornt") == "Vrtc") return null;
        if (Placement.Make(m[0], m[1], m[2], m[3], m[4], m[5]) is not { } placed) return null;

        var notes = new List<string>();
        if (reader.Remaining >= 2 && reader.U16() == 1 && reader.Descriptor(versioned: true) is { } warp
            && warp.Enum("warpStyle") is { } warpStyle && warpStyle is not ("warpNone" or "none"))
            notes.Add(WarpNote);

        var engine = text.Data("EngineData") is { } engineData ? Engine.Parse(engineData) : null;
        var content = Cleaned(text.String("Txt ") ?? text.String("Txt")) ?? (engine is null ? null : Cleaned(Engine.Walk(engine, "EngineDict", "Editor", "Text")?.Text));
        if (string.IsNullOrEmpty(content) || content.Length > LayerText.MaxLength) return null;

        var style = new LayerText { Content = content, Red = 0, Green = 0, Blue = 0 };
        style = engine is not null
            ? ApplyStyle(style, engine, placed.PixelScale, notes)
            : style with { FontSize = Math.Clamp(12 * placed.PixelScale, 1, 2000) };
        if (!double.IsFinite(style.FontSize) || style.FontSize <= 0) return null;

        var anchor = new PointD(placed.Tx, placed.Ty);
        bool anchorIsFrame = false;
        if (text.Rect("bounds") is { } bounds && text.Rect("boundingBox") is { } glyphs)
        {
            double bw = bounds.Right - bounds.Left, bh = bounds.Bottom - bounds.Top;
            double gw = glyphs.Right - glyphs.Left, gh = glyphs.Bottom - glyphs.Top;
            if (bw > gw + 4 && bh > gh + 4 && bw > 1 && bh > 1)
            {
                // A paragraph frame: kept as a box, or (when the model can't hold it) not imported as text at all,
                // since point text would lose the wrap without saying so.
                double pad = LayerText.Padding;
                var boxed = style with { BoxSize = new SizeD(bw * placed.PixelScale + pad * 2, bh * placed.PixelScale + pad * 2) };
                if (!boxed.IsValid) return null;
                style = boxed;
                anchor = placed.Map(new PointD(bounds.Left, bounds.Top));
                anchorIsFrame = true;
            }
        }
        if (!style.IsValid) return null;
        return new Source(style, notes, anchor, placed.Rotation, placed.FlipY, anchorIsFrame);
    }

    /// <summary>The text drawn and placed so its anchor lands where Photoshop's did.</summary>
    public static (RasterImage Image, LayerTransform Transform) Render(Source source, string name)
    {
        var layout = TextLayout.Make(source.Style);
        var image = layout.Render(name);
        var size = new SizeD(image.Width, image.Height);
        var imageAnchor = source.AnchorIsFrame
            ? new PointD(LayerText.Padding, LayerText.Padding)
            : new PointD(HorizontalAnchor(source.Style, size.Width), layout.Lines[0].Baseline);
        var transform = Placed(size, imageAnchor, source.DocumentAnchor, source.Rotation, source.FlipY);
        if (!transform.IsValid) throw new InvalidOperationException(L.T("文字太大。", "The text is too large."));
        return (image, transform);
    }

    private static double HorizontalAnchor(LayerText style, double width) => style.Alignment switch
    {
        TextAlign.Center => width / 2,
        TextAlign.Right => width - LayerText.Padding,
        _ => LayerText.Padding,
    };

    /// <summary>Matches LayerTransform.PixelToDocument: flip, then clockwise rotation about the center.</summary>
    private static LayerTransform Placed(SizeD image, PointD imageAnchor, PointD documentAnchor, double rotation, bool flipY)
    {
        double lx = imageAnchor.X - image.Width / 2, ly = imageAnchor.Y - image.Height / 2;
        if (flipY) ly = -ly;
        double radians = rotation * Math.PI / 180;
        double rx = lx * Math.Cos(radians) - ly * Math.Sin(radians), ry = lx * Math.Sin(radians) + ly * Math.Cos(radians);
        var center = new PointD(documentAnchor.X - rx, documentAnchor.Y - ry);
        return new LayerTransform(new PointD(center.X - image.Width / 2, center.Y - image.Height / 2), image, rotation, flipY: flipY);
    }

    /// <summary>Uniform scale, rotation and an optional vertical flip; shear and uneven scale are refused.</summary>
    private sealed record Placement(double PixelScale, double Rotation, bool FlipY, double Tx, double Ty, double Exx, double Exy, double Eyx, double Eyy)
    {
        public PointD Map(PointD p) => new(Exx * p.X + Exy * p.Y + Tx, Eyx * p.X + Eyy * p.Y + Ty);

        public static Placement? Make(double xx, double xy, double yx, double yy, double tx, double ty)
        {
            double scaleX = Math.Sqrt(xx * xx + yx * yx);
            if (scaleX <= 1e-6) return null;
            double cos = xx / scaleX, sin = yx / scaleX;
            double localX = cos * xy + sin * yy, localY = -sin * xy + cos * yy;
            double scaleY = Math.Abs(localY);
            if (scaleY <= 1e-6) return null;
            double largest = Math.Max(scaleX, scaleY);
            if (Math.Abs(localX) > 0.02 * largest || Math.Abs(scaleX - scaleY) > 0.02 * largest) return null;
            double sign = localY < 0 ? -1 : 1;
            return new Placement(scaleX, Math.Atan2(sin, cos) * 180 / Math.PI, localY < 0, tx, ty,
                cos * scaleX, -sin * scaleX * sign, sin * scaleX, cos * scaleX * sign);
        }
    }

    private static LayerText ApplyStyle(LayerText style, Engine.Value engine, double pixelScale, List<string> notes)
    {
        var runs = Engine.Array(Engine.Walk(engine, "EngineDict", "StyleRun", "RunArray"));
        var first = runs.FirstOrDefault() ?? engine;
        var data = Engine.Walk(first, "StyleSheet", "StyleSheetData") ?? first;
        double points = Engine.Walk(data, "FontSize")?.Number ?? 12;
        if (!double.IsFinite(points) || points <= 0) return style;
        style = style with { FontSize = Math.Clamp(points * pixelScale, 1, 2000) };
        var fonts = Engine.Array(Engine.Walk(engine, "ResourceDict", "FontSet"));
        int index = (int)Math.Round(Engine.Walk(data, "Font")?.Number ?? 0);
        if (index >= 0 && index < fonts.Count && Engine.Walk(fonts[index], "Name")?.Text is { Length: > 0 } fontName)
            style = style with { FontName = fontName };
        var channels = Engine.Array(Engine.Walk(data, "FillColor", "Values")).Where(v => v.Number is not null).Select(v => v.Number!.Value).ToList();
        if (channels.Count > 0)
        {
            var (r, g, b) = Color(channels);
            style = style with { Red = r, Green = g, Blue = b };
        }
        double tracking = Engine.Walk(data, "Tracking")?.Number ?? 0;
        if (double.IsFinite(tracking)) style = style with { Tracking = Math.Clamp(tracking * style.FontSize / 1000, -100, 1000) };
        bool auto = Engine.Walk(data, "AutoLeading")?.Bool ?? true;
        if (!auto && Engine.Walk(data, "Leading")?.Number is { } leading && double.IsFinite(leading) && leading > 0)
            style = style with { Leading = Math.Clamp(leading * pixelScale, 0, 5000) };
        if (Engine.Walk(data, "FauxBold")?.Bool == true || Engine.Walk(data, "FauxItalic")?.Bool == true) notes.Add(FauxNote);
        if (runs.Count > 1 && runs.Skip(1).Any(run => Signature(run) != Signature(first))) notes.Add(FirstStyleNote);
        var paragraphs = Engine.Array(Engine.Walk(engine, "EngineDict", "ParagraphRun", "RunArray"));
        double justification = Engine.Walk(paragraphs.FirstOrDefault() ?? engine, "ParagraphSheet", "Properties", "Justification")?.Number ?? 0;
        switch ((int)Math.Round(justification))
        {
            case 1: style = style with { Alignment = TextAlign.Right }; break;
            case 2: style = style with { Alignment = TextAlign.Center }; break;
            case 0: style = style with { Alignment = TextAlign.Left }; break;
            default: style = style with { Alignment = TextAlign.Left }; notes.Add(JustifyNote); break;
        }
        return style;
    }

    private static string Signature(Engine.Value run)
    {
        var data = Engine.Walk(run, "StyleSheet", "StyleSheetData") ?? run;
        double N(string key, double fallback) => Engine.Walk(data, key)?.Number ?? fallback;
        bool B(string key, bool fallback) => Engine.Walk(data, key)?.Bool ?? fallback;
        var channels = Engine.Array(Engine.Walk(data, "FillColor", "Values")).Where(v => v.Number is not null).Select(v => v.Number!.Value).ToList();
        var (r, g, b) = Color(channels);
        return string.Join("|", new object[]
        {
            N("Font", 0), N("FontSize", 0), N("Tracking", 0), B("AutoLeading", true), N("Leading", 0), N("HorizontalScale", 1),
            N("VerticalScale", 1), B("FauxBold", false), B("FauxItalic", false), r, g, b,
        }.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture)));
    }

    /// <summary>Photoshop's fill color: ARGB (0–1 or 0–255), RGB, or a gray.</summary>
    private static (double, double, double) Color(IReadOnlyList<double> values)
    {
        static double Unit(double v) => v > 1 ? Math.Clamp(v, 0, 255) / 255 : Math.Clamp(v, 0, 1);
        if (values.Count >= 4) return (Unit(values[1]), Unit(values[2]), Unit(values[3]));
        if (values.Count == 3) return (Unit(values[0]), Unit(values[1]), Unit(values[2]));
        if (values.Count > 0) { double g = Unit(values[0]); return (g, g, g); }
        return (0, 0, 0);
    }

    private static string? Cleaned(string? text)
    {
        if (text is null) return null;
        text = text.TrimStart('﻿', '\0').TrimEnd('\0');
        return text.Replace("\r\n", "\n").Replace('\r', '\n');
    }

    /// <summary>Photoshop's text-engine dictionary: a small PostScript-like language (<c>&lt;&lt; &gt;&gt;</c>, arrays, names, numbers, strings).</summary>
    internal static class Engine
    {
        internal sealed class Value
        {
            public double? Number;
            public bool? Bool;
            public string? Text;
            public Dictionary<string, Value>? Dict;
            public List<Value>? List;
        }

        public static Value? Walk(Value? value, params string[] keys)
        {
            var current = value;
            foreach (var key in keys)
            {
                if (current?.Dict is not { } items || !items.TryGetValue(key, out var next)) return null;
                current = next;
            }
            return current;
        }

        public static List<Value> Array(Value? value) => value?.List ?? new List<Value>();

        public static Value? Parse(byte[] data)
        {
            if (Dictionary(data, 0) is { } top) return top;
            int start = IndexOf(data, (byte)'<', (byte)'<');
            return start > 0 ? Dictionary(data, start) : null;
        }

        private static int IndexOf(byte[] data, byte a, byte b)
        {
            for (int i = 0; i + 1 < data.Length; i++) if (data[i] == a && data[i + 1] == b) return i;
            return -1;
        }

        private static Value? Dictionary(byte[] data, int start)
        {
            var cursor = new Cursor(data) { Index = start };
            return cursor.ParseValue() is { Dict: not null } dict ? dict : null;
        }

        private sealed class Cursor
        {
            private readonly byte[] _bytes;
            public int Index;
            private int _depth;

            public Cursor(byte[] bytes) => _bytes = bytes;

            private int? Peek => Index < _bytes.Length ? _bytes[Index] : null;
            private int? PeekAhead(int ahead) => Index + ahead < _bytes.Length ? _bytes[Index + ahead] : null;

            public Value? ParseValue()
            {
                SkipWhitespace();
                if (Peek is not { } b) return null;
                if (b == '<') return PeekAhead(1) == '<' ? ParseDictionary() : ParseHex();
                if (b == '[') return ParseArray();
                if (b == '(') return ParseString();
                if (b == '/') { Index++; return new Value { Text = ReadToken() }; }
                if (b is '-' or '+' or '.' || (b >= '0' && b <= '9')) return ParseNumber() is { } n ? new Value { Number = n } : null;
                if (TakeWord("true")) return new Value { Bool = true };
                if (TakeWord("false")) return new Value { Bool = false };
                if (TakeWord("null")) return new Value { Text = "" };
                return null;
            }

            private Value? ParseDictionary()
            {
                if (!Take("<<") || ++_depth > 200) return null;
                var items = new Dictionary<string, Value>();
                while (true)
                {
                    SkipWhitespace();
                    if (Peek is null or '>') break;
                    if (Peek != '/') return null;
                    Index++;
                    var key = ReadToken();
                    if (ParseValue() is not { } value) return null;
                    items[key] = value;
                }
                _depth--;
                return Take(">>") ? new Value { Dict = items } : null;
            }

            private Value? ParseArray()
            {
                if (!Take("[") || ++_depth > 200) return null;
                var items = new List<Value>();
                while (true)
                {
                    SkipWhitespace();
                    if (Peek is null or ']') break;
                    if (ParseValue() is not { } value) return null;
                    items.Add(value);
                }
                _depth--;
                return Take("]") ? new Value { List = items } : null;
            }

            private double? ParseNumber()
            {
                int start = Index;
                if (Peek is '+' or '-') Index++;
                while (Peek is >= '0' and <= '9') Index++;
                if (Peek == '.')
                {
                    Index++;
                    while (Peek is >= '0' and <= '9') Index++;
                }
                if (Peek is 'e' or 'E')
                {
                    Index++;
                    if (Peek is '+' or '-') Index++;
                    while (Peek is >= '0' and <= '9') Index++;
                }
                if (Index <= start) return null;
                var text = Encoding.ASCII.GetString(_bytes, start, Index - start);
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
            }

            private Value? ParseString()
            {
                if (!Take("(")) return null;
                var raw = new List<byte>();
                while (Peek is { } b)
                {
                    Index++;
                    if (b == ')') break;
                    if (b == '\\')
                    {
                        if (Peek is not { } escaped) return null;
                        Index++;
                        if (escaped == 'n') raw.Add(0x0A);
                        else if (escaped == 'r') raw.Add(0x0D);
                        else if (escaped == 't') raw.Add(0x09);
                        else if (escaped is >= '0' and <= '7')
                        {
                            int value = escaped - '0';
                            for (int k = 0; k < 2 && Peek is >= '0' and <= '7'; k++) value = value * 8 + (_bytes[Index++] - '0');
                            raw.Add((byte)(value & 0xFF));
                        }
                        else if (escaped is not ('\n' or '\r')) raw.Add((byte)escaped);
                    }
                    else raw.Add((byte)b);
                }
                return new Value { Text = Decode(raw.ToArray()) };
            }

            private Value? ParseHex()
            {
                if (!Take("<")) return null;
                var nibbles = new List<int>();
                while (Peek is { } b && b != '>')
                {
                    Index++;
                    int n = b switch { >= '0' and <= '9' => b - '0', >= 'a' and <= 'f' => b - 'a' + 10, >= 'A' and <= 'F' => b - 'A' + 10, _ => -1 };
                    if (n >= 0) nibbles.Add(n);
                }
                if (!Take(">")) return null;
                var raw = new byte[nibbles.Count / 2];
                for (int i = 0; i < raw.Length; i++) raw[i] = (byte)(nibbles[2 * i] << 4 | nibbles[2 * i + 1]);
                return new Value { Text = Decode(raw) };
            }

            private static string Decode(byte[] raw) =>
                raw.Length >= 2 && raw[0] == 0xFE && raw[1] == 0xFF
                    ? Encoding.BigEndianUnicode.GetString(raw, 2, raw.Length - 2)
                    : Encoding.Latin1.GetString(raw);

            private string ReadToken()
            {
                int start = Index;
                while (Peek is { } b && !IsDelimiter(b)) Index++;
                return Encoding.ASCII.GetString(_bytes, start, Index - start);
            }

            private static bool IsDelimiter(int b) => b <= 0x20 || b is '/' or '<' or '>' or '[' or ']' or '(' or ')';

            private bool TakeWord(string word)
            {
                if (!Matches(word)) return false;
                int after = Index + word.Length;
                if (after < _bytes.Length && !IsDelimiter(_bytes[after])) return false;
                Index = after;
                return true;
            }

            private bool Take(string token)
            {
                if (!Matches(token)) return false;
                Index += token.Length;
                return true;
            }

            private bool Matches(string token)
            {
                if (Index + token.Length > _bytes.Length) return false;
                for (int i = 0; i < token.Length; i++) if (_bytes[Index + i] != token[i]) return false;
                return true;
            }

            private void SkipWhitespace()
            {
                while (Peek is { } b && (b <= 0x20 || b == '%'))
                {
                    if (b == '%') { while (Peek is { } c && c != '\n' && c != '\r') Index++; }
                    else Index++;
                }
            }
        }
    }
}
