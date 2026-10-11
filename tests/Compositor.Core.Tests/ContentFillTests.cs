using System.Diagnostics;
using Compositor.Core.Editing;
using Compositor.Core.Inpainting;
using Compositor.Core.Model;
using Xunit.Abstractions;

namespace Compositor.Core.Tests;

/// <summary>Content-Aware Fill's engine: sampling areas, the smooth stretch, texture synthesis and the session's commit.</summary>
public class ContentFillTests
{
    private readonly ITestOutputHelper _output;
    public ContentFillTests(ITestOutputHelper output) => _output = output;

    private static byte[] Picture(int w, int h, Func<int, int, (byte R, byte G, byte B)> color)
    {
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (r, g, b) = color(x, y);
                int i = (y * w + x) * 4;
                rgba[i] = r; rgba[i + 1] = g; rgba[i + 2] = b; rgba[i + 3] = 255;
            }
        return rgba;
    }

    private static byte[] Box(int w, int h, int x0, int y0, int bw, int bh)
    {
        var mask = new byte[w * h];
        for (int y = y0; y < y0 + bh; y++)
            for (int x = x0; x < x0 + bw; x++) mask[y * w + x] = 255;
        return mask;
    }

    /// <summary>Mean absolute difference over the hole between the fill and what was there.</summary>
    private static double Error(byte[] filled, byte[] truth, byte[] hole)
    {
        double sum = 0;
        long n = 0;
        for (int i = 0; i < hole.Length; i++)
        {
            if (hole[i] == 0) continue;
            for (int k = 0; k < 3; k++) sum += Math.Abs(filled[i * 4 + k] - truth[i * 4 + k]);
            n += 3;
        }
        return sum / n;
    }

    [Fact]
    public void AGradientIsStretchedSmoothly()
    {
        int w = 200, h = 120;
        var truth = Picture(w, h, (x, y) => ((byte)(x * 255 / w), (byte)(60 + y), 140));
        var hole = Box(w, h, 70, 40, 60, 40);
        var sample = FillMasks.Auto(truth, hole, w, h);
        var outcome = ContentFill.Run(truth, w, h, hole, sample, new ContentFillSettings());
        Assert.Equal(FillMethod.Smooth, outcome.Method);
        var error = Error(outcome.Rgba, truth, hole);
        _output.WriteLine($"gradient error {error:0.00}");
        Assert.True(error < 6, error.ToString());
    }

    [Fact]
    public void StripesAreContinuedByTextureSynthesis()
    {
        int w = 240, h = 160;
        // Vertical stripes eight pixels across, with fine noise so the texture counts as texture.
        var rng = new Random(3);
        var truth = Picture(w, h, (x, y) =>
        {
            byte v = (byte)((x / 8) % 2 == 0 ? 40 : 210);
            int n = rng.Next(-6, 7);
            return ((byte)Math.Clamp(v + n, 0, 255), (byte)Math.Clamp(v + n, 0, 255), (byte)Math.Clamp(v / 2 + n, 0, 255));
        });
        var hole = Box(w, h, 90, 50, 50, 50);
        var sample = FillMasks.Auto(truth, hole, w, h);
        var watch = Stopwatch.StartNew();
        var outcome = ContentFill.Run(truth, w, h, hole, sample, new ContentFillSettings { Neural = false });
        _output.WriteLine($"stripes {watch.ElapsedMilliseconds} ms");
        Assert.Equal(FillMethod.Texture, outcome.Method);
        var error = Error(outcome.Rgba, truth, hole);
        _output.WriteLine($"stripe error {error:0.00}");
        // Blurring the stripes into gray would be off by about 85 on average.
        Assert.True(error < 30, error.ToString());
        // And the fill has stripes' contrast, not a blur.
        var values = Enumerable.Range(0, w * h).Where(i => hole[i] != 0).Select(i => (int)outcome.Rgba[i * 4]).ToList();
        Assert.True(values.Count(v => v < 90) > values.Count / 4 && values.Count(v => v > 160) > values.Count / 4);
    }

    /// <summary>Fine detail as the mean step between neighbors, inside the hole against outside it.</summary>
    private static double Sharpness(byte[] rgba, byte[] hole, int w, int h, bool inside)
    {
        double sum = 0;
        long n = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x + 1 < w; x++)
            {
                int i = y * w + x;
                if ((hole[i] != 0 && hole[i + 1] != 0) != inside) continue;
                if (!inside && (hole[i] != 0 || hole[i + 1] != 0)) continue;
                sum += Math.Abs(rgba[i * 4] - rgba[(i + 1) * 4]);
                n++;
            }
        return sum / n;
    }

    [Fact]
    public void FilledTextureKeepsItsGrain()
    {
        int w = 260, h = 200;
        var rng = new Random(21);
        // Grainy asphalt: a gray with strong pixel-to-pixel noise.
        var truth = Picture(w, h, (_, _) => { byte v = (byte)Math.Clamp(110 + rng.Next(-40, 41), 0, 255); return (v, v, v); });
        var hole = Box(w, h, 100, 70, 50, 50);
        var outside = Sharpness(truth, hole, w, h, inside: false);
        var texture = ContentFill.Run(truth, w, h, hole, FillMasks.Auto(truth, hole, w, h), new ContentFillSettings { Method = FillMethod.Texture, Neural = false });
        double ratio = Sharpness(texture.Rgba, hole, w, h, inside: true) / outside;
        _output.WriteLine($"texture grain kept {ratio:0.00}");
        Assert.True(ratio > 0.75, ratio.ToString());
        // A model that paints smooth gray must not leave the hole smooth.
        var smoothModel = new FlatModel { Gray = true };
        var neural = ContentFill.Run(truth, w, h, hole, FillMasks.Auto(truth, hole, w, h), new ContentFillSettings { Method = FillMethod.Texture, Neural = true }, smoothModel);
        ratio = Sharpness(neural.Rgba, hole, w, h, inside: true) / outside;
        _output.WriteLine($"neural grain kept {ratio:0.00}");
        Assert.True(ratio > 0.75, ratio.ToString());
    }

    [Fact]
    public void AnotherSeedGivesAnotherResult()
    {
        int w = 160, h = 120;
        var rng = new Random(5);
        var truth = Picture(w, h, (_, _) => { byte v = (byte)rng.Next(0, 256); return (v, (byte)(255 - v), 100); });
        var hole = Box(w, h, 60, 40, 30, 30);
        var sample = FillMasks.All(truth, hole);
        var first = ContentFill.Run(truth, w, h, hole, sample, new ContentFillSettings { Method = FillMethod.Texture, Neural = false, Seed = 1 });
        var second = ContentFill.Run(truth, w, h, hole, sample, new ContentFillSettings { Method = FillMethod.Texture, Neural = false, Seed = 2 });
        Assert.False(first.Rgba.AsSpan().SequenceEqual(second.Rgba));
        // Only the hole changes.
        for (int i = 0; i < hole.Length; i++)
            if (hole[i] == 0) Assert.Equal(truth[i * 4], first.Rgba[i * 4]);
    }

    private sealed class FlatModel : IInpaintModel
    {
        public int Calls;
        public bool Gray;
        public byte[] Inpaint(ReadOnlySpan<byte> rgba, byte[] hole, int width, int height)
        {
            Calls++;
            var rgb = new byte[width * height * 3];
            for (int i = 0; i < width * height; i++)
            {
                if (Gray) { rgb[i * 3] = rgb[i * 3 + 1] = rgb[i * 3 + 2] = 110; continue; }
                rgb[i * 3] = 200; rgb[i * 3 + 1] = 20; rgb[i * 3 + 2] = 220;
            }
            return rgb;
        }
    }

    [Fact]
    public void TheModelPaintsTexturedHoles()
    {
        int w = 200, h = 150;
        var rng = new Random(9);
        var truth = Picture(w, h, (_, _) => { byte v = (byte)rng.Next(0, 256); return (v, v, v); });
        var hole = Box(w, h, 80, 60, 30, 30);
        var model = new FlatModel();
        var outcome = ContentFill.Run(truth, w, h, hole, FillMasks.Auto(truth, hole, w, h), new ContentFillSettings(), model);
        Assert.True(outcome.Neural);
        Assert.Equal(1, model.Calls);
        // The model decides the colors and shapes (magenta here); synthesis only adds grain over them.
        int center = (75 * w + 95) * 4;
        Assert.True(outcome.Rgba[center] - outcome.Rgba[center + 1] > 60, $"{outcome.Rgba[center]},{outcome.Rgba[center + 1]}");
    }

    [Fact]
    public void AutoSamplingSurroundsTheHole()
    {
        int w = 400, h = 300;
        var picture = Picture(w, h, (_, _) => (10, 20, 30));
        var hole = Box(w, h, 180, 130, 40, 40);
        var area = FillMasks.Auto(picture, hole, w, h);
        Assert.Equal(255, area[150 * w + 175]); // just left of the hole
        Assert.Equal(0, area[150 * w + 200]);   // inside it
        Assert.Equal(0, area[5 * w + 5]);       // far away
    }

    [Fact]
    public void TheSessionFillsInPlaceOrOnANewLayer()
    {
        var session = new EditorSession { PreviewsInBackground = false };
        session.CreateDocument(120, 90, 72, new PaletteColor(0.2, 0.4, 0.6));
        session.SetSelection(DocumentSelection.Rectangle(new Geometry.RectD(40, 30, 20, 20)), "Rectangular Marquee");
        var job = session.BeginContentFill()!;
        Assert.Equal(400, job.Hole.Count(b => b != 0));
        var sample = FillMasks.Auto(job.Pixels, job.Hole, job.Width, job.Height);
        var outcome = ContentFill.Run(job.Pixels, job.Width, job.Height, job.Hole, sample, new ContentFillSettings());
        int count = session.Document!.Layers.Count;
        Assert.True(session.CommitContentFill(job, outcome.Rgba, job.Hole, newLayer: true));
        Assert.Equal(count + 1, session.Document!.Layers.Count);
        Assert.Equal("撤销 内容识别填充", session.UndoTitle);
        Assert.Equal(20, session.ActiveLayer!.Asset!.Width);
        session.Undo();

        var again = session.BeginContentFill()!;
        var fillArea = (byte[])again.Hole.Clone();
        fillArea[10 * again.Width + 10] = 255; // the fill area grown in the workspace
        var result = ContentFill.Run(again.Pixels, again.Width, again.Height, fillArea, FillMasks.All(again.Pixels, fillArea), new ContentFillSettings());
        Assert.True(session.CommitContentFill(again, result.Rgba, fillArea, newLayer: false));
        Assert.Equal(count, session.Document!.Layers.Count);
        Assert.True(session.Selection!.Contains(new Geometry.PointD(10.5, 10.5)));
    }

    [Fact]
    public void ABigFillStaysQuick()
    {
        int w = 1200, h = 900;
        var rng = new Random(11);
        var truth = Picture(w, h, (x, y) =>
        {
            int v = (int)(128 + 60 * Math.Sin(x * 0.15) * Math.Cos(y * 0.11)) + rng.Next(-8, 9);
            return ((byte)Math.Clamp(v, 0, 255), (byte)Math.Clamp(v - 20, 0, 255), (byte)Math.Clamp(v + 20, 0, 255));
        });
        var hole = Box(w, h, 500, 350, 220, 180);
        var watch = Stopwatch.StartNew();
        var outcome = ContentFill.Run(truth, w, h, hole, FillMasks.Auto(truth, hole, w, h), new ContentFillSettings { Method = FillMethod.Texture, Neural = false });
        _output.WriteLine($"big fill {watch.ElapsedMilliseconds} ms, error {Error(outcome.Rgba, truth, hole):0.0}");
        Assert.True(watch.ElapsedMilliseconds < 20_000);
    }
}
