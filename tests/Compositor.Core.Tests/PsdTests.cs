using Compositor.Core.Adjustments;
using Compositor.Core.Geometry;
using Compositor.Core.IO;
using Compositor.Core.IO.Psd;
using Compositor.Core.Model;
using Compositor.Core.Rendering;
using static Compositor.Core.Tests.TestImages;

namespace Compositor.Core.Tests;

public class PsdTests
{
    private static PsdImport Import(byte[] bytes) => PsdImporter.Make(PsdReader.Read(bytes));

    [Fact]
    public void ReadsLayersWithTheirPlacesAndLooks()
    {
        var red = PsdLayerSpec.Solid("背景", 0, 0, 40, 30, 255, 0, 0);
        var blue = PsdLayerSpec.Solid("Blue", 10, 5, 10, 8, 0, 0, 255);
        blue.Opacity = 128;
        blue.Blend = "mul ";
        var hidden = PsdLayerSpec.Solid("Hidden", 0, 0, 5, 5, 0, 255, 0);
        hidden.Hidden = true;
        hidden.PackBits = false;
        var import = Import(PsdFixture.Build(40, 30, new[] { red, blue, hidden }, resolution: 300));
        var document = import.Document;
        Assert.Equal((40, 30), (document.Width, document.Height));
        Assert.Equal(300, document.Resolution, 3);
        Assert.Equal(new[] { "背景", "Blue", "Hidden" }, document.Layers.Select(l => l.Name));
        var b = document.Layers[1];
        Assert.Equal(new PointD(10, 5), b.Transform.Origin);
        Assert.Equal(new SizeD(10, 8), b.Transform.Size);
        Assert.Equal(BlendMode.Multiply, b.BlendMode);
        Assert.InRange(b.Opacity, 0.49, 0.51);
        Assert.False(document.Layers[2].IsVisible);
        Assert.Empty(import.Conversions);
        using var image = DocumentRenderer.RenderToImage(document);
        Assert.Equal((255, 0, 0, 255), Pixel(image, 2, 2));
        var mixed = Pixel(image, 12, 7);
        Assert.InRange(mixed.R, 120, 135); // red × blue at half opacity
        Assert.InRange(mixed.B, 0, 3);
    }

    [Fact]
    public void FoldersHoldTheirChildren()
    {
        var bottom = PsdLayerSpec.Solid("Bottom", 0, 0, 10, 10, 255, 255, 255);
        var divider = new PsdLayerSpec { Name = "</Layer group>", Section = 3 };
        var child = PsdLayerSpec.Solid("Child", 2, 2, 4, 4, 0, 0, 0);
        var folder = new PsdLayerSpec { Name = "Folder", Section = 1, Blend = "pass", Opacity = 200 };
        var import = Import(PsdFixture.Build(10, 10, new[] { bottom, divider, child, folder }));
        var layers = import.Document.Layers;
        Assert.Equal(3, layers.Count);
        var f = layers.Single(l => l.Name == "Folder");
        Assert.True(f.IsGroup);
        Assert.InRange(f.Opacity, 0.78, 0.79);
        Assert.Equal(f.Id, layers.Single(l => l.Name == "Child").ParentId);
        Assert.Null(layers.Single(l => l.Name == "Bottom").ParentId);
    }

    [Fact]
    public void MasksLandOnTheLayersGrid()
    {
        var layer = PsdLayerSpec.Solid("Masked", 10, 10, 20, 20, 0, 255, 0);
        // A 10×20 patch of black over the layer's left half; Photoshop's default (white) everywhere else.
        layer.Mask = new byte[10 * 20];
        layer.MaskRect = new RectI(10, 10, 10, 20);
        layer.MaskDefault = 255;
        var import = Import(PsdFixture.Build(40, 40, new[] { layer }));
        var l = import.Document.Layers[0];
        Assert.NotNull(l.Mask);
        Assert.Equal((20, 20), (l.Mask!.Asset.Width, l.Mask.Asset.Height));
        using var image = DocumentRenderer.RenderToImage(import.Document);
        Assert.Equal(0, Pixel(image, 12, 15).A);
        Assert.Equal((0, 255, 0, 255), Pixel(image, 25, 15));
    }

    [Fact]
    public void ClippedLayersKeepTheirBase()
    {
        var baseLayer = PsdLayerSpec.Solid("Base", 0, 0, 10, 10, 255, 0, 0);
        var clipped = PsdLayerSpec.Solid("Clipped", 0, 0, 20, 20, 0, 0, 255);
        clipped.Clipping = true;
        var import = Import(PsdFixture.Build(20, 20, new[] { baseLayer, clipped }));
        var layers = import.Document.Layers;
        Assert.Equal(layers[0].Id, layers[1].MaskSourceId);
        using var image = DocumentRenderer.RenderToImage(import.Document);
        Assert.Equal((0, 0, 255, 255), Pixel(image, 5, 5));
        Assert.Equal(0, Pixel(image, 15, 15).A);
    }

    [Fact]
    public void LevelsBecomeAnAdjustmentLayerAndOddBlendsAreReported()
    {
        var gray = PsdLayerSpec.Solid("Gray", 0, 0, 4, 4, 128, 128, 128);
        var levels = new byte[292];
        // RGB record: input black 0, input white 128, output 0–255, gamma 1.00.
        void U16(int at, int v) { levels[at] = (byte)(v >> 8); levels[at + 1] = (byte)v; }
        U16(0, 2);
        for (int channel = 0; channel < 4; channel++)
        {
            int b = 2 + channel * 10;
            U16(b, 0); U16(b + 2, channel == 0 ? 128 : 255); U16(b + 4, 0); U16(b + 6, 255); U16(b + 8, 100);
        }
        var adjustment = new PsdLayerSpec { Name = "Levels 1" };
        adjustment.Extra["levl"] = levels;
        var dissolve = PsdLayerSpec.Solid("Dissolve", 0, 0, 1, 1, 0, 0, 0, 0);
        dissolve.Blend = "diss";
        var import = Import(PsdFixture.Build(4, 4, new[] { gray, adjustment, dissolve }));
        var layer = import.Document.Layers.Single(l => l.Name == "Levels 1");
        Assert.Equal(AdjustmentKind.Levels, layer.Adjustment!.Kind);
        Assert.Equal(128, layer.Adjustment.Levels.Ranges[0].White);
        Assert.Contains(import.Conversions, c => c.LayerName == "Dissolve");
        Assert.Contains(import.Conversions, c => c.LayerName == "Levels 1");
        using var image = DocumentRenderer.RenderToImage(import.Document);
        Assert.InRange(Pixel(image, 1, 1).R, 250, 255);
    }

    [Fact]
    public void DamagedAndUnsupportedFilesAreRefused()
    {
        var bytes = PsdFixture.Build(8, 8, new[] { PsdLayerSpec.Solid("A", 0, 0, 8, 8, 1, 2, 3) });
        Assert.ThrowsAny<Exception>(() => PsdReader.Read(bytes[..(bytes.Length / 2)]));
        var sixteen = (byte[])bytes.Clone();
        sixteen[22] = 0; sixteen[23] = 16; // depth
        Assert.Throws<PsdException>(() => PsdReader.Read(sixteen));
        Assert.Throws<ImageImportException>(() => PsdReader.Read(new byte[] { 1, 2, 3, 4, 5, 6 }));
    }

    [Fact]
    public void AFileWithOnlyABackgroundIsItsMergedImage()
    {
        // Header, empty sections, then the merged image PackBits-compressed: each channel one repeat run per row.
        var bytes = new List<byte>();
        void U16(int v) { bytes.Add((byte)(v >> 8)); bytes.Add((byte)v); }
        void U32(uint v) { U16((int)(v >> 16)); U16((int)(v & 0xFFFF)); }
        bytes.AddRange("8BPS"u8.ToArray()); U16(1); bytes.AddRange(new byte[6]); U16(3); U32(2); U32(3); U16(8); U16(3);
        U32(0); U32(0); U32(0);
        U16(1);
        for (int i = 0; i < 3 * 2; i++) U16(2);
        foreach (byte value in new byte[] { 10, 10, 20, 20, 30, 30 }) { bytes.Add(unchecked((byte)(sbyte)-2)); bytes.Add(value); }
        var import = Import(bytes.ToArray());
        var layer = Assert.Single(import.Document.Layers);
        using var image = DocumentRenderer.RenderToImage(import.Document);
        Assert.Equal((10, 20, 30, 255), Pixel(image, 2, 1));
        Assert.Equal((3, 2), (layer.Asset!.Width, layer.Asset.Height));
    }

    /// <summary>Writes a sample file for trying the app by hand, when PSD_FIXTURE_OUT names where.</summary>
    [Fact]
    public void WritesASampleWhenAsked()
    {
        if (Environment.GetEnvironmentVariable("PSD_FIXTURE_OUT") is not { Length: > 0 } path) return;
        var background = PsdLayerSpec.Solid("背景", 0, 0, 600, 400, 240, 236, 228);
        var divider = new PsdLayerSpec { Name = "</Layer group>", Section = 3 };
        var sun = PsdLayerSpec.Solid("太阳", 380, 60, 120, 120, 255, 170, 0);
        sun.Mask = new byte[120 * 120];
        for (int y = 0; y < 120; y++)
            for (int x = 0; x < 120; x++)
                sun.Mask[y * 120 + x] = (byte)((x - 60) * (x - 60) + (y - 60) * (y - 60) < 3600 ? 255 : 0);
        sun.MaskRect = new RectI(380, 60, 120, 120);
        var hill = PsdLayerSpec.Solid("山", 0, 250, 600, 150, 60, 140, 80);
        var shade = PsdLayerSpec.Solid("阴影", 0, 300, 600, 100, 0, 0, 0);
        shade.Clipping = true;
        shade.Opacity = 90;
        shade.Blend = "mul ";
        var folder = new PsdLayerSpec { Name = "风景", Section = 1, Blend = "pass" };
        var odd = PsdLayerSpec.Solid("溶解", 40, 40, 80, 60, 200, 30, 60);
        odd.Blend = "diss";
        File.WriteAllBytes(path, PsdFixture.Build(600, 400, new[] { background, divider, sun, hill, shade, folder, odd }, 150));
    }

    [Fact]
    public void ImportsIntoASession()
    {
        var path = Path.Combine(Path.GetTempPath(), $"compositor-{Guid.NewGuid():N}.psd");
        File.WriteAllBytes(path, PsdFixture.Build(16, 16, new[] { PsdLayerSpec.Solid("One", 0, 0, 16, 16, 9, 9, 9) }));
        try
        {
            Assert.True(PsdReader.Matches(path));
            var import = PsdImporter.Read(path);
            var session = new Editing.EditorSession();
            session.InstallDocument(import.Document, "test");
            Assert.Equal("One", session.ActiveLayer!.Name);
            Assert.Null(session.ProjectPath);
            Assert.False(session.CanUndo);
        }
        finally { File.Delete(path); }
    }
}
