using Compositor.Core.Localization;
using Compositor.Core.Model;

namespace Compositor.Core.Inpainting;

/// <summary>
/// Content-Aware Fill: the hole rebuilt from the sampling area. Automatic picks a smooth stretch for flat surroundings
/// (skies, gradients, plain walls) and texture synthesis for everything else; with the neural model on, the model
/// rebuilds the structure first and synthesis lays the picture's own grain over it, so the fill is as sharp as what
/// surrounds it.
/// </summary>
public static class ContentFill
{
    /// <summary>The most pixels worked on at once; a bigger sampling area is trimmed to a window around the hole.</summary>
    public const long MaxWorkPixels = 8_000_000;
    /// <summary>Below this share of textured pixels in the ring around the hole, the surroundings count as smooth.</summary>
    public const double SmoothTexture = 0.04;

    public sealed record Outcome(byte[] Rgba, FillMethod Method, bool Neural);

    /// <summary>
    /// The picture (premultiplied RGBA, <paramref name="width"/> × <paramref name="height"/>) with <paramref name="hole"/>
    /// filled from <paramref name="sample"/> (both one byte a pixel, 255 on). Filled pixels come out opaque.
    /// </summary>
    public static Outcome Run(byte[] rgba, int width, int height, byte[] hole, byte[] sample, ContentFillSettings settings,
                              IInpaintModel? model = null, CancellationToken cancel = default,
                              IGenerativeModel? generator = null, IProgress<double>? progress = null)
    {
        var result = (byte[])rgba.Clone();
        var holeBounds = FillMasks.Bounds(hole, width, height);
        if (holeBounds.Width == 0) return new Outcome(result, settings.Method, false);
        var usable = FillMasks.Usable(rgba, hole);
        var sampled = new byte[sample.Length];
        bool any = false;
        for (int i = 0; i < sampled.Length; i++) if (sample[i] != 0 && usable[i] != 0) { sampled[i] = 255; any = true; }
        if (!any) throw new InvalidOperationException(L.T("取样区域里没有可用的像素。请用取样画笔增加取样区域，或选择“全部”。",
            "The sampling area has no usable pixels. Add to it with the Sampling Brush, or choose All."));

        var region = WorkRegion(holeBounds, FillMasks.Bounds(sampled, width, height), width, height);
        int w = region.Width, h = region.Height;
        var image = new float[w * h * 3];
        var holeIn = new bool[w * h];
        var known = new bool[w * h];
        var sampleIn = new bool[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x, s = (region.Y + y) * width + region.X + x;
                int a = rgba[s * 4 + 3];
                for (int k = 0; k < 3; k++) image[i * 3 + k] = a == 0 ? 0 : Math.Min(255f, rgba[s * 4 + k] * 255f / a);
                holeIn[i] = hole[s] != 0;
                known[i] = usable[s] != 0;
                sampleIn[i] = sampled[s] != 0;
            }

        var method = settings.Method;
        if (method == FillMethod.Auto)
        {
            var cropRgba = Crop(rgba, width, region, 4);
            var cropHole = Crop(hole, width, region, 1);
            var cropUsable = Crop(usable, width, region, 1);
            method = FillMasks.RingTexture(cropRgba, cropHole, cropUsable, w, h) < SmoothTexture ? FillMethod.Smooth : FillMethod.Texture;
        }
        bool neural = false;
        if (method == FillMethod.Generate)
        {
            if (generator is null) throw new InvalidOperationException(L.T("找不到 AI 生成模型（Stable Diffusion）。", "The AI generation model (Stable Diffusion) can’t be found."));
            Generate(generator, rgba, hole, width, height, holeBounds, region, image, holeIn, known, sampleIn, settings, progress, cancel);
            neural = true;
        }
        else if (method == FillMethod.Smooth)
        {
            SmoothFill.Fill(image, holeIn, known, w, h, cancel);
            SmoothFill.Grain(image, holeIn, known, w, h, settings.Seed);
        }
        else
        {
            float[]? guess = null;
            double guessScale = 1;
            if (settings.Neural && model is not null)
            {
                cancel.ThrowIfCancellationRequested();
                var (painted, side) = Neural(model, rgba, hole, width, height, holeBounds, region);
                neural = true;
                guess = (float[])image.Clone();
                for (int i = 0; i < holeIn.Length; i++)
                    if (holeIn[i]) for (int k = 0; k < 3; k++) guess[i * 3 + k] = painted[i * 3 + k];
                // The model's answer is smooth at the pixel level (it paints at 512 pixels): synthesis keeps its
                // structure and lays the picture's own grain over it.
                guessScale = Math.Min(1, 512.0 / side);
            }
            PatchSynthesis.Fill(image, holeIn, known, sampleIn, w, h, settings.Seed, guess, guessScale, cancel);
            // The model decides what goes where (it continues a kerb or a lawn across the area, which matching patches
            // can't); synthesis only lends it the picture's grain. So the result is the model's picture with the fine
            // detail of the synthesized one: structure can't be traded away for a patch that merely matched better.
            if (guess is not null) TransferDetail(guess, image, holeIn, w, h, Math.Max(1.5, 1.2 / guessScale));
        }

        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (!holeIn[i]) continue;
                int s = ((region.Y + y) * width + region.X + x) * 4;
                for (int k = 0; k < 3; k++) result[s + k] = (byte)Math.Clamp(Math.Round(image[i * 3 + k]), 0, 255);
                result[s + 3] = 255;
            }
        return new Outcome(result, method, neural);
    }

    /// <summary>
    /// Generative Fill: the model paints the hole (with context around it), its colors are matched to the picture
    /// along the hole's edge so no seam shows, and when it painted a bigger area than it sees at full detail the
    /// picture's own grain is laid over it.
    /// </summary>
    private static void Generate(IGenerativeModel generator, byte[] rgba, byte[] hole, int width, int height, RectI holeBounds, RectI region,
                                 float[] image, bool[] holeIn, bool[] known, bool[] sampleIn, ContentFillSettings settings,
                                 IProgress<double>? progress, CancellationToken cancel)
    {
        int w = region.Width, h = region.Height;
        // Context on every side, half the hole's size (at least 32 pixels): enough to see what's around, while the
        // hole stays big in the model's 512-pixel view.
        int pad = Math.Max(32, Math.Max(holeBounds.Width, holeBounds.Height) / 2);
        int left = Math.Max(0, holeBounds.X - pad), top = Math.Max(0, holeBounds.Y - pad);
        int right = Math.Min(width, holeBounds.X + holeBounds.Width + pad), bottom = Math.Min(height, holeBounds.Y + holeBounds.Height + pad);
        var crop = new RectI(left, top, right - left, bottom - top);
        var rgb = generator.Generate(Crop(rgba, width, crop, 4), Crop(hole, width, crop, 1), crop.Width, crop.Height,
                                     settings.Seed, progress, cancel);
        if (rgb.Length < crop.Width * crop.Height * 3) throw new InvalidOperationException(L.T("AI 生成的图像大小不对。", "The AI-generated image is the wrong size."));
        // The model's picture over the work region (only where the crop reaches).
        var painted = (float[])image.Clone();
        var inCrop = new bool[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int gx = region.X + x, gy = region.Y + y;
                if (gx < crop.X || gy < crop.Y || gx >= crop.X + crop.Width || gy >= crop.Y + crop.Height) continue;
                int c = ((gy - crop.Y) * crop.Width + gx - crop.X) * 3, i = (y * w + x) * 3;
                painted[i] = rgb[c]; painted[i + 1] = rgb[c + 1]; painted[i + 2] = rgb[c + 2];
                inCrop[y * w + x] = true;
            }
        // Seam: the difference from the picture along the hole's edge, spread smoothly across the hole.
        var difference = new float[w * h * 3];
        var ring = new bool[w * h];
        var distance = FillMasks.Distance(holeIn.Select(b => b ? (byte)255 : (byte)0).ToArray(), w, h);
        for (int i = 0; i < ring.Length; i++)
        {
            if (holeIn[i] || !inCrop[i] || !known[i] || distance[i] > 3) continue;
            ring[i] = true;
            for (int k = 0; k < 3; k++) difference[i * 3 + k] = image[i * 3 + k] - painted[i * 3 + k];
        }
        SmoothFill.Fill(difference, holeIn, ring, w, h, cancel);
        for (int i = 0; i < holeIn.Length; i++)
            if (holeIn[i]) for (int k = 0; k < 3; k++) image[i * 3 + k] = Math.Clamp(painted[i * 3 + k] + difference[i * 3 + k], 0, 255);
        // Painted from a smaller view than the area: the picture's own grain over it.
        int side = Math.Max(crop.Width, crop.Height);
        if (side > 640)
        {
            var guess = (float[])image.Clone();
            double scale = 512.0 / side;
            PatchSynthesis.Fill(image, holeIn, known, sampleIn, w, h, settings.Seed, guess, scale, cancel);
            TransferDetail(guess, image, holeIn, w, h, Math.Max(1.5, 1.2 / scale));
        }
    }

    /// <summary>
    /// <paramref name="detail"/> becomes <paramref name="structure"/>'s colors and shapes (blurred by
    /// <paramref name="sigma"/>) plus <paramref name="detail"/>'s own finer variation, inside the hole.
    /// </summary>
    internal static void TransferDetail(float[] structure, float[] detail, bool[] hole, int width, int height, double sigma)
    {
        var bounds = FillMasks.Bounds(hole.Select(b => b ? (byte)255 : (byte)0).ToArray(), width, height);
        if (bounds.Width == 0) return;
        int reach = (int)Math.Ceiling(sigma * 3);
        int left = Math.Max(0, bounds.X - reach), top = Math.Max(0, bounds.Y - reach);
        int right = Math.Min(width, bounds.X + bounds.Width + reach), bottom = Math.Min(height, bounds.Y + bounds.Height + reach);
        var blurredStructure = Blur(structure, width, left, top, right, bottom, sigma);
        var blurredDetail = Blur(detail, width, left, top, right, bottom, sigma);
        int w = right - left;
        for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
            {
                int i = y * width + x;
                if (!hole[i]) continue;
                int b = ((y - top) * w + (x - left)) * 3;
                for (int k = 0; k < 3; k++)
                    detail[i * 3 + k] = Math.Clamp(blurredStructure[b + k] + detail[i * 3 + k] - blurredDetail[b + k], 0, 255);
            }
    }

    /// <summary>A Gaussian blur of a rectangle of a 3-channel picture (edges clamped), as its own small picture.</summary>
    private static float[] Blur(float[] image, int width, int left, int top, int right, int bottom, double sigma)
    {
        int w = right - left, h = bottom - top, radius = (int)Math.Ceiling(sigma * 3);
        var kernel = new float[radius * 2 + 1];
        float sum = 0;
        for (int i = -radius; i <= radius; i++) sum += kernel[i + radius] = (float)Math.Exp(-i * i / (2 * sigma * sigma));
        for (int i = 0; i < kernel.Length; i++) kernel[i] /= sum;
        var across = new float[w * h * 3];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
                for (int k = 0; k < 3; k++)
                {
                    float v = 0;
                    for (int t = -radius; t <= radius; t++)
                    {
                        int sx = Math.Clamp(left + x + t, left, right - 1);
                        v += image[((top + y) * width + sx) * 3 + k] * kernel[t + radius];
                    }
                    across[(y * w + x) * 3 + k] = v;
                }
        });
        var result = new float[w * h * 3];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
                for (int k = 0; k < 3; k++)
                {
                    float v = 0;
                    for (int t = -radius; t <= radius; t++)
                    {
                        int sy = Math.Clamp(y + t, 0, h - 1);
                        v += across[(sy * w + x) * 3 + k] * kernel[t + radius];
                    }
                    result[(y * w + x) * 3 + k] = v;
                }
        });
        return result;
    }

    /// <summary>The hole and the sampling area together, kept to <see cref="MaxWorkPixels"/> around the hole.</summary>
    private static RectI WorkRegion(RectI hole, RectI sample, int width, int height)
    {
        int left = Math.Min(hole.X, sample.X), top = Math.Min(hole.Y, sample.Y);
        int right = Math.Max(hole.X + hole.Width, sample.X + sample.Width), bottom = Math.Max(hole.Y + hole.Height, sample.Y + sample.Height);
        // A little room for the patches at the hole's edge.
        left = Math.Max(0, left - 4); top = Math.Max(0, top - 4);
        right = Math.Min(width, right + 4); bottom = Math.Min(height, bottom + 4);
        long area = (long)(right - left) * (bottom - top);
        if (area > MaxWorkPixels)
        {
            double shrink = Math.Sqrt((double)MaxWorkPixels / area);
            int w = Math.Max(hole.Width + 8, (int)((right - left) * shrink)), h = Math.Max(hole.Height + 8, (int)((bottom - top) * shrink));
            int cx = hole.X + hole.Width / 2, cy = hole.Y + hole.Height / 2;
            left = Math.Clamp(cx - w / 2, 0, Math.Max(0, width - w));
            top = Math.Clamp(cy - h / 2, 0, Math.Max(0, height - h));
            right = Math.Min(width, left + w);
            bottom = Math.Min(height, top + h);
        }
        return new RectI(left, top, right - left, bottom - top);
    }

    /// <summary>The model's answer for the hole, as straight RGB floats over the work region, and the side it worked on.</summary>
    private static (float[] Painted, int Side) Neural(IInpaintModel model, byte[] rgba, byte[] hole, int width, int height, RectI holeBounds, RectI region)
    {
        // Context on every side as big as the hole (at least 64 pixels), as the model was trained with.
        int pad = Math.Max(64, Math.Max(holeBounds.Width, holeBounds.Height));
        int left = Math.Max(0, holeBounds.X - pad), top = Math.Max(0, holeBounds.Y - pad);
        int right = Math.Min(width, holeBounds.X + holeBounds.Width + pad), bottom = Math.Min(height, holeBounds.Y + holeBounds.Height + pad);
        var crop = new RectI(left, top, right - left, bottom - top);
        var cropRgba = Crop(rgba, width, crop, 4);
        var cropHole = Crop(hole, width, crop, 1);
        var rgb = model.Inpaint(cropRgba, cropHole, crop.Width, crop.Height);
        if (rgb.Length < crop.Width * crop.Height * 3) throw new InvalidOperationException(L.T("模型的输出大小不对。", "The model’s output is the wrong size."));
        var painted = new float[region.Width * region.Height * 3];
        for (int y = 0; y < region.Height; y++)
            for (int x = 0; x < region.Width; x++)
            {
                int gx = region.X + x, gy = region.Y + y;
                if (gx < crop.X || gy < crop.Y || gx >= crop.X + crop.Width || gy >= crop.Y + crop.Height) continue;
                int c = ((gy - crop.Y) * crop.Width + gx - crop.X) * 3, i = (y * region.Width + x) * 3;
                painted[i] = rgb[c]; painted[i + 1] = rgb[c + 1]; painted[i + 2] = rgb[c + 2];
            }
        return (painted, Math.Max(crop.Width, crop.Height));
    }

    private static byte[] Crop(byte[] source, int width, RectI rect, int bytes)
    {
        var result = new byte[rect.Width * rect.Height * bytes];
        for (int y = 0; y < rect.Height; y++)
            source.AsSpan(((rect.Y + y) * width + rect.X) * bytes, rect.Width * bytes).CopyTo(result.AsSpan(y * rect.Width * bytes));
        return result;
    }
}
