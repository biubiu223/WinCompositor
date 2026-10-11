using System.Runtime.InteropServices;
using Compositor.Core.Geometry;
using Compositor.Core.Localization;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Core.Painting;

/// <summary>The Smear tool's modes: Liquify pushes pixels along, Blur softens, Smudge drags color.</summary>
public enum SmearMode { Liquify, Blur, Smudge }

public static class SmearModes
{
    public static string ToDisplayName(this SmearMode mode) => mode switch
    {
        SmearMode.Liquify => L.T("液化", "Liquify"),
        SmearMode.Blur => L.T("模糊", "Blur"),
        _ => L.T("涂抹", "Smudge"),
    };

    public static string ToName(this SmearMode mode) => mode.ToString();
}

/// <summary>
/// A Smudge or Liquify stroke in progress. It works on the active layer as the canvas shows it, at document size,
/// changing it dab by dab; the canvas shows that working copy in place of the layer. When the stroke ends the result is
/// painted into the layer's own pixels along the stroke's path. Mirrors WarpStroke in SmudgeLiquify.swift (its CPU path).
/// </summary>
public sealed class WarpStroke : IDisposable
{
    public Layer Layer { get; }
    public SmearMode Mode { get; }
    public double Diameter { get; }
    private readonly double _hardness, _strength;
    public int Width { get; }
    public int Height { get; }
    private readonly byte[] _pixels;
    private GCHandle _pin;
    private SKImage? _preview;
    /// <summary>Every dab's center, for painting the result into the layer.</summary>
    public List<PointD> Points { get; } = new();
    private PointD? _last;
    private float[] _carried = Array.Empty<float>();
    private float[] _scratch = Array.Empty<float>();
    public int Revision { get; private set; }

    public WarpStroke(Layer layer, RasterImage image, LayerTransform transform, int width, int height, SmearMode mode, BrushSettings settings)
    {
        Layer = layer;
        Mode = mode;
        Diameter = Math.Max(2, settings.Diameter);
        _hardness = Math.Clamp(settings.Hardness, 0, 0.98);
        _strength = Math.Clamp(settings.Opacity, 0.01, 1);
        Width = width;
        Height = height;
        _pixels = new byte[(long)width * height * 4 <= int.MaxValue ? width * height * 4 : throw new InvalidOperationException(L.T("画布太大。", "The canvas is too large."))];
        _pin = GCHandle.Alloc(_pixels, GCHandleType.Pinned);
        var info = RasterImage.ColorInfo(width, height);
        using (var surface = SKSurface.Create(info, _pin.AddrOfPinnedObject(), width * 4))
        {
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            canvas.Concat(transform.PixelToDocument(image.Width, image.Height).ToSK());
            using var paint = new SKPaint { IsAntialias = true };
            canvas.DrawImage(image.Image, new SKRect(0, 0, image.Width, image.Height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
            canvas.Flush();
        }
    }

    /// <summary>The working copy (document-sized), shared with the stroke: it changes as the stroke goes on.</summary>
    public SKImage Preview => _preview ??= SKImage.FromPixels(new SKPixmap(RasterImage.ColorInfo(Width, Height), _pin.AddrOfPinnedObject(), Width * 4));

    public byte[] Result => _pixels;

    private int Radius => (int)Math.Ceiling(Diameter / 2);

    /// <summary>How much a dab moves pixels at a distance <paramref name="u"/> (0 center, 1 rim) from its center.</summary>
    private float Weight(float u)
    {
        if (u >= 1) return 0;
        float h = (float)_hardness;
        if (u <= h) return 1;
        float t = (1 - u) / (1 - h);
        return t * t * (3 - 2 * t);
    }

    /// <summary>Continues the stroke to <paramref name="point"/>, dabbing along the way.</summary>
    public void Append(PointD point)
    {
        if (!point.IsFinite) return;
        if (_last is not { } from)
        {
            _last = point;
            if (Mode == SmearMode.Smudge) PickUp(point);
            return;
        }
        double distance = from.DistanceTo(point);
        // Smudge drags the pixels a pixel at a time so the steps run together into one smear, as Photoshop's does.
        double spacing = Math.Max(1, Diameter * (Mode == SmearMode.Smudge ? 0.005 : 0.025));
        if (distance < spacing) return;
        int steps = (int)Math.Ceiling(distance / spacing);
        var previous = from;
        for (int step = 1; step <= steps; step++)
        {
            double t = step / (double)steps;
            var next = new PointD(from.X + (point.X - from.X) * t, from.Y + (point.Y - from.Y) * t);
            if (Mode == SmearMode.Smudge) Smudge(next); else Push(previous, next);
            Points.Add(next);
            previous = next;
        }
        _last = point;
        Revision++;
    }

    private void PickUp(PointD center)
    {
        int r = Radius, side = 2 * r + 1;
        _carried = new float[side * side * 4];
        int cx = (int)Math.Round(center.X), cy = (int)Math.Round(center.Y);
        for (int dy = -r; dy <= r; dy++)
        {
            int y = cy + dy;
            if (y < 0 || y >= Height) continue;
            for (int dx = -r; dx <= r; dx++)
            {
                int x = cx + dx;
                if (x < 0 || x >= Width) continue;
                int p = (y * Width + x) * 4, c = ((dy + r) * side + dx + r) * 4;
                for (int k = 0; k < 4; k++) _carried[c + k] = _pixels[p + k];
            }
        }
    }

    private void Smudge(PointD center)
    {
        int r = Radius, side = 2 * r + 1;
        int cx = (int)Math.Round(center.X), cy = (int)Math.Round(center.Y);
        float keep = (float)_strength, invR = 1 / (float)(Diameter / 2);
        for (int dy = -r; dy <= r; dy++)
        {
            int y = cy + dy;
            if (y < 0 || y >= Height) continue;
            for (int dx = -r; dx <= r; dx++)
            {
                int x = cx + dx;
                if (x < 0 || x >= Width) continue;
                float w = Weight(MathF.Sqrt(dx * dx + dy * dy) * invR);
                if (w <= 0) continue;
                int p = (y * Width + x) * 4, c = ((dy + r) * side + dx + r) * 4;
                for (int k = 0; k < 4; k++)
                {
                    float under = _pixels[p + k];
                    // What was under the brush at the last dab, laid down here at the smudge's strength; the brush
                    // then carries what it just left, and nothing older (no ghost copies along the stroke).
                    float painted = under + (_carried[c + k] - under) * w * keep;
                    _pixels[p + k] = (byte)Math.Clamp(MathF.Round(painted), 0, 255);
                    _carried[c + k] = painted;
                }
            }
        }
    }

    /// <summary>Forward warp: pixels under the brush move with it, most at its center, fading to none at its rim.</summary>
    private void Push(PointD a, PointD b)
    {
        int r = Radius;
        float mx = (float)((b.X - a.X) * _strength), my = (float)((b.Y - a.Y) * _strength);
        int margin = (int)Math.Ceiling(Math.Max(Math.Abs(mx), Math.Abs(my))) + 2;
        int cx = (int)Math.Round(b.X), cy = (int)Math.Round(b.Y);
        int x0 = Math.Max(0, cx - r - margin), x1 = Math.Min(Width - 1, cx + r + margin);
        int y0 = Math.Max(0, cy - r - margin), y1 = Math.Min(Height - 1, cy + r + margin);
        if (x0 > x1 || y0 > y1) return;
        int cw = x1 - x0 + 1, ch = y1 - y0 + 1;
        if (cw < 2 || ch < 2) return;
        if (_scratch.Length < cw * ch * 4) _scratch = new float[cw * ch * 4];
        // A copy of the area as it was before this dab, which the dab samples from.
        for (int y = 0; y < ch; y++)
            for (int x = 0; x < cw; x++)
            {
                int p = ((y + y0) * Width + x + x0) * 4, s = (y * cw + x) * 4;
                for (int k = 0; k < 4; k++) _scratch[s + k] = _pixels[p + k];
            }
        float invR = 1 / (float)(Diameter / 2);
        for (int dy = -r; dy <= r; dy++)
        {
            int y = cy + dy;
            if (y < y0 || y > y1) continue;
            for (int dx = -r; dx <= r; dx++)
            {
                int x = cx + dx;
                if (x < x0 || x > x1) continue;
                float w = Weight(MathF.Sqrt(dx * dx + dy * dy) * invR);
                if (w <= 0) continue;
                // Bilinear sample of the old pixels, from behind the brush's travel.
                float sx = Math.Clamp(x - x0 - mx * w, 0, cw - 1), sy = Math.Clamp(y - y0 - my * w, 0, ch - 1);
                int ix = Math.Min(cw - 2, (int)sx), iy = Math.Min(ch - 2, (int)sy);
                float fx = sx - ix, fy = sy - iy;
                int p = (y * Width + x) * 4;
                int s00 = (iy * cw + ix) * 4, s10 = s00 + 4, s01 = s00 + cw * 4, s11 = s01 + 4;
                for (int k = 0; k < 4; k++)
                {
                    float top = _scratch[s00 + k] + (_scratch[s10 + k] - _scratch[s00 + k]) * fx;
                    float bottom = _scratch[s01 + k] + (_scratch[s11 + k] - _scratch[s01 + k]) * fx;
                    _pixels[p + k] = (byte)Math.Clamp(MathF.Round(top + (bottom - top) * fy), 0, 255);
                }
            }
        }
    }

    public void Dispose()
    {
        // The preview image reads the pinned pixels; it goes first, and the collector frees the array.
        _preview?.Dispose();
        _preview = null;
        if (_pin.IsAllocated) _pin.Free();
    }
}
