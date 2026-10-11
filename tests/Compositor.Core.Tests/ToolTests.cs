using System.Text.Json.Nodes;
using Compositor.Core.Editing;
using Compositor.Core.Geometry;
using Compositor.Core.IO;
using Compositor.Core.Model;
using Compositor.Core.Rendering;
using Compositor.Core.Text;
using static Compositor.Core.Tests.TestImages;

namespace Compositor.Core.Tests;

public class ToolTests
{
    private static EditorSession WhiteSession(int width = 100, int height = 60)
    {
        var session = new EditorSession();
        session.CreateDocument(width, height, 72, PaletteColor.White);
        session.ForegroundColor = PaletteColor.Black;
        return session;
    }

    [Fact]
    public void LinearGradientPreviewsThenAppliesAsOneStep()
    {
        var session = WhiteSession();
        session.GradientSettings = new GradientSettings(Style: GradientStyle.ForegroundToBackground);
        session.SelectTool(Tool.Gradient);
        int steps = session.History.UndoCount;
        session.BeginGradient(new PointD(0, 30));
        session.MoveGradient(end: new PointD(100, 30));
        Assert.NotNull(session.GradientEdit);
        Assert.Equal(steps, session.History.UndoCount);
        session.CommitGradient();
        Assert.Null(session.GradientEdit);
        Assert.Equal(steps + 1, session.History.UndoCount);
        using var image = DocumentRenderer.RenderToImage(session.Document!);
        Assert.InRange(Pixel(image, 2, 30).R, 0, 12);
        Assert.InRange(Pixel(image, 50, 30).R, 115, 140);
        Assert.InRange(Pixel(image, 98, 30).R, 243, 255);
    }

    [Fact]
    public void GradientOnAMaskIsGray()
    {
        var session = WhiteSession();
        session.AddMask();
        session.SelectTool(Tool.Gradient);
        session.GradientSettings = new GradientSettings(GradientShape.Radial, GradientStyle.ForegroundToBackground);
        session.BeginGradient(new PointD(50, 30));
        session.MoveGradient(end: new PointD(90, 30));
        session.SelectTool(Tool.Move); // switching tools applies it
        var mask = session.ActiveLayer!.Mask!.Asset;
        using var pixmap = mask.PeekPixels();
        var bytes = pixmap.GetPixelSpan();
        Assert.InRange(bytes[30 * pixmap.RowBytes + 50], 0, 10);
        Assert.InRange(bytes[30 * pixmap.RowBytes + 95], 245, 255);
    }

    [Fact]
    public void ShapesMakeLayersThatRedrawWhenScaled()
    {
        var session = WhiteSession();
        session.SelectTool(Tool.Shape);
        session.ShapeKind = ShapeKind.Rectangle;
        session.ShapeCornerRadius = 6;
        session.BeginShape(new PointD(10, 10));
        session.DragShape(new PointD(50, 40), square: false, fromCenter: false);
        session.FinishShape();
        var layer = session.ActiveLayer!;
        Assert.NotNull(layer.Shape);
        Assert.Equal(40, layer.Asset!.Width);
        Assert.Equal("矩形 1", layer.Name);
        using (var image = DocumentRenderer.RenderToImage(session.Document!))
        {
            Assert.Equal(0, Pixel(image, 30, 25).R);
            Assert.Equal(255, Pixel(image, 10, 10).R); // a rounded corner
        }
        session.SelectTool(Tool.Move);
        session.BeginTransform(persistent: true);
        session.PreviewTransform(layer.Transform with { Size = new SizeD(80, 40) });
        session.CommitTransform();
        Assert.Equal(80, session.ActiveLayer!.Asset!.Width);
        Assert.NotNull(session.ActiveLayer.Shape);

        session.SelectTool(Tool.Shape);
        session.ShapeKind = ShapeKind.Line;
        session.ShapeLineWidth = 4;
        session.BeginShape(new PointD(5, 50));
        session.DragShape(new PointD(95, 52), square: true, fromCenter: false); // snaps flat
        session.FinishShape();
        Assert.Equal(ShapeKind.Line, session.ActiveLayer!.Shape!.Kind);
        Assert.InRange(session.ActiveLayer.Asset!.Height, 4, 6);
    }

    [Fact]
    public void ShapeAndTextSurviveTheProject()
    {
        var shape = new LayerShape(ShapeKind.Line, 1, 0.5, 0, 0, 3, new PointD(0.1, 0.2), new PointD(0.9, 0.8));
        var back = LayerShape.FromJson(JsonNode.Parse(shape.ToJson().ToJsonString()));
        Assert.Equal(shape, back);

        var text = new LayerText { Content = "Hello 世界", FontName = "Arial", FontSize = 40, BoxSize = new SizeD(300, 120), Alignment = TextAlign.Center }
            .SettingColor(new PaletteColor(1, 0, 0), 0, 5);
        var json = text.ToJson();
        Assert.NotNull(json["colorRuns"]);
        foreach (var key in new[] { "content", "fontName", "fontSize", "red", "green", "blue", "alignment", "tracking", "leading" }) Assert.NotNull(json[key]);
        var parsed = LayerText.FromJson(JsonNode.Parse(json.ToJsonString()))!;
        Assert.Equal(text.Content, parsed.Content);
        Assert.Equal(text.ColorRuns, parsed.ColorRuns);
        Assert.Equal(new SizeD(300, 120), parsed.BoxSize);
        Assert.Equal(TextAlign.Center, parsed.Alignment);
    }

    [Fact]
    public void TypingKeepsRunsAndTextLayersEditAgain()
    {
        var session = WhiteSession(400, 200);
        session.SelectTool(Tool.Type);
        session.BeginText(new PointD(20, 100));
        var draft = session.TextDraft!;
        session.ReplaceText(0, 0, "abc");
        session.SetTextSelection(1, 1);
        session.ChangeTextStyle((s, start, length) => s.SettingColor(new PaletteColor(1, 0, 0), start, length));
        session.ReplaceText(2, 0, "X"); // typed after the red "b": red too
        Assert.Equal("abXc", draft.Style.Content);
        Assert.Equal(new PaletteColor(1, 0, 0), draft.Style.ColorAt(2));
        Assert.Equal(PaletteColor.Black, draft.Style.ColorAt(3));
        int steps = session.History.UndoCount;
        Assert.True(session.CommitText());
        Assert.Equal(steps + 1, session.History.UndoCount);
        var layer = session.ActiveLayer!;
        Assert.Equal("abXc", layer.Text!.Content);
        Assert.Equal("abXc", layer.Name);

        // Opened again, changed, applied: the same layer, one more step.
        session.BeginText(new PointD(layer.Transform.Center.X, layer.Transform.Center.Y));
        Assert.Equal(layer.Id, session.TextDraft!.LayerId);
        session.ReplaceText(0, 4, "Hello");
        session.CommitText();
        Assert.Equal("Hello", session.Document!.Find(layer.Id)!.Text!.Content);
        Assert.Equal(steps + 2, session.History.UndoCount);

        // Painting turns it into plain pixels.
        session.SelectTool(Tool.Brush);
        session.BeginBrush(new PointD(30, 100));
        session.FinishBrush();
        Assert.Null(session.Document!.Find(layer.Id)!.Text);
    }

    [Fact]
    public void LayoutWrapsAlignsAndFindsCarets()
    {
        var style = new LayerText { Content = "one two three four", FontName = "Arial", FontSize = 20, BoxSize = new SizeD(110, 200) };
        var layout = TextLayout.Make(style);
        Assert.True(layout.Lines.Count >= 2);
        Assert.All(layout.Lines, l => Assert.True(l.Width <= 110 - 2 * LayerText.Padding + 0.5));
        var first = layout.Lines[0];
        Assert.Equal(LayerText.Padding, first.X, 3);
        int index = layout.IndexAt(new PointD(first.Carets[2] + 0.1, first.Top + 5));
        Assert.Equal(first.Boundaries[2], index);
        var (x, top, _) = layout.Caret(index);
        Assert.Equal(first.Carets[2], x, 3);
        Assert.Equal(first.Top, top, 3);

        var centered = TextLayout.Make(style with { Alignment = TextAlign.Center });
        Assert.True(centered.Lines[0].X > LayerText.Padding);

        var point = TextLayout.Make(new LayerText { Content = "Hi\nthere", FontName = "Arial", FontSize = 30 });
        Assert.Equal(2, point.Lines.Count);
        Assert.Equal(Math.Ceiling(2 * 36 + 2 * LayerText.Padding), point.Height);
        using var image = point.Render().Image;
        Assert.Equal((int)point.Width, image.Width);

        // Chinese falls back to a face that has it.
        var chinese = TextLayout.Make(new LayerText { Content = "你好", FontName = "Arial", FontSize = 30 });
        Assert.True(chinese.Lines[0].Width > 40);
    }
}
