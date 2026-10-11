using System.Globalization;
using SkiaSharp;

namespace Compositor.Core.Model;

/// <summary>Size limits shared by import, painting and the project format. Mirrors DocumentLimits.swift.</summary>
public static class DocumentLimits
{
    /// <summary>Pixels per canvas or image side.</summary>
    public const int MaxSide = 30_000;
    /// <summary>Total source pixels across a document's layers (the Mac version scales this with memory).</summary>
    public static long DocumentPixelBudget { get; set; } = ComputeBudget();
    /// <summary>The largest single raster surface a pixel operation may allocate.</summary>
    public const long MaxSurfacePixels = 400_000_000;
    public const int MaxLayers = 10_000;

    public static long DocumentBudgetMegapixels => DocumentPixelBudget / 1_000_000;

    private static long ComputeBudget()
    {
        try
        {
            long memory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            // About a quarter of physical memory, at 4 bytes a pixel, between 100 and 1000 megapixels.
            long pixels = memory / 16;
            return Math.Clamp(pixels, 100_000_000, 1_000_000_000);
        }
        catch
        {
            return 100_000_000;
        }
    }
}

/// <summary>An sRGB color with components 0…1. Mirrors PaletteColor in ColorPalette.swift.</summary>
public readonly record struct PaletteColor(double Red, double Green, double Blue)
{
    public static readonly PaletteColor Black = new(0, 0, 0);
    public static readonly PaletteColor White = new(1, 1, 1);

    public byte R8 => ToByte(Red);
    public byte G8 => ToByte(Green);
    public byte B8 => ToByte(Blue);

    private static byte ToByte(double v) => (byte)Math.Clamp((int)Math.Round(v * 255, MidpointRounding.AwayFromZero), 0, 255);

    public static PaletteColor FromBytes(byte r, byte g, byte b) => new(r / 255.0, g / 255.0, b / 255.0);

    public SKColor ToSK(byte alpha = 255) => new(R8, G8, B8, alpha);

    public string Hex => $"{R8:X2}{G8:X2}{B8:X2}";

    public static PaletteColor? FromHex(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim().TrimStart('#');
        if (s.Length == 3) s = string.Concat(s.Select(c => new string(c, 2)));
        if (s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)) return null;
        return FromBytes((byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    /// <summary>Hue (degrees), saturation and brightness (0…1).</summary>
    public (double H, double S, double B) ToHsb()
    {
        double max = Math.Max(Red, Math.Max(Green, Blue)), min = Math.Min(Red, Math.Min(Green, Blue));
        double delta = max - min, h = 0;
        if (delta > 0)
        {
            if (max == Red) h = 60 * (((Green - Blue) / delta) % 6);
            else if (max == Green) h = 60 * ((Blue - Red) / delta + 2);
            else h = 60 * ((Red - Green) / delta + 4);
        }
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : delta / max, max);
    }

    public static PaletteColor FromHsb(double h, double s, double b)
    {
        h = ((h % 360) + 360) % 360;
        double c = b * s, x = c * (1 - Math.Abs((h / 60) % 2 - 1)), m = b - c;
        (double r, double g, double bl) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return new PaletteColor(r + m, g + m, bl + m);
    }

    /// <summary>Rec. 601 luma, as Photoshop's grayscale conversion of a mask paint color.</summary>
    public double Luminance => 0.299 * Red + 0.587 * Green + 0.114 * Blue;
}

public enum GuideAxis { Horizontal, Vertical }

/// <summary>A user-placed alignment line. Saved with the project; undo covers it.</summary>
public sealed record CanvasGuide(Guid Id, GuideAxis Axis, double Position);
