using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Compositor.Core.Geometry;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Core.IO;

/// <summary>
/// Draws an SVG file once into pixels, as the Mac app does with the system's renderer: fitted to the canvas when there
/// is one, otherwise at the size the file declares. It comes in as an ordinary image layer. Covers the static subset
/// of SVG most files use: shapes and paths, fills and strokes, gradients, groups and transforms, &lt;use&gt;, CSS from
/// &lt;style&gt;, clip paths, simple text and embedded images. No scripts, animation, filters or external references.
/// </summary>
public static class SvgRenderer
{
    public static bool IsSvgPath(string path) =>
        path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".svgz", StringComparison.OrdinalIgnoreCase);

    /// <summary>The file's declared size in pixels (300 × 150 when it declares none, as browsers do).</summary>
    public static SizeD IntrinsicSize(XElement root)
    {
        var viewBox = ViewBox(root);
        double? width = Length(root.Attribute("width")?.Value, 0), height = Length(root.Attribute("height")?.Value, 0);
        if (width is > 0 && height is > 0) return new SizeD(width.Value, height.Value);
        if (viewBox is { } box && box.Width > 0 && box.Height > 0)
        {
            if (width is > 0) return new SizeD(width.Value, width.Value * box.Height / box.Width);
            if (height is > 0) return new SizeD(height.Value * box.Width / box.Height, height.Value);
            return new SizeD(box.Width, box.Height);
        }
        return new SizeD(width ?? 300, height ?? 150);
    }

    public static RasterImage Render(string path, SizeD? fitting, long remainingPixels = long.MaxValue)
    {
        var root = Load(path);
        var size = IntrinsicSize(root);
        if (size.Width <= 0 || size.Height <= 0) throw ImageImportException.Unreadable();
        double scale = fitting is { Width: > 0, Height: > 0 } f ? Math.Min(f.Width / size.Width, f.Height / size.Height) : 1;
        int width = Math.Max(1, (int)Math.Round(size.Width * scale)), height = Math.Max(1, (int)Math.Round(size.Height * scale));
        if (width > DocumentLimits.MaxSide || height > DocumentLimits.MaxSide || (long)width * height > Math.Min(remainingPixels, DocumentLimits.MaxSurfacePixels))
            throw ImageImportException.TooLarge();
        return RenderRoot(root, width, height, Path.GetFileNameWithoutExtension(path));
    }

    public static RasterImage RenderString(string xml, int width, int height, string name = "SVG") => RenderRoot(Parse(xml), width, height, name);

    private static XElement Load(string path)
    {
        using var file = File.OpenRead(path);
        Stream stream = file;
        if (path.EndsWith(".svgz", StringComparison.OrdinalIgnoreCase)) stream = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress);
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    public static XElement Parse(string xml)
    {
        try
        {
            // No DTDs and no external resolution: an SVG can't reach outside itself.
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = 64_000_000 };
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            var root = XDocument.Load(reader).Root;
            if (root is null || root.Name.LocalName != "svg") throw ImageImportException.Unreadable();
            return root;
        }
        catch (XmlException) { throw ImageImportException.Unreadable(); }
    }

    private static RasterImage RenderRoot(XElement root, int width, int height, string name)
    {
        using var bitmap = new SKBitmap(RasterImage.ColorInfo(width, height));
        bitmap.Erase(SKColors.Transparent);
        using (var canvas = new SKCanvas(bitmap))
        {
            var context = new Context(root);
            var size = IntrinsicSize(root);
            canvas.Scale((float)(width / size.Width), (float)(height / size.Height));
            context.DrawViewport(canvas, root, new SKRect(0, 0, (float)size.Width, (float)size.Height), Style.Initial);
        }
        return RasterImage.FromBitmap(bitmap, name: name);
    }

    private static SKRect? ViewBox(XElement element)
    {
        var values = Numbers(element.Attribute("viewBox")?.Value);
        return values.Count == 4 && values[2] > 0 && values[3] > 0 ? new SKRect(values[0], values[1], values[0] + values[2], values[1] + values[3]) : null;
    }

    // MARK: Values

    private static readonly Regex NumberPattern = new(@"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?", RegexOptions.Compiled);

    private static List<float> Numbers(string? text) =>
        text is null ? new List<float>() : NumberPattern.Matches(text).Select(m => float.Parse(m.Value, CultureInfo.InvariantCulture)).ToList();

    /// <summary>A length in user units; percentages of <paramref name="reference"/>.</summary>
    private static double? Length(string? text, double reference, double fontSize = 16)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        var match = NumberPattern.Match(text);
        if (!match.Success || match.Index != 0) return null;
        double value = double.Parse(match.Value, CultureInfo.InvariantCulture);
        string unit = text[match.Length..].Trim().ToLowerInvariant();
        return unit switch
        {
            "" or "px" => value,
            "pt" => value * 4 / 3,
            "pc" => value * 16,
            "mm" => value * 96 / 25.4,
            "cm" => value * 96 / 2.54,
            "in" => value * 96,
            "em" => value * fontSize,
            "ex" => value * fontSize / 2,
            "%" => value * reference / 100,
            _ => value,
        };
    }

    private static SKMatrix Transform(string? text)
    {
        var result = SKMatrix.Identity;
        if (string.IsNullOrWhiteSpace(text)) return result;
        foreach (Match m in Regex.Matches(text, @"(matrix|translate|scale|rotate|skewX|skewY)\s*\(([^)]*)\)"))
        {
            var a = Numbers(m.Groups[2].Value);
            SKMatrix t = SKMatrix.Identity;
            switch (m.Groups[1].Value)
            {
                case "matrix" when a.Count == 6: t = new SKMatrix(a[0], a[2], a[4], a[1], a[3], a[5], 0, 0, 1); break;
                case "translate" when a.Count >= 1: t = SKMatrix.CreateTranslation(a[0], a.Count > 1 ? a[1] : 0); break;
                case "scale" when a.Count >= 1: t = SKMatrix.CreateScale(a[0], a.Count > 1 ? a[1] : a[0]); break;
                case "rotate" when a.Count >= 1:
                    t = a.Count >= 3 ? SKMatrix.CreateRotationDegrees(a[0], a[1], a[2]) : SKMatrix.CreateRotationDegrees(a[0]);
                    break;
                case "skewX" when a.Count == 1: t = SKMatrix.CreateSkew((float)Math.Tan(a[0] * Math.PI / 180), 0); break;
                case "skewY" when a.Count == 1: t = SKMatrix.CreateSkew(0, (float)Math.Tan(a[0] * Math.PI / 180)); break;
            }
            result = result.PreConcat(t);
        }
        return result;
    }

    private static readonly Dictionary<string, SKColor> NamedColors = typeof(SKColors)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(f => f.FieldType == typeof(SKColor))
        .ToDictionary(f => f.Name.ToLowerInvariant(), f => (SKColor)f.GetValue(null)!);

    private static SKColor? Color(string? text, SKColor current)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim().ToLowerInvariant();
        if (text == "currentcolor") return current;
        if (text == "transparent") return SKColors.Transparent;
        if (text.StartsWith('#'))
        {
            var hex = text[1..];
            if (hex.Length is 3 or 4) hex = string.Concat(hex.Select(c => $"{c}{c}"));
            if (hex.Length == 6 && uint.TryParse(hex, NumberStyles.HexNumber, null, out var rgb)) return new SKColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            if (hex.Length == 8 && uint.TryParse(hex, NumberStyles.HexNumber, null, out var rgba)) return new SKColor((byte)(rgba >> 24), (byte)(rgba >> 16), (byte)(rgba >> 8), (byte)rgba);
            return null;
        }
        var function = Regex.Match(text, @"^(rgba?|hsla?)\(([^)]*)\)$");
        if (function.Success)
        {
            var parts = function.Groups[2].Value.Split(new[] { ',', ' ', '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return null;
            double Part(string p, double full) => p.EndsWith('%') ? double.Parse(p[..^1], CultureInfo.InvariantCulture) * full / 100 : double.Parse(p, CultureInfo.InvariantCulture);
            try
            {
                double alpha = parts.Length > 3 ? Part(parts[3], 1) : 1;
                if (function.Groups[1].Value.StartsWith("rgb"))
                    return new SKColor(ToByte(Part(parts[0], 255)), ToByte(Part(parts[1], 255)), ToByte(Part(parts[2], 255)), ToByte(alpha * 255));
                var hsl = SKColor.FromHsl((float)double.Parse(parts[0].TrimEnd('d', 'e', 'g'), CultureInfo.InvariantCulture),
                    (float)Part(parts[1], 100), (float)Part(parts[2], 100));
                return hsl.WithAlpha(ToByte(alpha * 255));
            }
            catch (FormatException) { return null; }
        }
        return NamedColors.TryGetValue(text, out var named) ? named : null;
    }

    private static byte ToByte(double v) => (byte)Math.Round(Math.Clamp(v, 0, 255));

    // MARK: Styles

    /// <summary>The inherited presentation properties at one element.</summary>
    private sealed record Style
    {
        public string Fill = "black";
        public string Stroke = "none";
        public double StrokeWidth = 1;
        public double FillOpacity = 1, StrokeOpacity = 1;
        public string FillRule = "nonzero";
        public string LineCap = "butt", LineJoin = "miter";
        public double MiterLimit = 4;
        public string? DashArray;
        public double DashOffset;
        public double FontSize = 16;
        public string FontFamily = "sans-serif";
        public string FontWeight = "normal", FontStyle = "normal";
        public string TextAnchor = "start";
        public bool Visible = true;
        public SKColor CurrentColor = SKColors.Black;
        public static readonly Style Initial = new();
    }

    private sealed class Context
    {
        private readonly Dictionary<string, XElement> _ids = new();
        private readonly List<(string Selector, Dictionary<string, string> Declarations, int Specificity, int Order)> _rules = new();
        private int _depth;

        public Context(XElement root)
        {
            foreach (var element in root.DescendantsAndSelf())
            {
                if (element.Attribute("id")?.Value is { Length: > 0 } id) _ids.TryAdd(id, element);
                if (element.Name.LocalName == "style") ParseCss(element.Value);
            }
        }

        private void ParseCss(string css)
        {
            css = Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline);
            foreach (Match rule in Regex.Matches(css, @"([^{}@]+)\{([^}]*)\}"))
            {
                var declarations = Declarations(rule.Groups[2].Value);
                foreach (var raw in rule.Groups[1].Value.Split(','))
                {
                    var selector = raw.Trim();
                    if (selector.Length == 0 || selector.Contains(' ') || selector.Contains('>') || selector.Contains(':')) continue;
                    int specificity = selector.StartsWith('#') ? 100 : selector.Contains('.') ? 10 : 1;
                    _rules.Add((selector, declarations, specificity, _rules.Count));
                }
            }
        }

        private static Dictionary<string, string> Declarations(string text)
        {
            var result = new Dictionary<string, string>();
            foreach (var part in text.Split(';'))
            {
                int colon = part.IndexOf(':');
                if (colon <= 0) continue;
                result[part[..colon].Trim().ToLowerInvariant()] = part[(colon + 1)..].Replace("!important", "").Trim();
            }
            return result;
        }

        private bool Matches(string selector, XElement element)
        {
            string tag = element.Name.LocalName;
            var classes = (element.Attribute("class")?.Value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (selector.StartsWith('#')) return element.Attribute("id")?.Value == selector[1..];
            var pieces = selector.Split('.');
            if (pieces[0].Length > 0 && pieces[0] != "*" && pieces[0] != tag) return false;
            return pieces.Skip(1).All(classes.Contains);
        }

        /// <summary>Presentation attributes, then matching CSS rules, then the style attribute.</summary>
        private Dictionary<string, string> Properties(XElement element)
        {
            var result = new Dictionary<string, string>();
            foreach (var attribute in element.Attributes()) result[attribute.Name.LocalName] = attribute.Value;
            foreach (var rule in _rules.Where(r => Matches(r.Selector, element)).OrderBy(r => r.Specificity).ThenBy(r => r.Order))
                foreach (var (key, value) in rule.Declarations) result[key] = value;
            if (element.Attribute("style")?.Value is { } inline)
                foreach (var (key, value) in Declarations(inline)) result[key] = value;
            return result;
        }

        private static Style Inherit(Style parent, Dictionary<string, string> p)
        {
            var s = parent with { };
            string? Get(string key) => p.TryGetValue(key, out var v) && v != "inherit" ? v : null;
            if (Get("color") is { } color && Color(color, parent.CurrentColor) is { } c) s.CurrentColor = c;
            if (Get("fill") is { } fill) s.Fill = fill;
            if (Get("stroke") is { } stroke) s.Stroke = stroke;
            if (Get("font-size") is { } fontSize && Length(fontSize, parent.FontSize, parent.FontSize) is { } fs) s.FontSize = fs;
            if (Get("stroke-width") is { } sw && Length(sw, 100, s.FontSize) is { } w) s.StrokeWidth = w;
            if (Get("fill-opacity") is { } fo && Opacity(fo) is { } f) s.FillOpacity = f;
            if (Get("stroke-opacity") is { } so && Opacity(so) is { } o) s.StrokeOpacity = o;
            if (Get("fill-rule") is { } rule) s.FillRule = rule;
            if (Get("stroke-linecap") is { } cap) s.LineCap = cap;
            if (Get("stroke-linejoin") is { } join) s.LineJoin = join;
            if (Get("stroke-miterlimit") is { } miter && double.TryParse(miter, NumberStyles.Float, CultureInfo.InvariantCulture, out var m)) s.MiterLimit = m;
            if (Get("stroke-dasharray") is { } dash) s.DashArray = dash;
            if (Get("stroke-dashoffset") is { } dashOffset && Length(dashOffset, 100) is { } d) s.DashOffset = d;
            if (Get("font-family") is { } family) s.FontFamily = family;
            if (Get("font-weight") is { } weight) s.FontWeight = weight;
            if (Get("font-style") is { } fontStyle) s.FontStyle = fontStyle;
            if (Get("text-anchor") is { } anchor) s.TextAnchor = anchor;
            if (Get("visibility") is { } visibility) s.Visible = visibility is not ("hidden" or "collapse");
            return s;
        }

        private static double? Opacity(string text)
        {
            text = text.Trim();
            bool percent = text.EndsWith('%');
            if (!double.TryParse(percent ? text[..^1] : text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return null;
            return Math.Clamp(percent ? v / 100 : v, 0, 1);
        }

        // MARK: Drawing

        /// <summary>An &lt;svg&gt; element: its viewBox fitted into <paramref name="viewport"/> as preserveAspectRatio says.</summary>
        public void DrawViewport(SKCanvas canvas, XElement svg, SKRect viewport, Style parent)
        {
            var properties = Properties(svg);
            var style = Inherit(parent, properties);
            canvas.Save();
            if (ViewBox(svg) is { } box)
            {
                var aspect = (svg.Attribute("preserveAspectRatio")?.Value ?? "xMidYMid meet").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                float sx = viewport.Width / box.Width, sy = viewport.Height / box.Height;
                float tx = viewport.Left, ty = viewport.Top;
                if (aspect[0] != "none")
                {
                    float s = aspect.Length > 1 && aspect[1] == "slice" ? Math.Max(sx, sy) : Math.Min(sx, sy);
                    float extraX = viewport.Width - box.Width * s, extraY = viewport.Height - box.Height * s;
                    string align = aspect[0];
                    tx += align.Contains("xMid") ? extraX / 2 : align.Contains("xMax") ? extraX : 0;
                    ty += align.Contains("YMid") ? extraY / 2 : align.Contains("YMax") ? extraY : 0;
                    sx = sy = s;
                }
                canvas.ClipRect(viewport);
                canvas.Translate(tx, ty);
                canvas.Scale(sx, sy);
                canvas.Translate(-box.Left, -box.Top);
            }
            else canvas.Translate(viewport.Left, viewport.Top);
            foreach (var child in svg.Elements()) Draw(canvas, child, style);
            canvas.Restore();
        }

        private void Draw(SKCanvas canvas, XElement element, Style parent)
        {
            if (_depth > 64) return; // a <use> loop
            string tag = element.Name.LocalName;
            if (tag is "defs" or "style" or "title" or "desc" or "metadata" or "symbol" or "clipPath" or "mask" or "linearGradient"
                or "radialGradient" or "pattern" or "filter" or "marker" or "script") return;
            var p = Properties(element);
            if (p.TryGetValue("display", out var display) && display.Trim() == "none") return;
            var style = Inherit(parent, p);
            double opacity = p.TryGetValue("opacity", out var op) ? Opacity(op) ?? 1 : 1;
            if (opacity <= 0) return;
            _depth++;
            canvas.Save();
            try
            {
                canvas.Concat(Transform(p.GetValueOrDefault("transform")));
                if (p.TryGetValue("clip-path", out var clip) && Reference(clip) is { } clipElement)
                {
                    using var clipPath = ClipPath(clipElement, element);
                    if (clipPath is not null) canvas.ClipPath(clipPath, antialias: true);
                }
                if (opacity < 1) canvas.SaveLayer(new SKPaint { Color = SKColors.Black.WithAlpha(ToByte(opacity * 255)) });
                switch (tag)
                {
                    case "svg":
                    {
                        double x = Length(p.GetValueOrDefault("x"), 0) ?? 0, y = Length(p.GetValueOrDefault("y"), 0) ?? 0;
                        var bounds = canvas.LocalClipBounds;
                        double w = Length(p.GetValueOrDefault("width"), bounds.Width) ?? bounds.Width;
                        double h = Length(p.GetValueOrDefault("height"), bounds.Height) ?? bounds.Height;
                        DrawViewport(canvas, element, new SKRect((float)x, (float)y, (float)(x + w), (float)(y + h)), parent);
                        break;
                    }
                    case "g" or "a" or "switch":
                        foreach (var child in element.Elements()) Draw(canvas, child, style);
                        break;
                    case "use":
                        if (Reference(p.GetValueOrDefault("href")) is { } target)
                        {
                            canvas.Translate((float)(Length(p.GetValueOrDefault("x"), 0) ?? 0), (float)(Length(p.GetValueOrDefault("y"), 0) ?? 0));
                            if (target.Name.LocalName == "symbol")
                            {
                                var bounds = canvas.LocalClipBounds;
                                double w = Length(p.GetValueOrDefault("width"), 0) ?? Length(target.Attribute("width")?.Value, 0) ?? bounds.Width;
                                double h = Length(p.GetValueOrDefault("height"), 0) ?? Length(target.Attribute("height")?.Value, 0) ?? bounds.Height;
                                DrawViewport(canvas, target, new SKRect(0, 0, (float)w, (float)h), style);
                            }
                            else Draw(canvas, target, style);
                        }
                        break;
                    case "text":
                        DrawText(canvas, element, style);
                        break;
                    case "image":
                        DrawImage(canvas, p);
                        break;
                    default:
                        if (Shape(tag, p, style) is { } path)
                        {
                            using (path)
                            {
                                if (style.Visible) Paint(canvas, path, style, element);
                            }
                        }
                        break;
                }
                if (opacity < 1) canvas.Restore();
            }
            finally
            {
                canvas.Restore();
                _depth--;
            }
        }

        private XElement? Reference(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var match = Regex.Match(text, @"#([^)'""\s]+)");
            return match.Success && _ids.TryGetValue(match.Groups[1].Value, out var element) ? element : null;
        }

        private static SKPath? Shape(string tag, Dictionary<string, string> p, Style style)
        {
            double L(string key, double fallback = 0) => Length(p.GetValueOrDefault(key), 100, style.FontSize) ?? fallback;
            SKPath? path = null;
            switch (tag)
            {
                case "path":
                    path = p.TryGetValue("d", out var d) ? SKPath.ParseSvgPathData(d) : null;
                    break;
                case "rect":
                {
                    double w = L("width"), h = L("height");
                    if (w <= 0 || h <= 0) return null;
                    double rx = L("rx", -1), ry = L("ry", -1);
                    if (rx < 0) rx = Math.Max(0, ry);
                    if (ry < 0) ry = rx;
                    path = new SKPath();
                    var rect = SKRect.Create((float)L("x"), (float)L("y"), (float)w, (float)h);
                    if (rx > 0) path.AddRoundRect(rect, (float)Math.Min(rx, w / 2), (float)Math.Min(ry, h / 2));
                    else path.AddRect(rect);
                    break;
                }
                case "circle":
                {
                    double r = L("r");
                    if (r <= 0) return null;
                    path = new SKPath();
                    path.AddCircle((float)L("cx"), (float)L("cy"), (float)r);
                    break;
                }
                case "ellipse":
                {
                    double rx = L("rx"), ry = L("ry");
                    if (rx <= 0 || ry <= 0) return null;
                    path = new SKPath();
                    path.AddOval(new SKRect((float)(L("cx") - rx), (float)(L("cy") - ry), (float)(L("cx") + rx), (float)(L("cy") + ry)));
                    break;
                }
                case "line":
                    path = new SKPath();
                    path.MoveTo((float)L("x1"), (float)L("y1"));
                    path.LineTo((float)L("x2"), (float)L("y2"));
                    break;
                case "polyline" or "polygon":
                {
                    var n = Numbers(p.GetValueOrDefault("points"));
                    if (n.Count < 4) return null;
                    path = new SKPath();
                    path.MoveTo(n[0], n[1]);
                    for (int i = 2; i + 1 < n.Count; i += 2) path.LineTo(n[i], n[i + 1]);
                    if (tag == "polygon") path.Close();
                    break;
                }
            }
            if (path is not null) path.FillType = style.FillRule == "evenodd" ? SKPathFillType.EvenOdd : SKPathFillType.Winding;
            return path;
        }

        private void Paint(SKCanvas canvas, SKPath path, Style style, XElement element)
        {
            var bounds = path.Bounds;
            using (var fill = PaintFor(style.Fill, style.FillOpacity, bounds, style))
            {
                if (fill is not null)
                {
                    fill.Style = SKPaintStyle.Fill;
                    canvas.DrawPath(path, fill);
                }
            }
            using var stroke = PaintFor(style.Stroke, style.StrokeOpacity, bounds, style);
            if (stroke is null || style.StrokeWidth <= 0) return;
            stroke.Style = SKPaintStyle.Stroke;
            stroke.StrokeWidth = (float)style.StrokeWidth;
            stroke.StrokeCap = style.LineCap switch { "round" => SKStrokeCap.Round, "square" => SKStrokeCap.Square, _ => SKStrokeCap.Butt };
            stroke.StrokeJoin = style.LineJoin switch { "round" => SKStrokeJoin.Round, "bevel" => SKStrokeJoin.Bevel, _ => SKStrokeJoin.Miter };
            stroke.StrokeMiter = (float)style.MiterLimit;
            if (style.DashArray is { } dash && dash != "none")
            {
                var intervals = Numbers(dash);
                if (intervals.Count % 2 == 1) intervals.AddRange(intervals);
                if (intervals.Count >= 2 && intervals.All(v => v >= 0) && intervals.Sum() > 0)
                    stroke.PathEffect = SKPathEffect.CreateDash(intervals.ToArray(), (float)style.DashOffset);
            }
            canvas.DrawPath(path, stroke);
        }

        /// <summary>A fill or stroke: a color, a gradient by reference, or nothing.</summary>
        private SKPaint? PaintFor(string value, double opacity, SKRect bounds, Style style)
        {
            value = value.Trim();
            if (value is "none" or "") return null;
            var paint = new SKPaint { IsAntialias = true };
            if (value.StartsWith("url(", StringComparison.Ordinal))
            {
                if (Reference(value) is { } gradient && Shader(gradient, bounds, opacity) is { } shader)
                {
                    paint.Shader = shader;
                    return paint;
                }
                // A missing reference falls back to the color after it, if any.
                var fallback = value[(value.IndexOf(')') + 1)..].Trim();
                if (Color(fallback, style.CurrentColor) is not { } color) { paint.Dispose(); return null; }
                paint.Color = color.WithAlpha(ToByte(color.Alpha * opacity));
                return paint;
            }
            if (Color(value, style.CurrentColor) is not { } c) { paint.Dispose(); return null; }
            paint.Color = c.WithAlpha(ToByte(c.Alpha * opacity));
            return paint;
        }

        /// <summary>A gradient's attribute, looked up through its href chain.</summary>
        private string? GradientAttribute(XElement gradient, string name, int depth = 0)
        {
            if (gradient.Attribute(name)?.Value is { } value) return value;
            if (depth > 8) return null;
            var href = gradient.Attributes().FirstOrDefault(a => a.Name.LocalName == "href")?.Value;
            return Reference(href) is { } parent ? GradientAttribute(parent, name, depth + 1) : null;
        }

        private List<XElement> Stops(XElement gradient, int depth = 0)
        {
            var stops = gradient.Elements().Where(e => e.Name.LocalName == "stop").ToList();
            if (stops.Count > 0 || depth > 8) return stops;
            var href = gradient.Attributes().FirstOrDefault(a => a.Name.LocalName == "href")?.Value;
            return Reference(href) is { } parent ? Stops(parent, depth + 1) : stops;
        }

        private SKShader? Shader(XElement gradient, SKRect bounds, double opacity)
        {
            string kind = gradient.Name.LocalName;
            if (kind is not ("linearGradient" or "radialGradient")) return null;
            var stops = Stops(gradient);
            if (stops.Count == 0) return null;
            var colors = new List<SKColor>();
            var positions = new List<float>();
            float last = 0;
            foreach (var stop in stops)
            {
                var p = Properties(stop);
                var offsetText = p.GetValueOrDefault("offset") ?? "0";
                float offset = (float)(offsetText.EndsWith('%') ? (Length(offsetText, 1) ?? 0) : double.TryParse(offsetText, NumberStyles.Float, CultureInfo.InvariantCulture, out var o) ? o : 0);
                offset = Math.Max(last, Math.Clamp(offset, 0, 1));
                last = offset;
                var color = Color(p.GetValueOrDefault("stop-color") ?? "black", SKColors.Black) ?? SKColors.Black;
                double stopOpacity = p.TryGetValue("stop-opacity", out var so) ? Opacity(so) ?? 1 : 1;
                colors.Add(color.WithAlpha(ToByte(color.Alpha * stopOpacity * opacity)));
                positions.Add(offset);
            }
            bool userSpace = GradientAttribute(gradient, "gradientUnits") == "userSpaceOnUse";
            var matrix = Transform(GradientAttribute(gradient, "gradientTransform"));
            if (!userSpace)
            {
                if (bounds.Width <= 0 || bounds.Height <= 0) return null;
                matrix = SKMatrix.CreateTranslation(bounds.Left, bounds.Top).PreConcat(SKMatrix.CreateScale(bounds.Width, bounds.Height)).PreConcat(matrix);
            }
            double Unit(string name, double fallback, double reference)
            {
                var text = GradientAttribute(gradient, name);
                if (text is null) return fallback;
                if (userSpace) return Length(text, reference) ?? fallback;
                return text.EndsWith('%') ? (Length(text, 1) ?? fallback) : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
            }
            var mode = GradientAttribute(gradient, "spreadMethod") switch { "reflect" => SKShaderTileMode.Mirror, "repeat" => SKShaderTileMode.Repeat, _ => SKShaderTileMode.Clamp };
            double full = userSpace ? 100 : 1;
            if (kind == "linearGradient")
            {
                var start = new SKPoint((float)Unit("x1", 0, full), (float)Unit("y1", 0, full));
                var end = new SKPoint((float)Unit("x2", userSpace ? 100 : 1, full), (float)Unit("y2", 0, full));
                return SKShader.CreateLinearGradient(start, end, colors.ToArray(), positions.ToArray(), mode, matrix);
            }
            double cx = Unit("cx", userSpace ? 50 : 0.5, full), cy = Unit("cy", userSpace ? 50 : 0.5, full), r = Unit("r", userSpace ? 50 : 0.5, full);
            double fx = Unit("fx", cx, full), fy = Unit("fy", cy, full);
            if (r <= 0) return null;
            if (Math.Abs(fx - cx) < 1e-6 && Math.Abs(fy - cy) < 1e-6)
                return SKShader.CreateRadialGradient(new SKPoint((float)cx, (float)cy), (float)r, colors.ToArray(), positions.ToArray(), mode, matrix);
            return SKShader.CreateTwoPointConicalGradient(new SKPoint((float)fx, (float)fy), 0, new SKPoint((float)cx, (float)cy), (float)r,
                colors.ToArray(), positions.ToArray(), mode, matrix);
        }

        /// <summary>A &lt;clipPath&gt;'s shapes, united, in the clipped element's space (or its bounding box, by clipPathUnits).</summary>
        private SKPath? ClipPath(XElement clip, XElement clipped)
        {
            if (clip.Name.LocalName != "clipPath") return null;
            var result = new SKPath();
            var clipStyle = Inherit(Style.Initial, Properties(clip));
            foreach (var child in clip.Elements())
            {
                var p = Properties(child);
                if (Shape(child.Name.LocalName, p, Inherit(clipStyle, p)) is not { } shape) continue;
                using (shape)
                {
                    shape.Transform(Transform(p.GetValueOrDefault("transform")));
                    using var united = result.Op(shape, SKPathOp.Union);
                    if (united is not null) { result.Dispose(); result = new SKPath(united); }
                }
            }
            result.Transform(Transform(clip.Attribute("transform")?.Value));
            if (clip.Attribute("clipPathUnits")?.Value == "objectBoundingBox")
            {
                var p = Properties(clipped);
                using var shape = Shape(clipped.Name.LocalName, p, Style.Initial);
                var bounds = shape?.Bounds ?? SKRect.Empty;
                result.Transform(SKMatrix.CreateTranslation(bounds.Left, bounds.Top).PreConcat(SKMatrix.CreateScale(bounds.Width, bounds.Height)));
            }
            return result;
        }

        // MARK: Text and images

        private void DrawText(SKCanvas canvas, XElement text, Style style)
        {
            var p = Properties(text);
            float x = (float)(Length(Numbers(p.GetValueOrDefault("x")).FirstOrDefault().ToString(CultureInfo.InvariantCulture), 0) ?? 0);
            float y = (float)(Length(Numbers(p.GetValueOrDefault("y")).FirstOrDefault().ToString(CultureInfo.InvariantCulture), 0) ?? 0);
            // The runs: the text's own characters and each tspan's, one after another along the line.
            var runs = new List<(string Text, Style Style, float? X, float? Y, float Dx, float Dy)>();
            foreach (var node in text.Nodes())
            {
                if (node is XText t) runs.Add((Collapse(t.Value), style, null, null, 0, 0));
                else if (node is XElement { Name.LocalName: "tspan" } span)
                {
                    var sp = Properties(span);
                    var spanStyle = Inherit(style, sp);
                    var xs = Numbers(sp.GetValueOrDefault("x")); var ys = Numbers(sp.GetValueOrDefault("y"));
                    var dxs = Numbers(sp.GetValueOrDefault("dx")); var dys = Numbers(sp.GetValueOrDefault("dy"));
                    runs.Add((Collapse(span.Value), spanStyle, xs.Count > 0 ? xs[0] : null, ys.Count > 0 ? ys[0] : null,
                        dxs.Count > 0 ? dxs[0] : 0, dys.Count > 0 ? dys[0] : 0));
                }
            }
            if (runs.Count > 0) runs[0] = runs[0] with { Text = runs[0].Text.TrimStart() };
            if (runs.Count > 0) runs[^1] = runs[^1] with { Text = runs[^1].Text.TrimEnd() };
            // Anchoring needs the whole line's width.
            float total = runs.Sum(r => Measure(r.Text, r.Style));
            float shift = style.TextAnchor switch { "middle" => -total / 2, "end" => -total, _ => 0 };
            float penX = x + shift, penY = y;
            foreach (var run in runs)
            {
                if (run.X is { } rx) penX = rx + shift;
                if (run.Y is { } ry) penY = ry;
                penX += run.Dx; penY += run.Dy;
                if (run.Text.Length == 0 || !run.Style.Visible) continue;
                using var font = Font(run.Style);
                using var blob = SKTextBlob.Create(run.Text, font);
                using var fill = PaintFor(run.Style.Fill, run.Style.FillOpacity, SKRect.Create(penX, penY - font.Size, Measure(run.Text, run.Style), font.Size), run.Style);
                if (blob is not null && fill is not null) canvas.DrawText(blob, penX, penY, fill);
                penX += Measure(run.Text, run.Style);
            }
        }

        private static string Collapse(string text) => Regex.Replace(text, @"\s+", " ");

        private static SKFont Font(Style style)
        {
            var weight = style.FontWeight is "bold" or "bolder" or "600" or "700" or "800" or "900" ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal;
            var slant = style.FontStyle is "italic" or "oblique" ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright;
            SKTypeface? typeface = null;
            foreach (var raw in style.FontFamily.Split(','))
            {
                var family = raw.Trim().Trim('\'', '"') switch
                {
                    "sans-serif" => "Arial", "serif" => "Times New Roman", "monospace" => "Consolas", var name => name,
                };
                typeface = SKTypeface.FromFamilyName(family, weight, SKFontStyleWidth.Normal, slant);
                if (typeface is not null && string.Equals(typeface.FamilyName, family, StringComparison.OrdinalIgnoreCase)) break;
            }
            typeface ??= SKTypeface.FromFamilyName("Microsoft YaHei", weight, SKFontStyleWidth.Normal, slant);
            return new SKFont(typeface, (float)style.FontSize) { Subpixel = true, Edging = SKFontEdging.Antialias };
        }

        private static float Measure(string text, Style style)
        {
            using var font = Font(style);
            return font.MeasureText(text);
        }

        /// <summary>An embedded image (a data: URI); files outside the SVG aren't read.</summary>
        private static void DrawImage(SKCanvas canvas, Dictionary<string, string> p)
        {
            var href = p.GetValueOrDefault("href");
            if (href is null || !href.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return;
            int comma = href.IndexOf(',');
            if (comma < 0 || !href[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase)) return;
            byte[] bytes;
            try { bytes = Convert.FromBase64String(href[(comma + 1)..].Trim()); }
            catch (FormatException) { return; }
            using var image = SKImage.FromEncodedData(bytes);
            if (image is null) return;
            double x = Length(p.GetValueOrDefault("x"), 0) ?? 0, y = Length(p.GetValueOrDefault("y"), 0) ?? 0;
            double w = Length(p.GetValueOrDefault("width"), 0) ?? image.Width, h = Length(p.GetValueOrDefault("height"), 0) ?? image.Height;
            if (w <= 0 || h <= 0) return;
            double s = Math.Min(w / image.Width, h / image.Height);
            float dw = (float)(image.Width * s), dh = (float)(image.Height * s);
            var rect = SKRect.Create((float)(x + (w - dw) / 2), (float)(y + (h - dh) / 2), dw, dh);
            canvas.DrawImage(image, rect, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        }
    }
}
