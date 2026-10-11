using System.Runtime.InteropServices;
using SkiaSharp;

namespace Compositor.Core.Model;

/// <summary>
/// Immutable pixels shared by the document, its history and the renderers. A color image is 8-bit sRGB,
/// premultiplied RGBA; a mask is 8-bit coverage (white reveals, black hides) held as an alpha-only image so
/// it can be drawn straight into a layer with Dst-In. Equality is identity, as CGImage's is in the original.
/// </summary>
public sealed class RasterImage
{
    public static readonly SKColorSpace Srgb = SKColorSpace.CreateSrgb();
    public const int ThumbnailSide = 96;

    private SKImage? _thumbnail;
    private readonly long _pressure;

    public SKImage Image { get; }
    public int Width { get; }
    public int Height { get; }
    public bool IsMask { get; }
    public string Name { get; }

    private RasterImage(SKImage image, bool isMask, string name)
    {
        Image = image;
        Width = image.Width;
        Height = image.Height;
        IsMask = isMask;
        Name = name;
        _pressure = (long)Width * Height * (isMask ? 1 : 4);
        if (_pressure > 0) GC.AddMemoryPressure(_pressure);
    }

    ~RasterImage()
    {
        if (_pressure > 0) GC.RemoveMemoryPressure(_pressure);
    }

    public SizeDims Size => new(Width, Height);
    public long PixelCount => (long)Width * Height;
    /// <summary>A 1×1 (or 2×2) mask stretched over its layer: reveal-all or hide-all.</summary>
    public bool IsUniform => Width <= 2 && Height <= 2;

    public static SKImageInfo ColorInfo(int width, int height) =>
        new(width, height, SKColorType.Rgba8888, SKAlphaType.Premul, Srgb);

    public static SKImageInfo MaskInfo(int width, int height) =>
        new(width, height, SKColorType.Alpha8, SKAlphaType.Premul);

    /// <summary>Copies premultiplied RGBA bytes (tightly packed unless <paramref name="rowBytes"/> says otherwise).</summary>
    public static RasterImage FromRgba(ReadOnlySpan<byte> pixels, int width, int height, string name = "Layer", int rowBytes = 0)
    {
        var info = ColorInfo(width, height);
        var image = SKImage.FromPixelCopy(info, pixels, rowBytes == 0 ? width * 4 : rowBytes)
                    ?? throw new InvalidOperationException("Could not create image.");
        return new RasterImage(image, false, name);
    }

    /// <summary>Copies 8-bit mask coverage.</summary>
    public static RasterImage FromMask(ReadOnlySpan<byte> coverage, int width, int height, string name = "Layer Mask", int rowBytes = 0)
    {
        var info = MaskInfo(width, height);
        var image = SKImage.FromPixelCopy(info, coverage, rowBytes == 0 ? width : rowBytes)
                    ?? throw new InvalidOperationException("Could not create mask.");
        return new RasterImage(image, true, name);
    }

    /// <summary>Takes a copy of a bitmap's pixels, converting to the color or mask layout as needed.</summary>
    public static RasterImage FromBitmap(SKBitmap bitmap, bool isMask = false, string name = "Layer")
    {
        var target = isMask ? MaskInfo(bitmap.Width, bitmap.Height) : ColorInfo(bitmap.Width, bitmap.Height);
        if (bitmap.ColorType == target.ColorType && bitmap.AlphaType == target.AlphaType)
        {
            var image = SKImage.FromPixelCopy(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes)
                        ?? throw new InvalidOperationException("Could not create image.");
            return new RasterImage(image, isMask, name);
        }
        using var converted = new SKBitmap(target);
        if (!bitmap.CopyTo(converted, target.ColorType))
        {
            using var canvas = new SKCanvas(converted);
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(bitmap, 0, 0);
        }
        var copy = SKImage.FromPixelCopy(converted.Info, converted.GetPixels(), converted.RowBytes)
                   ?? throw new InvalidOperationException("Could not create image.");
        return new RasterImage(copy, isMask, name);
    }

    /// <summary>Wraps an image that is already raster-backed in the right layout, otherwise copies it into one.</summary>
    public static RasterImage FromImage(SKImage image, bool isMask = false, string name = "Layer")
    {
        var info = isMask ? MaskInfo(image.Width, image.Height) : ColorInfo(image.Width, image.Height);
        if (!image.IsTextureBacked && image.ColorType == info.ColorType && image.AlphaType == info.AlphaType && image.PeekPixels() is { } pixmap)
        {
            using (pixmap)
            {
                var copy = SKImage.FromPixelCopy(pixmap.Info, pixmap.GetPixels(), pixmap.RowBytes)
                           ?? throw new InvalidOperationException("Could not create image.");
                return new RasterImage(copy, isMask, name);
            }
        }
        using var bitmap = new SKBitmap(info);
        if (!image.ReadPixels(info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0))
        {
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Transparent);
            canvas.DrawImage(image, 0, 0);
        }
        return FromBitmap(bitmap, isMask, name);
    }

    public static RasterImage SolidMask(bool revealing) => FromMask(new[] { revealing ? (byte)255 : (byte)0 }, 1, 1);

    /// <summary>The pixels as a pixmap over the image's own memory (no copy). Dispose the pixmap after use.</summary>
    public SKPixmap PeekPixels() => Image.PeekPixels() ?? throw new InvalidOperationException("Image is not raster-backed.");

    /// <summary>A copy of the pixels, tightly packed.</summary>
    public byte[] CopyPixels()
    {
        int bpp = IsMask ? 1 : 4;
        var bytes = new byte[Width * Height * bpp];
        var info = IsMask ? MaskInfo(Width, Height) : ColorInfo(Width, Height);
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try { Image.ReadPixels(info, handle.AddrOfPinnedObject(), Width * bpp, 0, 0); }
        finally { handle.Free(); }
        return bytes;
    }

    /// <summary>A copy of the pixels in a new bitmap, for editing.</summary>
    public SKBitmap ToBitmap()
    {
        var info = IsMask ? MaskInfo(Width, Height) : ColorInfo(Width, Height);
        var bitmap = new SKBitmap(info);
        Image.ReadPixels(info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0);
        return bitmap;
    }

    /// <summary>A small copy (at most 96 px on its longest side), for the Layers panel.</summary>
    public SKImage Thumbnail => _thumbnail ??= MakeThumbnail();

    private SKImage MakeThumbnail()
    {
        double factor = Math.Min(1.0, ThumbnailSide / (double)Math.Max(Width, Height));
        int w = Math.Max(1, (int)(Width * factor)), h = Math.Max(1, (int)(Height * factor));
        if (IsMask)
        {
            // Grayscale for display: coverage drawn white over black.
            var info = new SKImageInfo(w, h, SKColorType.Gray8, SKAlphaType.Opaque);
            using var surface = SKSurface.Create(info);
            surface.Canvas.Clear(SKColors.Black);
            using var paint = new SKPaint { Color = SKColors.White };
            surface.Canvas.DrawImage(Image, new SKRect(0, 0, w, h), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
            return surface.Snapshot();
        }
        else
        {
            using var surface = SKSurface.Create(ColorInfo(w, h));
            surface.Canvas.Clear(SKColors.Transparent);
            surface.Canvas.DrawImage(Image, new SKRect(0, 0, w, h), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            return surface.Snapshot();
        }
    }

    /// <summary>The same pixels under another name.</summary>
    public RasterImage Renamed(string name) => new(Image, IsMask, name);

    public override string ToString() => $"{(IsMask ? "Mask" : "Image")} {Width}×{Height}";
}

public readonly record struct SizeDims(int Width, int Height);
