using System.Buffers.Binary;
using Compositor.Core.Localization;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Core.IO;

public sealed class ImageImportException : Exception
{
    public ImageImportException(string message) : base(message) { }
    public static ImageImportException Unsupported() => new(L.T("不支持这种文件格式。", "This file format isn’t supported."));
    public static ImageImportException Unreadable() => new(L.T("无法读取这个图像，它可能已损坏。", "The image could not be read. It may be damaged or unavailable."));
    public static ImageImportException RawCodecMissing() =>
        new(L.T("Windows 无法解码这个相机 RAW 文件。请从 Microsoft Store 安装“Raw 图像扩展”(Raw Image Extension)，然后再试。",
                "Windows can’t decode this camera raw file. Install the Raw Image Extension from the Microsoft Store, then try again."));
    public static ImageImportException TooLarge() =>
        new(L.T($"图像太大：每边最多 {DocumentLimits.MaxSide:N0} 像素，文档总计最多 {DocumentLimits.DocumentBudgetMegapixels} 百万像素。",
                $"This import exceeds the current {DocumentLimits.DocumentBudgetMegapixels}-megapixel document budget or {DocumentLimits.MaxSide:N0}-pixel side limit."));
}

/// <summary>Decodes image files into layer pixels. Mirrors ImageImporter.swift for the formats Windows can read.</summary>
public static class ImageImporter
{
    /// <summary>Formats Skia decodes itself.</summary>
    public static readonly string[] NativeExtensions = { ".png", ".jpg", ".jpeg", ".jpe", ".jfif", ".webp", ".gif", ".bmp", ".ico", ".wbmp" };
    /// <summary>Formats left to Windows' own codecs (WIC), set up by the app through <see cref="FallbackDecoder"/>.</summary>
    public static readonly string[] SystemExtensions = { ".tif", ".tiff", ".heic", ".heif", ".avif", ".jxr", ".wdp", ".dds" };
    /// <summary>Camera raw files, developed by Windows' raw codec (built in for DNG; the Raw Image Extension for the rest).</summary>
    public static readonly string[] RawExtensions =
    {
        ".dng", ".cr2", ".cr3", ".crw", ".nef", ".nrw", ".arw", ".srf", ".sr2", ".raf", ".orf", ".rw2", ".pef", ".srw",
        ".x3f", ".3fr", ".mef", ".mos", ".erf", ".kdc", ".dcr", ".iiq", ".rwl",
    };

    /// <summary>Decodes what Skia can't (TIFF, HEIC…) into premultiplied RGBA. Set by the app.</summary>
    public static Func<string, SKBitmap?>? FallbackDecoder { get; set; }

    public static bool IsImageFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return NativeExtensions.Contains(ext) || SystemExtensions.Contains(ext) || RawExtensions.Contains(ext) || ext is ".svg" or ".svgz";
    }

    public static bool IsProject(string path) =>
        Directory.Exists(path) && (path.TrimEnd('\\', '/').EndsWith(ProjectStore.Extension, StringComparison.OrdinalIgnoreCase)
                                   || File.Exists(Path.Combine(path, ProjectManifest.ManifestName)));

    /// <summary>
    /// An image file's pixels. An SVG is drawn once, fitted to <paramref name="fitting"/> (the canvas) when there is one,
    /// otherwise at the size it declares.
    /// </summary>
    public static RasterImage Decode(string path, long remainingPixels = long.MaxValue, Geometry.SizeD? fitting = null)
    {
        if (SvgRenderer.IsSvgPath(path)) return SvgRenderer.Render(path, fitting, remainingPixels);
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception) { throw ImageImportException.Unreadable(); }

        using (var data = SKData.CreateCopy(bytes))
        using (var codec = SKCodec.Create(data))
        {
            if (codec is not null)
            {
                var info = codec.Info;
                Check(info.Width, info.Height, remainingPixels);
                var target = RasterImage.ColorInfo(info.Width, info.Height);
                using var bitmap = new SKBitmap(target);
                var result = codec.GetPixels(target, bitmap.GetPixels());
                if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput) throw ImageImportException.Unreadable();
                using var oriented = Orient(bitmap, codec.EncodedOrigin);
                return RasterImage.FromBitmap(oriented ?? bitmap, false, name);
            }
        }
        if (FallbackDecoder is { } fallback)
        {
            using var decoded = fallback(path) ?? throw (RawExtensions.Contains(ext) ? ImageImportException.RawCodecMissing()
                : SystemExtensions.Contains(ext) ? ImageImportException.Unreadable() : ImageImportException.Unsupported());
            Check(decoded.Width, decoded.Height, remainingPixels);
            return RasterImage.FromBitmap(decoded, false, name);
        }
        throw ImageImportException.Unsupported();
    }

    /// <summary>Decodes image bytes (from the clipboard or a drag).</summary>
    public static RasterImage? DecodeBytes(byte[] bytes, string name = "Pasted")
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec is null) return null;
        var info = codec.Info;
        Check(info.Width, info.Height, DocumentLimits.DocumentPixelBudget);
        var target = RasterImage.ColorInfo(info.Width, info.Height);
        using var bitmap = new SKBitmap(target);
        var result = codec.GetPixels(target, bitmap.GetPixels());
        if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput) return null;
        return RasterImage.FromBitmap(bitmap, false, name);
    }

    private static void Check(int width, int height, long remainingPixels)
    {
        if (width < 1 || height < 1) throw ImageImportException.Unreadable();
        if (width > DocumentLimits.MaxSide || height > DocumentLimits.MaxSide || (long)width * height > remainingPixels)
            throw ImageImportException.TooLarge();
    }

    /// <summary>Applies the EXIF orientation, so a photo taken sideways comes in upright. Null when already upright.</summary>
    private static SKBitmap? Orient(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft || origin == SKEncodedOrigin.Default) return null;
        bool swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        int w = swap ? bitmap.Height : bitmap.Width, h = swap ? bitmap.Width : bitmap.Height;
        var result = new SKBitmap(RasterImage.ColorInfo(w, h));
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.Transparent);
        var m = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, w, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, w, -1, 0, h, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, h, 0, 0, 1),
            _ => SKMatrix.Identity,
        };
        canvas.SetMatrix(m);
        using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
        canvas.DrawBitmap(bitmap, 0, 0, paint);
        canvas.Flush();
        return result;
    }
}

/// <summary>Writes flattened exports. Mirrors ImageExporter.swift: both PNG and JPEG carry the document's resolution.</summary>
public static class ImageExporter
{
    public static byte[] EncodePng(SKImage image, double dpi)
    {
        using var data = image.Encode(SKEncodedImageFormat.Png, 100) ?? throw new InvalidOperationException(L.T("PNG 编码失败。", "The image could not be encoded as PNG."));
        return PngWithResolution(data.ToArray(), dpi);
    }

    /// <summary>JPEG has no transparency: the image is flattened onto white (or <paramref name="background"/>) first, as Photoshop does.</summary>
    public static byte[] EncodeJpeg(SKImage image, int quality, double dpi, SKColor? background = null)
    {
        using var flat = Flattened(image, background ?? SKColors.White);
        using var data = flat.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(quality, 0, 100)) ?? throw new InvalidOperationException(L.T("JPEG 编码失败。", "The image could not be encoded as JPEG."));
        return JpegWithResolution(data.ToArray(), dpi);
    }

    /// <summary>The image over an opaque color.</summary>
    public static SKImage Flattened(SKImage image, SKColor background)
    {
        using var surface = SKSurface.Create(new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Opaque, RasterImage.Srgb))
                            ?? throw new InvalidOperationException(L.T("内存不足。", "Not enough memory."));
        surface.Canvas.Clear(background.WithAlpha(255));
        surface.Canvas.DrawImage(image, 0, 0);
        surface.Canvas.Flush();
        return surface.Snapshot();
    }

    /// <summary>The image resampled to <paramref name="width"/> × <paramref name="height"/> (Export As's scaling); itself at its own size.</summary>
    public static SKImage Resized(SKImage image, int width, int height)
    {
        if (width == image.Width && height == image.Height) return SKImage.FromBitmap(SKBitmap.FromImage(image));
        if ((long)width * height > DocumentLimits.MaxSurfacePixels || width > DocumentLimits.MaxSide || height > DocumentLimits.MaxSide)
            throw new InvalidOperationException(L.T("导出的尺寸太大。", "The export size is too large."));
        var info = new SKImageInfo(Math.Max(1, width), Math.Max(1, height), SKColorType.Rgba8888, SKAlphaType.Premul, RasterImage.Srgb);
        using var bitmap = new SKBitmap(info);
        using (var pixmap = bitmap.PeekPixels())
        {
            // Shrinking averages through mipmaps; enlarging interpolates smoothly.
            var sampling = width < image.Width ? new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear) : new SKSamplingOptions(SKCubicResampler.Mitchell);
            if (!image.ScalePixels(pixmap, sampling)) throw new InvalidOperationException(L.T("无法缩放图像。", "The image could not be resized."));
        }
        bitmap.SetImmutable();
        return SKImage.FromBitmap(bitmap);
    }

    /// <summary>
    /// A one-page PDF of the image: the page is the image's size at <paramref name="dpi"/>, its transparent parts over
    /// <paramref name="background"/> (a page shows through them). Mirrors ImageExporter.pdfData.
    /// </summary>
    public static byte[] EncodePdf(SKImage image, double dpi, SKColor background)
    {
        if (!(dpi > 0) || !double.IsFinite(dpi)) dpi = 72;
        float pageWidth = (float)(image.Width / dpi * 72), pageHeight = (float)(image.Height / dpi * 72);
        using var stream = new MemoryStream();
        var metadata = new SKDocumentPdfMetadata { Creator = "WinCompositor", Producer = "WinCompositor", RasterDpi = (float)dpi };
        using (var document = SKDocument.CreatePdf(stream, metadata) ?? throw new InvalidOperationException(L.T("PDF 编码失败。", "The image could not be encoded as PDF.")))
        {
            var canvas = document.BeginPage(pageWidth, pageHeight);
            using (var paint = new SKPaint { Color = background.WithAlpha(255) }) canvas.DrawRect(0, 0, pageWidth, pageHeight, paint);
            canvas.DrawImage(image, new SKRect(0, 0, pageWidth, pageHeight), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            document.EndPage();
            document.Close();
        }
        return stream.ToArray();
    }

    /// <summary>Inserts (or replaces) the pHYs chunk with pixels per meter for <paramref name="dpi"/>.</summary>
    public static byte[] PngWithResolution(byte[] png, double dpi)
    {
        if (png.Length < 33 || dpi <= 0) return png;
        uint ppm = (uint)Math.Round(dpi / 0.0254);
        var output = new List<byte>(png.Length + 21);
        output.AddRange(png.AsSpan(0, 8).ToArray());
        int offset = 8;
        bool inserted = false;
        while (offset + 8 <= png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
            string type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            int total = 12 + length;
            if (offset + total > png.Length) break;
            if (type == "pHYs") { offset += total; continue; }
            if (!inserted && (type == "IDAT" || type == "PLTE"))
            {
                output.AddRange(Chunk("pHYs", PhysData(ppm)));
                inserted = true;
            }
            output.AddRange(png.AsSpan(offset, total).ToArray());
            offset += total;
        }
        return inserted ? output.ToArray() : png;
    }

    private static byte[] PhysData(uint ppm)
    {
        var data = new byte[9];
        BinaryPrimitives.WriteUInt32BigEndian(data, ppm);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), ppm);
        data[8] = 1; // meters
        return data;
    }

    private static byte[] Chunk(string type, byte[] data)
    {
        var chunk = new byte[12 + data.Length];
        BinaryPrimitives.WriteInt32BigEndian(chunk, data.Length);
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(chunk, 4);
        data.CopyTo(chunk, 8);
        uint crc = Crc32(chunk.AsSpan(4, 4 + data.Length));
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + data.Length), crc);
        return chunk;
    }

    private static uint[]? _crcTable;

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var table = _crcTable ??= Enumerable.Range(0, 256).Select(n =>
        {
            uint c = (uint)n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            return c;
        }).ToArray();
        uint crc = 0xFFFFFFFF;
        foreach (var b in bytes) crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }

    /// <summary>Sets the JFIF density to <paramref name="dpi"/> dots per inch, adding a JFIF header when there is none.</summary>
    public static byte[] JpegWithResolution(byte[] jpeg, double dpi)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8 || dpi <= 0) return jpeg;
        ushort density = (ushort)Math.Clamp(Math.Round(dpi), 1, 65535);
        // APP0: FF E0, length, "JFIF\0", version (2), units (1), X density (2), Y density (2), thumbnail size (2).
        if (jpeg[2] == 0xFF && jpeg[3] == 0xE0 && jpeg.Length > 20
            && jpeg[6] == (byte)'J' && jpeg[7] == (byte)'F' && jpeg[8] == (byte)'I' && jpeg[9] == (byte)'F' && jpeg[10] == 0)
        {
            var copy = (byte[])jpeg.Clone();
            copy[13] = 1; // units: dots per inch
            BinaryPrimitives.WriteUInt16BigEndian(copy.AsSpan(14), density);
            BinaryPrimitives.WriteUInt16BigEndian(copy.AsSpan(16), density);
            return copy;
        }
        var app0 = new byte[18];
        app0[0] = 0xFF; app0[1] = 0xE0;
        BinaryPrimitives.WriteUInt16BigEndian(app0.AsSpan(2), 16);
        "JFIF\0"u8.ToArray().CopyTo(app0, 4);
        app0[9] = 1; app0[10] = 1; app0[11] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(app0.AsSpan(12), density);
        BinaryPrimitives.WriteUInt16BigEndian(app0.AsSpan(14), density);
        // app0[16], app0[17]: no thumbnail.
        var result = new byte[jpeg.Length + app0.Length];
        result[0] = 0xFF; result[1] = 0xD8;
        app0.CopyTo(result, 2);
        jpeg.AsSpan(2).CopyTo(result.AsSpan(2 + app0.Length));
        return result;
    }
}
