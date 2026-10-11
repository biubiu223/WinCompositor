using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Core.Rendering;

/// <summary>Small per-pixel operations on premultiplied RGBA (the ports of LayerPixels/BrushPixels in the Mac app).</summary>
public static class PixelOps
{
    /// <summary>Keeps each pixel's alpha aside and makes the pixel opaque with its unpremultiplied color.</summary>
    public static byte[] ExtractAlphaAndMakeOpaque(SKBitmap bitmap)
    {
        int w = bitmap.Width, h = bitmap.Height, stride = bitmap.RowBytes;
        var alpha = new byte[w * h];
        var span = bitmap.GetPixelSpan();
        for (int y = 0; y < h; y++)
        {
            int row = y * stride;
            for (int x = 0; x < w; x++)
            {
                int p = row + x * 4;
                byte a = span[p + 3];
                alpha[y * w + x] = a;
                if (a == 255) continue;
                if (a == 0)
                {
                    span[p] = span[p + 1] = span[p + 2] = 0;
                }
                else
                {
                    span[p] = (byte)Math.Min(255, (span[p] * 255 + a / 2) / a);
                    span[p + 1] = (byte)Math.Min(255, (span[p + 1] * 255 + a / 2) / a);
                    span[p + 2] = (byte)Math.Min(255, (span[p + 2] * 255 + a / 2) / a);
                }
                span[p + 3] = 255;
            }
        }
        bitmap.NotifyPixelsChanged();
        return alpha;
    }

    /// <summary>Puts back the alpha <see cref="ExtractAlphaAndMakeOpaque"/> kept, premultiplying the colors by it.</summary>
    public static void RestoreAlpha(SKBitmap bitmap, byte[] alpha)
    {
        int w = bitmap.Width, h = bitmap.Height, stride = bitmap.RowBytes;
        var span = bitmap.GetPixelSpan();
        for (int y = 0; y < h; y++)
        {
            int row = y * stride;
            for (int x = 0; x < w; x++)
            {
                int p = row + x * 4;
                int original = alpha[y * w + x];
                int current = span[p + 3];
                // Composite alpha is 255 after drawing over an opaque surface; scale the colors to the kept alpha.
                if (current == 0) { span[p] = span[p + 1] = span[p + 2] = span[p + 3] = 0; continue; }
                int scale = original * 255 / current;
                span[p] = (byte)Math.Min(original, (span[p] * scale + 127) / 255);
                span[p + 1] = (byte)Math.Min(original, (span[p + 1] * scale + 127) / 255);
                span[p + 2] = (byte)Math.Min(original, (span[p + 2] * scale + 127) / 255);
                span[p + 3] = (byte)original;
            }
        }
        bitmap.NotifyPixelsChanged();
    }

    /// <summary>The bounds of every pixel with nonzero alpha; empty when all are clear.</summary>
    public static RectI AlphaBounds(ReadOnlySpan<byte> rgba, int width, int height, int stride)
    {
        int minX = width, minY = height, maxX = -1, maxY = -1;
        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            int first = -1, last = -1;
            for (int x = 0; x < width; x++)
            {
                if (rgba[row + x * 4 + 3] != 0) { first = x; break; }
            }
            if (first < 0) continue;
            for (int x = width - 1; x >= first; x--)
            {
                if (rgba[row + x * 4 + 3] != 0) { last = x; break; }
            }
            if (first < minX) minX = first;
            if (last > maxX) maxX = last;
            if (y < minY) minY = y;
            maxY = y;
        }
        return maxX < 0 ? new RectI(0, 0, 0, 0) : new RectI(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    public static RectI AlphaBounds(RasterImage image)
    {
        using var pixmap = image.PeekPixels();
        return AlphaBounds(pixmap.GetPixelSpan(), image.Width, image.Height, pixmap.RowBytes);
    }

    /// <summary>A copy of <paramref name="rect"/> of an image's pixels.</summary>
    public static RasterImage Crop(RasterImage image, RectI rect, string? name = null)
    {
        using var pixmap = image.PeekPixels();
        int bpp = image.IsMask ? 1 : 4;
        var source = pixmap.GetPixelSpan();
        var bytes = new byte[rect.Width * rect.Height * bpp];
        for (int y = 0; y < rect.Height; y++)
        {
            source.Slice((rect.Y + y) * pixmap.RowBytes + rect.X * bpp, rect.Width * bpp).CopyTo(bytes.AsSpan(y * rect.Width * bpp));
        }
        return image.IsMask
            ? RasterImage.FromMask(bytes, rect.Width, rect.Height, name ?? image.Name)
            : RasterImage.FromRgba(bytes, rect.Width, rect.Height, name ?? image.Name);
    }

    /// <summary>Inverts colors (keeping alpha) of premultiplied RGBA, or coverage of a mask.</summary>
    public static RasterImage Invert(RasterImage image, SelectionClip? clip = null, Geometry.Affine? pixelToDocument = null)
    {
        var bytes = image.CopyPixels();
        int w = image.Width, h = image.Height;
        bool mask = image.IsMask;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double amount = 1;
                if (clip is not null && pixelToDocument is { } map)
                {
                    var doc = map.Apply(x + 0.5, y + 0.5);
                    amount = clip.At(doc.X, doc.Y);
                    if (amount <= 0) continue;
                }
                if (mask)
                {
                    int i = y * w + x;
                    byte inv = (byte)(255 - bytes[i]);
                    bytes[i] = (byte)Math.Round(bytes[i] + (inv - bytes[i]) * amount);
                    continue;
                }
                int p = (y * w + x) * 4;
                int a = bytes[p + 3];
                if (a == 0) continue;
                for (int c = 0; c < 3; c++)
                {
                    // Inverting straight color c is (a − premultiplied c) premultiplied.
                    int inverted = a - bytes[p + c];
                    bytes[p + c] = (byte)Math.Round(bytes[p + c] + (inverted - bytes[p + c]) * amount);
                }
            }
        }
        return mask ? RasterImage.FromMask(bytes, w, h, image.Name) : RasterImage.FromRgba(bytes, w, h, image.Name);
    }

    /// <summary>After resampling with a filter that rings (bicubic), keeps each color channel within its pixel's alpha.</summary>
    public static void ClampPremultiplied(SKBitmap bitmap)
    {
        if (bitmap.ColorType != SKColorType.Rgba8888 && bitmap.ColorType != SKColorType.Bgra8888) return;
        var span = bitmap.GetPixelSpan();
        for (int p = 0; p + 3 < span.Length; p += 4)
        {
            byte a = span[p + 3];
            if (span[p] > a) span[p] = a;
            if (span[p + 1] > a) span[p + 1] = a;
            if (span[p + 2] > a) span[p + 2] = a;
        }
        bitmap.NotifyPixelsChanged();
    }

    /// <summary>Unpremultiplied color of one premultiplied pixel.</summary>
    public static (byte R, byte G, byte B, byte A) Unpremultiply(byte r, byte g, byte b, byte a)
    {
        if (a == 0) return (0, 0, 0, 0);
        if (a == 255) return (r, g, b, a);
        return ((byte)Math.Min(255, (r * 255 + a / 2) / a), (byte)Math.Min(255, (g * 255 + a / 2) / a),
                (byte)Math.Min(255, (b * 255 + a / 2) / a), a);
    }
}
