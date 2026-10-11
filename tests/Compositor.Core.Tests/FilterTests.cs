using Compositor.Core.Editing;
using Compositor.Core.Filters;
using Compositor.Core.Geometry;
using Compositor.Core.Model;
using Compositor.Core.Rendering;
using static Compositor.Core.Tests.TestImages;

namespace Compositor.Core.Tests;

public class FilterTests
{
    private static EditorSession GraySession(int width = 80, int height = 60)
    {
        var session = new EditorSession { PreviewsInBackground = false };
        session.CreateDocument(width, height, 72, new PaletteColor(0.5, 0.5, 0.5));
        return session;
    }

    private static (byte R, byte G, byte B, byte A) At(EditorSession session, int x, int y)
    {
        using var image = DocumentRenderer.RenderToImage(session.Document!);
        return Pixel(image, x, y);
    }

    private static void Run(EditorSession session, FilterKind kind, Func<FilterSettings, FilterSettings>? change = null)
    {
        Assert.True(session.BeginFilter(kind));
        var edit = session.AdjustmentEdit!;
        Assert.Equal(kind, edit.Filter);
        if (change is not null) session.UpdateFilterEdit(change(edit.FilterSettings));
        session.CommitAdjustmentEdit();
        Assert.Null(session.AdjustmentEdit);
    }

    [Fact]
    public void VignetteDarkensTheCornersAndRepeats()
    {
        var session = GraySession();
        int steps = session.History.UndoCount;
        Run(session, FilterKind.Vignette, s => s with { VignetteAmount = 80 });
        Assert.Equal(steps + 1, session.History.UndoCount);
        Assert.Equal("撤销 暗角", session.UndoTitle);
        Assert.InRange(At(session, 40, 30).R, 120, 132);
        var corner = At(session, 1, 1).R;
        Assert.True(corner < 90, $"corner {corner}");
        Assert.Equal(FilterKind.Vignette, session.LastFilterKind);
        Assert.Equal("暗角", session.LastFilterTitle);
        Assert.True(session.CanRepeatLastFilter);
        session.RepeatLastFilter();
        Assert.True(At(session, 1, 1).R < corner);
        // The panel opens with the settings it was left at.
        Assert.True(session.BeginFilter(FilterKind.Vignette));
        Assert.Equal(80, session.AdjustmentEdit!.FilterSettings.VignetteAmount);
        session.CancelAdjustmentEdit();
    }

    [Fact]
    public void VignetteFillsAnEmptyLayer()
    {
        var session = GraySession();
        session.AddBlankLayer();
        Assert.False(session.CanAdjustPixels);
        Assert.True(session.CanVignette);
        Run(session, FilterKind.Vignette, s => s with { VignetteAmount = 100 });
        Assert.NotNull(session.ActiveLayer!.Asset);
        Assert.True(At(session, 1, 1).R < 80);
        Assert.InRange(At(session, 40, 30).R, 120, 132);
    }

    [Fact]
    public void BloomSpreadsLightPastTheLayer()
    {
        var session = new EditorSession { PreviewsInBackground = false };
        session.CreateDocument(80, 60, 72, PaletteColor.Black);
        session.ForegroundColor = PaletteColor.White;
        session.SelectTool(Tool.Brush);
        session.BrushSettings = session.BrushSettings with { Diameter = 8, Hardness = 1, Opacity = 1, Smoothing = 0 };
        session.BeginBrush(new PointD(40, 30));
        session.FinishBrush();
        Assert.True(At(session, 50, 30).R < 5);
        Run(session, FilterKind.BloomGlow, s => s with { BloomAmount = 100, BloomRadius = 8 });
        Assert.True(At(session, 50, 30).R > 5);
        Assert.Equal(255, At(session, 40, 30).R);
    }

    [Fact]
    public void FiltersThatDoNothingAddNoUndoStep()
    {
        var session = GraySession();
        int steps = session.History.UndoCount;
        Run(session, FilterKind.TonalContrast, s => s with { TonalAmount = 0 });
        Run(session, FilterKind.LensCorrection);
        Assert.Equal(steps, session.History.UndoCount);
    }

    [Fact]
    public void LensCorrectionKeepsTheCenter()
    {
        var bytes = new byte[60 * 60 * 4];
        for (int y = 0; y < 60; y++)
            for (int x = 0; x < 60; x++)
            {
                int p = (y * 60 + x) * 4;
                byte v = (byte)(((x / 6) + (y / 6)) % 2 == 0 ? 255 : 0);
                bytes[p] = bytes[p + 1] = bytes[p + 2] = v; bytes[p + 3] = 255;
            }
        var session = new EditorSession { PreviewsInBackground = false };
        session.InstallProject(new IO.ProjectSnapshot(Document(60, 60, Layer(RasterImage.FromRgba(bytes, 60, 60))), null), null);
        session.SelectLayer(session.Document!.Layers[0].Id);
        using var before = DocumentRenderer.RenderToImage(session.Document!);
        Run(session, FilterKind.LensCorrection, s => s with { Distortion = 80 });
        using var after = DocumentRenderer.RenderToImage(session.Document!);
        Assert.Equal(Pixel(before, 31, 31), Pixel(after, 31, 31));
        int changed = 0;
        for (int i = 0; i < 60; i++) if (Pixel(before, i, 2) != Pixel(after, i, 2)) changed++;
        Assert.True(changed > 3, $"changed {changed}");
        Assert.Equal("撤销 镜头校正", session.UndoTitle);
    }

    [Fact]
    public void TonalContrastChangesAGradient()
    {
        var bytes = new byte[64 * 16 * 4];
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 64; x++)
            {
                int p = (y * 64 + x) * 4;
                bytes[p] = bytes[p + 1] = bytes[p + 2] = (byte)(x * 4); bytes[p + 3] = 255;
            }
        var session = new EditorSession { PreviewsInBackground = false };
        session.InstallProject(new IO.ProjectSnapshot(Document(64, 16, Layer(RasterImage.FromRgba(bytes, 64, 16))), null), null);
        session.SelectLayer(session.Document!.Layers[0].Id);
        using var before = DocumentRenderer.RenderToImage(session.Document!);
        Run(session, FilterKind.TonalContrast, s => s with { TonalAmount = 100 });
        using var after = DocumentRenderer.RenderToImage(session.Document!);
        int changed = 0;
        for (int x = 0; x < 64; x++) if (Math.Abs(Pixel(before, x, 8).R - Pixel(after, x, 8).R) > 2) changed++;
        Assert.True(changed > 10, $"changed {changed}");
    }

    [Fact]
    public void DitherMakesBlackAndWhite()
    {
        var session = GraySession(40, 40);
        Run(session, FilterKind.Dither);
        using var image = DocumentRenderer.RenderToImage(session.Document!);
        int black = 0, white = 0;
        for (int y = 0; y < 40; y++)
            for (int x = 0; x < 40; x++)
            {
                var p = Pixel(image, x, y);
                if (p.R == 0) black++;
                else if (p.R == 255) white++;
            }
        Assert.Equal(1600, black + white);
        Assert.InRange(black, 400, 1200);
        Assert.Equal("撤销 仿色", session.UndoTitle);

        // Every style runs.
        foreach (var style in Enum.GetValues<DitherStyle>())
        {
            session.Undo();
            Run(session, FilterKind.Dither, s => s with { Dither = s.Dither with { Style = style } });
            Assert.Equal("撤销 仿色", session.UndoTitle);
        }
    }

    [Fact]
    public void ScanlinesDrawDarkAndLightRows()
    {
        var session = new EditorSession { PreviewsInBackground = false };
        session.CreateDocument(40, 40, 72, new PaletteColor(0.8, 0.8, 0.8));
        Run(session, FilterKind.Scanlines, s => s with { Scanlines = s.Scanlines with { LineSpacing = 4, Thickness = 50 } });
        using var image = DocumentRenderer.RenderToImage(session.Document!);
        var column = Enumerable.Range(0, 40).Select(y => Pixel(image, 20, y).R).ToArray();
        Assert.Contains(column, v => v < 60);
        Assert.Contains(column, v => v > 180);
    }

    [Fact]
    public void ContentAwareFillCoversASpot()
    {
        var session = GraySession();
        Assert.False(session.CanContentAwareFill);
        session.ForegroundColor = PaletteColor.Black;
        session.SelectTool(Tool.Brush);
        session.BrushSettings = session.BrushSettings with { Diameter = 6, Hardness = 1, Opacity = 1, Smoothing = 0 };
        session.BeginBrush(new PointD(40, 30));
        session.FinishBrush();
        Assert.True(At(session, 40, 30).R < 20);
        session.SetSelection(DocumentSelection.Rectangle(new RectD(34, 24, 12, 12)), "Rectangular Marquee");
        Assert.True(session.CanContentAwareFill);
        Run(session, FilterKind.ContentAwareFill);
        Assert.InRange(At(session, 40, 30).R, 110, 145);
        Assert.Equal("撤销 内容识别填充", session.UndoTitle);
        // Not a "last filter".
        Assert.Null(session.LastFilterKind);
    }

    [Fact]
    public void ContentAwareFillReachesPastTheLayer()
    {
        // A layer smaller than the canvas: the fill extends it over the selection.
        var session = new EditorSession { PreviewsInBackground = false };
        session.InstallProject(new IO.ProjectSnapshot(Document(60, 40, Layer(Solid(40, 40, 100, 150, 200))), null), null);
        session.SelectLayer(session.Document!.Layers[0].Id);
        session.SetSelection(DocumentSelection.Rectangle(new RectD(36, 10, 8, 8)), "Rectangular Marquee");
        Run(session, FilterKind.ContentAwareFill);
        var p = At(session, 42, 14);
        Assert.Equal(255, p.A);
        Assert.InRange(p.B, 180, 220);
    }
}
