using Compositor.Core.Editing;
using Compositor.Core.Filters;
using Compositor.Core.Geometry;
using Compositor.Core.Model;
using Compositor.Core.Rendering;
using Compositor.Core.Segmentation;
using static Compositor.Core.Tests.TestImages;

namespace Compositor.Core.Tests;

/// <summary>Select Subject, Object Selection and Remove Background, with a stand-in model.</summary>
public class SubjectTests
{
    /// <summary>"Sees" a subject wherever the input is bright (so tests choose it by what they paint).</summary>
    private sealed class BrightModel : ISubjectModel
    {
        public SubjectModelSpec Spec { get; } = new(64, new[] { 0f, 0f, 0f }, new[] { 1f, 1f, 1f }, false);
        public int Calls;
        public float[] Infer(float[] input)
        {
            Calls++;
            int plane = 64 * 64;
            var output = new float[plane];
            for (int i = 0; i < plane; i++) output[i] = (input[i] + input[plane + i] + input[2 * plane + i]) / 3 > 0.5f ? 0.9f : 0.1f;
            return output;
        }
    }

    private static EditorSession Session(BrightModel model, int width = 120, int height = 90)
    {
        var session = new EditorSession { PreviewsInBackground = false, SubjectModelSource = () => model };
        session.CreateDocument(width, height, 72, new PaletteColor(0, 0, 0));
        return session;
    }

    /// <summary>Paints a white rectangle on the active layer.</summary>
    private static void Paint(EditorSession session, RectI rect)
    {
        var layer = session.ActiveLayer!;
        var pixels = layer.Asset!.CopyPixels();
        for (int y = rect.Y; y < rect.Y + rect.Height; y++)
            for (int x = rect.X; x < rect.X + rect.Width; x++)
                pixels.AsSpan((y * layer.Asset.Width + x) * 4, 4).Fill(255);
        var image = RasterImage.FromRgba(pixels, layer.Asset.Width, layer.Asset.Height, layer.Name);
        session.Document = session.Document!.UpdateLayer(layer.Id, l => l with { Asset = image });
    }

    [Fact]
    public async Task SelectSubjectOutlinesTheForeground()
    {
        var model = new BrightModel();
        var session = Session(model);
        Paint(session, new RectI(30, 20, 60, 40));
        await session.SelectSubjectAsync();
        var bounds = session.Selection!.Bounds;
        Assert.InRange(bounds.X, 27, 33);
        Assert.InRange(bounds.Width, 54, 66);
        Assert.Equal("撤销 选择主体", session.UndoTitle);
    }

    [Fact]
    public async Task ObjectSelectionTakesOnlyTheClickedObject()
    {
        var model = new BrightModel();
        var session = Session(model, 200, 100);
        Paint(session, new RectI(10, 20, 50, 50));
        Paint(session, new RectI(120, 20, 60, 50));
        await session.SelectObjectAsync(new PointD(150, 45), SelectionMode.Replace);
        var bounds = session.Selection!.Bounds;
        Assert.InRange(bounds.X, 115, 125);
        Assert.InRange(bounds.Right, 175, 185);
        // Clicking the background selects nothing.
        await session.SelectObjectAsync(new PointD(90, 90), SelectionMode.Replace);
        Assert.Null(session.Selection);
    }

    [Fact]
    public void RemoveBackgroundMasksTheLayer()
    {
        var model = new BrightModel();
        var session = Session(model);
        Paint(session, new RectI(30, 20, 60, 40));
        Assert.True(session.BeginFilter(FilterKind.RemoveBackground));
        var edit = session.AdjustmentEdit!;
        Assert.NotNull(edit.PreviewImage); // the background already clear on the canvas
        using (var shown = DocumentRenderer.RenderToImage(session.Document!))
        {
            var options = new RenderOptions();
            session.ApplyAdjustmentPreview(options);
            using var preview = DocumentRenderer.RenderToImage(session.Document!, options);
            Assert.Equal(0, Pixel(preview, 5, 5).A);
            Assert.Equal(255, Pixel(preview, 60, 40).A);
        }
        session.CommitAdjustmentEdit();
        Assert.Equal(1, model.Calls); // worked out once, for the preview and OK alike
        Assert.Equal("撤销 移除背景", session.UndoTitle);
        var layer = session.ActiveLayer!;
        var mask = layer.Mask!.Asset.CopyPixels();
        Assert.Equal(0, mask[5 * 120 + 5]);
        Assert.Equal(255, mask[40 * 120 + 60]);
        Assert.True(session.IsMaskSelected);
        // The pixels themselves are untouched.
        Assert.Equal(255, layer.Asset!.CopyPixels()[(5 * 120 + 5) * 4 + 3]);
    }

    [Fact]
    public void RefiningKeepsTheMaskInRange()
    {
        int w = 80, h = 60;
        var mask = new byte[w * h];
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (x >= 40) mask[y * w + x] = 255;
                if (x >= 38) rgba.AsSpan((y * w + x) * 4, 4).Fill(255); else rgba[(y * w + x) * 4 + 3] = 255;
            }
        var settings = new RemoveBackgroundSettings { Quality = BackgroundQuality.Advanced, RefineEdges = 4, MatteContrast = 50, ShiftEdge = -2 };
        var refined = SubjectMask.Refine(mask, rgba, w, h, settings, int.MaxValue);
        Assert.Equal(0, refined[30 * w + 5]);
        Assert.Equal(255, refined[30 * w + 75]);
        // Shrinking the edge leaves the old edge column out.
        Assert.True(refined[30 * w + 40] < 128, refined[30 * w + 40].ToString());
        var grown = SubjectMask.Offset(SubjectMask.Threshold(mask), w, h, -3);
        Assert.Equal(255, grown[30 * w + 37]);
    }
}
