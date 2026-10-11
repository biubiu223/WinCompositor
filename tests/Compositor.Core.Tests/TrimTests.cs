using Compositor.Core.Editing;
using Compositor.Core.Geometry;
using Compositor.Core.Model;
using static Compositor.Core.Tests.TestImages;

namespace Compositor.Core.Tests;

public class TrimTests
{
    [Fact]
    public void TrimsTransparentEdges()
    {
        var session = new EditorSession();
        session.InstallProject(new IO.ProjectSnapshot(Document(40, 30, Layer(Solid(10, 8, 255, 0, 0), 5, 6)), null), null);
        Assert.True(session.Trim(new TrimOptions()));
        Assert.Equal((10, 8), (session.Document!.Width, session.Document.Height));
        Assert.Equal(new PointD(0, 0), session.Document.Layers[0].Transform.Origin);
        Assert.Equal("撤销 裁切", session.UndoTitle);
        session.Undo();
        Assert.Equal(40, session.Document!.Width);
    }

    [Fact]
    public void TrimsByCornerColorAndOnlyChosenSides()
    {
        var session = new EditorSession();
        session.InstallProject(new IO.ProjectSnapshot(Document(40, 30, Layer(Solid(40, 30, 255, 255, 255)), Layer(Solid(10, 10, 0, 0, 0), 20, 5)), null), null);
        Assert.True(session.Trim(new TrimOptions(TrimBasedOn.TopLeftPixelColor, Left: false)));
        Assert.Equal((30, 10), (session.Document!.Width, session.Document.Height));
        // A canvas of one color has nothing to keep.
        var plain = new EditorSession();
        plain.CreateDocument(10, 10, 72, PaletteColor.White);
        Assert.False(plain.Trim(new TrimOptions(TrimBasedOn.BottomRightPixelColor)));
    }
}
