using Compositor.Core.Geometry;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Core.Tests;

internal static class TestImages
{
    /// <summary>A solid image of one straight (unpremultiplied) color.</summary>
    public static RasterImage Solid(int width, int height, byte r, byte g, byte b, byte a = 255)
    {
        var bytes = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            bytes[i * 4] = (byte)(r * a / 255);
            bytes[i * 4 + 1] = (byte)(g * a / 255);
            bytes[i * 4 + 2] = (byte)(b * a / 255);
            bytes[i * 4 + 3] = a;
        }
        return RasterImage.FromRgba(bytes, width, height);
    }

    public static (byte R, byte G, byte B, byte A) Pixel(SKImage image, int x, int y)
    {
        var info = new SKImageInfo(1, 1, SKColorType.Rgba8888, SKAlphaType.Premul, RasterImage.Srgb);
        var bytes = new byte[4];
        unsafe
        {
            fixed (byte* p = bytes) image.ReadPixels(info, (IntPtr)p, 4, x, y);
        }
        return (bytes[0], bytes[1], bytes[2], bytes[3]);
    }

    public static CanvasDocument Document(int width, int height, params Layer[] layers) => new()
    {
        Width = width,
        Height = height,
        Layers = System.Collections.Immutable.ImmutableList.CreateRange(layers),
    };

    public static Layer Layer(RasterImage image, double x = 0, double y = 0, BlendMode mode = BlendMode.Normal, double opacity = 1) =>
        Model.Layer.FromImage(image, new PointD(x, y)) with { BlendMode = mode, Opacity = opacity };
}
