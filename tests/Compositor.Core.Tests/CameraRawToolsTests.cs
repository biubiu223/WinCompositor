using Compositor.Core.CameraRaw;
using Compositor.Core.Filters;
using Compositor.Core.Geometry;
using Compositor.Core.Model;
using Compositor.Core.Rendering;
using SkiaSharp;
using static Compositor.Core.Tests.TestImages;

namespace Compositor.Core.Tests;

/// <summary>Camera Raw's panel tools: point colors, Guided Upright, the scope and the preview's view modes.</summary>
public class CameraRawToolsTests
{
    private static SKBitmap Solid(int width, int height, byte r, byte g, byte b)
    {
        var bitmap = new SKBitmap(RasterImage.ColorInfo(width, height));
        bitmap.Erase(new SKColor(r, g, b));
        return bitmap;
    }

    [Fact]
    public void APointColorShiftsOnlyColorsNearIt()
    {
        var (hue, saturation, lightness) = CameraRawPixels.HslOf(1, 0.3, 0);
        var point = new CameraRawPointColor(hue, saturation, lightness, HueShift: 100);
        var settings = new CameraRawSettings { Mixer = new CameraRawMixerSettings { Points = new[] { point } } };
        Assert.True(settings.Mixer.Adjusts);

        using var orange = Solid(4, 4, 255, 77, 0);
        CameraRawProcessor.Apply(orange, settings);
        var shifted = orange.GetPixel(1, 1);
        Assert.True(shifted.Green > 110, shifted.ToString()); // toward yellow

        using var blue = Solid(4, 4, 40, 80, 220);
        CameraRawProcessor.Apply(blue, settings);
        var kept = blue.GetPixel(1, 1);
        Assert.InRange(kept.Red, 38, 42);
        Assert.InRange(kept.Blue, 218, 222);

        // Visualizing its range dims what it doesn't reach.
        using var outside = Solid(4, 4, 40, 80, 220);
        CameraRawProcessor.Apply(outside, settings, visualizePoint: 0);
        Assert.True(outside.GetPixel(1, 1).Blue < 100);
    }

    [Fact]
    public void GuidesStraightenAndSquareUp()
    {
        // One plumb line whose top leans left: the picture turns clockwise (negative) to stand it up.
        var leaning = new CameraRawGeometrySettings { Guides = new[] { new CameraRawGuide(0.40, 0.1, 0.45, 0.9) } };
        var (_, _, rotate) = leaning.Effective(1000, 1000);
        Assert.InRange(rotate, -4, -3);

        // A level line lower on the right: turned the other way.
        var tilted = new CameraRawGeometrySettings { Guides = new[] { new CameraRawGuide(0.1, 0.50, 0.9, 0.55) } };
        Assert.True(tilted.Effective(1000, 1000).Rotate > 3);

        // Two verticals closer at the top: Vertical widens the top, with no turn.
        var converging = new CameraRawGeometrySettings
        {
            Guides = new[] { new CameraRawGuide(0.25, 0.1, 0.20, 0.9), new CameraRawGuide(0.75, 0.1, 0.80, 0.9) },
        };
        var (vertical, _, turn) = converging.Effective(1000, 1000);
        Assert.True(vertical > 5, vertical.ToString());
        Assert.InRange(turn, -0.01, 0.01);
        Assert.True(converging.Adjusts);
    }

    [Fact]
    public void TheScopeCountsChannelsAndHues()
    {
        using var red = Solid(10, 10, 255, 0, 0);
        var scope = CameraRawScope.Make(red, 1);
        Assert.Equal(100, scope.Red[255], 3);
        Assert.Equal(100, scope.Green[0], 3);
        // Pure red sits at the right edge of the vectorscope's middle row.
        int middle = CameraRawScope.ScopeSide / 2;
        double right = Enumerable.Range(middle - 1, 3).Sum(y => scope.Vectorscope[y * CameraRawScope.ScopeSide + CameraRawScope.ScopeSide - 2]);
        Assert.True(right > 50, right.ToString());
    }

    [Fact]
    public void TheViewShowsOnTheCanvasButIsNeverApplied()
    {
        var session = new Editing.EditorSession { PreviewsInBackground = false };
        session.CreateDocument(40, 30, 72, new PaletteColor(1, 1, 1));
        Assert.True(session.BeginFilter(FilterKind.CameraRaw));
        var edit = session.AdjustmentEdit!;
        Assert.NotNull(edit.RawScope); // the layer as it is, before anything changes

        // The highlight warning paints the white canvas red, though the settings change nothing.
        session.SetCameraRawView(new CameraRawView { HighlightWarning = true });
        Assert.NotNull(edit.PreviewImage);
        var shown = session.CameraRawColorAt(new PointD(10, 10))!.Value;
        Assert.True(shown.Red > 0.9 && shown.Green < 0.5, shown.ToString());

        // Points map to the layer and back.
        var fraction = session.CameraRawLayerPoint(new PointD(20, 15))!.Value;
        Assert.Equal(0.5, fraction.X, 3);
        Assert.Equal(20, session.CameraRawDocumentPoint(fraction)!.Value.X, 3);

        var settings = edit.FilterSettings;
        session.UpdateFilterEdit(settings with { CameraRaw = settings.CameraRaw with { Exposure = -1 } });
        session.CommitAdjustmentEdit();
        Assert.Equal("撤销 Camera Raw 滤镜", session.UndoTitle);
        using var image = DocumentRenderer.RenderToImage(session.Document!);
        var pixel = Pixel(image, 10, 10);
        Assert.True(Math.Abs(pixel.R - pixel.G) < 6 && Math.Abs(pixel.R - pixel.B) < 6, pixel.ToString()); // neutral, not painted red
    }
}
