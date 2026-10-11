using Compositor.Core.Editing;
using Compositor.Core.Geometry;
using Compositor.Core.Model;
using Compositor.Core.Rendering;
using static Compositor.Core.Tests.TestImages;

namespace Compositor.Core.Tests;

public class DistortTests
{
    private static EditorSession RedSquare()
    {
        var session = new EditorSession();
        session.InstallProject(new IO.ProjectSnapshot(Document(100, 100, Layer(Solid(40, 40, 255, 0, 0), 30, 30)), null), null);
        session.SelectLayer(session.Document!.Layers[0].Id);
        return session;
    }

    private static (byte R, byte G, byte B, byte A) At(EditorSession session, int x, int y)
    {
        using var image = DocumentRenderer.RenderToImage(session.Document!);
        return Pixel(image, x, y);
    }

    [Fact]
    public void HomographyMapsTheUnitSquare()
    {
        var corners = new[] { new PointD(10, 10), new PointD(90, 0), new PointD(100, 100), new PointD(0, 80) };
        Assert.True(DistortWarp.IsConvex(corners));
        for (int i = 0; i < 4; i++)
        {
            var unit = new[] { new PointD(0, 0), new PointD(1, 0), new PointD(1, 1), new PointD(0, 1) }[i];
            var p = DistortWarp.Map(corners, unit);
            Assert.Equal(corners[i].X, p.X, 3);
            Assert.Equal(corners[i].Y, p.Y, 3);
        }
        // A corner dragged past its neighbour folds the shape.
        Assert.False(DistortWarp.IsConvex(new[] { new PointD(0, 0), new PointD(100, 0), new PointD(-20, 100), new PointD(0, 100) }));
        Assert.False(DistortWarp.IsUsable(new[] { new PointD(0, 0), new PointD(1, 0), new PointD(2, 0), new PointD(3, 0) }));
    }

    [Fact]
    public void DistortingALayerResamplesItIntoTheShape()
    {
        var session = RedSquare();
        session.BeginTransform(persistent: false);
        Assert.True(session.BeginDistort());
        Assert.True(session.IsDistorting);
        Assert.True(session.TransformEdit!.Persistent);
        var corners = session.TransformEdit.Corners!;
        corners[1] = new PointD(95, 5); // pull the top-right corner up and out
        session.PreviewCorners(corners);
        var options = new RenderOptions();
        session.ApplyTransformPreview(options);
        Assert.NotNull(options.For(session.ActiveLayerId!.Value)?.Image);
        session.CommitTransform();
        Assert.Null(session.TransformEdit);
        Assert.Equal("撤销 扭曲", session.UndoTitle);
        var layer = session.ActiveLayer!;
        Assert.Equal(new PointD(30, 5), layer.Transform.Origin);
        Assert.Equal(65, layer.Transform.Size.Width);
        Assert.Equal((255, 0, 0, 255), At(session, 50, 50));
        Assert.Equal(255, At(session, 90, 12).R); // inside the pulled corner
        Assert.Equal(0, At(session, 35, 10).A); // outside the shape, inside its bounds
        session.Undo();
        Assert.Equal(new PointD(30, 30), session.ActiveLayer!.Transform.Origin);
    }

    [Fact]
    public void AFoldedShapeStillApplies()
    {
        var session = RedSquare();
        session.DistortCommand();
        var corners = session.TransformEdit!.Corners!;
        corners[2] = new PointD(20, 80); // bottom-right dragged past bottom-left
        session.PreviewCorners(corners);
        session.CommitTransform();
        Assert.Equal("撤销 扭曲", session.UndoTitle);
        Assert.Equal(255, At(session, 35, 35).R);
    }

    [Fact]
    public void CancelLeavesTheLayerAlone()
    {
        var session = RedSquare();
        var before = session.Document;
        session.DistortCommand();
        var corners = session.TransformEdit!.Corners!;
        corners[0] = new PointD(0, 0);
        session.PreviewCorners(corners);
        session.CancelTransform();
        Assert.Same(before, session.Document);
    }

    [Fact]
    public void ArrowKeysMoveTheShape()
    {
        var session = RedSquare();
        session.DistortCommand();
        session.NudgeLayer(10, 0);
        Assert.Equal(new PointD(40, 30), session.TransformEdit!.Corners![0]);
        session.CommitTransform();
        Assert.Equal(new PointD(40, 30), session.ActiveLayer!.Transform.Origin);
    }

    [Fact]
    public void DistortedSelectedPixelsMergeBackWithTheSelection()
    {
        var session = RedSquare();
        session.SetSelection(DocumentSelection.Rectangle(new RectD(30, 30, 20, 20)), "Rectangular Marquee");
        session.DistortCommand();
        Assert.NotNull(session.TransformEdit!.Floating);
        var corners = session.TransformEdit.Corners!;
        corners[2] = new PointD(60, 60);
        session.PreviewCorners(corners);
        session.CommitTransform();
        Assert.Equal("撤销 扭曲", session.UndoTitle);
        Assert.Equal(255, At(session, 52, 52).R);
        Assert.True(session.Selection!.Contains(new PointD(52, 52)));
    }

    [Fact]
    public void AMaskCoveringTheLayerDistortsWithIt()
    {
        var session = RedSquare();
        session.AddMask();
        session.SelectLayerTarget(session.ActiveLayerId!.Value, mask: false);
        // Hide the left half through the mask.
        var bytes = new byte[40 * 40];
        for (int y = 0; y < 40; y++) for (int x = 20; x < 40; x++) bytes[y * 40 + x] = 255;
        var id = session.ActiveLayerId!.Value;
        session.InstallProject(new IO.ProjectSnapshot(session.Document!.UpdateLayer(id, l => l with { Mask = new LayerMask(RasterImage.FromMask(bytes, 40, 40)) }), null), null);
        session.SelectLayer(id);
        session.DistortCommand();
        var corners = session.TransformEdit!.Corners!;
        corners[1] = new PointD(90, 30);
        corners[2] = new PointD(90, 70);
        session.PreviewCorners(corners);
        session.CommitTransform();
        var layer = session.ActiveLayer!;
        Assert.Equal(60, layer.Transform.Size.Width);
        Assert.Equal(0, At(session, 40, 50).A);   // left half still hidden
        Assert.Equal(255, At(session, 80, 50).R); // right half shown, stretched
    }

    [Fact]
    public void LayersDistortTogether()
    {
        var session = new EditorSession();
        session.InstallProject(new IO.ProjectSnapshot(Document(100, 100,
            Layer(Solid(20, 20, 255, 0, 0), 10, 10), Layer(Solid(20, 20, 0, 0, 255), 60, 60)), null), null);
        var ids = session.Document!.Layers.Select(l => l.Id).ToList();
        session.SelectLayers(ids, ids[0]);
        session.DistortCommand();
        Assert.NotNull(session.TransformEdit!.Group);
        var corners = session.TransformEdit.Corners!;
        corners[2] = new PointD(95, 95);
        session.PreviewCorners(corners);
        session.CommitTransform();
        Assert.Equal("撤销 扭曲图层", session.UndoTitle);
        // The red layer (top-left) is barely moved; the blue one (bottom-right) is stretched toward the corner.
        Assert.Equal(255, At(session, 15, 15).R);
        Assert.Equal(255, At(session, 85, 85).B);
    }
}
