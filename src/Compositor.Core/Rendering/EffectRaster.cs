using Compositor.Core.Geometry;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Core.Rendering;

/// <summary>Which effects to draw: everything, only those on the pixels, or only those behind them.</summary>
[Flags]
internal enum EffectParts { Behind = 1, OnPixels = 2, All = Behind | OnPixels }

/// <summary>
/// Draws a layer with its effects. The layer as shown (its pixels through its mask) is drawn into a raster aligned
/// with the layer's own pixels — at the view's resolution, never finer than the layer's — covering what the view shows
/// plus the room the effects reach and read. The effects are made from its alpha there, as LayerEffectsRenderer.render
/// does on the Mac: behind the pixels a drop shadow, an outer glow and an outside stroke; on them, keeping their alpha,
/// a color overlay, an inner glow, an inner shadow and an inside stroke. Strokes use a sliding-window maximum (constant
/// cost whatever the size; Skia's own morphology is far too slow on the CPU), blurs Skia's. Results are cached, so a
/// styled layer costs nothing to redraw while other layers change.
/// </summary>
internal static unsafe class EffectRaster
{
    private const long MaxPixels = 40_000_000;

    /// <summary>The effects that apply, in raster-independent terms (layer pixels).</summary>
    private sealed class Plan
    {
        public LayerEffect? Shadow, Glow, OutsideStroke, Overlay, InnerGlow, InnerShadow, InsideStroke;
        public double Margin, Padding;
        public bool IsEmpty => Shadow is null && Glow is null && OutsideStroke is null && Overlay is null && InnerGlow is null
                               && InnerShadow is null && InsideStroke is null;
    }

    private static Plan MakePlan(LayerEffects effects, EffectParts parts)
    {
        var v = effects.Visible;
        var plan = new Plan();
        if (parts.HasFlag(EffectParts.Behind))
        {
            plan.Shadow = v[LayerEffectKind.Shadow] is { Opacity: > 0 } s ? s : null;
            plan.Glow = v[LayerEffectKind.OuterGlow] is { Opacity: > 0, Size: > 0 } g ? g : null;
            plan.OutsideStroke = v[LayerEffectKind.Stroke] is { Inside: false, Opacity: > 0, Size: > 0 } o ? o : null;
        }
        if (parts.HasFlag(EffectParts.OnPixels))
        {
            plan.Overlay = v[LayerEffectKind.ColorOverlay] is { Opacity: > 0 } c ? c : null;
            plan.InnerGlow = v[LayerEffectKind.InnerGlow] is { Opacity: > 0, Size: > 0 } ig ? ig : null;
            plan.InnerShadow = v[LayerEffectKind.InnerShadow] is { Opacity: > 0 } ins ? ins : null;
            plan.InsideStroke = v[LayerEffectKind.Stroke] is { Inside: true, Opacity: > 0, Size: > 0 } i ? i : null;
        }
        // How far the effects draw beyond the pixels, and how far around what they draw they read.
        double margin = 0, padding = 0;
        if (plan.Shadow is { } shadow) { margin = Math.Max(margin, shadow.Distance + shadow.Blur * 1.5); padding = Math.Max(padding, shadow.Distance + shadow.Blur * 1.5); }
        if (plan.Glow is { } glow) { margin = Math.Max(margin, glow.Size * 1.5); padding = Math.Max(padding, glow.Size * 1.5); }
        if (plan.OutsideStroke is { } outside) { margin = Math.Max(margin, outside.Size); padding = Math.Max(padding, outside.Size); }
        if (plan.InnerGlow is { } innerGlow) padding = Math.Max(padding, innerGlow.Size * 1.5);
        if (plan.InnerShadow is { } inner) padding = Math.Max(padding, inner.Distance + inner.Blur * 1.5);
        if (plan.InsideStroke is { } inside) padding = Math.Max(padding, inside.Size);
        plan.Margin = Math.Ceiling(margin) + 2;
        plan.Padding = Math.Ceiling(padding) + 2;
        return plan;
    }

    /// <summary>Whether these effects draw anything for <paramref name="parts"/>.</summary>
    public static bool HasWork(LayerEffects effects, EffectParts parts) => !MakePlan(effects, parts).IsEmpty;

    /// <summary>How far the effects for <paramref name="parts"/> reach beyond the pixels, in layer pixels.</summary>
    public static double Margin(LayerEffects effects, EffectParts parts) => MakePlan(effects, parts).Margin;

    // MARK: Cache

    private sealed record Key(object Content, LayerEffects Effects, EffectParts Parts, SKMatrix Matrix, SKRectI Clip);

    private sealed class Entry
    {
        public required Key Key;
        public required SKImage Image;
        public required SKMatrix Placement;
        public long Bytes => (long)Image.Width * Image.Height * 4;
    }

    private static readonly object CacheLock = new();
    private static readonly LinkedList<Entry> Cache = new();
    private const long CacheBudget = 192L << 20;

    private static Entry? Lookup(Key key)
    {
        lock (CacheLock)
        {
            for (var node = Cache.First; node is not null; node = node.Next)
            {
                if (!node.Value.Key.Equals(key)) continue;
                Cache.Remove(node);
                Cache.AddFirst(node);
                return node.Value;
            }
        }
        return null;
    }

    private static void Store(Entry entry)
    {
        lock (CacheLock)
        {
            Cache.AddFirst(entry);
            long total = Cache.Sum(e => e.Bytes);
            while (Cache.Count > 1 && (total > CacheBudget || Cache.Count > 24))
            {
                var last = Cache.Last!.Value;
                Cache.RemoveLast();
                total -= last.Bytes;
                // Another thread may be drawing it: let the collector release it.
            }
        }
    }

    // MARK: Drawing

    /// <summary>
    /// Draws the layer (through <paramref name="drawContent"/>, in document coordinates) with its effects onto
    /// <paramref name="canvas"/>. <paramref name="contentKey"/> identifies what the content draws, for the cache; null
    /// when it can change without its identity changing (a stroke being painted).
    /// </summary>
    public static void Draw(SKCanvas canvas, LayerEffects effects, EffectParts parts, Affine pixelToDocument, int gridWidth, int gridHeight,
                            object? contentKey, Action<SKCanvas> drawContent)
    {
        var plan = MakePlan(effects, parts);
        var ctm = canvas.TotalMatrix;
        var toDevice = pixelToDocument.Concat(Affine.FromSK(ctm));
        if (!toDevice.IsInvertible || !pixelToDocument.IsInvertible) return;
        var clip = canvas.DeviceClipBounds;
        if (clip.Width <= 0 || clip.Height <= 0) return;
        if (plan.IsEmpty)
        {
            drawContent(canvas);
            return;
        }
        Key? key = contentKey is null ? null : new Key(contentKey, effects, parts, ctm, clip);
        if (key is not null && Lookup(key) is { } hit)
        {
            DrawResult(canvas, hit.Image, hit.Placement);
            return;
        }

        // The raster: the layer's pixel grid scaled to the view (at most one raster pixel per layer pixel), covering
        // what shows of the layer and its effects, plus what the effects read around that.
        double s = Math.Min(1, Math.Sqrt(Math.Abs(toDevice.Determinant)));
        var grown = new RectD(-plan.Margin, -plan.Margin, gridWidth + 2 * plan.Margin, gridHeight + 2 * plan.Margin);
        var visible = toDevice.Invert().Apply(new RectD(clip.Left, clip.Top, clip.Width, clip.Height));
        var output = visible.Inflate(2 / s, 2 / s).Intersect(grown);
        if (output.IsNull || output.IsEmpty) return;
        var input = output.Inflate(plan.Padding, plan.Padding).Intersect(grown.Inflate(plan.Padding, plan.Padding));
        double area = input.Width * input.Height * s * s;
        if (area > MaxPixels) s *= Math.Sqrt(MaxPixels / area);
        int x0 = (int)Math.Floor(input.X * s), y0 = (int)Math.Floor(input.Y * s);
        int w = Math.Max(1, (int)Math.Ceiling(input.Right * s) - x0), h = Math.Max(1, (int)Math.Ceiling(input.Bottom * s) - y0);
        var pixelToRaster = Affine.Scale(s, s).Concat(Affine.Translation(-x0, -y0));

        using var source = new SKBitmap(RasterImage.ColorInfo(w, h));
        if (source.GetPixels() == IntPtr.Zero) return;
        using (var c = new SKCanvas(source))
        {
            c.Clear(SKColors.Transparent);
            c.SetMatrix(pixelToDocument.Invert().Concat(pixelToRaster).ToSK());
            drawContent(c);
            c.Flush();
        }
        var result = Compose(source, plan, s);
        var placement = pixelToRaster.Invert().Concat(toDevice).ToSK();
        DrawResult(canvas, result, placement);
        if (key is not null) Store(new Entry { Key = key, Image = result, Placement = placement });
        else result.Dispose();
    }

    private static void DrawResult(SKCanvas canvas, SKImage image, SKMatrix rasterToDevice)
    {
        canvas.Save();
        canvas.SetMatrix(rasterToDevice);
        bool aligned = Math.Abs(rasterToDevice.ScaleX - 1) < 1e-6 && Math.Abs(rasterToDevice.ScaleY - 1) < 1e-6
                       && Math.Abs(rasterToDevice.SkewX) < 1e-6 && Math.Abs(rasterToDevice.SkewY) < 1e-6
                       && Math.Abs(rasterToDevice.TransX - Math.Round(rasterToDevice.TransX)) < 1e-4
                       && Math.Abs(rasterToDevice.TransY - Math.Round(rasterToDevice.TransY)) < 1e-4;
        using var paint = new SKPaint { IsAntialias = !aligned };
        canvas.DrawImage(image, 0, 0, aligned ? new SKSamplingOptions(SKFilterMode.Nearest) : new SKSamplingOptions(SKFilterMode.Linear), paint);
        canvas.Restore();
    }

    // MARK: Making the effects

    /// <summary>The layer with its effects, from its pixels in <paramref name="source"/> (raster pixels are <paramref name="s"/> layer pixels).</summary>
    private static SKImage Compose(SKBitmap source, Plan plan, double s)
    {
        int w = source.Width, h = source.Height, n = w * h, stride = source.RowBytes;
        byte* src = (byte*)source.GetPixels();
        var alpha = new byte[n];
        fixed (byte* a = alpha)
        {
            byte* ap = a;
            Parallel.For(0, h, y =>
            {
                byte* row = src + (long)y * stride;
                for (int x = 0; x < w; x++) ap[y * w + x] = row[x * 4 + 3];
            });
        }

        byte[]? shadow = null, glow = null, ring = null, innerGlow = null, innerShadow = null, inside = null;
        int Reach(LayerEffect e) => Math.Max(1, (int)Math.Round(e.Size * s));
        (int, int) Shift(LayerEffect e) { var (dx, dy) = e.Offset; return ((int)Math.Round(dx * s), (int)Math.Round(dy * s)); }
        if (plan.Shadow is { } sh) shadow = Shifted(Blurred(alpha, w, h, sh.Blur / 2 * s), w, h, Shift(sh));
        if (plan.Glow is { } gl) glow = Blurred(alpha, w, h, gl.Size / 2 * s);
        if (plan.OutsideStroke is { } os) ring = Extreme(alpha, w, h, Reach(os), max: true);
        if (plan.InnerGlow is { } ig) innerGlow = Blurred(alpha, w, h, ig.Size / 2 * s);
        if (plan.InnerShadow is { } ins) innerShadow = Shifted(Blurred(alpha, w, h, ins.Blur / 2 * s), w, h, Shift(ins));
        if (plan.InsideStroke is { } st) inside = Extreme(alpha, w, h, Reach(st), max: false);

        static (float R, float G, float B, float A) Paint(LayerEffect? e) =>
            e is null ? default : ((float)e.Red * 255, (float)e.Green * 255, (float)e.Blue * 255, (float)Math.Clamp(e.Opacity, 0, 1));
        var cShadow = Paint(plan.Shadow); var cGlow = Paint(plan.Glow); var cRing = Paint(plan.OutsideStroke);
        var cOverlay = Paint(plan.Overlay); var cInnerGlow = Paint(plan.InnerGlow); var cInner = Paint(plan.InnerShadow);
        var cInside = Paint(plan.InsideStroke);

        var output = new SKBitmap(RasterImage.ColorInfo(w, h));
        byte* dst = (byte*)output.GetPixels();
        int outStride = output.RowBytes;
        Parallel.For(0, h, y =>
        {
            byte* inRow = src + (long)y * stride, outRow = dst + (long)y * outStride;
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                byte* p = inRow + x * 4;
                // Behind: shadow, then glow, each laid over what is there.
                float r = 0, g = 0, b = 0, a = 0;
                if (shadow is not null && shadow[i] != 0)
                {
                    float k = shadow[i] / 255f * cShadow.A;
                    r = cShadow.R * k; g = cShadow.G * k; b = cShadow.B * k; a = k;
                }
                float pa = p[3] / 255f;
                if (glow is not null && glow[i] != 0)
                {
                    float k = glow[i] / 255f * (1 - pa) * cGlow.A;
                    r = cGlow.R * k + r * (1 - k); g = cGlow.G * k + g * (1 - k); b = cGlow.B * k + b * (1 - k); a = k + a * (1 - k);
                }
                // The pixels, recolored in place (source-atop: they keep their alpha).
                float pr = p[0], pg = p[1], pb = p[2];
                if (pa > 0)
                {
                    if (plan.Overlay is not null) Recolor(ref pr, ref pg, ref pb, pa, cOverlay, cOverlay.A);
                    if (innerGlow is not null) Recolor(ref pr, ref pg, ref pb, pa, cInnerGlow, cInnerGlow.A * (1 - innerGlow[i] / 255f));
                    if (innerShadow is not null) Recolor(ref pr, ref pg, ref pb, pa, cInner, cInner.A * (1 - innerShadow[i] / 255f));
                    if (inside is not null) Recolor(ref pr, ref pg, ref pb, pa, cInside, cInside.A * (1 - inside[i] / 255f));
                }
                float qa = pa;
                if (ring is not null && ring[i] != 0)
                {
                    // The outside stroke and the pixels add up: each covers what the other leaves, with no seam.
                    float k = ring[i] / 255f * (1 - pa) * cRing.A;
                    pr += cRing.R * k; pg += cRing.G * k; pb += cRing.B * k; qa += k;
                }
                // The (stroked) pixels over what is behind them.
                float rest = 1 - qa;
                r = pr + r * rest; g = pg + g * rest; b = pb + b * rest; a = qa + a * rest;
                byte* o = outRow + x * 4;
                byte oa = (byte)Math.Clamp(a * 255 + 0.5f, 0, 255);
                o[3] = oa;
                o[0] = (byte)Math.Min(oa, Math.Clamp(r + 0.5f, 0, 255));
                o[1] = (byte)Math.Min(oa, Math.Clamp(g + 0.5f, 0, 255));
                o[2] = (byte)Math.Min(oa, Math.Clamp(b + 0.5f, 0, 255));
            }
        });
        output.NotifyPixelsChanged();
        output.SetImmutable();
        var image = SKImage.FromBitmap(output);
        output.Dispose();
        return image;
    }

    /// <summary>Moves a premultiplied color toward <paramref name="c"/> by <paramref name="amount"/>, keeping its alpha.</summary>
    private static void Recolor(ref float r, ref float g, ref float b, float alpha, (float R, float G, float B, float A) c, float amount)
    {
        if (amount <= 0) return;
        r = c.R * alpha * amount + r * (1 - amount);
        g = c.G * alpha * amount + g * (1 - amount);
        b = c.B * alpha * amount + b * (1 - amount);
    }

    /// <summary>A Gaussian blur of an alpha plane (σ in raster pixels), treating everything beyond it as clear.</summary>
    private static byte[] Blurred(byte[] plane, int w, int h, double sigma)
    {
        var result = new byte[plane.Length];
        if (sigma <= 0.05) { Array.Copy(plane, result, plane.Length); return result; }
        var info = new SKImageInfo(w, h, SKColorType.Alpha8, SKAlphaType.Premul);
        fixed (byte* from = plane)
        fixed (byte* to = result)
        {
            using var image = SKImage.FromPixels(new SKPixmap(info, (IntPtr)from, w));
            using var surface = SKSurface.Create(info, (IntPtr)to, w);
            using var filter = SKImageFilter.CreateBlur((float)sigma, (float)sigma, SKShaderTileMode.Decal);
            using var paint = new SKPaint { ImageFilter = filter };
            surface.Canvas.Clear(SKColors.Transparent);
            surface.Canvas.DrawImage(image, 0, 0, paint);
            surface.Canvas.Flush();
        }
        return result;
    }

    /// <summary>The plane moved by (dx, dy): each pixel takes the value from that far back; clear beyond the edge.</summary>
    private static byte[] Shifted(byte[] plane, int w, int h, (int Dx, int Dy) offset)
    {
        var (dx, dy) = offset;
        if (dx == 0 && dy == 0) return plane;
        var result = new byte[plane.Length];
        Parallel.For(0, h, y =>
        {
            int sy = y - dy;
            if (sy < 0 || sy >= h) return;
            int from = Math.Max(0, dx), to = Math.Min(w, w + dx);
            if (to > from) Array.Copy(plane, sy * w + (from - dx), result, y * w + from, to - from);
        });
        return result;
    }

    /// <summary>
    /// The largest (or smallest) value within <paramref name="reach"/> pixels each way — a square reach, as on the Mac,
    /// so a rectangle's stroke keeps square corners — with everything beyond the plane counted as clear. Van Herk /
    /// Gil-Werman: constant work per pixel whatever the reach.
    /// </summary>
    internal static byte[] Extreme(byte[] plane, int w, int h, int reach, bool max)
    {
        var pass = new byte[plane.Length];
        var result = new byte[plane.Length];
        Parallel.For(0, h, () => new Window(w + 2 * reach), (y, _, window) =>
        {
            window.Run(plane, y * w, 1, w, reach, max, pass, y * w, 1);
            return window;
        }, _ => { });
        Parallel.For(0, w, () => new Window(h + 2 * reach), (x, _, window) =>
        {
            window.Run(pass, x, w, h, reach, max, result, x, w);
            return window;
        }, _ => { });
        return result;
    }

    private sealed class Window
    {
        private readonly byte[] _line, _prefix, _suffix;

        public Window(int length)
        {
            _line = new byte[length];
            _prefix = new byte[length];
            _suffix = new byte[length];
        }

        public void Run(byte[] input, int start, int step, int count, int reach, bool max, byte[] output, int outStart, int outStep)
        {
            int k = 2 * reach + 1, length = count + 2 * reach;
            var line = _line;
            Array.Clear(line, 0, length);
            for (int i = 0; i < count; i++) line[reach + i] = input[start + i * step];
            for (int i = 0; i < length; i++)
                _prefix[i] = i % k == 0 ? line[i] : max ? Math.Max(_prefix[i - 1], line[i]) : Math.Min(_prefix[i - 1], line[i]);
            for (int i = length - 1; i >= 0; i--)
                _suffix[i] = i % k == k - 1 || i == length - 1 ? line[i] : max ? Math.Max(_suffix[i + 1], line[i]) : Math.Min(_suffix[i + 1], line[i]);
            for (int i = 0; i < count; i++)
            {
                // The window [i, i + 2·reach] of the padded line, centered on pixel i.
                byte a = _suffix[i], b = _prefix[i + 2 * reach];
                output[outStart + i * outStep] = max ? Math.Max(a, b) : Math.Min(a, b);
            }
        }
    }
}
