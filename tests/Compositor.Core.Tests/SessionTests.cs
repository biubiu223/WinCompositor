using Compositor.Core.Editing;
using Compositor.Core.Geometry;
using Compositor.Core.IO;
using Compositor.Core.Model;
using Compositor.Core.Painting;
using Compositor.Core.Rendering;
using static Compositor.Core.Tests.TestImages;

namespace Compositor.Core.Tests;

public class SessionTests
{
    private static EditorSession NewSession(int width = 100, int height = 80, PaletteColor? background = null)
    {
        var session = new EditorSession();
        session.CreateDocument(width, height, 72, background);
        return session;
    }

    [Fact]
    public void NewCanvasHasOneLayerAndCleanHistory()
    {
        var session = NewSession(background: PaletteColor.White);
        Assert.Single(session.Document!.Layers);
        Assert.Equal("背景", session.Document.Layers[0].Name);
        Assert.False(session.CanUndo);
        Assert.False(session.IsModified);
    }

    [Fact]
    public void BrushStrokePaintsAndUndoes()
    {
        var session = NewSession();
        session.SelectTool(Tool.Brush);
        session.ForegroundColor = new PaletteColor(1, 0, 0);
        session.BrushSettings = session.BrushSettings with { Diameter = 10, Hardness = 1 };
        session.BeginBrush(new PointD(20, 20));
        session.ContinueBrush(new PointD(60, 20));
        session.FinishBrush();
        var layer = session.Document!.Layers[0];
        Assert.NotNull(layer.Asset);
        using var image = DocumentRenderer.RenderToImage(session.Document);
        var painted = Pixel(image, 40, 20);
        Assert.Equal(255, painted.R);
        Assert.Equal(255, painted.A);
        Assert.Equal(0, Pixel(image, 40, 50).A);
        Assert.True(session.CanUndo);
        session.Undo();
        Assert.Null(session.Document!.Layers[0].Asset);
    }

    [Fact]
    public void EraserClearsPixels()
    {
        var session = NewSession(background: PaletteColor.White);
        session.SelectTool(Tool.Brush);
        session.BrushMode = BrushToolMode.Erase;
        session.BrushSettings = session.BrushSettings with { Diameter = 20, Hardness = 1 };
        session.BeginBrush(new PointD(50, 40));
        session.FinishBrush();
        using var image = DocumentRenderer.RenderToImage(session.Document!);
        Assert.Equal(0, Pixel(image, 50, 40).A);
        Assert.Equal(255, Pixel(image, 5, 5).A);
    }

    [Fact]
    public void BrushRespectsSelection()
    {
        var session = NewSession();
        session.SetSelection(DocumentSelection.Rectangle(new RectD(0, 0, 50, 80)), "Rectangular Marquee");
        session.SelectTool(Tool.Brush);
        session.BrushSettings = session.BrushSettings with { Diameter = 10 };
        session.BeginBrush(new PointD(10, 40));
        session.ContinueBrush(new PointD(90, 40));
        session.FinishBrush();
        using var image = DocumentRenderer.RenderToImage(session.Document! with { Selection = null });
        Assert.Equal(255, Pixel(image, 30, 40).A);
        Assert.Equal(0, Pixel(image, 70, 40).A);
    }

    [Fact]
    public void PaintingAMaskHidesPixels()
    {
        var session = NewSession(background: PaletteColor.White);
        session.AddMask();
        Assert.True(session.IsMaskSelected);
        session.SelectTool(Tool.Brush);
        session.ForegroundColor = PaletteColor.Black;
        session.BrushSettings = session.BrushSettings with { Diameter = 20 };
        session.BeginBrush(new PointD(50, 40));
        session.FinishBrush();
        using var image = DocumentRenderer.RenderToImage(session.Document!);
        Assert.Equal(0, Pixel(image, 50, 40).A);
        Assert.Equal(255, Pixel(image, 5, 5).A);
    }

    [Fact]
    public void ProjectRoundTripKeepsLayersMasksAndGroups()
    {
        var session = NewSession(background: new PaletteColor(0, 0.5, 1));
        session.AddMask();
        session.AddBlankLayer();
        session.GroupSelectedLayers();
        var folderId = session.ActiveLayerId!.Value;
        session.SetLayerOpacity(0.5);
        session.AddGuide(GuideAxis.Vertical, 33);
        var path = Path.Combine(Path.GetTempPath(), "compositor-test-" + Guid.NewGuid().ToString("N") + ".comp");
        try
        {
            ProjectStore.Save(session.ProjectSnapshot()!, path);
            Assert.True(File.Exists(Path.Combine(path, "manifest.json")));
            var loaded = ProjectStore.Load(path);
            var doc = loaded.Document;
            Assert.Equal(session.Document!.Id, doc.Id);
            Assert.Equal(3, doc.Layers.Count);
            var folder = doc.Find(folderId)!;
            Assert.True(folder.IsGroup);
            Assert.Equal(0.5, folder.Opacity);
            Assert.NotNull(doc.Layers[0].Mask);
            Assert.Single(doc.Guides);
            using var a = DocumentRenderer.RenderToImage(session.Document);
            using var b = DocumentRenderer.RenderToImage(doc);
            Assert.Equal(Pixel(a, 10, 10), Pixel(b, 10, 10));
            Assert.InRange(Pixel(b, 10, 10).G, 126, 130);
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    [Fact]
    public void LoadsMacStyleManifest()
    {
        var path = Path.Combine(Path.GetTempPath(), "compositor-mac-" + Guid.NewGuid().ToString("N") + ".comp");
        Directory.CreateDirectory(Path.Combine(path, "images"));
        try
        {
            var id = "6F1D3C2A-0B7E-4E8A-9C4D-2A1B3C4D5E6F";
            var image = Solid(4, 3, 255, 0, 0);
            File.WriteAllBytes(Path.Combine(path, "images", id + ".png"), image.Image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100).ToArray());
            File.WriteAllText(Path.Combine(path, "manifest.json"), $$"""
            {
              "format": "com.compositor.project", "version": 11, "colorSpace": "sRGB",
              "documentID": "0C5E7A91-3B2D-4F6A-8E1C-9D0B7A6F5E4D", "width": 8, "height": 6, "resolution": 72,
              "activeLayerID": "{{id}}",
              "layers": [ { "id": "{{id}}", "name": "Background", "imageFile": "{{id}}.png", "isVisible": true,
                "isGroup": false, "opacity": 1, "blendMode": "Multiply",
                "transform": { "origin": [2, 1], "size": [4, 3], "rotation": 0, "flipX": false, "flipY": false, "sampling": "High quality" } } ]
            }
            """);
            var loaded = ProjectStore.Load(path);
            Assert.Equal(8, loaded.Document.Width);
            var layer = Assert.Single(loaded.Document.Layers);
            Assert.Equal(BlendMode.Multiply, layer.BlendMode);
            Assert.Equal(new PointD(2, 1), layer.Transform.Origin);
        }
        finally { Directory.Delete(path, true); }
    }

    [Fact]
    public void RejectsMismatchedImageName()
    {
        var path = Path.Combine(Path.GetTempPath(), "compositor-bad-" + Guid.NewGuid().ToString("N") + ".comp");
        Directory.CreateDirectory(path);
        try
        {
            File.WriteAllText(Path.Combine(path, "manifest.json"), """
            { "format": "com.compositor.project", "version": 11, "colorSpace": "sRGB",
              "documentID": "0C5E7A91-3B2D-4F6A-8E1C-9D0B7A6F5E4D", "width": 8, "height": 6,
              "layers": [ { "id": "6F1D3C2A-0B7E-4E8A-9C4D-2A1B3C4D5E6F", "name": "A", "imageFile": "other.png", "isVisible": true,
                "transform": { "origin": [0, 0], "size": [4, 3] } } ] }
            """);
            Assert.Throws<ProjectException>(() => ProjectStore.Load(path));
        }
        finally { Directory.Delete(path, true); }
    }

    [Fact]
    public void SelectionAddSubtractAndInverse()
    {
        var session = NewSession();
        using var a = new SkiaSharp.SKPath();
        a.AddRect(new SkiaSharp.SKRect(0, 0, 50, 80));
        session.ApplySelection(a, SelectionMode.Replace, "Rectangular Marquee");
        using var b = new SkiaSharp.SKPath();
        b.AddRect(new SkiaSharp.SKRect(40, 0, 100, 40));
        session.ApplySelection(b, SelectionMode.Add, "Rectangular Marquee");
        Assert.True(session.Selection!.Contains(new PointD(80, 10)));
        Assert.False(session.Selection.Contains(new PointD(80, 70)));
        session.ApplySelection(b, SelectionMode.Subtract, "Rectangular Marquee");
        Assert.False(session.Selection!.Contains(new PointD(45, 10)));
        session.InvertSelection();
        Assert.True(session.Selection!.Contains(new PointD(80, 70)));
        Assert.False(session.Selection.Contains(new PointD(10, 70)));
    }

    [Fact]
    public void MagicWandSelectsContiguousColor()
    {
        var session = NewSession(40, 40, PaletteColor.White);
        session.SetSelection(DocumentSelection.Rectangle(new RectD(10, 10, 20, 20)), "Rectangular Marquee");
        session.ForegroundColor = new PaletteColor(0, 0, 1);
        session.FillSelection(EditorSession.FillSource.Foreground);
        session.Deselect();
        session.WandSettings = session.WandSettings with { Tolerance = 10 };
        session.MagicWandAt(new PointD(15, 15), SelectionMode.Replace);
        var selection = session.Selection!;
        Assert.True(selection.Contains(new PointD(20, 20)));
        Assert.False(selection.Contains(new PointD(5, 5)));
        Assert.Equal(new RectD(10, 10, 20, 20), selection.Bounds);
    }

    [Fact]
    public void GroupUngroupAndMergeDown()
    {
        var session = NewSession(background: PaletteColor.White);
        var background = session.ActiveLayerId!.Value;
        session.AddBlankLayer();
        session.SelectTool(Tool.Brush);
        session.BeginBrush(new PointD(10, 10));
        session.FinishBrush();
        session.SelectTool(Tool.Move);
        Assert.True(session.CanMergeLayers);
        session.MergeLayers();
        Assert.Single(session.Document!.Layers);
        session.AddBlankLayer();
        session.SelectLayers(session.Document!.Layers.Select(l => l.Id), session.ActiveLayerId);
        session.GroupSelectedLayers();
        Assert.Equal(3, session.Document!.Layers.Count);
        Assert.True(session.ActiveLayer!.IsGroup);
        session.UngroupLayers();
        Assert.Equal(2, session.Document!.Layers.Count);
        Assert.All(session.Document.Layers, l => Assert.Null(l.ParentId));
        Assert.NotEqual(background, Guid.Empty);
    }

    [Fact]
    public void TransformMovesAndUndoes()
    {
        var session = NewSession();
        session.ImportImages(Array.Empty<string>());
        session.InsertImage(Solid(10, 10, 0, 255, 0));
        var id = session.ActiveLayerId!.Value;
        session.BeginTransform(persistent: false);
        var draft = session.TransformEdit!.Draft;
        session.PreviewTransform(draft with { Origin = new PointD(0, 0), Rotation = 90 });
        session.CommitTransform();
        var moved = session.Document!.Find(id)!;
        Assert.Equal(new PointD(0, 0), moved.Transform.Origin);
        Assert.Equal(90, moved.Transform.Rotation);
        session.Undo();
        Assert.Equal(draft, session.Document!.Find(id)!.Transform);
    }

    [Fact]
    public void MovingSelectedPixelsLeavesAHole()
    {
        var session = NewSession(40, 40, PaletteColor.White);
        session.SetSelection(DocumentSelection.Rectangle(new RectD(0, 0, 10, 10)), "Rectangular Marquee");
        Assert.True(session.BeginFloatingTransform(duplicate: false, persistent: false));
        var draft = session.TransformEdit!.Draft;
        session.PreviewTransform(draft with { Origin = new PointD(20, 20) });
        session.CommitTransform();
        using var image = DocumentRenderer.RenderToImage(session.Document! with { Selection = null });
        Assert.Equal(0, Pixel(image, 5, 5).A);
        Assert.Equal(255, Pixel(image, 25, 25).A);
        Assert.True(session.Selection!.Contains(new PointD(25, 25)));
    }

    [Fact]
    public void RotateCanvasSwapsSize()
    {
        var session = NewSession(100, 50, PaletteColor.White);
        session.RotateCanvas(clockwise: true);
        Assert.Equal(50, session.Document!.Width);
        Assert.Equal(100, session.Document.Height);
        using var image = DocumentRenderer.RenderToImage(session.Document);
        Assert.Equal(255, Pixel(image, 25, 90).A);
    }

    [Fact]
    public void ImageSizeResamplesLayers()
    {
        var session = NewSession(100, 50, PaletteColor.White);
        session.ResizeImage(50, 25, 72);
        Assert.Equal(50, session.Document!.Width);
        var layer = session.Document.Layers[0];
        Assert.Equal(50, layer.Asset!.Width);
        Assert.Equal(25, layer.Asset.Height);
    }

    [Fact]
    public void CopyPasteMakesLayerInPlace()
    {
        var session = NewSession(40, 40, PaletteColor.White);
        session.SetSelection(DocumentSelection.Rectangle(new RectD(5, 6, 10, 12)), "Rectangular Marquee");
        var copied = session.CopyPixels();
        Assert.NotNull(copied);
        Assert.Equal(new RectD(5, 6, 10, 12), copied!.Source);
        session.PastePixels(copied.Image, copied.Source);
        Assert.Equal(2, session.Document!.Layers.Count);
        Assert.Equal(new PointD(5, 6), session.ActiveLayer!.Transform.Origin);
    }

    [Fact]
    public void ExportPngCarriesResolution()
    {
        var image = Solid(2, 2, 1, 2, 3);
        var png = ImageExporter.EncodePng(image.Image, 300);
        var text = System.Text.Encoding.ASCII.GetString(png);
        Assert.Contains("pHYs", text);
        var jpeg = ImageExporter.EncodeJpeg(image.Image, 90, 300);
        Assert.Equal(1, jpeg[13]);
        Assert.Equal(300, jpeg[14] << 8 | jpeg[15]);
    }
}
