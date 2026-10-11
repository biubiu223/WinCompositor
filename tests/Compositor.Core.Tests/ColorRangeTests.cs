using Compositor.Core.Editing;
using Compositor.Core.Geometry;
using Compositor.Core.Model;
using static Compositor.Core.Tests.TestImages;

namespace Compositor.Core.Tests;

public class ColorRangeTests
{
    /// <summary>A 60×40 canvas: red on the left half, blue on the right, a dark-red stripe at the top.</summary>
    private static EditorSession Halves()
    {
        var session = new EditorSession { PreviewsInBackground = false };
        var bytes = new byte[60 * 40 * 4];
        for (int y = 0; y < 40; y++)
            for (int x = 0; x < 60; x++)
            {
                int p = (y * 60 + x) * 4;
                bool left = x < 30;
                bytes[p] = (byte)(left ? (y < 5 ? 200 : 255) : 0); bytes[p + 2] = (byte)(left ? 0 : 255); bytes[p + 3] = 255;
            }
        session.InstallProject(new IO.ProjectSnapshot(Document(60, 40, Layer(RasterImage.FromRgba(bytes, 60, 40))), null), null);
        return session;
    }

    [Fact]
    public void PickingAColorSelectsEveryPixelLikeIt()
    {
        var session = Halves();
        int steps = session.History.UndoCount;
        Assert.True(session.CanSelectColorRange);
        session.BeginColorRange();
        Assert.NotNull(session.ColorRange);
        Assert.False(session.CanUseHistory);
        session.SampleColorRange(new PointD(10, 20));
        var shown = session.DisplayedSelection!;
        Assert.True(shown.Contains(new PointD(10, 20)));
        Assert.False(shown.Contains(new PointD(50, 20)));
        // Fuzziness 40 leaves out the darker stripe (55 away); 60 takes it in.
        Assert.False(shown.Contains(new PointD(10, 2)));
        session.SetColorRangeOptions(60, false);
        Assert.True(session.DisplayedSelection!.Contains(new PointD(10, 2)));
        Assert.NotNull(session.ColorRange!.Preview);
        // Nothing is recorded until OK.
        Assert.Null(session.Selection);
        Assert.Equal(steps, session.History.UndoCount);
        session.CommitColorRange();
        Assert.Null(session.ColorRange);
        Assert.True(session.Selection!.Contains(new PointD(10, 20)));
        Assert.False(session.Selection.Contains(new PointD(50, 20)));
        Assert.Equal("撤销 色彩范围", session.UndoTitle);
    }

    [Fact]
    public void AddRemoveAndInvert()
    {
        var session = Halves();
        session.BeginColorRange();
        session.SampleColorRange(new PointD(10, 20));
        session.SampleColorRange(new PointD(50, 20), shift: true);
        Assert.True(session.DisplayedSelection!.Contains(new PointD(50, 20)));
        session.SampleColorRange(new PointD(10, 20), alt: true);
        Assert.False(session.DisplayedSelection!.Contains(new PointD(10, 20)));
        Assert.True(session.DisplayedSelection!.Contains(new PointD(50, 20)));
        session.SetColorRangeOptions(40, invert: true);
        Assert.True(session.DisplayedSelection!.Contains(new PointD(10, 20)));
        Assert.False(session.DisplayedSelection!.Contains(new PointD(50, 20)));
    }

    [Fact]
    public void CancelKeepsTheSelectionThereWas()
    {
        var session = Halves();
        session.SetSelection(DocumentSelection.Rectangle(new RectD(0, 0, 5, 5)), "Rectangular Marquee");
        var before = session.Selection;
        session.BeginColorRange();
        Assert.Same(before, session.DisplayedSelection);
        session.SampleColorRange(new PointD(50, 20));
        Assert.NotSame(before, session.DisplayedSelection);
        session.CancelColorRange();
        Assert.Same(before, session.Selection);
        Assert.Same(before, session.DisplayedSelection);
    }

    [Fact]
    public void NoMatchDeselects()
    {
        var session = Halves();
        session.SetSelection(DocumentSelection.Rectangle(new RectD(0, 0, 5, 5)), "Rectangular Marquee");
        session.BeginColorRange();
        session.SampleColorRange(new PointD(10, 20));
        session.SetColorRangeOptions(0, invert: false);
        session.SampleColorRange(new PointD(10, 20), alt: true);
        Assert.Null(session.DisplayedSelection);
        session.CommitColorRange();
        Assert.Null(session.Selection);
    }
}
