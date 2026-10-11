using Compositor.Core.Editing;
using Compositor.Core.Geometry;
using Compositor.Core.Model;
using Compositor.Core.Painting;
using Compositor.Core.Pixels;
using Compositor.Core.Rendering;
using static Compositor.Core.Tests.TestImages;

namespace Compositor.Core.Tests;

public class RetouchTests
{
    /// <summary>A 60×40 canvas: red on the left half, blue on the right.</summary>
    private static EditorSession Halves()
    {
        var session = new EditorSession();
        var bytes = new byte[60 * 40 * 4];
        for (int y = 0; y < 40; y++)
            for (int x = 0; x < 60; x++)
            {
                int p = (y * 60 + x) * 4;
                bool left = x < 30;
                bytes[p] = (byte)(left ? 255 : 0); bytes[p + 2] = (byte)(left ? 0 : 255); bytes[p + 3] = 255;
            }
        session.InstallProject(new IO.ProjectSnapshot(Document(60, 40, Layer(RasterImage.FromRgba(bytes, 60, 40))), null), null);
        session.SelectLayer(session.Document!.Layers[0].Id);
        session.BrushSettings = session.BrushSettings with { Diameter = 8, Hardness = 1, Opacity = 1, Smoothing = 0 };
        return session;
    }

    private static (byte R, byte G, byte B, byte A) At(EditorSession session, int x, int y)
    {
        using var image = DocumentRenderer.RenderToImage(session.Document!);
        return Pixel(image, x, y);
    }

    [Fact]
    public void CloneStampCopiesFromTheSource()
    {
        var session = Halves();
        session.SelectTool(Tool.CloneStamp);
        // Clone Stamp keeps a tip of its own (soft at first); a hard one copies exact colors.
        Assert.Equal(0, session.BrushSettings.Hardness);
        session.BrushSettings = session.BrushSettings with { Diameter = 8, Hardness = 1 };
        session.BeginBrush(new PointD(45, 20)); // no source yet: refused
        Assert.Null(session.BrushStroke);
        session.SetCloneSource(new PointD(10, 20));
        session.BeginBrush(new PointD(45, 20));
        session.ContinueBrush(new PointD(46, 20));
        session.FinishBrush();
        Assert.Equal((255, 0, 0, 255), At(session, 45, 20));
        Assert.Equal((0, 0, 255, 255), At(session, 55, 20));
        Assert.Equal("撤销 仿制图章", session.UndoTitle);
        // Aligned: the next stroke keeps the same offset (35 px left of the brush).
        session.BeginBrush(new PointD(50, 30));
        session.FinishBrush();
        Assert.Equal((255, 0, 0, 255), At(session, 50, 30));
    }

    [Fact]
    public void SpotHealingRemovesASpot()
    {
        var session = new EditorSession();
        session.CreateDocument(80, 60, 72, new PaletteColor(0.5, 0.5, 0.5));
        session.BrushSettings = session.BrushSettings with { Diameter = 6, Hardness = 1, Opacity = 1, Smoothing = 0 };
        session.SelectTool(Tool.Brush);
        session.ForegroundColor = PaletteColor.Black;
        session.BeginBrush(new PointD(40, 30));
        session.FinishBrush();
        Assert.True(At(session, 40, 30).R < 20);
        session.BrushSettings = session.BrushSettings with { Diameter = 12 };
        foreach (var mode in new[] { SpotHealingMode.ContentAware, SpotHealingMode.CreateTexture })
        {
            session.SpotHealingMode = mode;
            session.SelectTool(Tool.SpotHealing);
            session.BeginBrush(new PointD(40, 30));
            session.FinishBrush();
            Assert.InRange(At(session, 40, 30).R, 110, 145);
            session.Undo();
        }
    }

    [Fact]
    public void BlurSoftensAnEdgeAndWorksOnMasks()
    {
        var session = Halves();
        session.SelectTool(Tool.Smear);
        session.SmearMode = SmearMode.Blur;
        session.BlurRadius = 3;
        session.BrushSettings = session.BrushSettings with { Diameter = 16, Hardness = 1 };
        session.BeginBrush(new PointD(30, 20));
        session.FinishBrush();
        var edge = At(session, 29, 20);
        Assert.InRange(edge.R, 60, 230);
        Assert.InRange(edge.B, 30, 200);
        Assert.Equal("撤销 模糊", session.UndoTitle);

        session.AddMask();
        session.ForegroundColor = PaletteColor.Black;
        session.SelectTool(Tool.Brush);
        session.BeginBrush(new PointD(10, 10));
        session.FinishBrush();
        session.SelectTool(Tool.Smear);
        session.BeginBrush(new PointD(10, 10));
        session.FinishBrush();
        Assert.Equal("撤销 模糊", session.UndoTitle);
    }

    [Fact]
    public void LiquifyPushesAndSmudgeDrags()
    {
        var session = Halves();
        session.SelectTool(Tool.Smear);
        session.SmearMode = SmearMode.Liquify;
        session.BrushSettings = session.BrushSettings with { Diameter = 20, Hardness = 0.5, Opacity = 1 };
        session.BeginBrush(new PointD(26, 20));
        Assert.NotNull(session.WarpStroke);
        for (int x = 27; x <= 40; x++) session.ContinueBrush(new PointD(x, 20));
        var options = new RenderOptions();
        session.ApplyWarpPreview(options);
        Assert.NotNull(options.For(session.ActiveLayerId!.Value)?.Image);
        session.FinishBrush();
        Assert.Null(session.WarpStroke);
        Assert.Equal("撤销 液化", session.UndoTitle);
        // Red pushed into the blue half.
        Assert.True(At(session, 33, 20).R > 128);

        session.Undo();
        session.SmearMode = SmearMode.Smudge;
        session.BeginBrush(new PointD(20, 20));
        for (int x = 21; x <= 40; x++) session.ContinueBrush(new PointD(x, 20));
        session.FinishBrush();
        Assert.Equal("撤销 涂抹", session.UndoTitle);
        Assert.True(At(session, 34, 20).R > 40);
    }

    [Fact]
    public void HealingAndCloningRefuseMasks()
    {
        var session = Halves();
        session.AddMask();
        Assert.True(session.IsMaskSelected);
        string? message = null;
        session.Message += (_, m) => message = m.Text;
        session.SelectTool(Tool.SpotHealing);
        session.BeginBrush(new PointD(10, 10));
        Assert.Null(session.BrushStroke);
        Assert.NotNull(message);
    }
}
