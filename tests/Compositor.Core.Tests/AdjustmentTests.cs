using System.Text.Json.Nodes;
using Compositor.Core.Adjustments;
using Compositor.Core.Geometry;
using Compositor.Core.IO;
using Compositor.Core.Model;
using Compositor.Core.Rendering;
using static Compositor.Core.Tests.TestImages;

namespace Compositor.Core.Tests;

public class AdjustmentTests
{
    private static Layer AdjustmentLayer(LayerAdjustment adjustment, int width, int height) =>
        Model.Layer.Blank(adjustment.Kind.ToDisplayName(), new SizeD(width, height)) with { Adjustment = adjustment };

    [Fact]
    public void InvertLayerInvertsEverythingBelow()
    {
        var document = Document(4, 4,
            Layer(Solid(4, 4, 255, 0, 0)),
            AdjustmentLayer(new LayerAdjustment(AdjustmentKind.Invert), 4, 4));
        using var image = DocumentRenderer.RenderToImage(document);
        var p = Pixel(image, 2, 2);
        Assert.Equal((0, 255, 255, 255), (p.R, p.G, p.B, p.A));
    }

    [Fact]
    public void LayersAboveAnAdjustmentAreNotChanged()
    {
        var document = Document(4, 4,
            Layer(Solid(4, 4, 255, 0, 0)),
            AdjustmentLayer(new LayerAdjustment(AdjustmentKind.Invert), 4, 4),
            Layer(Solid(2, 4, 0, 0, 255)));
        using var image = DocumentRenderer.RenderToImage(document);
        Assert.Equal((byte)255, Pixel(image, 0, 0).B);
        Assert.Equal((byte)0, Pixel(image, 0, 0).R);
        Assert.Equal((byte)255, Pixel(image, 3, 0).G);
    }

    [Fact]
    public void LevelsWhitePointBrightensMidGray()
    {
        var levels = new LayerAdjustment(AdjustmentKind.Levels);
        levels.Levels.Ranges[0] = new LevelRange(White: 128);
        var document = Document(2, 2, Layer(Solid(2, 2, 128, 128, 128)), AdjustmentLayer(levels, 2, 2));
        using var image = DocumentRenderer.RenderToImage(document);
        Assert.InRange(Pixel(image, 0, 0).R, 253, 255);
    }

    [Fact]
    public void AdjustmentOpacityAndMaskLimitTheEffect()
    {
        var mask = RasterImage.FromMask(new byte[] { 255, 0, 255, 0 }, 2, 2);
        var invert = AdjustmentLayer(new LayerAdjustment(AdjustmentKind.Invert), 2, 2) with { Opacity = 0.5, Mask = new LayerMask(mask) };
        var document = Document(2, 2, Layer(Solid(2, 2, 255, 255, 255)), invert);
        using var image = DocumentRenderer.RenderToImage(document);
        Assert.InRange(Pixel(image, 0, 0).R, 126, 129);
        Assert.Equal((byte)255, Pixel(image, 1, 0).R);
    }

    [Fact]
    public void ClippedAdjustmentChangesOnlyItsBase()
    {
        var bottom = Layer(Solid(4, 4, 255, 255, 255));
        var baseLayer = Layer(Solid(2, 4, 255, 0, 0));
        var invert = AdjustmentLayer(new LayerAdjustment(AdjustmentKind.Invert), 4, 4) with { MaskSourceId = baseLayer.Id };
        using var image = DocumentRenderer.RenderToImage(Document(4, 4, bottom, baseLayer, invert));
        var inside = Pixel(image, 0, 0);
        Assert.Equal((0, 255, 255), (inside.R, inside.G, inside.B));
        var outside = Pixel(image, 3, 0);
        Assert.Equal((255, 255, 255), (outside.R, outside.G, outside.B));
    }

    [Fact]
    public void HiddenAdjustmentIsSkipped()
    {
        var invert = AdjustmentLayer(new LayerAdjustment(AdjustmentKind.Invert), 2, 2) with { IsVisible = false };
        using var image = DocumentRenderer.RenderToImage(Document(2, 2, Layer(Solid(2, 2, 255, 0, 0)), invert));
        Assert.Equal((byte)255, Pixel(image, 0, 0).R);
    }

    [Fact]
    public void GaussianBlurSoftensAnEdgeAtAnyScale()
    {
        var left = Layer(Solid(20, 20, 0, 0, 0));
        var right = Layer(Solid(10, 20, 255, 255, 255), x: 10);
        var blur = new LayerAdjustment(AdjustmentKind.GaussianBlur) { BlurRadius = 3 };
        var document = Document(20, 20, left, right, AdjustmentLayer(blur, 20, 20));
        using var full = DocumentRenderer.RenderToImage(document);
        Assert.InRange(Pixel(full, 9, 10).R, 40, 200);
        Assert.InRange(Pixel(full, 10, 10).R, 60, 220);
        Assert.InRange(Pixel(full, 0, 10).R, 0, 5);
        using var half = DocumentRenderer.RenderToImage(document, scale: 0.5);
        Assert.InRange(Pixel(half, 4, 5).R, 20, 230);
    }

    [Fact]
    public void HueSaturationJsonRoundTripsWithAlternatingDictionaries()
    {
        var a = new LayerAdjustment(AdjustmentKind.HueSaturation) { HsvSettings = new HueSaturationSettings { Range = ColorRange.Reds } };
        a.HsvSettings.Adjustments[ColorRange.Master] = new RangeAdjustment(30, -20, 5);
        a.HsvSettings.Adjustments[ColorRange.Reds] = new RangeAdjustment(0, 50, 0);
        var json = a.ToJson();
        var adjustments = Assert.IsType<JsonArray>(json["hsvSettings"]!["adjustments"]);
        Assert.Equal(4, adjustments.Count);
        Assert.Equal("Master", (string)adjustments[0]!);
        foreach (var key in new[] { "kind", "hue", "saturation", "lightness", "colorize", "levels", "curves" }) Assert.NotNull(json[key]);

        var back = LayerAdjustment.FromJson(JsonNode.Parse(json.ToJsonString()))!;
        Assert.Equal(AdjustmentKind.HueSaturation, back.Kind);
        Assert.Equal(ColorRange.Reds, back.HsvSettings!.Range);
        Assert.Equal(new RangeAdjustment(30, -20, 5), back.HsvSettings.Adjustments[ColorRange.Master]);
        Assert.Equal(a.HsvSettings.Bands[ColorRange.Cyans], back.HsvSettings.Bands[ColorRange.Cyans]);
        Assert.True(back.IsValid);
    }

    private static Editing.EditorSession GraySession(int width = 40, int height = 30)
    {
        var session = new Editing.EditorSession { PreviewsInBackground = false };
        session.CreateDocument(width, height, 72, new PaletteColor(0.5, 0.5, 0.5));
        return session;
    }

    [Fact]
    public void ImageLevelsChangesOnlyTheSelectionAsOneUndoStep()
    {
        var session = GraySession();
        session.SetSelection(DocumentSelection.Rectangle(new RectD(0, 0, 20, 30)), "Rectangular Marquee");
        int steps = session.History.UndoCount;
        Assert.True(session.BeginImageAdjustment(AdjustmentKind.Levels));
        var edit = session.AdjustmentEdit!;
        Assert.NotNull(edit.Histogram);
        Assert.True(edit.Histogram![1][128] > 0 || edit.Histogram[1][127] > 0);
        var settings = edit.Settings.Clone();
        settings.Levels.Ranges[0] = new LevelRange(White: 128);
        session.UpdateAdjustmentEdit(settings);
        Assert.False(session.CanEditLayers);
        session.CommitAdjustmentEdit();
        Assert.Null(session.AdjustmentEdit);
        Assert.Equal(steps + 1, session.History.UndoCount);
        using (var image = DocumentRenderer.RenderToImage(session.Document!))
        {
            Assert.InRange(Pixel(image, 5, 5).R, 250, 255);
            Assert.InRange(Pixel(image, 30, 5).R, 126, 129);
        }
        session.Undo();
        using (var image = DocumentRenderer.RenderToImage(session.Document!))
            Assert.InRange(Pixel(image, 5, 5).R, 126, 129);
    }

    [Fact]
    public void CancelledAdjustmentLeavesPixelsAlone()
    {
        var session = GraySession();
        var before = session.Document;
        Assert.True(session.BeginImageAdjustment(AdjustmentKind.Exposure));
        var settings = session.AdjustmentEdit!.Settings.Clone();
        settings.ExposureSettings = new ExposureSettings(2);
        session.UpdateAdjustmentEdit(settings);
        var options = new RenderOptions();
        session.ApplyAdjustmentPreview(options);
        Assert.NotNull(options.For(session.ActiveLayerId!.Value)?.Image);
        session.CancelAdjustmentEdit();
        Assert.Same(before, session.Document);
    }

    [Fact]
    public void GaussianBlurGrowsTheLayerThenTrims()
    {
        var session = GraySession();
        var layer = Model.Layer.FromImage(Solid(10, 10, 255, 0, 0), new PointD(15, 10), "Red");
        session.InsertLayer(layer);
        Assert.True(session.BeginImageAdjustment(AdjustmentKind.GaussianBlur));
        var settings = session.AdjustmentEdit!.Settings.Clone();
        settings.BlurRadius = 2;
        session.UpdateAdjustmentEdit(settings);
        session.CommitAdjustmentEdit();
        var blurred = session.Document!.Find(layer.Id)!;
        Assert.True(blurred.Asset!.Width > 10 && blurred.Asset.Width < 10 + 2 * 16);
        Assert.True(blurred.Transform.Origin.X < 15);
        Assert.Equal(AdjustmentKind.GaussianBlur, session.LastFilter);
        using var image = DocumentRenderer.RenderToImage(session.Document);
        // Soft at the old edge, still solid in the middle.
        Assert.InRange(Pixel(image, 15, 15).R, 150, 240);
        Assert.InRange(Pixel(image, 20, 15).R, 250, 255);
    }

    [Fact]
    public void AdjustmentLayerEditsLiveAndCancelRestores()
    {
        var session = GraySession();
        session.AddAdjustmentLayer(AdjustmentKind.Levels);
        var edit = session.AdjustmentEdit!;
        Assert.True(edit.EditsLayer);
        Assert.NotNull(edit.Histogram);
        var id = edit.LayerId;
        var settings = edit.Settings.Clone();
        settings.Levels.Ranges[0] = new LevelRange(White: 128);
        session.UpdateAdjustmentEdit(settings);
        using (var live = DocumentRenderer.RenderToImage(session.Document!))
            Assert.InRange(Pixel(live, 5, 5).R, 250, 255);
        session.CancelAdjustmentEdit();
        Assert.True(session.Document!.Find(id)!.Adjustment!.Levels.IsIdentity);

        Assert.True(session.EditAdjustmentLayer(id));
        session.UpdateAdjustmentEdit(settings);
        session.CommitAdjustmentEdit();
        Assert.False(session.Document!.Find(id)!.Adjustment!.Levels.IsIdentity);
        session.Undo();
        Assert.True(session.Document!.Find(id)!.Adjustment!.Levels.IsIdentity);
        session.Undo();
        Assert.Null(session.Document!.Find(id));
    }

    [Fact]
    public void MergingAnAdjustmentDownBakesItIn()
    {
        var session = GraySession();
        session.AddAdjustmentLayer(AdjustmentKind.Invert);
        Assert.Null(session.AdjustmentEdit);
        Assert.True(session.CanMergeLayers);
        session.MergeLayers();
        var merged = Assert.Single(session.Document!.Layers);
        Assert.False(merged.IsAdjustment);
        using var image = DocumentRenderer.RenderToImage(session.Document);
        Assert.InRange(Pixel(image, 3, 3).R, 125, 129);
        // Nothing merges down into an adjustment layer.
        session.AddAdjustmentLayer(AdjustmentKind.Invert);
        session.AddBlankLayer();
        Assert.False(session.CanMergeLayers);
    }

    [Fact]
    public void AutoLevelsStretchesTheRange()
    {
        var histogram = new double[4][];
        for (int c = 0; c < 4; c++) { histogram[c] = new double[256]; histogram[c][60] = 100; histogram[c][200] = 100; }
        var settings = LevelsAuto.Contrast.Settings(histogram);
        Assert.Equal(60, settings.Ranges[0].Black);
        Assert.Equal(200, settings.Ranges[0].White);
    }

    [Fact]
    public void ProjectKeepsAdjustmentLayers()
    {
        var curves = new LayerAdjustment(AdjustmentKind.Curves);
        curves.Curves.Channels[0] = new List<CurvePoint> { new(0, 0), new(128, 180), new(255, 255) };
        var adjustment = AdjustmentLayer(curves, 4, 4);
        var document = Document(4, 4, Layer(Solid(4, 4, 128, 128, 128)), adjustment);
        var path = Path.Combine(Path.GetTempPath(), "compositor-adj-" + Guid.NewGuid().ToString("N") + ".comp");
        try
        {
            ProjectStore.Save(new ProjectSnapshot(document, adjustment.Id), path);
            var loaded = ProjectStore.Load(path).Document;
            var layer = loaded.Find(adjustment.Id)!;
            Assert.Equal(AdjustmentKind.Curves, layer.Adjustment!.Kind);
            Assert.Equal(3, layer.Adjustment.Curves.Channels[0].Count);
            using var a = DocumentRenderer.RenderToImage(document);
            using var b = DocumentRenderer.RenderToImage(loaded);
            Assert.Equal(Pixel(a, 1, 1), Pixel(b, 1, 1));
            Assert.InRange(Pixel(b, 1, 1).R, 170, 190);
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }
}
