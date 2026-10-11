using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.App.Services;

/// <summary>Conversions between Skia images and WPF bitmaps.</summary>
public static class Bitmaps
{
    private static readonly ConditionalWeakTable<RasterImage, BitmapSource> Thumbnails = new();

    /// <summary>A WPF bitmap of an image's thumbnail, cached per image (images are immutable).</summary>
    public static BitmapSource Thumbnail(RasterImage image) => Thumbnails.GetValue(image, i => ToBitmapSource(i.Thumbnail));

    public static BitmapSource ToBitmapSource(SKImage image)
    {
        int w = image.Width, h = image.Height;
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        var bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Pbgra32, null);
        bitmap.Lock();
        try
        {
            image.ReadPixels(info, bitmap.BackBuffer, bitmap.BackBufferStride, 0, 0);
            bitmap.AddDirtyRect(new Int32Rect(0, 0, w, h));
        }
        finally { bitmap.Unlock(); }
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>A WPF bitmap's pixels as premultiplied RGBA layer pixels.</summary>
    public static SKBitmap? FromBitmapSource(BitmapSource source)
    {
        var converted = source.Format == PixelFormats.Pbgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        int w = converted.PixelWidth, h = converted.PixelHeight;
        if (w <= 0 || h <= 0) return null;
        var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        converted.CopyPixels(new Int32Rect(0, 0, w, h), bitmap.GetPixels(), bitmap.ByteCount, bitmap.RowBytes);
        var rgba = new SKBitmap(RasterImage.ColorInfo(w, h));
        bitmap.CopyTo(rgba, SKColorType.Rgba8888);
        bitmap.Dispose();
        return rgba;
    }

    /// <summary>Decodes what Skia can't (TIFF, HEIC with the Windows extension, JPEG XR…) through Windows' own codecs.</summary>
    public static SKBitmap? DecodeWithWindows(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            BitmapSource source = frame;
            // EXIF orientation, where the codec reports it.
            if (frame.Metadata is BitmapMetadata metadata)
            {
                try
                {
                    if (metadata.GetQuery("System.Photo.Orientation") is ushort orientation)
                    {
                        var transform = orientation switch
                        {
                            3 => new RotateTransform(180),
                            6 => new RotateTransform(90),
                            8 => new RotateTransform(270),
                            _ => null,
                        };
                        if (transform is not null) source = new TransformedBitmap(frame, transform);
                    }
                }
                catch { /* no orientation */ }
            }
            return FromBitmapSource(source);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// A camera RAW frame through Windows' raw codec at 16 bits a channel (straight RGBA, 4 values a pixel), oriented,
    /// its long side at most <paramref name="maxSide"/> when given. Null when the codec can't read it.
    /// </summary>
    public static (ushort[] Pixels, int Width, int Height)? DecodeWide(string path, int? maxSide = null)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            BitmapSource source = frame;
            var transform = new System.Windows.Media.TransformGroup();
            if (maxSide is { } limit && Math.Max(frame.PixelWidth, frame.PixelHeight) > limit)
            {
                double scale = limit / (double)Math.Max(frame.PixelWidth, frame.PixelHeight);
                transform.Children.Add(new ScaleTransform(scale, scale));
            }
            if (frame.Metadata is BitmapMetadata metadata)
            {
                try
                {
                    if (metadata.GetQuery("System.Photo.Orientation") is ushort orientation && orientation is 3 or 6 or 8)
                        transform.Children.Add(new RotateTransform(orientation == 3 ? 180 : orientation == 6 ? 90 : 270));
                }
                catch { /* no orientation */ }
            }
            if (transform.Children.Count > 0) source = new TransformedBitmap(frame, transform);
            var wide = new FormatConvertedBitmap(source, PixelFormats.Rgba64, null, 0);
            int w = wide.PixelWidth, h = wide.PixelHeight;
            if (w <= 0 || h <= 0) return null;
            var pixels = new ushort[w * h * 4];
            wide.CopyPixels(pixels, w * 8, 0);
            return (pixels, w, h);
        }
        catch { return null; }
    }

    /// <summary>A cursor drawn with Skia at 32×32 (scaled for the screen), as a .cur built in memory.</summary>
    public static Cursor MakeCursor(Action<SKCanvas> draw, int hotX, int hotY, double scale = 1)
    {
        int size = scale >= 1.75 ? 64 : scale >= 1.25 ? 48 : 32;
        double factor = size / 32.0;
        var info = new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Scale((float)factor);
            draw(canvas);
        }
        var pixels = bitmap.GetPixelSpan();
        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory);
        int maskStride = ((size + 31) / 32) * 4;
        int xorSize = size * size * 4, andSize = maskStride * size;
        // ICONDIR (type 2 = cursor) and one entry carrying the hotspot.
        writer.Write((ushort)0); writer.Write((ushort)2); writer.Write((ushort)1);
        writer.Write((byte)(size >= 256 ? 0 : size)); writer.Write((byte)(size >= 256 ? 0 : size));
        writer.Write((byte)0); writer.Write((byte)0);
        writer.Write((ushort)Math.Round(hotX * factor)); writer.Write((ushort)Math.Round(hotY * factor));
        writer.Write(40 + xorSize + andSize); writer.Write(22);
        // BITMAPINFOHEADER: height doubled for the AND mask.
        writer.Write(40); writer.Write(size); writer.Write(size * 2); writer.Write((ushort)1); writer.Write((ushort)32);
        writer.Write(0); writer.Write(xorSize + andSize); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
        // Bottom-up rows of straight BGRA.
        for (int y = size - 1; y >= 0; y--)
        {
            for (int x = 0; x < size; x++)
            {
                int p = (y * size + x) * 4;
                byte a = pixels[p + 3];
                byte b = pixels[p], g = pixels[p + 1], r = pixels[p + 2];
                if (a > 0 && a < 255) { b = (byte)(b * 255 / a); g = (byte)(g * 255 / a); r = (byte)(r * 255 / a); }
                writer.Write(b); writer.Write(g); writer.Write(r); writer.Write(a);
            }
        }
        writer.Write(new byte[andSize]);
        writer.Flush();
        memory.Position = 0;
        return new Cursor(memory, true);
    }

    public static SolidColorBrush Brush(PaletteColor color) => new(Color.FromRgb(color.R8, color.G8, color.B8));
}
