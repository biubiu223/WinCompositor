using Compositor.Core.Localization;

namespace Compositor.Core.Pixels;

/// <summary>How Spot Healing rebuilds a spot. Mirrors SpotHealingMode.</summary>
public enum SpotHealingMode { ContentAware, CreateTexture, ProximityMatch }

public static class SpotHealingModes
{
    public static string ToDisplayName(this SpotHealingMode mode) => mode switch
    {
        SpotHealingMode.ContentAware => L.T("内容识别", "Content-Aware"),
        SpotHealingMode.CreateTexture => L.T("创建纹理", "Create Texture"),
        _ => L.T("近似匹配", "Proximity Match"),
    };
}

/// <summary>
/// Spot Healing: rebuilds the painted spot from its surroundings. A patch whose ring best matches the ring around the
/// spot is borrowed (Content-Aware, Proximity Match) — or none, with grain matched to the surroundings (Create
/// Texture) — and the difference along the edge is spread smoothly across the spot (a membrane), so the patch blends
/// in. A port of HealPixels.c.
/// </summary>
public static class SpotHeal
{
    private const byte Outside = 0, Ring = 1, Hole = 2;

    private static uint Hash(uint x)
    {
        x ^= x >> 16; x *= 0x7feb352dU;
        x ^= x >> 15; x *= 0x846ca68bU;
        x ^= x >> 16;
        return x;
    }

    private static double Unit(uint key) => (Hash(key) >> 8) / 16777216.0;

    /// <summary>The bounds of the nonzero coverage: (x0, y0, x1, y1), all zero when there is none.</summary>
    public static (int X0, int Y0, int X1, int Y1) Bounds(byte[] coverage, int width, int height)
    {
        int x0 = width, y0 = height, x1 = 0, y1 = 0;
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                if (coverage[row + x] == 0) continue;
                if (x < x0) x0 = x;
                if (x + 1 > x1) x1 = x + 1;
                if (y < y0) y0 = y;
                if (y + 1 > y1) y1 = y + 1;
            }
        }
        return x1 <= x0 || y1 <= y0 ? (0, 0, 0, 0) : (x0, y0, x1, y1);
    }

    private static double Score(byte[] rgba, int stride, byte[] role, int wx0, int wy0, int ww, int wh, int dx, int dy, int W, int H)
    {
        if (Math.Abs(dx) < ww && Math.Abs(dy) < wh) return double.PositiveInfinity;
        if (wx0 + dx < 0 || wy0 + dy < 0 || wx0 + ww + dx > W || wy0 + wh + dy > H) return double.PositiveInfinity;
        double sum = 0;
        long n = 0;
        for (int y = 0; y < wh; y++)
        {
            for (int x = 0; x < ww; x++)
            {
                if (role[y * ww + x] != Ring) continue;
                int t = (wy0 + y) * stride + (wx0 + x) * 4;
                int s = (wy0 + y + dy) * stride + (wx0 + x + dx) * 4;
                for (int c = 0; c < 4; c++) { double d = rgba[t + c] - rgba[s + c]; sum += d * d; }
                n++;
            }
        }
        return n > 0 ? sum / n : double.PositiveInfinity;
    }

    /// <summary>Smooth values over the hole, fixed to the ring around it; a coarser copy solved first seeds large spots.</summary>
    private static void Solve(float[] value, byte[] role, int w, int h, int depth)
    {
        int iterations = 300;
        if (w > 32 && h > 32 && depth < 16)
        {
            int cw = (w + 1) / 2, ch = (h + 1) / 2;
            var coarse = new float[cw * ch * 4];
            var coarseRole = new byte[cw * ch];
            for (int y = 0; y < ch; y++)
            {
                for (int x = 0; x < cw; x++)
                {
                    int known = 0, hole = 0;
                    Span<float> knownSum = stackalloc float[4], holeSum = stackalloc float[4];
                    knownSum.Clear(); holeSum.Clear();
                    for (int j = 0; j < 2; j++)
                        for (int i = 0; i < 2; i++)
                        {
                            int fx = x * 2 + i, fy = y * 2 + j;
                            if (fx >= w || fy >= h) continue;
                            int p = fy * w + fx;
                            if (role[p] == Ring) { known++; for (int c = 0; c < 4; c++) knownSum[c] += value[p * 4 + c]; }
                            else if (role[p] == Hole) { hole++; for (int c = 0; c < 4; c++) holeSum[c] += value[p * 4 + c]; }
                        }
                    int q = y * cw + x;
                    if (known > 0) { coarseRole[q] = Ring; for (int c = 0; c < 4; c++) coarse[q * 4 + c] = knownSum[c] / known; }
                    else if (hole > 0) { coarseRole[q] = Hole; for (int c = 0; c < 4; c++) coarse[q * 4 + c] = holeSum[c] / hole; }
                }
            }
            Solve(coarse, coarseRole, cw, ch, depth + 1);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int p = y * w + x, q = (y / 2) * cw + x / 2;
                    if (role[p] == Hole && coarseRole[q] == Hole) Array.Copy(coarse, q * 4, value, p * 4, 4);
                }
            iterations = 40;
        }
        const float omega = 1.8f;
        for (int it = 0; it < iterations; it++)
        {
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int p = y * w + x;
                    if (role[p] != Hole) continue;
                    float s0 = 0, s1 = 0, s2 = 0, s3 = 0;
                    int n = 0;
                    void Add(int nx, int ny)
                    {
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) return;
                        int q = ny * w + nx;
                        if (role[q] == Outside) return;
                        s0 += value[q * 4]; s1 += value[q * 4 + 1]; s2 += value[q * 4 + 2]; s3 += value[q * 4 + 3];
                        n++;
                    }
                    Add(x - 1, y); Add(x + 1, y); Add(x, y - 1); Add(x, y + 1);
                    if (n == 0) continue;
                    value[p * 4] += omega * (s0 / n - value[p * 4]);
                    value[p * 4 + 1] += omega * (s1 / n - value[p * 4 + 1]);
                    value[p * 4 + 2] += omega * (s2 / n - value[p * 4 + 2]);
                    value[p * 4 + 3] += omega * (s3 / n - value[p * 4 + 3]);
                }
            }
        }
    }

    /// <summary>
    /// Heals premultiplied RGBA <paramref name="rgba"/> (<paramref name="width"/> × <paramref name="height"/>, rows of
    /// <paramref name="stride"/> bytes) in place where <paramref name="coverage"/> (one byte a pixel) is set, by
    /// coverage × <paramref name="opacity"/>.
    /// </summary>
    public static void Run(byte[] rgba, byte[] coverage, int width, int height, int stride, double opacity, SpotHealingMode mode, uint seed)
    {
        int W = width, H = height;
        var (bx0, by0, bx1, by1) = Bounds(coverage, width, height);
        if (bx1 <= bx0) return;
        int bw = bx1 - bx0, bh = by1 - by0, size = Math.Max(bw, bh);
        int ring = Math.Clamp(size / 8, 2, 16);
        int wx0 = Math.Max(0, bx0 - ring), wy0 = Math.Max(0, by0 - ring);
        int wx1 = Math.Min(W, bx1 + ring), wy1 = Math.Min(H, by1 + ring);
        int ww = wx1 - wx0, wh = wy1 - wy0, wn = ww * wh;

        var role = new byte[wn];
        var near = new byte[wn];
        var prefix = new int[Math.Max(ww, wh) + 1];
        var value = new float[wn * 4];
        for (int y = 0; y < wh; y++)
            for (int x = 0; x < ww; x++)
                role[y * ww + x] = coverage[(wy0 + y) * width + wx0 + x] != 0 ? Hole : Outside;
        // The ring: pixels within `ring` of the spot (a square dilation, rows then columns).
        for (int y = 0; y < wh; y++)
        {
            prefix[0] = 0;
            for (int x = 0; x < ww; x++) prefix[x + 1] = prefix[x] + (role[y * ww + x] == Hole ? 1 : 0);
            for (int x = 0; x < ww; x++)
            {
                int lo = Math.Max(0, x - ring), hi = Math.Min(ww, x + ring + 1);
                near[y * ww + x] = (byte)(prefix[hi] - prefix[lo] > 0 ? 1 : 0);
            }
        }
        for (int x = 0; x < ww; x++)
        {
            prefix[0] = 0;
            for (int y = 0; y < wh; y++) prefix[y + 1] = prefix[y] + near[y * ww + x];
            for (int y = 0; y < wh; y++)
            {
                int lo = Math.Max(0, y - ring), hi = Math.Min(wh, y + ring + 1);
                if (role[y * ww + x] == Outside && prefix[hi] - prefix[lo] > 0) role[y * ww + x] = Ring;
            }
        }
        long ringCount = role.Count(r => r == Ring);
        if (ringCount == 0) return;

        // A source patch for Content-Aware and Proximity Match.
        int ox = 0, oy = 0;
        bool haveSource = false;
        if (mode != SpotHealingMode.CreateTexture)
        {
            double[] factors = { 1.05, 1.35, 1.75, 2.25, 2.8 };
            int count = mode == SpotHealingMode.ProximityMatch ? 2 : 5;
            double best = double.PositiveInfinity;
            for (int f = 0; f < count; f++)
            {
                for (int a = 0; a < 24; a++)
                {
                    double angle = a * Math.PI / 12.0;
                    int dx = (int)Math.Round(Math.Cos(angle) * factors[f] * ww), dy = (int)Math.Round(Math.Sin(angle) * factors[f] * wh);
                    double score = Score(rgba, stride, role, wx0, wy0, ww, wh, dx, dy, W, H);
                    if (!double.IsFinite(score)) continue;
                    score *= mode == SpotHealingMode.ProximityMatch ? 1.0 + 0.6 * f : 1.0 + 0.1 * f; // nearer patches win ties
                    if (score < best) { best = score; ox = dx; oy = dy; }
                }
            }
            if (double.IsFinite(best))
            {
                // Fine-tune the alignment so repeating texture lines up.
                int cx = ox, cy = oy;
                double refined = Score(rgba, stride, role, wx0, wy0, ww, wh, cx, cy, W, H);
                for (int j = -3; j <= 3; j++)
                    for (int i = -3; i <= 3; i++)
                    {
                        double score = Score(rgba, stride, role, wx0, wy0, ww, wh, cx + i, cy + j, W, H);
                        if (score < refined) { refined = score; ox = cx + i; oy = cy + j; }
                    }
                haveSource = true;
            }
        }

        // The membrane: the edge difference between the original and the patch (or the original itself, for a smooth
        // fill), spread across the spot.
        var mean = new double[4];
        var detail = new double[3];
        for (int y = 0; y < wh; y++)
        {
            for (int x = 0; x < ww; x++)
            {
                int p = y * ww + x;
                if (role[p] != Ring) { value[p * 4] = value[p * 4 + 1] = value[p * 4 + 2] = value[p * 4 + 3] = 0; continue; }
                int ix = wx0 + x, iy = wy0 + y;
                int t = iy * stride + ix * 4;
                int s = haveSource ? (iy + oy) * stride + (ix + ox) * 4 : -1;
                for (int c = 0; c < 4; c++)
                {
                    value[p * 4 + c] = rgba[t + c] - (s >= 0 ? rgba[s + c] : 0);
                    mean[c] += value[p * 4 + c];
                }
                if (!haveSource)
                {
                    // Fine detail around the spot: each pixel against the average of its neighbors.
                    for (int c = 0; c < 3; c++)
                    {
                        double around = 0;
                        int n = 0;
                        foreach (var (nx, ny) in new[] { (ix - 1, iy), (ix + 1, iy), (ix, iy - 1), (ix, iy + 1) })
                        {
                            if (nx < 0 || ny < 0 || nx >= W || ny >= H) continue;
                            around += rgba[ny * stride + nx * 4 + c];
                            n++;
                        }
                        if (n > 0) { double d = rgba[t + c] - around / n; detail[c] += d * d; }
                    }
                }
            }
        }
        for (int c = 0; c < 4; c++) mean[c] /= ringCount;
        for (int p = 0; p < wn; p++)
            if (role[p] == Hole) for (int c = 0; c < 4; c++) value[p * 4 + c] = (float)mean[c];
        Solve(value, role, ww, wh, 0);
        for (int c = 0; c < 3; c++) detail[c] = Math.Sqrt(detail[c] / ringCount) * 0.9;

        for (int y = 0; y < wh; y++)
        {
            for (int x = 0; x < ww; x++)
            {
                int p = y * ww + x;
                if (role[p] != Hole) continue;
                int ix = wx0 + x, iy = wy0 + y;
                int t = iy * stride + ix * 4;
                int s = haveSource ? (iy + oy) * stride + (ix + ox) * 4 : -1;
                double amount = coverage[iy * width + ix] / 255.0 * opacity;
                double grain = 0;
                if (!haveSource)
                {
                    uint key = Hash(seed ^ Hash((uint)(iy * W + ix)));
                    double u1 = Unit(key), u2 = Unit(key ^ 0x68e31da4U);
                    grain = Math.Sqrt(-2.0 * Math.Log(1.0 - u1)) * Math.Cos(2.0 * Math.PI * u2);
                }
                Span<double> output = stackalloc double[4];
                for (int c = 0; c < 4; c++)
                {
                    double healed = (s >= 0 ? rgba[s + c] : 0) + value[p * 4 + c] + (c < 3 ? grain * detail[c] : 0);
                    output[c] = rgba[t + c] + (healed - rgba[t + c]) * amount;
                }
                byte alpha = (byte)Math.Round(Math.Clamp(output[3], 0, 255));
                rgba[t + 3] = alpha;
                for (int c = 0; c < 3; c++) rgba[t + c] = (byte)Math.Round(Math.Clamp(output[c], 0, alpha));
            }
        }
    }
}
