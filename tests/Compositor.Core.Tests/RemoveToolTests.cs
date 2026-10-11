using Compositor.Core.Editing;
using Compositor.Core.Geometry;
using Compositor.Core.Inpainting;
using Compositor.Core.Model;

namespace Compositor.Core.Tests;

/// <summary>The Remove tool: paint over something, let go, and it's filled away.</summary>
public class RemoveToolTests
{
    [Fact]
    public void ACircledObjectCountsAsPainted()
    {
        int w = 60, h = 60;
        var ring = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double d = Math.Sqrt((x - 30) * (x - 30) + (y - 30) * (y - 30));
                if (d >= 15 && d <= 18) ring[y * w + x] = 255;
            }
        var closed = RemoveMask.CloseLoops(ring, w, h);
        Assert.Equal(255, closed[30 * w + 30]); // the middle, never painted
        Assert.Equal(0, closed[2 * w + 2]);     // outside stays out
    }

    [Fact]
    public async Task PaintingOverAnObjectRemovesIt()
    {
        var session = new EditorSession { PreviewsInBackground = false };
        session.CreateDocument(160, 120, 72, new PaletteColor(0.3, 0.5, 0.3));
        // A white block on the green.
        var layer = session.ActiveLayer!;
        var pixels = layer.Asset!.CopyPixels();
        for (int y = 50; y < 70; y++)
            for (int x = 70; x < 90; x++) pixels.AsSpan((y * 160 + x) * 4, 4).Fill(255);
        session.Document = session.Document!.UpdateLayer(layer.Id, l => l with { Asset = RasterImage.FromRgba(pixels, 160, 120) });
        session.SelectTool(Tool.Remove);
        session.BrushSettings = session.BrushSettings with { Diameter = 30, Hardness = 1 };
        session.BeginBrush(new PointD(72, 60));
        session.ContinueBrush(new PointD(80, 60));
        session.ContinueBrush(new PointD(88, 60));
        session.FinishBrush();
        for (int i = 0; i < 200 && session.RemoveBusy; i++) await Task.Delay(25);
        Assert.False(session.RemoveBusy);
        Assert.Null(session.BrushStroke);
        Assert.Equal("撤销 抹除", session.UndoTitle);
        var after = session.ActiveLayer!.Asset!.CopyPixels();
        int center = (60 * 160 + 80) * 4;
        Assert.InRange(after[center], 60, 95);      // green's red, not white
        Assert.InRange(after[center + 1], 110, 140);
    }
}
