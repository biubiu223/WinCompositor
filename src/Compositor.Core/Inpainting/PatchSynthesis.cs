using Compositor.Core.Localization;

namespace Compositor.Core.Inpainting;

/// <summary>
/// Patch-based image completion (Wexler, Shechtman &amp; Irani's expectation-maximization with Barnes et al.'s
/// PatchMatch): coarse to fine, every patch touching the hole finds its closest patch in the sampling area, and each
/// hole pixel becomes the weighted vote of the patches over it. What comes out is made of the picture's own texture.
/// </summary>
internal sealed class PatchSynthesis
{
    private const int Radius = 3, Size = Radius * 2 + 1;

    /// <summary>One pyramid level.</summary>
    private sealed class Level
    {
        public int W, H;
        public float[] Image = Array.Empty<float>();
        public bool[] Hole = Array.Empty<bool>();
        public bool[] Known = Array.Empty<bool>();
        public bool[] Sample = Array.Empty<bool>();
        /// <summary>Patch centers whose whole patch is known and in the sampling area.</summary>
        public bool[] SourceOk = Array.Empty<bool>();
        public int[] Sources = Array.Empty<int>();
        /// <summary>Patch centers whose patch touches the hole (kept inside the image).</summary>
        public int[] Targets = Array.Empty<int>();
        public bool[] IsTarget = Array.Empty<bool>();
        /// <summary>The targets row by row, for searching rows side by side.</summary>
        public int[][] TargetRows = Array.Empty<int[]>();
        /// <summary>How far each pixel lies inside the hole (0 outside it), for trusting patches near its edge more.</summary>
        public float[] Depth = Array.Empty<float>();
    }

    /// <summary>
    /// How much more a patch nearer the hole's edge counts in a vote, per pixel of depth (Wexler et al.'s confidence):
    /// what the edge's patches say is grounded in the real picture, so it carries inward instead of being averaged away.
    /// </summary>
    private const double EdgeTrust = 1.3;
    /// <summary>The most patch comparisons the onion-peel start makes in all (it fills the coarsest level only).</summary>
    private const long PeelBudget = 8_000_000;

    private readonly List<Level> _levels = new();
    private readonly int _seed;
    private readonly CancellationToken _cancel;

    private PatchSynthesis(int seed, CancellationToken cancel)
    {
        _seed = seed;
        _cancel = cancel;
    }

    /// <summary>
    /// Fills <paramref name="image"/> (3 floats a pixel, 0–255) inside <paramref name="hole"/> from patches in
    /// <paramref name="sample"/> (pixels also <paramref name="known"/>). With <paramref name="guess"/> (the same layout,
    /// the hole already painted, say by a model), synthesis starts from it at the level of detail it carries
    /// (<paramref name="guessScale"/>, its pixels per real pixel) instead of from nothing.
    /// </summary>
    public static void Fill(float[] image, bool[] hole, bool[] known, bool[] sample, int width, int height, int seed,
                            float[]? guess = null, double guessScale = 1, CancellationToken cancel = default)
    {
        var synthesis = new PatchSynthesis(seed, cancel);
        synthesis.Build(image, hole, known, sample, width, height);
        if (synthesis._levels.Count == 0) throw new InvalidOperationException(L.T("取样区域太小，找不到可以复制的图块。请扩大取样区域。",
                "The sampling area is too small to find patches to copy from. Make the sampling area larger."));
        synthesis.Run(guess, guessScale);
        var finest = synthesis._levels[0];
        for (int i = 0; i < hole.Length; i++)
            if (hole[i])
                for (int k = 0; k < 3; k++) image[i * 3 + k] = Math.Clamp(finest.Image[i * 3 + k], 0, 255);
    }

    // MARK: Pyramid

    private void Build(float[] image, bool[] hole, bool[] known, bool[] sample, int width, int height)
    {
        var level = new Level { W = width, H = height, Image = (float[])image.Clone(), Hole = hole, Known = known, Sample = sample };
        int holeSide = HoleSide(hole, width, height);
        while (true)
        {
            if (!Prepare(level)) break;
            _levels.Add(level);
            holeSide = (holeSide + 1) / 2;
            int cw = (level.W + 1) / 2, ch = (level.H + 1) / 2;
            // Coarse enough: the hole a couple of patches across, or the picture too small to halve again.
            if (holeSide <= Size * 2 || Math.Min(cw, ch) < Size * 3) break;
            level = Downsample(level);
        }
    }

    private static int HoleSide(bool[] hole, int width, int height)
    {
        int minX = width, minY = height, maxX = -1, maxY = -1;
        for (int i = 0; i < hole.Length; i++)
        {
            if (!hole[i]) continue;
            int x = i % width, y = i / width;
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }
        return maxX < 0 ? 0 : Math.Max(maxX - minX + 1, maxY - minY + 1);
    }

    private static Level Downsample(Level fine)
    {
        int w = (fine.W + 1) / 2, h = (fine.H + 1) / 2;
        var level = new Level { W = w, H = h, Image = new float[w * h * 3], Hole = new bool[w * h], Known = new bool[w * h], Sample = new bool[w * h] };
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int c = y * w + x;
                bool anyHole = false, allKnown = true, allSample = true;
                float r = 0, g = 0, b = 0, n = 0, ar = 0, ag = 0, ab = 0, an = 0;
                for (int dy = 0; dy < 2; dy++)
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int fx = Math.Min(fine.W - 1, x * 2 + dx), fy = Math.Min(fine.H - 1, y * 2 + dy), f = fy * fine.W + fx;
                        anyHole |= fine.Hole[f];
                        allKnown &= fine.Known[f] && !fine.Hole[f];
                        allSample &= fine.Sample[f];
                        ar += fine.Image[f * 3]; ag += fine.Image[f * 3 + 1]; ab += fine.Image[f * 3 + 2]; an++;
                        if (!fine.Known[f] || fine.Hole[f]) continue;
                        r += fine.Image[f * 3]; g += fine.Image[f * 3 + 1]; b += fine.Image[f * 3 + 2]; n++;
                    }
                // Known pixels average only known ones, so the hole's placeholder never bleeds into them.
                if (n > 0) { level.Image[c * 3] = r / n; level.Image[c * 3 + 1] = g / n; level.Image[c * 3 + 2] = b / n; }
                else { level.Image[c * 3] = ar / an; level.Image[c * 3 + 1] = ag / an; level.Image[c * 3 + 2] = ab / an; }
                level.Hole[c] = anyHole;
                level.Known[c] = allKnown || (n > 0 && !anyHole);
                level.Sample[c] = allSample && allKnown;
            }
        });
        return level;
    }

    /// <summary>Finds a level's source and target patches; false when it has no source patches at all.</summary>
    private static bool Prepare(Level level)
    {
        int w = level.W, h = level.H;
        if (w < Size || h < Size) return false;
        // A source patch lies wholly in the sampling area: the area eroded by the radius, as two box passes.
        var rows = new int[w * h];
        Parallel.For(0, h, y =>
        {
            int run = 0;
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                run = level.Sample[i] && level.Known[i] && !level.Hole[i] ? run + 1 : 0;
                rows[i] = run;
            }
        });
        var ok = new bool[w * h];
        Parallel.For(Radius, w - Radius, x =>
        {
            int run = 0;
            for (int y = 0; y < h; y++)
            {
                run = rows[y * w + Math.Min(w - 1, x + Radius)] >= Size ? run + 1 : 0;
                if (run >= Size) ok[(y - Radius) * w + x] = true;
            }
        });
        var sources = new List<int>();
        for (int i = 0; i < ok.Length; i++) if (ok[i]) sources.Add(i);
        if (sources.Count == 0) return false;
        level.SourceOk = ok;
        level.Sources = sources.ToArray();

        // Targets: centers (kept a radius inside the picture) whose patch holds a hole pixel.
        var isTarget = new bool[w * h];
        for (int i = 0; i < level.Hole.Length; i++)
        {
            if (!level.Hole[i]) continue;
            int x = i % w, y = i / w;
            for (int cy = Math.Max(Radius, y - Radius); cy <= Math.Min(h - 1 - Radius, y + Radius); cy++)
                for (int cx = Math.Max(Radius, x - Radius); cx <= Math.Min(w - 1 - Radius, x + Radius); cx++)
                    isTarget[cy * w + cx] = true;
        }
        var targets = new List<int>();
        for (int i = 0; i < isTarget.Length; i++) if (isTarget[i]) targets.Add(i);
        level.IsTarget = isTarget;
        level.Targets = targets.ToArray();
        level.TargetRows = level.Targets.GroupBy(t => t / w).Select(g => g.ToArray()).ToArray();
        var outside = new byte[w * h];
        for (int i = 0; i < outside.Length; i++) outside[i] = level.Hole[i] ? (byte)0 : (byte)255;
        level.Depth = FillMasks.Distance(outside, w, h);
        return true;
    }

    // MARK: Synthesis

    private void Run(float[]? guess, double guessScale)
    {
        int coarsest = _levels.Count - 1;
        int start = coarsest;
        if (guess is not null)
        {
            // Start where the guess's detail runs out: the level at about its scale.
            int levelOfGuess = (int)Math.Round(Math.Log2(Math.Max(1, 1 / Math.Max(1e-6, guessScale))));
            start = Math.Clamp(levelOfGuess, 0, coarsest);
            var level = _levels[start];
            var resampled = Resample(guess, _levels[0].W, _levels[0].H, level.W, level.H);
            for (int i = 0; i < level.Hole.Length; i++)
                if (level.Hole[i]) for (int k = 0; k < 3; k++) level.Image[i * 3 + k] = resampled[i * 3 + k];
        }
        else
        {
            var top = _levels[coarsest];
            // Everything gets a value first (in case some pixel can't be reached), then the onion peel replaces it.
            SmoothFill.Fill(top.Image, top.Hole, top.Known, top.W, top.H, _cancel);
            Peel(top);
        }
        int[]? nnf = null;
        for (int l = start; l >= 0; l--)
        {
            _cancel.ThrowIfCancellationRequested();
            var level = _levels[l];
            if (nnf is null) nnf = RandomField(level, l);
            else
            {
                var coarse = _levels[l + 1];
                // The coarse result drawn up as the starting estimate, and the coarse matches carried up with it.
                var up = Resample(coarse.Image, coarse.W, coarse.H, level.W, level.H);
                for (int i = 0; i < level.Hole.Length; i++)
                    if (level.Hole[i]) for (int k = 0; k < 3; k++) level.Image[i * 3 + k] = up[i * 3 + k];
                nnf = Upsample(nnf, coarse, level, l);
            }
            var dist = new float[level.W * level.H];
            foreach (var t in level.Targets) dist[t] = Distance(level, t, nnf[t], float.MaxValue);
            bool guided = guess is not null && l == start;
            int rounds = l == start ? (guided ? 4 : 10) : Math.Max(2, 6 - (start - l));
            if (l == 0) rounds = Math.Min(rounds, level.Targets.Length > 400_000 ? 3 : 4);
            for (int round = 0; round < rounds; round++)
            {
                int passes = round == 0 ? 4 : 2;
                for (int pass = 0; pass < passes; pass++)
                {
                    _cancel.ThrowIfCancellationRequested();
                    Search(level, nnf, dist, (l * 131 + round) * 17 + pass, reverse: pass % 2 == 1);
                }
                // The last rounds at full size vote sharply, and search again against that sharper estimate.
                Vote(level, nnf, dist, sharp: l == 0 && round >= rounds - 2);
                // Distances against the new estimate.
                Parallel.ForEach(level.Targets, t => dist[t] = Distance(level, t, nnf[t], float.MaxValue));
            }
        }
    }

    /// <summary>
    /// The starting estimate without a guess: the hole filled ring by ring from its edge inward, each pixel copied from
    /// the source patch that best matches what's already there around it (only the known and already-filled pixels
    /// count). A smooth blend of the edge colors as the start would mix, say, grass and road into a mud that then
    /// matches only the flattest patches; peeling keeps each side's own colors and carries edges in.
    /// </summary>
    private void Peel(Level level)
    {
        int w = level.W, h = level.H;
        var filled = new bool[w * h];
        int holes = 0;
        for (int i = 0; i < filled.Length; i++)
        {
            filled[i] = !level.Hole[i] && level.Known[i];
            if (level.Hole[i]) holes++;
        }
        if (holes == 0) return;
        // Every source when that's affordable; otherwise an even spread of them (neighbors' matches shifted over make
        // up for the gaps).
        var candidates = level.Sources;
        long perPixel = Math.Max(256, PeelBudget / holes);
        if (candidates.Length > perPixel)
        {
            var rng = new Random(_seed * 6151 + 3);
            candidates = Enumerable.Range(0, (int)perPixel).Select(_ => level.Sources[rng.Next(level.Sources.Length)]).ToArray();
        }
        var match = new int[w * h];
        Array.Fill(match, -1);
        var front = new List<int>();
        while (true)
        {
            _cancel.ThrowIfCancellationRequested();
            front.Clear();
            for (int i = 0; i < filled.Length; i++)
            {
                if (filled[i] || !level.Hole[i]) continue;
                int x = i % w, y = i / w;
                bool touches = false;
                for (int dy = -1; dy <= 1 && !touches; dy++)
                    for (int dx = -1; dx <= 1 && !touches; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        touches = nx >= 0 && ny >= 0 && nx < w && ny < h && filled[ny * w + nx];
                    }
                if (touches) front.Add(i);
            }
            if (front.Count == 0) break;
            var chosen = new int[front.Count];
            Parallel.For(0, front.Count, n =>
            {
                int p = front[n], px = p % w, py = p / w;
                int best = -1;
                float bestDist = float.MaxValue;
                void Consider(int s)
                {
                    if (s < 0 || s >= level.SourceOk.Length || !level.SourceOk[s]) return;
                    int sx = s % w;
                    if (sx < Radius || sx >= w - Radius) return;
                    float d = PartialDistance(level, filled, px, py, s, bestDist);
                    if (d < bestDist) { bestDist = d; best = s; }
                }
                // Coherence first: a filled neighbor's match, shifted by the step to here.
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = px + dx, ny = py + dy;
                        if ((dx | dy) == 0 || nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        int q = ny * w + nx;
                        if (match[q] >= 0) Consider(match[q] - dy * w - dx);
                    }
                foreach (var s in candidates) Consider(s);
                chosen[n] = best;
            });
            // Written after the whole ring is chosen, so every choice saw the same picture.
            for (int n = 0; n < front.Count; n++)
            {
                int p = front[n], s = chosen[n];
                if (s >= 0)
                {
                    for (int k = 0; k < 3; k++) level.Image[p * 3 + k] = level.Image[s * 3 + k];
                    match[p] = s;
                }
                filled[p] = true;
            }
        }
    }

    /// <summary>Squared differences between the patch around (px, py), counting only its filled pixels, and the source patch at s.</summary>
    private static float PartialDistance(Level level, bool[] filled, int px, int py, int s, float limit)
    {
        int w = level.W, h = level.H, sx = s % w, sy = s / w;
        var img = level.Image;
        float sum = 0;
        for (int dy = -Radius; dy <= Radius; dy++)
        {
            int y = py + dy;
            if (y < 0 || y >= h) continue;
            for (int dx = -Radius; dx <= Radius; dx++)
            {
                int x = px + dx;
                if (x < 0 || x >= w) continue;
                int q = y * w + x;
                if (!filled[q]) continue;
                int r = ((sy + dy) * w + sx + dx) * 3;
                float d0 = img[q * 3] - img[r], d1 = img[q * 3 + 1] - img[r + 1], d2 = img[q * 3 + 2] - img[r + 2];
                sum += d0 * d0 + d1 * d1 + d2 * d2;
            }
            if (sum > limit) return sum;
        }
        return sum;
    }

    private int[] RandomField(Level level, int l)
    {
        var nnf = new int[level.W * level.H];
        var rng = new Random(_seed * 7919 + l);
        foreach (var t in level.Targets) nnf[t] = level.Sources[rng.Next(level.Sources.Length)];
        return nnf;
    }

    private int[] Upsample(int[] coarseNnf, Level coarse, Level fine, int l)
    {
        var nnf = new int[fine.W * fine.H];
        var rng = new Random(_seed * 104729 + l);
        foreach (var t in fine.Targets)
        {
            int tx = t % fine.W, ty = t / fine.W;
            int cx = Math.Clamp(tx / 2, Radius, coarse.W - 1 - Radius), cy = Math.Clamp(ty / 2, Radius, coarse.H - 1 - Radius);
            int c = cy * coarse.W + cx;
            int s = -1;
            if (coarse.IsTarget[c])
            {
                int sc = coarseNnf[c];
                int sx = (sc % coarse.W) * 2 + (tx - cx * 2), sy = (sc / coarse.W) * 2 + (ty - cy * 2);
                if (sx >= Radius && sy >= Radius && sx < fine.W - Radius && sy < fine.H - Radius && fine.SourceOk[sy * fine.W + sx]) s = sy * fine.W + sx;
            }
            nnf[t] = s >= 0 ? s : fine.Sources[rng.Next(fine.Sources.Length)];
        }
        return nnf;
    }

    /// <summary>Sum of squared differences between the patches at two centers, giving up past <paramref name="limit"/>.</summary>
    private static float Distance(Level level, int t, int s, float limit)
    {
        int w = level.W;
        var img = level.Image;
        float sum = 0;
        for (int dy = -Radius; dy <= Radius; dy++)
        {
            int rowT = (t + dy * w - Radius) * 3, rowS = (s + dy * w - Radius) * 3;
            for (int k = 0; k < Size * 3; k++)
            {
                float d = img[rowT + k] - img[rowS + k];
                sum += d * d;
            }
            if (sum > limit) return sum;
        }
        return sum;
    }

    /// <summary>One PatchMatch pass: each target tries its neighbors' matches shifted over, then ever-closer random ones.</summary>
    private void Search(Level level, int[] nnf, float[] dist, int salt, bool reverse)
    {
        int w = level.W, h = level.H, maxRadius = Math.Max(w, h);
        // Rows run side by side; within a row, matches travel along it.
        var rowsOfTargets = level.TargetRows;
        Parallel.For(0, rowsOfTargets.Length, r =>
        {
            var row = rowsOfTargets[reverse ? rowsOfTargets.Length - 1 - r : r];
            uint state = (uint)(_seed * 2654435761u) ^ (uint)(row[0] * 40503) ^ (uint)(salt * 2246822519u) | 1;
            for (int n = 0; n < row.Length; n++)
            {
                int t = row[reverse ? row.Length - 1 - n : n];
                int best = nnf[t];
                float bestDist = dist[t];
                int step = reverse ? 1 : -1;
                // Propagation: the neighbor before (in the row and the row before) has a match; its shift may suit too.
                int across = t + step, down = t + step * w;
                if (across >= 0 && across < nnf.Length && level.IsTarget[across]) Try(level, t, nnf[across] - step, ref best, ref bestDist);
                if (down >= 0 && down < nnf.Length && level.IsTarget[down]) Try(level, t, nnf[down] - step * w, ref best, ref bestDist);
                // Random search around the best so far, the window halving each time, and one anywhere.
                Try(level, t, level.Sources[(int)(Next(ref state) % (uint)level.Sources.Length)], ref best, ref bestDist);
                for (int radius = maxRadius; radius >= 1; radius /= 2)
                {
                    int bx = best % w + (int)(Next(ref state) % (uint)(2 * radius + 1)) - radius;
                    int by = best / w + (int)(Next(ref state) % (uint)(2 * radius + 1)) - radius;
                    if (bx < 0 || by < 0 || bx >= w || by >= h) continue;
                    Try(level, t, by * w + bx, ref best, ref bestDist);
                }
                nnf[t] = best;
                dist[t] = bestDist;
            }
        });
    }

    private static void Try(Level level, int t, int candidate, ref int best, ref float bestDist)
    {
        if (candidate < 0 || candidate >= level.SourceOk.Length || candidate == best || !level.SourceOk[candidate]) return;
        // A candidate must not wrap around the picture's edge.
        int w = level.W, cx = candidate % w;
        if (cx < Radius || cx >= w - Radius) return;
        float d = Distance(level, t, candidate, bestDist);
        if (d < bestDist) { best = candidate; bestDist = d; }
    }

    /// <summary>
    /// Each hole pixel becomes the weighted mean of what the patches over it say it should be. While converging, every
    /// patch has a say (a fall-off scaled to the typical match), which settles the structure. The last vote is
    /// <paramref name="sharp"/>: only the patches matching about as well as the best one over the pixel count, so the
    /// picture's own grain comes through instead of being averaged flat.
    /// </summary>
    private static void Vote(Level level, int[] nnf, float[] dist, bool sharp)
    {
        int w = level.W;
        var sample = level.Targets.Length > 2000 ? level.Targets.Where((_, i) => i % (level.Targets.Length / 2000) == 0).ToArray() : level.Targets;
        var typical = sample.Select(t => dist[t]).OrderBy(d => d).ElementAtOrDefault(sample.Length * 3 / 4);
        float sigma2 = Math.Max(1f, typical / (Size * Size * 3)) * (sharp ? 0.02f : 1f);
        var holes = new List<int>();
        for (int i = 0; i < level.Hole.Length; i++) if (level.Hole[i]) holes.Add(i);
        var result = new float[holes.Count * 3];
        Parallel.For(0, holes.Count, n =>
        {
            int p = holes[n], px = p % w, py = p / w;
            double r = 0, g = 0, b = 0, total = 0;
            // Distances count from the best patch over this pixel, so a sharp vote always has one patch at full weight.
            float best = 0;
            if (sharp)
            {
                best = float.MaxValue;
                for (int dy = -Radius; dy <= Radius; dy++)
                    for (int dx = -Radius; dx <= Radius; dx++)
                    {
                        int tx = px - dx, ty = py - dy;
                        if (tx < Radius || ty < Radius || tx >= w - Radius || ty >= level.H - Radius) continue;
                        int t = ty * w + tx;
                        if (level.IsTarget[t] && dist[t] < best) best = dist[t];
                    }
                if (best == float.MaxValue) best = 0;
            }
            for (int dy = -Radius; dy <= Radius; dy++)
                for (int dx = -Radius; dx <= Radius; dx++)
                {
                    int tx = px - dx, ty = py - dy;
                    if (tx < Radius || ty < Radius || tx >= w - Radius || ty >= level.H - Radius) continue;
                    int t = ty * w + tx;
                    if (!level.IsTarget[t]) continue;
                    int q = nnf[t] + dy * w + dx;
                    // Patches nearer the hole's edge than this pixel count more, deeper ones less.
                    double trust = Math.Pow(EdgeTrust, level.Depth[p] - level.Depth[t]);
                    double weight = Math.Exp(-(dist[t] - best) / (Size * Size * 3) / (2 * sigma2)) * trust + 1e-12;
                    r += level.Image[q * 3] * weight; g += level.Image[q * 3 + 1] * weight; b += level.Image[q * 3 + 2] * weight;
                    total += weight;
                }
            if (total > 0) { result[n * 3] = (float)(r / total); result[n * 3 + 1] = (float)(g / total); result[n * 3 + 2] = (float)(b / total); }
            else { result[n * 3] = level.Image[p * 3]; result[n * 3 + 1] = level.Image[p * 3 + 1]; result[n * 3 + 2] = level.Image[p * 3 + 2]; }
        });
        // The sources lie wholly outside the hole, so writing now changes nothing another pixel read.
        for (int n = 0; n < holes.Count; n++)
        {
            int p = holes[n];
            level.Image[p * 3] = result[n * 3]; level.Image[p * 3 + 1] = result[n * 3 + 1]; level.Image[p * 3 + 2] = result[n * 3 + 2];
        }
    }

    /// <summary>A 3-channel float picture resampled bilinearly.</summary>
    internal static float[] Resample(float[] image, int width, int height, int w, int h)
    {
        var result = new float[w * h * 3];
        double sx = (double)width / w, sy = (double)height / h;
        Parallel.For(0, h, y =>
        {
            double fy = Math.Clamp((y + 0.5) * sy - 0.5, 0, height - 1);
            int y0 = (int)fy, y1 = Math.Min(height - 1, y0 + 1);
            float ay = (float)(fy - y0);
            for (int x = 0; x < w; x++)
            {
                double fx = Math.Clamp((x + 0.5) * sx - 0.5, 0, width - 1);
                int x0 = (int)fx, x1 = Math.Min(width - 1, x0 + 1);
                float ax = (float)(fx - x0);
                for (int k = 0; k < 3; k++)
                {
                    float top = image[(y0 * width + x0) * 3 + k] * (1 - ax) + image[(y0 * width + x1) * 3 + k] * ax;
                    float bottom = image[(y1 * width + x0) * 3 + k] * (1 - ax) + image[(y1 * width + x1) * 3 + k] * ax;
                    result[(y * w + x) * 3 + k] = top * (1 - ay) + bottom * ay;
                }
            }
        });
        return result;
    }

    private static uint Next(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state;
    }
}
