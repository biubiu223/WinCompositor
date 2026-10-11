using System.Diagnostics;
using Compositor.Core.Editing;
using Compositor.Core.Geometry;
using Compositor.Core.Model;
using Compositor.Core.Rendering;
using SkiaSharp;
using Xunit.Abstractions;

namespace Compositor.Core.Tests;

/// <summary>Timings on a large document, printed for reference (they only fail when wildly slow).</summary>
public class PerformanceTests
{
    private readonly ITestOutputHelper _output;
    public PerformanceTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void LargeCanvasBrushAndRender()
    {
        SKGraphics.SetResourceCacheTotalByteLimit(768L * 1024 * 1024);
        var session = new EditorSession();
        var watch = Stopwatch.StartNew();
        session.CreateDocument(6000, 4000, 72, PaletteColor.White);
        _output.WriteLine($"create 6000x4000: {watch.ElapsedMilliseconds} ms");

        session.SelectTool(Tool.Brush);
        session.BrushSettings = session.BrushSettings with { Diameter = 300, Hardness = 0.5 };
        watch.Restart();
        session.BeginBrush(new PointD(500, 500));
        var first = watch.ElapsedMilliseconds;
        var moves = new List<long>();
        for (int i = 1; i <= 50; i++)
        {
            var step = Stopwatch.StartNew();
            session.ContinueBrush(new PointD(500 + i * 80, 500 + i * 40));
            moves.Add(step.ElapsedMilliseconds);
        }
        watch.Restart();
        session.FinishBrush();
        var commit = watch.ElapsedMilliseconds;
        _output.WriteLine($"brush begin: {first} ms, per move avg {moves.Average():0.0} ms (max {moves.Max()}), commit {commit} ms");

        // The canvas: a 2560x1440 view of the whole document (zoomed out), as the window draws it.
        var document = session.Document!;
        double scale = Math.Min(2560.0 / document.Width, 1440.0 / document.Height);
        using var surface = SKSurface.Create(new SKImageInfo(2560, 1440, SKColorType.Rgba8888, SKAlphaType.Premul));
        var times = new List<long>();
        for (int i = 0; i < 5; i++)
        {
            watch.Restart();
            surface.Canvas.Clear(SKColors.Transparent);
            surface.Canvas.ResetMatrix();
            surface.Canvas.Scale((float)scale);
            surface.Canvas.SaveLayer();
            DocumentRenderer.Render(surface.Canvas, document, new RenderOptions());
            surface.Canvas.Restore();
            surface.Canvas.Flush();
            times.Add(watch.ElapsedMilliseconds);
        }
        _output.WriteLine($"render fit view: first {times[0]} ms, then {string.Join(", ", times.Skip(1))} ms");

        // Mid-stroke previews on the big layer.
        session.BeginBrush(new PointD(3000, 2000));
        var preview = new List<long>();
        for (int i = 0; i < 5; i++)
        {
            session.ContinueBrush(new PointD(3000 + i * 50, 2000));
            watch.Restart();
            var options = new RenderOptions();
            session.BrushStroke!.ApplyPreview(options);
            surface.Canvas.Clear(SKColors.Transparent);
            surface.Canvas.SaveLayer();
            DocumentRenderer.Render(surface.Canvas, document, options);
            surface.Canvas.Restore();
            surface.Canvas.Flush();
            preview.Add(watch.ElapsedMilliseconds);
        }
        session.CancelBrush();
        _output.WriteLine($"render with stroke preview: {string.Join(", ", preview)} ms");
        Assert.True(moves.Average() < 1000);
    }

    [Fact]
    public void AdjustmentLayersOnALargeCanvas()
    {
        var session = new EditorSession { PreviewsInBackground = false };
        session.CreateDocument(6000, 4000, 72, new PaletteColor(0.4, 0.6, 0.8));
        foreach (var kind in new[] { Adjustments.AdjustmentKind.Curves, Adjustments.AdjustmentKind.HueSaturation, Adjustments.AdjustmentKind.GaussianBlur })
        {
            session.AddAdjustmentLayer(kind);
            var settings = session.AdjustmentEdit!.Settings.Clone();
            if (kind == Adjustments.AdjustmentKind.Curves) settings.Curves.Channels[0] = new List<Adjustments.CurvePoint> { new(0, 0), new(128, 170), new(255, 255) };
            if (kind == Adjustments.AdjustmentKind.HueSaturation) settings.HsvSettings!.Adjustments[Adjustments.ColorRange.Master] = new(30, 20, 0);
            session.UpdateAdjustmentEdit(settings);
            session.CommitAdjustmentEdit();
        }
        var document = session.Document!;
        var watch = new Stopwatch();
        using var surface = SKSurface.Create(new SKImageInfo(2560, 1440, SKColorType.Rgba8888, SKAlphaType.Premul));
        foreach (var zoom in new[] { Math.Min(2560.0 / document.Width, 1440.0 / document.Height), 1.0 })
        {
            var times = new List<long>();
            for (int i = 0; i < 4; i++)
            {
                watch.Restart();
                surface.Canvas.Clear(SKColors.Transparent);
                surface.Canvas.ResetMatrix();
                surface.Canvas.Scale((float)zoom);
                surface.Canvas.SaveLayer();
                DocumentRenderer.Render(surface.Canvas, document, new RenderOptions());
                surface.Canvas.Restore();
                surface.Canvas.Flush();
                times.Add(watch.ElapsedMilliseconds);
            }
            _output.WriteLine($"3 adjustment layers, zoom {zoom:0.##}: {string.Join(", ", times)} ms");
        }
        // Layer effects on a big transparent-edged layer.
        var shape = Layer.FromImage(RasterImage.FromBitmap(MakeCircle(5000, 3200)), new PointD(500, 400));
        shape = shape with
        {
            Effects = new LayerEffects(new[]
            {
                LayerEffect.Default(LayerEffectKind.Shadow, PaletteColor.Black) with { Distance = 40, Blur = 60 },
                LayerEffect.Default(LayerEffectKind.Stroke, PaletteColor.White) with { Size = 12 },
                LayerEffect.Default(LayerEffectKind.OuterGlow, PaletteColor.White) with { Size = 40 },
                LayerEffect.Default(LayerEffectKind.InnerShadow, PaletteColor.Black),
            }),
        };
        var styled = Document(document.Width, document.Height, shape);
        foreach (var zoom in new[] { Math.Min(2560.0 / document.Width, 1440.0 / document.Height), 1.0 })
        {
            var times = new List<long>();
            for (int i = 0; i < 4; i++)
            {
                watch.Restart();
                surface.Canvas.Clear(SKColors.Transparent);
                surface.Canvas.ResetMatrix();
                surface.Canvas.Translate(zoom >= 1 ? -1000 : 0, zoom >= 1 ? -800 : 0);
                surface.Canvas.Scale((float)zoom);
                surface.Canvas.SaveLayer();
                DocumentRenderer.Render(surface.Canvas, styled, new RenderOptions());
                surface.Canvas.Restore();
                surface.Canvas.Flush();
                times.Add(watch.ElapsedMilliseconds);
            }
            _output.WriteLine($"4 effects on a 5000x3200 layer, zoom {zoom:0.##}: {string.Join(", ", times)} ms");
        }

        // A destructive blur on the whole big layer.
        session.SelectLayer(document.Layers[0].Id);
        watch.Restart();
        Assert.True(session.BeginImageAdjustment(Adjustments.AdjustmentKind.GaussianBlur));
        _output.WriteLine($"begin blur (preview at 2048): {watch.ElapsedMilliseconds} ms");
        watch.Restart();
        session.CommitAdjustmentEdit();
        _output.WriteLine($"commit blur 6000x4000: {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void EachEffectOnALargeLayer()
    {
        var circle = RasterImage.FromBitmap(MakeCircle(5000, 3200));
        using var surface = SKSurface.Create(new SKImageInfo(2560, 1440, SKColorType.Rgba8888, SKAlphaType.Premul));
        var watch = new Stopwatch();
        foreach (var effect in new[]
                 {
                     LayerEffect.Default(LayerEffectKind.Shadow, PaletteColor.Black) with { Distance = 40, Blur = 60 },
                     LayerEffect.Default(LayerEffectKind.Stroke, PaletteColor.White) with { Size = 12 },
                     LayerEffect.Default(LayerEffectKind.Stroke, PaletteColor.White) with { Size = 12, Inside = true },
                     LayerEffect.Default(LayerEffectKind.OuterGlow, PaletteColor.White) with { Size = 40 },
                     LayerEffect.Default(LayerEffectKind.InnerShadow, PaletteColor.Black),
                     LayerEffect.Default(LayerEffectKind.ColorOverlay, PaletteColor.Black),
                 })
        {
            var layer = Layer.FromImage(circle, new PointD(500, 400)) with { Effects = new LayerEffects(new[] { effect }) };
            var document = Document(6000, 4000, layer);
            var times = new List<long>();
            for (int i = 0; i < 3; i++)
            {
                watch.Restart();
                surface.Canvas.Clear(SKColors.Transparent);
                surface.Canvas.ResetMatrix();
                surface.Canvas.Translate(-1000, -800);
                surface.Canvas.SaveLayer();
                DocumentRenderer.Render(surface.Canvas, document, new RenderOptions());
                surface.Canvas.Restore();
                surface.Canvas.Flush();
                times.Add(watch.ElapsedMilliseconds);
            }
            _output.WriteLine($"{effect.Kind}{(effect.Inside ? " inside" : "")} at 100%: {string.Join(", ", times)} ms");
        }
    }

    private static SKBitmap MakeCircle(int width, int height)
    {
        var bitmap = new SKBitmap(RasterImage.ColorInfo(width, height));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { Color = new SKColor(220, 80, 40), IsAntialias = true };
        canvas.DrawOval(new SKRect(0, 0, width, height), paint);
        return bitmap;
    }

    private static CanvasDocument Document(int width, int height, params Layer[] layers) => new()
    {
        Width = width,
        Height = height,
        Layers = System.Collections.Immutable.ImmutableList.CreateRange(layers),
    };
}
