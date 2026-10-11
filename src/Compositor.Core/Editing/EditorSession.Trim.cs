using Compositor.Core.Localization;
using Compositor.Core.Model;
using Compositor.Core.Rendering;
using SkiaSharp;

namespace Compositor.Core.Editing;

/// <summary>What Image ▸ Trim measures against: clear pixels, or the color of a corner pixel.</summary>
public enum TrimBasedOn { TransparentPixels, TopLeftPixelColor, BottomRightPixelColor }

public sealed record TrimOptions(TrimBasedOn BasedOn = TrimBasedOn.TransparentPixels, bool Top = true, bool Bottom = true,
                                 bool Left = true, bool Right = true, byte Tolerance = 0)
{
    public bool TrimsAny => Top || Bottom || Left || Right;
}

/// <summary>Image ▸ Trim: the canvas cut down to its content. Mirrors ImageTrim.swift.</summary>
public static class ImageTrim
{
    /// <summary>The rect of a premultiplied RGBA image to keep, or null when nothing would remain.</summary>
    public static RectI? TrimRect(byte[] rgba, int width, int height, TrimOptions options)
    {
        if (!options.TrimsAny || width <= 0 || height <= 0) return null;
        RectI found;
        if (options.BasedOn == TrimBasedOn.TransparentPixels)
        {
            found = PixelOps.AlphaBounds(rgba, width, height, width * 4);
            if (found.IsEmpty) return null;
        }
        else
        {
            var (sx, sy) = options.BasedOn == TrimBasedOn.TopLeftPixelColor ? (0, 0) : (width - 1, height - 1);
            int s = (sy * width + sx) * 4, tolerance = options.Tolerance;
            bool Matches(int x, int y)
            {
                int p = (y * width + x) * 4;
                return Math.Abs(rgba[p] - rgba[s]) <= tolerance && Math.Abs(rgba[p + 1] - rgba[s + 1]) <= tolerance
                    && Math.Abs(rgba[p + 2] - rgba[s + 2]) <= tolerance && Math.Abs(rgba[p + 3] - rgba[s + 3]) <= tolerance;
            }
            int left = width, right = 0, top = height, bottom = 0;
            for (int y = 0; y < height; y++)
            {
                int first = 0;
                while (first < width && Matches(first, y)) first++;
                if (first == width) continue;
                int last = width;
                while (last > first && Matches(last - 1, y)) last--;
                left = Math.Min(left, first);
                right = Math.Max(right, last);
                top = Math.Min(top, y);
                bottom = y + 1;
            }
            if (right == 0) return null;
            found = new RectI(left, top, right - left, bottom - top);
        }
        int minX = options.Left ? found.X : 0, minY = options.Top ? found.Y : 0;
        int maxX = options.Right ? found.Right : width, maxY = options.Bottom ? found.Bottom : height;
        return maxX > minX && maxY > minY ? new RectI(minX, minY, maxX - minX, maxY - minY) : null;
    }
}

public sealed partial class EditorSession
{
    public bool CanTrim => CanEditLayers && Document is not null;

    /// <summary>Image ▸ Trim: the canvas cut to the composite's content. False when nothing would remain or nothing changes.</summary>
    public bool Trim(TrimOptions options)
    {
        CommitTransform();
        if (!CanTrim || Document is not { } document) return false;
        using var image = DocumentRenderer.RenderToImage(document with { Selection = null });
        using var pixmap = image.PeekPixels();
        var bytes = new byte[document.Width * document.Height * 4];
        var span = pixmap.GetPixelSpan();
        for (int y = 0; y < document.Height; y++) span.Slice(y * pixmap.RowBytes, document.Width * 4).CopyTo(bytes.AsSpan(y * document.Width * 4));
        if (ImageTrim.TrimRect(bytes, document.Width, document.Height, options) is not { } rect)
        {
            ShowError(L.T("无法裁切", "Couldn’t trim image"), L.T("裁切后不会剩下任何内容。", "No content remained after trimming."));
            return false;
        }
        if (rect == new RectI(0, 0, document.Width, document.Height)) return false;
        ResizeCanvas(rect.Width, rect.Height, contentOffset: new Geometry.PointD(-rect.X, -rect.Y), name: "Trim");
        return true;
    }
}
