using Compositor.Core.Localization;
using SkiaSharp;

namespace Compositor.Core.Segmentation;

/// <summary>How a salient-object model wants its picture: a square of <see cref="Side"/>, each channel (0–1) less its mean, over its spread.</summary>
public sealed record SubjectModelSpec(int Side, float[] Mean, float[] Std, bool ScaleByBrightest)
{
    /// <summary>IS-Net (DIS): 1024², mean 0.5, spread 1, the picture divided by its brightest value (as rembg feeds it).</summary>
    public static readonly SubjectModelSpec IsNet = new(1024, new[] { 0.5f, 0.5f, 0.5f }, new[] { 1f, 1f, 1f }, true);
    /// <summary>U²-Net and its smaller cuts: 320², ImageNet's means and spreads.</summary>
    public static readonly SubjectModelSpec U2Net = new(320, new[] { 0.485f, 0.456f, 0.406f }, new[] { 0.229f, 0.224f, 0.225f }, true);
}

/// <summary>A salient-object model: a 1×3×Side×Side picture in, Side×Side foreground likelihoods out.</summary>
public interface ISubjectModel
{
    SubjectModelSpec Spec { get; }
    float[] Infer(float[] input);
}

/// <summary>Remove Background's two ways of working: the model's mask as it comes, or refined against the picture.</summary>
public enum BackgroundQuality { Basic, Advanced }

/// <summary>Remove Background's panel. Mirrors the Remove Background part of FilterSettings in Filters.swift.</summary>
public sealed record RemoveBackgroundSettings
{
    public BackgroundQuality Quality { get; init; } = BackgroundQuality.Basic;
    /// <summary>How far the mask is pulled onto the picture's own edges, in layer pixels (0 off).</summary>
    public double RefineEdges { get; init; } = 12;
    /// <summary>Pushes the mask's grays toward black and white, 0–100, clearing haze in thin areas.</summary>
    public double MatteContrast { get; init; } = 25;
    /// <summary>Contracts (negative) or expands (positive) the edge, in layer pixels.</summary>
    public double ShiftEdge { get; init; }

    public RemoveBackgroundSettings Normalized => this with
    {
        RefineEdges = Filters.DitherSettings.Clamp(RefineEdges, 0, 40, 12),
        MatteContrast = Filters.DitherSettings.Clamp(MatteContrast, 0, 100, 25),
        ShiftEdge = Filters.DitherSettings.Clamp(ShiftEdge, -10, 10, 0),
    };
}

/// <summary>
/// The model-agnostic part of Select Subject, Object Selection and Remove Background: the picture made into the model's
/// input, its answer made into a mask the picture's size, that mask refined (a guided filter with the picture as its
/// guide, an edge shift and a contrast), and the one object under a point picked out of it. Mirrors SubjectRemoval.swift,
/// GuidedMatte.swift and ObjectSelection.swift, with an open model in place of Vision.
/// </summary>
public static class SubjectMask
{
    /// <summary>A premultiplied RGBA picture as the model's input (1×3×Side×Side, channel planes).</summary>
    public static float[] Input(ReadOnlySpan<byte> rgba, int width, int height, SubjectModelSpec spec)
    {
        int side = spec.Side;
        using var source = Bitmap(rgba, width, height);
        using var scaled = new SKBitmap(new SKImageInfo(side, side, SKColorType.Rgba8888, SKAlphaType.Premul));
        source.ScalePixels(scaled, new SKSamplingOptions(SKCubicResampler.Mitchell));
        var pixels = scaled.GetPixelSpan();
        int plane = side * side;
        var input = new float[3 * plane];
        float brightest = 1;
        if (spec.ScaleByBrightest)
        {
            int top = 0;
            for (int i = 0; i < plane; i++) top = Math.Max(top, Math.Max(pixels[i * 4], Math.Max(pixels[i * 4 + 1], pixels[i * 4 + 2])));
            brightest = Math.Max(top, 1) / 255f;
        }
        for (int i = 0; i < plane; i++)
            for (int c = 0; c < 3; c++)
                input[c * plane + i] = (pixels[i * 4 + c] / 255f / brightest - spec.Mean[c]) / spec.Std[c];
        return input;
    }

    /// <summary>The model's answer for a picture, stretched to 0–1 (as rembg does), Side × Side.</summary>
    public static float[] Infer(ISubjectModel model, ReadOnlySpan<byte> rgba, int width, int height)
    {
        int side = model.Spec.Side;
        var output = model.Infer(Input(rgba, width, height, model.Spec));
        if (output.Length < side * side) throw new InvalidOperationException(L.T("模型的输出大小不对。", "The model’s output is the wrong size."));
        float lo = float.MaxValue, hi = float.MinValue;
        for (int i = 0; i < side * side; i++) { lo = Math.Min(lo, output[i]); hi = Math.Max(hi, output[i]); }
        var result = new float[side * side];
        float span = hi - lo;
        for (int i = 0; i < result.Length; i++) result[i] = span > 1e-6f ? (output[i] - lo) / span : 0;
        return result;
    }

    /// <summary>A Side × Side answer as a mask <paramref name="width"/> × <paramref name="height"/> (255 the subject).</summary>
    public static byte[] Resize(float[] answer, int side, int width, int height)
    {
        using var small = new SKBitmap(new SKImageInfo(side, side, SKColorType.Gray8, SKAlphaType.Opaque));
        var pixels = small.GetPixelSpan();
        for (int i = 0; i < side * side; i++) pixels[i] = (byte)Math.Clamp(answer[i] * 255 + 0.5f, 0, 255);
        return Scaled(small, width, height);
    }

    /// <summary>The picture's subject at its own size: the model's answer, then (Advanced) refined.</summary>
    public static byte[] Mask(float[] answer, int side, ReadOnlySpan<byte> rgba, int width, int height, RemoveBackgroundSettings? settings = null, int limit = int.MaxValue)
    {
        var mask = Resize(answer, side, width, height);
        return settings is { Quality: BackgroundQuality.Advanced } s ? Refine(mask, rgba, width, height, s.Normalized, limit) : mask;
    }

    // MARK: Refining

    /// <summary>
    /// Refine pulls the mask onto the picture's edges (recovering hair the model cuts through), Shift Edge grows or
    /// shrinks it, Contrast pushes its grays apart. Done on a copy no larger than <paramref name="limit"/>, then drawn back up.
    /// </summary>
    public static byte[] Refine(byte[] mask, ReadOnlySpan<byte> rgba, int width, int height, RemoveBackgroundSettings settings, int limit)
    {
        double factor = Math.Min(1, (double)limit / Math.Max(width, height));
        int w = Math.Max(1, (int)Math.Round(width * factor)), h = Math.Max(1, (int)Math.Round(height * factor));
        var levels = Levels(mask, width, height, w, h);
        if (settings.RefineEdges > 0)
        {
            var guide = Gray(rgba, width, height, w, h);
            levels = GuidedFilter(levels, guide, w, h, Math.Max(1, (int)Math.Round(settings.RefineEdges * factor)), 1e-4f);
        }
        if (settings.ShiftEdge != 0)
        {
            // A blur then a hard cut at the matching level moves the edge by the blur's reach.
            double reach = Math.Abs(settings.ShiftEdge) * factor;
            levels = Blur(levels, w, h, (float)(reach / 2));
            float level = settings.ShiftEdge < 0 ? 0.75f : 0.25f;
            for (int i = 0; i < levels.Length; i++) levels[i] = Math.Clamp((levels[i] - level) * 1000f, 0, 1);
        }
        if (settings.MatteContrast > 0)
        {
            // 0 leaves the mask as it is; 100 is a hard cut at the middle.
            float slope = 1 / Math.Max(0.02f, 1 - (float)settings.MatteContrast / 100 * 0.98f);
            for (int i = 0; i < levels.Length; i++) levels[i] = Math.Clamp(levels[i] * slope + (1 - slope) / 2, 0, 1);
        }
        using var small = new SKBitmap(new SKImageInfo(w, h, SKColorType.Gray8, SKAlphaType.Opaque));
        var pixels = small.GetPixelSpan();
        for (int i = 0; i < levels.Length; i++) pixels[i] = (byte)Math.Clamp(levels[i] * 255 + 0.5f, 0, 255);
        return w == width && h == height ? pixels.ToArray() : Scaled(small, width, height);
    }

    /// <summary>Mean over a (2r+1)² square, as two running-sum passes.</summary>
    internal static float[] Box(float[] source, int width, int height, int radius)
    {
        float span = radius * 2 + 1;
        var pass = new float[width * height];
        Parallel.For(0, height, y =>
        {
            int row = y * width;
            float sum = 0;
            for (int x = -radius; x <= radius; x++) sum += source[row + Math.Clamp(x, 0, width - 1)];
            for (int x = 0; x < width; x++)
            {
                pass[row + x] = sum / span;
                sum -= source[row + Math.Clamp(x - radius, 0, width - 1)];
                sum += source[row + Math.Clamp(x + radius + 1, 0, width - 1)];
            }
        });
        var result = new float[width * height];
        Parallel.For(0, width, x =>
        {
            float sum = 0;
            for (int y = -radius; y <= radius; y++) sum += pass[Math.Clamp(y, 0, height - 1) * width + x];
            for (int y = 0; y < height; y++)
            {
                result[y * width + x] = sum / span;
                sum -= pass[Math.Clamp(y - radius, 0, height - 1) * width + x];
                sum += pass[Math.Clamp(y + radius + 1, 0, height - 1) * width + x];
            }
        });
        return result;
    }

    /// <summary>He, Sun &amp; Tang's guided filter: <paramref name="mask"/> following <paramref name="guide"/>'s edges (both 0–1).</summary>
    internal static float[] GuidedFilter(float[] mask, float[] guide, int width, int height, int radius, float epsilon)
    {
        int count = width * height;
        var meanGuide = Box(guide, width, height, radius);
        var meanMask = Box(mask, width, height, radius);
        var squares = new float[count];
        var products = new float[count];
        for (int i = 0; i < count; i++) { squares[i] = guide[i] * guide[i]; products[i] = guide[i] * mask[i]; }
        var meanSquares = Box(squares, width, height, radius);
        var meanProducts = Box(products, width, height, radius);
        var slope = squares;
        var offset = products;
        for (int i = 0; i < count; i++)
        {
            float variance = meanSquares[i] - meanGuide[i] * meanGuide[i];
            float covariance = meanProducts[i] - meanGuide[i] * meanMask[i];
            slope[i] = covariance / (variance + epsilon);
            offset[i] = meanMask[i] - slope[i] * meanGuide[i];
        }
        var meanSlope = Box(slope, width, height, radius);
        var meanOffset = Box(offset, width, height, radius);
        var result = new float[count];
        for (int i = 0; i < count; i++) result[i] = Math.Clamp(meanSlope[i] * guide[i] + meanOffset[i], 0, 1);
        return result;
    }

    private static float[] Blur(float[] levels, int width, int height, float sigma)
    {
        if (sigma < 0.3f) return levels;
        // Three box passes approximate a Gaussian.
        int radius = Math.Max(1, (int)Math.Round(sigma * 0.9));
        return Box(Box(Box(levels, width, height, radius), width, height, radius), width, height, radius);
    }

    // MARK: Objects

    /// <summary>
    /// The one connected object (pixels at least half covered) that holds (<paramref name="x"/>, <paramref name="y"/>), as
    /// a 0/255 mask, or null when the point is on background.
    /// </summary>
    public static byte[]? RegionAt(byte[] mask, int width, int height, int x, int y)
    {
        if (x < 0 || y < 0 || x >= width || y >= height || mask[y * width + x] < 128) return null;
        var region = new byte[width * height];
        var stack = new Stack<int>();
        stack.Push(y * width + x);
        region[y * width + x] = 255;
        while (stack.Count > 0)
        {
            int i = stack.Pop(), px = i % width, py = i / width;
            void Visit(int nx, int ny)
            {
                if (nx < 0 || ny < 0 || nx >= width || ny >= height) return;
                int n = ny * width + nx;
                if (region[n] != 0 || mask[n] < 128) return;
                region[n] = 255;
                stack.Push(n);
            }
            Visit(px + 1, py); Visit(px - 1, py); Visit(px, py + 1); Visit(px, py - 1);
        }
        return region;
    }

    /// <summary>A 0/255 mask eroded (positive) or dilated (negative) by up to ten pixels. Mirrors ObjectSelection.adjusted.</summary>
    public static byte[] Offset(byte[] mask, int width, int height, int edgeOffset)
    {
        int steps = Math.Min(10, Math.Abs(edgeOffset));
        for (int s = 0; s < steps; s++)
        {
            var next = (byte[])mask.Clone();
            bool erode = edgeOffset > 0;
            Parallel.For(0, height, y =>
            {
                for (int x = 0; x < width; x++)
                {
                    bool set = mask[y * width + x] != 0;
                    if (set != erode) continue;
                    bool flip = false;
                    for (int ny = Math.Max(0, y - 1); ny <= Math.Min(height - 1, y + 1) && !flip; ny++)
                        for (int nx = Math.Max(0, x - 1); nx <= Math.Min(width - 1, x + 1); nx++)
                            if ((mask[ny * width + nx] != 0) != set) { flip = true; break; }
                    if (flip) next[y * width + x] = erode ? (byte)0 : (byte)255;
                }
            });
            mask = next;
        }
        return mask;
    }

    /// <summary>Pixels at least half covered, as 0/255.</summary>
    public static byte[] Threshold(byte[] mask)
    {
        var result = new byte[mask.Length];
        for (int i = 0; i < mask.Length; i++) result[i] = mask[i] >= 128 ? (byte)255 : (byte)0;
        return result;
    }

    // MARK: Pixels

    private static SKBitmap Bitmap(ReadOnlySpan<byte> rgba, int width, int height)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        rgba[..(width * height * 4)].CopyTo(bitmap.GetPixelSpan());
        return bitmap;
    }

    private static byte[] Scaled(SKBitmap small, int width, int height)
    {
        using var large = new SKBitmap(new SKImageInfo(width, height, SKColorType.Gray8, SKAlphaType.Opaque));
        small.ScalePixels(large, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        return large.GetPixelSpan().ToArray();
    }

    private static float[] Levels(byte[] mask, int width, int height, int w, int h)
    {
        byte[] bytes = mask;
        if (w != width || h != height)
        {
            using var full = new SKBitmap(new SKImageInfo(width, height, SKColorType.Gray8, SKAlphaType.Opaque));
            mask.AsSpan().CopyTo(full.GetPixelSpan());
            using var small = new SKBitmap(new SKImageInfo(w, h, SKColorType.Gray8, SKAlphaType.Opaque));
            full.ScalePixels(small, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            bytes = small.GetPixelSpan().ToArray();
        }
        var levels = new float[w * h];
        for (int i = 0; i < levels.Length; i++) levels[i] = bytes[i] / 255f;
        return levels;
    }

    /// <summary>The picture's gray levels (0–1) at <paramref name="w"/> × <paramref name="h"/>.</summary>
    private static float[] Gray(ReadOnlySpan<byte> rgba, int width, int height, int w, int h)
    {
        using var source = Bitmap(rgba, width, height);
        using var small = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        source.ScalePixels(small, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        var pixels = small.GetPixelSpan();
        var gray = new float[w * h];
        for (int i = 0; i < gray.Length; i++)
            gray[i] = (0.299f * pixels[i * 4] + 0.587f * pixels[i * 4 + 1] + 0.114f * pixels[i * 4 + 2]) / 255f;
        return gray;
    }
}
