using System.Text.Json.Nodes;
using Compositor.Core.Geometry;
using Compositor.Core.IO;
using Compositor.Core.Model;
using Compositor.Core.Rendering;
using static Compositor.Core.Tests.TestImages;

namespace Compositor.Core.Tests;

public class EffectsTests
{
    /// <summary>A 10×10 red square at (20, 20) on a 60×60 canvas, with <paramref name="effects"/>.</summary>
    private static (CanvasDocument Document, Layer Layer) Square(params LayerEffect[] effects)
    {
        var layer = Layer(Solid(10, 10, 255, 0, 0), 20, 20) with { Effects = new LayerEffects(effects) };
        return (Document(60, 60, layer), layer);
    }

    private static (byte R, byte G, byte B, byte A) At(CanvasDocument document, int x, int y)
    {
        using var image = DocumentRenderer.RenderToImage(document);
        return Pixel(image, x, y);
    }

    [Fact]
    public void DropShadowFallsAwayFromTheLight()
    {
        var shadow = LayerEffect.Default(LayerEffectKind.Shadow, PaletteColor.Black) with { Distance = 6, Blur = 0, Opacity = 1 };
        var (document, _) = Square(shadow);
        Assert.Equal((255, 0, 0, 255), At(document, 25, 25));
        // Angle 90: lit from above, so the shadow drops straight down.
        Assert.Equal((byte)255, At(document, 25, 33).A);
        Assert.Equal((byte)0, At(document, 25, 33).R);
        Assert.Equal((byte)0, At(document, 25, 17).A);
    }

    [Fact]
    public void StrokesSitOutsideOrInside()
    {
        var outside = LayerEffect.Default(LayerEffectKind.Stroke, PaletteColor.White) with { Size = 3 };
        var (document, _) = Square(outside);
        Assert.Equal((255, 255, 255, 255), At(document, 18, 25));
        Assert.Equal((byte)0, At(document, 15, 25).A);
        Assert.Equal((255, 0, 0, 255), At(document, 25, 25));
        // A square reach keeps the corners square.
        Assert.Equal((byte)255, At(document, 18, 18).A);

        var inside = outside with { Inside = true };
        (document, _) = Square(inside);
        Assert.Equal((255, 255, 255, 255), At(document, 21, 25));
        Assert.Equal((255, 0, 0, 255), At(document, 25, 25));
        Assert.Equal((byte)0, At(document, 18, 25).A);
    }

    [Fact]
    public void ColorOverlayKeepsTheShape()
    {
        var overlay = LayerEffect.Default(LayerEffectKind.ColorOverlay, new PaletteColor(0, 0, 1));
        var (document, _) = Square(overlay);
        Assert.Equal((0, 0, 255, 255), At(document, 25, 25));
        Assert.Equal((byte)0, At(document, 35, 25).A);
    }

    [Fact]
    public void GlowsAndInnerShadowFallWhereExpected()
    {
        var glow = LayerEffect.Default(LayerEffectKind.OuterGlow, PaletteColor.White) with { Size = 6, Opacity = 1 };
        var (document, _) = Square(glow);
        Assert.InRange(At(document, 32, 25).A, 20, 254);
        Assert.Equal((255, 0, 0, 255), At(document, 25, 25));

        var inner = LayerEffect.Default(LayerEffectKind.InnerShadow, PaletteColor.Black) with { Distance = 4, Blur = 0, Opacity = 1 };
        (document, _) = Square(inner);
        var top = At(document, 25, 21);
        Assert.Equal((byte)255, top.A);
        Assert.True(top.R < 40);
        Assert.Equal((255, 0, 0, 255), At(document, 25, 28));

        var innerGlow = LayerEffect.Default(LayerEffectKind.InnerGlow, PaletteColor.White) with { Size = 4, Opacity = 1 };
        (document, _) = Square(innerGlow);
        Assert.True(At(document, 20, 25).G > At(document, 25, 25).G);
    }

    [Fact]
    public void HiddenEffectsDrawNothing()
    {
        var shadow = LayerEffect.Default(LayerEffectKind.Shadow, PaletteColor.Black) with { Distance = 6, Blur = 0, Opacity = 1, Enabled = false };
        var (document, layer) = Square(shadow);
        Assert.Null(layer.VisibleEffects);
        Assert.Equal((byte)0, At(document, 25, 33).A);
    }

    [Fact]
    public void ClippedLayersStayOffTheBaseShadow()
    {
        var shadow = LayerEffect.Default(LayerEffectKind.Shadow, PaletteColor.Black) with { Distance = 6, Blur = 0, Opacity = 1 };
        var baseLayer = Layer(Solid(10, 10, 255, 0, 0), 20, 20) with { Effects = new LayerEffects(new[] { shadow }) };
        var child = Layer(Solid(60, 60, 0, 255, 0)) with { MaskSourceId = baseLayer.Id };
        var document = Document(60, 60, baseLayer, child);
        Assert.Equal((0, 255, 0, 255), At(document, 25, 25));
        var below = At(document, 25, 33);
        Assert.Equal((byte)255, below.A);
        Assert.Equal((byte)0, below.G);
    }

    [Fact]
    public void AddingAnEffectIsOneUndoStepAndCancelRemovesIt()
    {
        var session = new Editing.EditorSession();
        session.CreateDocument(40, 30, 72, PaletteColor.White);
        var id = session.ActiveLayerId!.Value;
        int steps = session.History.UndoCount;

        Assert.True(session.OpenEffect(LayerEffectKind.Shadow));
        Assert.False(session.CanEditLayers);
        var shadow = session.EditingEffect!;
        session.UpdateEffect(shadow with { Distance = 3 });
        session.UpdateEffect(shadow with { Distance = 9 });
        session.CommitEffectEdit();
        Assert.Equal(steps + 1, session.History.UndoCount);
        Assert.Equal("撤销 添加投影", session.UndoTitle);
        Assert.Equal(9, session.Document!.Find(id)!.Effects![LayerEffectKind.Shadow]!.Distance);

        Assert.True(session.OpenEffect(LayerEffectKind.Stroke));
        session.CancelEffectEdit();
        Assert.False(session.Document!.Find(id)!.Effects!.Contains(LayerEffectKind.Stroke));
        Assert.Equal(steps + 1, session.History.UndoCount);

        session.ToggleEffect(id, LayerEffectKind.Shadow);
        Assert.False(session.Document!.Find(id)!.Effects!.IsEnabled(LayerEffectKind.Shadow));
        session.CopyEffects();
        session.AddBlankLayer();
        Assert.False(session.CanPasteEffects);
        session.ClearEffects();
        session.SelectLayer(id);
        session.ClearEffects();
        Assert.Null(session.Document!.Find(id)!.Effects);
        session.Undo();
        Assert.NotNull(session.Document!.Find(id)!.Effects);
    }

    [Fact]
    public void EffectsRoundTripThroughTheProject()
    {
        var stroke = LayerEffect.Default(LayerEffectKind.Stroke, new PaletteColor(1, 0.5, 0)) with { Size = 7, Inside = true };
        var shadow = LayerEffect.Default(LayerEffectKind.Shadow, PaletteColor.Black) with { Enabled = false, Angle = 120 };
        var json = new LayerEffects(new[] { stroke, shadow }).ToJson();
        Assert.Equal(new[] { "shadow", "stroke" }, json.Select(p => p.Key).ToArray());
        foreach (var key in new[] { "size", "inside", "red", "green", "blue", "opacity" }) Assert.NotNull(json["stroke"]![key]);
        Assert.Null(json["stroke"]!["enabled"]);
        Assert.False((bool)json["shadow"]!["enabled"]!);

        var back = LayerEffects.FromJson(JsonNode.Parse(json.ToJsonString()))!;
        Assert.Equal(stroke, back[LayerEffectKind.Stroke]);
        Assert.Equal(shadow, back[LayerEffectKind.Shadow]);

        var (document, layer) = Square(stroke, shadow);
        var path = Path.Combine(Path.GetTempPath(), "compositor-fx-" + Guid.NewGuid().ToString("N") + ".comp");
        try
        {
            ProjectStore.Save(new ProjectSnapshot(document, layer.Id), path);
            var loaded = ProjectStore.Load(path).Document.Find(layer.Id)!;
            Assert.True(loaded.Effects!.ContentEquals(layer.Effects));
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
}
