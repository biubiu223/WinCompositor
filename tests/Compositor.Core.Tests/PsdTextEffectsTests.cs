using System.Text;
using Compositor.Core.IO.Psd;
using Compositor.Core.Model;

namespace Compositor.Core.Tests;

/// <summary>Photoshop type layers and layer styles brought in as editable text and effects.</summary>
public class PsdTextEffectsTests
{
    /// <summary>Writes action descriptors as Photoshop does, for building test files.</summary>
    private sealed class W
    {
        private readonly List<byte> _b = new();
        public byte[] Bytes => _b.ToArray();
        public void U8(int v) => _b.Add((byte)v);
        public void U16(int v) { _b.Add((byte)(v >> 8)); _b.Add((byte)v); }
        public void U32(long v) { for (int s = 24; s >= 0; s -= 8) _b.Add((byte)(v >> s)); }
        public void F64(double v) { long bits = BitConverter.DoubleToInt64Bits(v); for (int s = 56; s >= 0; s -= 8) _b.Add((byte)(bits >> s)); }
        public void Ascii(string s) => _b.AddRange(Encoding.ASCII.GetBytes(s));
        public void Raw(byte[] d) => _b.AddRange(d);
        public void Unicode(string s) { U32(s.Length); _b.AddRange(Encoding.BigEndianUnicode.GetBytes(s)); }
        public void Id(string s) { if (s.Length == 4) { U32(0); Ascii(s); } else { U32(s.Length); Ascii(s); } }
        public void Body(string cls, (string Key, Action<W> Value)[] items)
        {
            Unicode("");
            Id(cls);
            U32(items.Length);
            foreach (var (key, value) in items) { Id(key); value(this); }
        }
    }

    private static Action<W> Doub(double v) => w => { w.Ascii("doub"); w.F64(v); };
    private static Action<W> Unit(string unit, double v) => w => { w.Ascii("UntF"); w.Ascii(unit); w.F64(v); };
    private static Action<W> Bool(bool v) => w => { w.Ascii("bool"); w.U8(v ? 1 : 0); };
    private static Action<W> Text(string s) => w => { w.Ascii("TEXT"); w.Unicode(s); };
    private static Action<W> Enum(string type, string v) => w => { w.Ascii("enum"); w.Id(type); w.Id(v); };
    private static Action<W> Data(byte[] d) => w => { w.Ascii("tdta"); w.U32(d.Length); w.Raw(d); };
    private static Action<W> Obj(string cls, params (string, Action<W>)[] items) => w => { w.Ascii("Objc"); w.Body(cls, items); };
    private static Action<W> Rgb(double r, double g, double b) => Obj("RGBC", ("Rd  ", Doub(r)), ("Grn ", Doub(g)), ("Bl  ", Doub(b)));

    private static byte[] TypeBlock(string content, double x, double y)
    {
        var engine = Encoding.Latin1.GetBytes(
            "\n\n<<\n\t/EngineDict\n\t<<\n\t\t/Editor << /Text (" + content + "\\r) >>\n" +
            "\t\t/ParagraphRun << /RunArray [ << /ParagraphSheet << /Properties << /Justification 2 >> >> >> ] >>\n" +
            "\t\t/StyleRun << /RunArray [ << /StyleSheet << /StyleSheetData << /Font 0 /FontSize 36.0 /AutoLeading true /Tracking 0" +
            " /FillColor << /Type 1 /Values [ 1.0 1.0 0.0 0.0 ] >> >> >> >> ] >>\n\t>>\n" +
            "\t/ResourceDict << /FontSet [ << /Name (þÿ\0A\0r\0i\0a\0l\0M\0T) /Type 1 >> ] >>\n>>");
        var w = new W();
        w.U16(1);
        foreach (var v in new[] { 1.0, 0, 0, 1, x, y }) w.F64(v);
        w.U16(50);
        w.U32(16);
        w.Body("TxLr", new (string, Action<W>)[]
        {
            ("Txt ", Text(content)),
            ("Ornt", Enum("Ornt", "Hrzn")),
            ("EngineData", Data(engine)),
        });
        w.U16(1);
        w.U32(16);
        w.Body("warp", new (string, Action<W>)[] { ("warpStyle", Enum("warpStyle", "warpNone")) });
        foreach (var v in new[] { 0.0, 0, 0, 0 }) w.F64(v);
        return w.Bytes;
    }

    private static byte[] StyleBlock()
    {
        var w = new W();
        w.U32(0);
        w.U32(16);
        w.Body("null", new (string, Action<W>)[]
        {
            ("Scl ", Unit("#Prc", 100)),
            ("masterFXSwitch", Bool(true)),
            ("DrSh", Obj("DrSh", ("enab", Bool(true)), ("Md  ", Enum("BlnM", "Mltp")), ("Clr ", Rgb(255, 0, 0)), ("Opct", Unit("#Prc", 60)),
                ("uglg", Bool(true)), ("lagl", Unit("#Ang", 45)), ("Dstn", Unit("#Pxl", 8)), ("Ckmt", Unit("#Pxl", 0)), ("blur", Unit("#Pxl", 6)))),
            ("FrFX", Obj("FrFX", ("enab", Bool(true)), ("Styl", Enum("FStl", "InsF")), ("PntT", Enum("FrFl", "SClr")), ("Md  ", Enum("BlnM", "Nrml")),
                ("Opct", Unit("#Prc", 100)), ("Sz  ", Unit("#Pxl", 3)), ("Clr ", Rgb(0, 0, 255)))),
            ("OrGl", Obj("OrGl", ("enab", Bool(false)), ("Md  ", Enum("BlnM", "Scrn")), ("Clr ", Rgb(255, 255, 190)), ("Opct", Unit("#Prc", 75)),
                ("Ckmt", Unit("#Pxl", 0)), ("blur", Unit("#Pxl", 10)))),
            ("ebbl", Obj("ebbl", ("enab", Bool(true)))),
        });
        return w.Bytes;
    }

    [Fact]
    public void TypeLayersStayEditableText()
    {
        var text = PsdLayerSpec.Solid("Hello", 60, 50, 80, 40, 255, 0, 0);
        text.Extra["TySh"] = TypeBlock("Hello", 100, 80);
        var file = PsdFixture.Build(200, 150, new[] { PsdLayerSpec.Solid("Back", 0, 0, 200, 150, 255, 255, 255), text });
        var import = PsdImporter.Make(PsdReader.Read(file));
        var layer = import.Document.Layers.Single(l => l.Name == "Hello");
        Assert.NotNull(layer.Text);
        Assert.Equal("Hello", layer.Text!.Content);
        Assert.Equal("ArialMT", layer.Text.FontName);
        Assert.Equal(36, layer.Text.FontSize, 3);
        Assert.Equal(TextAlign.Center, layer.Text.Alignment);
        Assert.Equal((1.0, 0.0, 0.0), (layer.Text.Red, layer.Text.Green, layer.Text.Blue));
        // Centered on the anchor, with the first baseline through it.
        Assert.InRange(layer.Transform.Center.X, 99, 101);
        var baseline = Compositor.Core.Text.TextLayout.Make(layer.Text).Lines[0].Baseline;
        Assert.InRange(layer.Transform.Origin.Y + baseline, 79, 81);
        Assert.DoesNotContain(import.Conversions, c => c.Message == PsdImporter.RasterizedTextNote);
        Assert.True(Compositor.Core.Text.TextLayout.IsInstalled("ArialMT"));
    }

    [Fact]
    public void LayerStylesBecomeEffects()
    {
        var shape = PsdLayerSpec.Solid("Shape", 40, 40, 60, 60, 0, 200, 0);
        shape.Extra["lfx2"] = StyleBlock();
        var file = PsdFixture.Build(200, 150, new[] { shape });
        var import = PsdImporter.Make(PsdReader.Read(file));
        var layer = import.Document.Layers.Single(l => l.Name == "Shape");
        var effects = layer.Effects!;
        var shadow = effects[LayerEffectKind.Shadow]!;
        Assert.Equal(PsdEffects.DefaultGlobalAngle, shadow.Angle); // follows the global light
        Assert.Equal(8, shadow.Distance, 3);
        Assert.Equal(6, shadow.Blur, 3);
        Assert.Equal(0.6, shadow.Opacity, 3);
        Assert.Equal(1, shadow.Red, 3);
        var stroke = effects[LayerEffectKind.Stroke]!;
        Assert.True(stroke.Inside);
        Assert.Equal(3, stroke.Size, 3);
        Assert.Equal(1, stroke.Blue, 3);
        var glow = effects[LayerEffectKind.OuterGlow]!;
        Assert.False(glow.IsEnabled); // switched off in Photoshop, kept switched off
        Assert.Contains(import.Conversions, c => c.Message.Contains("斜面和浮雕"));
        Assert.DoesNotContain(import.Conversions, c => c.Message.Contains("图层效果已舍弃"));
    }
}
