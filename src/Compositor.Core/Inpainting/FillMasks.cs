using Compositor.Core.Model;

namespace Compositor.Core.Inpainting;

/// <summary>The masks Content-Aware Fill works with (one byte a pixel, 255 on): distances, the automatic sampling area, growing.</summary>
public static class FillMasks
{
    /// <summary>Approximate Euclidean distance (chamfer 3-4) of every pixel to the nearest pixel that is on.</summary>
    public static float[] Distance(byte[] mask, int width, int height)
    {
        const float Inf = 1e9f, A = 1f, D = 1.4142135f;
        var dist = new float[width * height];
        for (int i = 0; i < dist.Length; i++) dist[i] = mask[i] != 0 ? 0 : Inf;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x;
                float d = dist[i];
                if (d == 0) continue;
                if (x > 0) d = Math.Min(d, dist[i - 1] + A);
                if (y > 0)
                {
                    d = Math.Min(d, dist[i - width] + A);
                    if (x > 0) d = Math.Min(d, dist[i - width - 1] + D);
                    if (x + 1 < width) d = Math.Min(d, dist[i - width + 1] + D);
                }
                dist[i] = d;
            }
        for (int y = height - 1; y >= 0; y--)
            for (int x = width - 1; x >= 0; x--)
            {
                int i = y * width + x;
                float d = dist[i];
                if (d == 0) continue;
                if (x + 1 < width) d = Math.Min(d, dist[i + 1] + A);
                if (y + 1 < height)
                {
                    d = Math.Min(d, dist[i + width] + A);
                    if (x + 1 < width) d = Math.Min(d, dist[i + width + 1] + D);
                    if (x > 0) d = Math.Min(d, dist[i + width - 1] + D);
                }
                dist[i] = d;
            }
        return dist;
    }

    /// <summary>The mask grown by <paramref name="radius"/> pixels (round).</summary>
    public static byte[] Grow(byte[] mask, int width, int height, double radius)
    {
        if (radius <= 0) return (byte[])mask.Clone();
        var dist = Distance(mask, width, height);
        var result = new byte[mask.Length];
        for (int i = 0; i < result.Length; i++) result[i] = dist[i] <= radius ? (byte)255 : (byte)0;
        return result;
    }

    /// <summary>The smallest rectangle holding every pixel that is on; empty when none is.</summary>
    public static RectI Bounds(byte[] mask, int width, int height)
    {
        int minX = width, minY = height, maxX = -1, maxY = -1;
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                if (mask[row + x] == 0) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                maxY = y;
            }
        }
        return maxX < 0 ? new RectI(0, 0, 0, 0) : new RectI(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>Pixels that can be copied from: fully opaque and outside the hole.</summary>
    public static byte[] Usable(ReadOnlySpan<byte> rgba, byte[] hole)
    {
        var usable = new byte[hole.Length];
        for (int i = 0; i < usable.Length; i++) usable[i] = hole[i] == 0 && rgba[i * 4 + 3] == 255 ? (byte)255 : (byte)0;
        return usable;
    }

    /// <summary>Everything usable on the layer.</summary>
    public static byte[] All(ReadOnlySpan<byte> rgba, byte[] hole) => Usable(rgba, hole);

    /// <summary>
    /// The automatic sampling area: usable pixels in a band around the hole, as wide as the hole is big (at least 40
    /// pixels), widened until it holds a few times the hole's area.
    /// </summary>
    public static byte[] Auto(ReadOnlySpan<byte> rgba, byte[] hole, int width, int height)
    {
        var bounds = Bounds(hole, width, height);
        // Nothing to fill yet, nothing to sample around.
        if (bounds.Width == 0) return new byte[hole.Length];
        var usable = Usable(rgba, hole);
        long holeArea = 0;
        foreach (var b in hole) if (b != 0) holeArea++;
        var dist = Distance(hole, width, height);
        double band = Math.Clamp(Math.Max(bounds.Width, bounds.Height) * 0.75, 40, 600);
        long usableCount = 0;
        foreach (var b in usable) if (b != 0) usableCount++;
        while (true)
        {
            var area = new byte[usable.Length];
            long count = 0;
            for (int i = 0; i < area.Length; i++)
                if (usable[i] != 0 && dist[i] <= band) { area[i] = 255; count++; }
            if (count >= Math.Min(usableCount, Math.Max(holeArea * 3, 4000)) || band > Math.Max(width, height)) return area;
            band *= 1.6;
        }
    }

    /// <summary>
    /// How much of the ring around the hole has texture: the share of its pixels whose luminance steps to their
    /// neighbors (0–255) add up to more than <paramref name="step"/>. Sky, gradients and plain walls come out near 0;
    /// a hole crossing a treeline doesn't, even if most of its edge is sky.
    /// </summary>
    public static double RingTexture(ReadOnlySpan<byte> rgba, byte[] hole, byte[] usable, int width, int height, double step = 8)
    {
        var dist = Distance(hole, width, height);
        // Measured over a distance that grows with the hole, so a big picture's smooth-looking neighbors still show
        // the treeline a big hole crosses.
        var bounds = Bounds(hole, width, height);
        int d = Math.Max(1, (int)Math.Round(Math.Max(bounds.Width, bounds.Height) / 64.0));
        double ring = Math.Max(12, d * 3);
        long textured = 0;
        long count = 0;
        for (int y = 0; y + d < height; y++)
            for (int x = 0; x + d < width; x++)
            {
                int i = y * width + x;
                if (dist[i] == 0 || dist[i] > ring || usable[i] == 0 || usable[i + d] == 0 || usable[i + d * width] == 0) continue;
                double l = Luma(rgba, i), lx = Luma(rgba, i + d), ly = Luma(rgba, i + d * width);
                if (Math.Abs(lx - l) + Math.Abs(ly - l) > step) textured++;
                count++;
            }
        return count == 0 ? 0 : (double)textured / count;
    }

    private static double Luma(ReadOnlySpan<byte> rgba, int i) => 0.299 * rgba[i * 4] + 0.587 * rgba[i * 4 + 1] + 0.114 * rgba[i * 4 + 2];
}
