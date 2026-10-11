using Compositor.Core.CameraRaw;
using Compositor.Core.Editing;
using Compositor.Core.Filters;
using Compositor.Core.Model;
using Compositor.Core.Rendering;
using SkiaSharp;
using static Compositor.Core.Tests.TestImages;

namespace Compositor.Core.Tests;

public class CameraRawTests
{
    private static SKBitmap Solid(int width, int height, byte r, byte g, byte b)
    {
        var bitmap = new SKBitmap(RasterImage.ColorInfo(width, height));
        bitmap.Erase(new SKColor(r, g, b));
        return bitmap;
    }

    private static (byte R, byte G, byte B, byte A) At(SKBitmap bitmap, int x, int y)
    {
        var c = bitmap.GetPixel(x, y);
        return (c.Red, c.Green, c.Blue, c.Alpha);
    }

    [Fact]
    public void TheMeasuredTablesLoad()
    {
        Assert.True(CameraRawTables.Available);
        var identity = CameraRawTables.Identity()!;
        // No change: the composing grid's own colors.
        int grid = CameraRawTables.Grid, mid = grid / 2;
        int at = ((mid * grid + mid) * grid + mid) * 3;
        Assert.Equal(0.5f, identity[at], 2);
    }

    [Fact]
    public void ExposureAndWhiteBalanceMoveTheColors()
    {
        using var gray = Solid(8, 8, 128, 128, 128);
        CameraRawProcessor.Apply(gray, new CameraRawSettings { Exposure = 1 });
        var brighter = At(gray, 4, 4);
        Assert.InRange(brighter.R, 160, 200);
        Assert.InRange(Math.Abs(brighter.R - brighter.B), 0, 3);

        using var warm = Solid(8, 8, 128, 128, 128);
        CameraRawProcessor.Apply(warm, new CameraRawSettings { Temperature = 60 });
        var w = At(warm, 4, 4);
        Assert.True(w.R > w.B + 10, w.ToString());

        // The white-balance solver warms a bluish gray back toward neutral.
        var solved = CameraRawTables.Neutralize(0.45, 0.5, 0.6)!.Value;
        Assert.True(solved.Temperature > 0, solved.ToString());
    }

    [Fact]
    public void ContrastSpreadsAGradient()
    {
        using var ramp = new SKBitmap(RasterImage.ColorInfo(256, 4));
        for (int x = 0; x < 256; x++) for (int y = 0; y < 4; y++) ramp.SetPixel(x, y, new SKColor((byte)x, (byte)x, (byte)x));
        CameraRawProcessor.Apply(ramp, new CameraRawSettings { Contrast = 80 });
        Assert.True(At(ramp, 50, 1).R < 50);
        Assert.True(At(ramp, 205, 1).R > 205);
    }

    [Fact]
    public void EffectsDetailOpticsAndGeometryRun()
    {
        using var image = Solid(64, 48, 150, 140, 130);
        CameraRawProcessor.Apply(image, new CameraRawSettings { VignetteAmount = -100 });
        Assert.True(At(image, 1, 1).R < At(image, 32, 24).R - 30);

        using var grain = Solid(64, 48, 128, 128, 128);
        CameraRawProcessor.Apply(grain, new CameraRawSettings { GrainAmount = 60 });
        var values = Enumerable.Range(0, 64).Select(x => At(grain, x, 10).R).Distinct().Count();
        Assert.True(values > 3);

        using var purple = Solid(16, 16, 160, 40, 200);
        CameraRawProcessor.Apply(purple, new CameraRawSettings { Optics = new CameraRawOpticsSettings { PurpleAmount = 100 } });
        var p = At(purple, 8, 8);
        Assert.True(p.B - p.G < 80, p.ToString());

        using var sharp = new SKBitmap(RasterImage.ColorInfo(32, 32));
        for (int x = 0; x < 32; x++) for (int y = 0; y < 32; y++) sharp.SetPixel(x, y, x < 16 ? new SKColor(80, 80, 80) : new SKColor(170, 170, 170));
        CameraRawProcessor.Apply(sharp, new CameraRawSettings { Detail = new CameraRawDetailSettings { SharpenAmount = 150 }, Clarity = 50, Texture = 50 });
        Assert.True(At(sharp, 15, 16).R < 80 || At(sharp, 16, 16).R > 170);

        using var turned = Solid(40, 40, 10, 200, 10);
        CameraRawProcessor.Apply(turned, new CameraRawSettings { Geometry = new CameraRawGeometrySettings { Rotate = 30 } });
        Assert.Equal(0, At(turned, 0, 0).A);
        Assert.Equal(255, At(turned, 20, 20).A);
    }

    [Fact]
    public void CurveMixerAndGradingFoldIntoTheTable()
    {
        using var red = Solid(8, 8, 200, 60, 50);
        var mixer = new CameraRawMixerSettings { Saturation = new double[] { -100, 0, 0, 0, 0, 0, 0, 0 } };
        CameraRawProcessor.Apply(red, new CameraRawSettings { Mixer = mixer });
        var r = At(red, 4, 4);
        Assert.True(r.R - r.G < 100, r.ToString());

        using var gray = Solid(8, 8, 128, 128, 128);
        var curve = new CameraRawCurveSettings { Rgb = new Adjustments.CurvePoint[] { new(0, 0), new(0.5, 0.7), new(1, 1) } };
        CameraRawProcessor.Apply(gray, new CameraRawSettings { Curve = curve });
        Assert.InRange(At(gray, 4, 4).R, 170, 185);

        using var tinted = Solid(8, 8, 128, 128, 128);
        var grading = new CameraRawGradingSettings { Global = new CameraRawGradeWheel(220, 60, 0) };
        CameraRawProcessor.Apply(tinted, new CameraRawSettings { Grading = grading });
        var t = At(tinted, 4, 4);
        Assert.True(t.B > t.R + 5, t.ToString());
    }

    [Fact]
    public void TheFilterAppliesThroughTheSession()
    {
        var session = new EditorSession { PreviewsInBackground = false };
        session.CreateDocument(40, 30, 72, new PaletteColor(0.5, 0.5, 0.5));
        Assert.True(session.BeginFilter(FilterKind.CameraRaw));
        var settings = session.AdjustmentEdit!.FilterSettings;
        session.UpdateFilterEdit(settings with { CameraRaw = settings.CameraRaw with { Exposure = 1 } });
        session.CommitAdjustmentEdit();
        Assert.Equal("撤销 Camera Raw 滤镜", session.UndoTitle);
        using var image = DocumentRenderer.RenderToImage(session.Document!);
        Assert.InRange(Pixel(image, 10, 10).R, 160, 200);
    }
}
