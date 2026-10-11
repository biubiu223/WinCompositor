namespace Compositor.Core.Inpainting;

/// <summary>
/// The hole filled smoothly from its edges: push-pull (known colors averaged down a pyramid, then drawn back up), then a
/// few relaxation passes so the gradients run on, and grain matched to the surroundings so it doesn't look painted.
/// </summary>
internal static class SmoothFill
{
    /// <summary>Fills <paramref name="image"/> (3 floats a pixel) where <paramref name="hole"/> is set, from the pixels <paramref name="known"/> marks.</summary>
    public static void Fill(float[] image, bool[] hole, bool[] known, int width, int height, CancellationToken cancel = default)
    {
        var weights = new float[width * height];
        for (int i = 0; i < weights.Length; i++) weights[i] = known[i] && !hole[i] ? 1 : 0;
        var filled = PushPull(image, weights, width, height);
        for (int i = 0; i < weights.Length; i++)
            if (hole[i]) { image[i * 3] = filled[i * 3]; image[i * 3 + 1] = filled[i * 3 + 1]; image[i * 3 + 2] = filled[i * 3 + 2]; }
        Relax(image, hole, width, height, 24, cancel);
    }

    /// <summary>Every pixel's color from the weighted known ones: exact where weight is 1, interpolated across the rest.</summary>
    private static float[] PushPull(float[] image, float[] weights, int width, int height)
    {
        if (width <= 1 && height <= 1)
        {
            var single = new float[3];
            if (weights[0] > 0) { single[0] = image[0]; single[1] = image[1]; single[2] = image[2]; }
            return single;
        }
        int cw = (width + 1) / 2, ch = (height + 1) / 2;
        var coarse = new float[cw * ch * 3];
        var coarseWeights = new float[cw * ch];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x, c = (y / 2) * cw + x / 2;
                float w = weights[i];
                if (w <= 0) continue;
                coarse[c * 3] += image[i * 3] * w;
                coarse[c * 3 + 1] += image[i * 3 + 1] * w;
                coarse[c * 3 + 2] += image[i * 3 + 2] * w;
                coarseWeights[c] += w;
            }
        for (int c = 0; c < coarseWeights.Length; c++)
        {
            float w = coarseWeights[c];
            if (w <= 0) continue;
            coarse[c * 3] /= w; coarse[c * 3 + 1] /= w; coarse[c * 3 + 2] /= w;
            coarseWeights[c] = Math.Min(1, w);
        }
        var pulled = width == cw && height == ch ? coarse : PushPull(coarse, coarseWeights, cw, ch);
        var result = new float[width * height * 3];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x;
                // Bilinear from the coarser level, centered on its pixels.
                double fx = Math.Clamp((x - 0.5) / 2, 0, cw - 1), fy = Math.Clamp((y - 0.5) / 2, 0, ch - 1);
                int x0 = (int)fx, y0 = (int)fy, x1 = Math.Min(cw - 1, x0 + 1), y1 = Math.Min(ch - 1, y0 + 1);
                float ax = (float)(fx - x0), ay = (float)(fy - y0);
                float w = weights[i];
                for (int k = 0; k < 3; k++)
                {
                    float top = pulled[(y0 * cw + x0) * 3 + k] * (1 - ax) + pulled[(y0 * cw + x1) * 3 + k] * ax;
                    float bottom = pulled[(y1 * cw + x0) * 3 + k] * (1 - ax) + pulled[(y1 * cw + x1) * 3 + k] * ax;
                    float up = top * (1 - ay) + bottom * ay;
                    result[i * 3 + k] = w >= 1 ? image[i * 3 + k] : image[i * 3 + k] * w + up * (1 - w);
                }
            }
        return result;
    }

    /// <summary>Jacobi relaxation inside the hole: each pixel toward the mean of its four neighbors.</summary>
    internal static void Relax(float[] image, bool[] hole, int width, int height, int passes, CancellationToken cancel = default)
    {
        var inside = new List<int>();
        for (int i = 0; i < hole.Length; i++) if (hole[i]) inside.Add(i);
        if (inside.Count == 0 || inside.Count > 4_000_000) return;
        var next = new float[inside.Count * 3];
        for (int pass = 0; pass < passes; pass++)
        {
            cancel.ThrowIfCancellationRequested();
            Parallel.For(0, inside.Count, n =>
            {
                int i = inside[n], x = i % width, y = i / width;
                int l = x > 0 ? i - 1 : i, r = x + 1 < width ? i + 1 : i, u = y > 0 ? i - width : i, d = y + 1 < height ? i + width : i;
                for (int k = 0; k < 3; k++)
                    next[n * 3 + k] = (image[l * 3 + k] + image[r * 3 + k] + image[u * 3 + k] + image[d * 3 + k]) * 0.25f;
            });
            for (int n = 0; n < inside.Count; n++)
            {
                int i = inside[n];
                image[i * 3] = next[n * 3]; image[i * 3 + 1] = next[n * 3 + 1]; image[i * 3 + 2] = next[n * 3 + 2];
            }
        }
    }

    /// <summary>Adds noise as strong as the fine detail in the ring around the hole (its median deviation from a local mean).</summary>
    public static void Grain(float[] image, bool[] hole, bool[] known, int width, int height, int seed)
    {
        var deviations = new List<float>();
        var rng = new Random(seed);
        for (int y = 1; y + 1 < height; y++)
            for (int x = 1; x + 1 < width; x++)
            {
                int i = y * width + x;
                if (!known[i] || hole[i] || !(hole[i - 1] || hole[i + 1] || hole[i - width] || hole[i + width] || Near(hole, i, width, height))) continue;
                if (!known[i - 1] || !known[i + 1] || !known[i - width] || !known[i + width]) continue;
                for (int k = 0; k < 3; k++)
                {
                    float mean = (image[(i - 1) * 3 + k] + image[(i + 1) * 3 + k] + image[(i - width) * 3 + k] + image[(i + width) * 3 + k]) / 4;
                    deviations.Add(Math.Abs(image[i * 3 + k] - mean));
                }
            }
        if (deviations.Count < 16) return;
        deviations.Sort();
        float sigma = deviations[deviations.Count / 2] * 1.25f;
        if (sigma < 0.4f) return;
        for (int i = 0; i < hole.Length; i++)
        {
            if (!hole[i]) continue;
            // Mostly shared across channels, as sensor noise mostly is.
            float shared = Gaussian(rng) * sigma;
            for (int k = 0; k < 3; k++) image[i * 3 + k] += shared * 0.8f + Gaussian(rng) * sigma * 0.35f;
        }
    }

    private static bool Near(bool[] hole, int i, int width, int height)
    {
        int x = i % width, y = i / width;
        for (int dy = -6; dy <= 6; dy += 3)
            for (int dx = -6; dx <= 6; dx += 3)
            {
                int nx = x + dx, ny = y + dy;
                if (nx >= 0 && ny >= 0 && nx < width && ny < height && hole[ny * width + nx]) return true;
            }
        return false;
    }

    private static float Gaussian(Random rng)
    {
        double u = 1 - rng.NextDouble(), v = rng.NextDouble();
        return (float)(Math.Sqrt(-2 * Math.Log(u)) * Math.Cos(2 * Math.PI * v));
    }
}
