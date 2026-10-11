using Compositor.Core.Localization;
using SkiaSharp;

namespace Compositor.Core.Pixels;

public enum WandSampleSize { Point = 0, ThreeByThree = 1, FiveByFive = 2 }

/// <summary>The Magic Wand's options-bar settings. Mirrors WandSettings in MagicWand.swift.</summary>
public record struct WandSettings
{
    public WandSettings() { }
    /// <summary>How far (0–255) each channel may differ from the sampled color and still be selected.</summary>
    public int Tolerance { get; set; } = 32;
    public WandSampleSize SampleSize { get; set; } = WandSampleSize.Point;
    /// <summary>Only similar pixels connected to the clicked one.</summary>
    public bool Contiguous { get; set; } = true;
    /// <summary>Read the visible composite rather than just the active layer.</summary>
    public bool SampleAllLayers { get; set; } = false;
}

public sealed class WandException : Exception
{
    public WandException(string message) : base(message) { }
}

/// <summary>Selects pixels similar to a clicked one and traces their outline. Port of WandPixels.c.</summary>
public static class MagicWand
{
    private const int East = 1, South = 2, West = 4, North = 8;
    private const long EdgeLimit = 8_000_000;

    private static bool Matches(ReadOnlySpan<byte> rgba, int offset, int r, int g, int b, int a, int tolerance) =>
        Math.Abs(rgba[offset] - r) <= tolerance && Math.Abs(rgba[offset + 1] - g) <= tolerance
        && Math.Abs(rgba[offset + 2] - b) <= tolerance && Math.Abs(rgba[offset + 3] - a) <= tolerance;

    /// <summary>
    /// A mask (255 selected) of the pixels matching the one at (<paramref name="seedX"/>, <paramref name="seedY"/>),
    /// averaged over <paramref name="radius"/> pixels either side. Returns how many were selected.
    /// </summary>
    public static long Mask(ReadOnlySpan<byte> rgba, int width, int height, int stride, int seedX, int seedY, int radius,
                            int tolerance, bool contiguous, byte[] mask)
    {
        Array.Clear(mask);
        if (width <= 0 || height <= 0 || seedX < 0 || seedY < 0 || seedX >= width || seedY >= height) return 0;
        int x0 = Math.Max(0, seedX - radius), x1 = Math.Min(width - 1, seedX + radius);
        int y0 = Math.Max(0, seedY - radius), y1 = Math.Min(height - 1, seedY + radius);
        long sr = 0, sg = 0, sb = 0, sa = 0, samples = 0;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++, samples++)
            {
                int p = y * stride + x * 4;
                sr += rgba[p]; sg += rgba[p + 1]; sb += rgba[p + 2]; sa += rgba[p + 3];
            }
        int r = (int)((sr + samples / 2) / samples), g = (int)((sg + samples / 2) / samples);
        int b = (int)((sb + samples / 2) / samples), a = (int)((sa + samples / 2) / samples);

        long count = 0;
        if (!contiguous)
        {
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    if (Matches(rgba, y * stride + x * 4, r, g, b, a, tolerance)) { mask[y * width + x] = 255; count++; }
            return count;
        }

        // Scanline flood fill: each seed fills its whole run, then pushes one seed per matching run above and below.
        var stack = new Stack<(int X, int Y)>();
        stack.Push((seedX, seedY));
        while (stack.Count > 0)
        {
            var (x, y) = stack.Pop();
            int row = y * stride, outRow = y * width;
            if (mask[outRow + x] != 0 || !Matches(rgba, row + x * 4, r, g, b, a, tolerance)) continue;
            int left = x, right = x;
            while (left > 0 && mask[outRow + left - 1] == 0 && Matches(rgba, row + (left - 1) * 4, r, g, b, a, tolerance)) left--;
            while (right + 1 < width && mask[outRow + right + 1] == 0 && Matches(rgba, row + (right + 1) * 4, r, g, b, a, tolerance)) right++;
            Array.Fill(mask, (byte)255, outRow + left, right - left + 1);
            count += right - left + 1;
            for (int side = 0; side < 2; side++)
            {
                if (side == 0 ? y == 0 : y + 1 >= height) continue;
                int ny = side == 0 ? y - 1 : y + 1;
                int nrow = ny * stride, nout = ny * width;
                bool inRun = false;
                for (int nx = left; nx <= right; nx++)
                {
                    bool candidate = mask[nout + nx] == 0 && Matches(rgba, nrow + nx * 4, r, g, b, a, tolerance);
                    if (candidate && !inRun) stack.Push((nx, ny));
                    inRun = candidate;
                }
            }
        }
        return count;
    }

    private static int TurnRight(int d) => d == North ? East : d << 1;
    private static int TurnLeft(int d) => d == East ? North : d >> 1;

    /// <summary>
    /// The outline of a mask's nonzero pixels along exact pixel edges, as a path (winding rule), or null when empty.
    /// Two loops meeting at a corner are kept apart by turning right.
    /// </summary>
    public static SKPath? Outline(byte[] mask, int width, int height)
    {
        if (width <= 0 || height <= 0 || mask.Length < width * height) return null;
        int stride = width + 1;
        long vertices = (long)stride * (height + 1);
        if (vertices > int.MaxValue) throw new WandException(L.T("选区太大，无法描出轮廓。", "That selection is too large to outline."));
        var edgesOut = new byte[vertices];
        long edges = 0;
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                if (mask[row + x] == 0) continue;
                if (y == 0 || mask[row - width + x] == 0) { edgesOut[y * stride + x] |= East; edges++; }
                if (x + 1 == width || mask[row + x + 1] == 0) { edgesOut[y * stride + x + 1] |= South; edges++; }
                if (y + 1 == height || mask[row + width + x] == 0) { edgesOut[(y + 1) * stride + x + 1] |= West; edges++; }
                if (x == 0 || mask[row + x - 1] == 0) { edgesOut[(y + 1) * stride + x] |= North; edges++; }
            }
            if (edges > EdgeLimit) throw new WandException(L.T("这个选区太复杂，无法描出轮廓。试试换个容差，或打开“连续”。",
            "That selection is too detailed to outline. Try a different Tolerance, or turn on Contiguous."));
        }

        var path = new SKPath { FillType = SKPathFillType.Winding };
        var corners = new List<(int X, int Y)>();
        bool any = false;
        for (int start = 0; start < vertices; start++)
        {
            while (edgesOut[start] != 0)
            {
                corners.Clear();
                int v = start, heading = 0, initial = 0;
                do
                {
                    int bits = edgesOut[v], d;
                    if (heading == 0) d = bits & -bits;
                    else if ((bits & TurnRight(heading)) != 0) d = TurnRight(heading);
                    else if ((bits & heading) != 0) d = heading;
                    else if ((bits & TurnLeft(heading)) != 0) d = TurnLeft(heading);
                    else d = bits & -bits;
                    if (d == 0) break;
                    edgesOut[v] &= (byte)~d;
                    if (d != heading) corners.Add((v % stride, v / stride));
                    if (heading == 0) initial = d;
                    heading = d;
                    v = d == East ? v + 1 : d == West ? v - 1 : d == South ? v + stride : v - stride;
                } while (v != start);
                // The start is a corner unless the loop arrives on the heading it left with.
                if (heading == initial && corners.Count > 0) corners.RemoveAt(0);
                if (corners.Count < 3) continue;
                path.MoveTo(corners[0].X, corners[0].Y);
                for (int i = 1; i < corners.Count; i++) path.LineTo(corners[i].X, corners[i].Y);
                path.Close();
                any = true;
            }
        }
        if (!any)
        {
            path.Dispose();
            return null;
        }
        return path;
    }

    /// <summary>The outline of pixels in <paramref name="rgba"/> matching the one at the point, or null when none match.</summary>
    public static SKPath? Select(ReadOnlySpan<byte> rgba, int width, int height, int stride, int x, int y, WandSettings settings)
    {
        if (x < 0 || y < 0 || x >= width || y >= height) return null;
        var mask = new byte[width * height];
        long count = Mask(rgba, width, height, stride, x, y, (int)settings.SampleSize, Math.Clamp(settings.Tolerance, 0, 255), settings.Contiguous, mask);
        return count <= 0 ? null : Outline(mask, width, height);
    }
}
