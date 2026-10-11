using System.Text;
using Compositor.Core.Adjustments;
using Compositor.Core.Geometry;
using Compositor.Core.Localization;
using Compositor.Core.Model;

namespace Compositor.Core.IO.Psd;

public sealed class PsdException : Exception
{
    public PsdException(string message) : base(message) { }
    public static PsdException Truncated() =>
        new(L.T("无法读取这个 Photoshop 文件，它可能已损坏或不完整。", "The Photoshop file could not be read. It may be damaged or incomplete."));
    public static PsdException UnsupportedVersion() =>
        new(L.T("这个 Photoshop 文件使用了无法读取的格式版本。", "This Photoshop file uses a format version WinCompositor can’t read."));
    public static PsdException UnsupportedMode() => new(L.T("只能导入 8 位 RGB 的 Photoshop 文件。", "Only 8-bit RGB Photoshop files can be imported."));
    public static PsdException UnsupportedCompression() =>
        new(L.T("这个 Photoshop 文件的图层使用了不支持的压缩方式。", "This Photoshop file uses a layer compression method that isn’t supported."));
}

/// <summary>One thing changed on the way in, reported before anything is opened.</summary>
public sealed record PsdConversion(string LayerName, string Message);

public enum PsdLayerKind { Raster, Group, Adjustment, Text, SmartObject, Effects, Vector, Other }

/// <summary>A layer record as read, bottom to top. Mirrors PSDRecord.</summary>
public sealed class PsdRecord
{
    public Guid Id = Guid.NewGuid();
    public Guid? ParentId;
    public string Name = "Layer";
    public bool IsGroup;
    public bool IsVisible = true;
    public double Opacity = 1;
    public string BlendKey = "norm";
    public bool Clipping;
    public bool CroppedToCanvas;
    public RectI Bounds;
    /// <summary>Premultiplied RGBA, Bounds-sized.</summary>
    public RasterImage? Image;
    public byte[]? Mask;
    /// <summary>Where Mask sits on the document, and the value everywhere outside it.</summary>
    public RectI MaskBounds;
    public byte MaskDefault = 255;
    public bool MaskEnabled = true;
    public bool MaskLinked = true;
    public LayerAdjustment? Adjustment;
    public PsdLayerKind Kind = PsdLayerKind.Raster;
    public LayerShape? Shape;
    public List<string> ShapeNotes = new();
    /// <summary>A type layer that maps onto editable text.</summary>
    public PsdText.Source? Text;
    /// <summary>The layer style, as far as the editor's effects go.</summary>
    public PsdEffects.Result? Effects;

    public BlendMode? BlendMode => PsdReader.BlendFromKey(BlendKey);
}

public sealed record PsdDocument(int Width, int Height, double Resolution, List<PsdRecord> Layers);

/// <summary>
/// Reads Photoshop .psd and .psb files from Adobe's Photoshop File Formats Specification: the 8BPS header, layer
/// records, PackBits, and additional layer info. 8-bit RGB only. Mirrors PSDReader.swift.
/// </summary>
public static class PsdReader
{
    public static bool Matches(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> magic = stackalloc byte[4];
            return stream.Read(magic) == 4 && magic.SequenceEqual("8BPS"u8);
        }
        catch (Exception) { return false; }
    }

    public static bool IsPsdPath(string path) =>
        path.EndsWith(".psd", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".psb", StringComparison.OrdinalIgnoreCase);

    public static BlendMode? BlendFromKey(string key) => key switch
    {
        "norm" => Model.BlendMode.Normal,
        "mul " => Model.BlendMode.Multiply,
        "scrn" => Model.BlendMode.Screen,
        "over" => Model.BlendMode.Overlay,
        "sLit" => Model.BlendMode.SoftLight,
        "dark" => Model.BlendMode.Darken,
        "lite" => Model.BlendMode.Lighten,
        "diff" => Model.BlendMode.Difference,
        "div " => Model.BlendMode.ColorDodge,
        "idiv" => Model.BlendMode.ColorBurn,
        "hue " => Model.BlendMode.Hue,
        "sat " => Model.BlendMode.Saturation,
        "colr" => Model.BlendMode.Color,
        "lum " => Model.BlendMode.Luminosity,
        "lbrn" => Model.BlendMode.LinearBurn,
        "lddg" => Model.BlendMode.LinearDodge,
        "hLit" => Model.BlendMode.HardLight,
        "vLit" => Model.BlendMode.VividLight,
        "lLit" => Model.BlendMode.LinearLight,
        "pLit" => Model.BlendMode.PinLight,
        "hMix" => Model.BlendMode.HardMix,
        "smud" => Model.BlendMode.Exclusion,
        "fsub" => Model.BlendMode.Subtract,
        "fdiv" => Model.BlendMode.Divide,
        // Dissolve, Darker Color and Lighter Color have no equivalent: Normal, and the report says so.
        _ => null,
    };

    public static PsdDocument Read(string path, long remainingPixels = long.MaxValue) => Read(File.ReadAllBytes(path), remainingPixels);

    public static PsdDocument Read(byte[] data, long remainingPixels = long.MaxValue)
    {
        remainingPixels = Math.Min(remainingPixels, DocumentLimits.DocumentPixelBudget);
        var cursor = new Cursor(data);
        if (cursor.Text(4) != "8BPS") throw ImageImportException.Unreadable();
        int version = cursor.U16();
        if (version is not (1 or 2)) throw PsdException.UnsupportedVersion();
        bool isPsb = version == 2;
        cursor.Skip(6);
        int channelCount = cursor.U16();
        int canvasHeight = (int)cursor.U32(), canvasWidth = (int)cursor.U32();
        int depth = cursor.U16(), mode = cursor.U16();
        if (canvasWidth < 1 || canvasHeight < 1 || canvasWidth > DocumentLimits.MaxSide || canvasHeight > DocumentLimits.MaxSide
            || (long)canvasWidth * canvasHeight > DocumentLimits.MaxSurfacePixels) throw ImageImportException.TooLarge();
        if (depth != 8 || mode != 3) throw PsdException.UnsupportedMode();
        cursor.Skip(Length(cursor.U32()));
        int resourcesLength = Length(cursor.U32());
        int resourcesEnd = cursor.Offset + resourcesLength;
        double resolution = 72, globalAngle = PsdEffects.DefaultGlobalAngle;
        while (cursor.Offset + 12 <= resourcesEnd)
        {
            if (cursor.Text(4) != "8BIM") break;
            int id = cursor.U16();
            int nameLength = cursor.U8();
            cursor.Skip(nameLength);
            if ((nameLength + 1) % 2 == 1) cursor.Skip(1);
            int length = Length(cursor.U32());
            int start = cursor.Offset;
            if (id == 1005 && length >= 4)
            {
                resolution = cursor.U32() / 65536.0;
                if (!double.IsFinite(resolution) || resolution < 1) resolution = 72;
                resolution = Math.Clamp(resolution, 1, 9600);
            }
            else if (id == 1037 && length >= 4)
            {
                // The global light angle that layer styles may follow.
                int angle = unchecked((int)cursor.U32());
                if (angle is >= -360 and <= 360) globalAngle = angle;
            }
            cursor.Offset = start + length;
            if (length % 2 == 1) cursor.Skip(1);
        }
        cursor.Offset = resourcesEnd;
        int layerSection = Length(isPsb ? cursor.U64() : cursor.U32());
        int layerSectionEnd = cursor.Offset + layerSection;
        if (layerSection < 4)
        {
            cursor.Offset = layerSectionEnd;
            return Flattened(ref cursor, canvasWidth, canvasHeight, channelCount, resolution, isPsb);
        }
        Length(isPsb ? cursor.U64() : cursor.U32());
        int count = Math.Abs((int)cursor.I16());
        if (count == 0)
        {
            cursor.Offset = layerSectionEnd;
            return Flattened(ref cursor, canvasWidth, canvasHeight, channelCount, resolution, isPsb);
        }
        if (count > DocumentLimits.MaxLayers) throw ImageImportException.TooLarge();
        var raw = new List<RawLayer>(count);
        for (int i = 0; i < count; i++) raw.Add(ReadRecord(ref cursor, isPsb));
        if (!FitsBudget(raw, remainingPixels))
        {
            foreach (var layer in raw) CropToCanvas(layer, canvasWidth, canvasHeight);
            if (!FitsBudget(raw, remainingPixels)) throw ImageImportException.TooLarge();
        }
        long used = 0;
        foreach (var layer in raw)
        {
            DecodeChannels(ref cursor, layer, remainingPixels - used, isPsb);
            if (layer.Image is { } image) used += (long)image.Width * image.Height;
        }
        cursor.Offset = layerSectionEnd;
        return new PsdDocument(canvasWidth, canvasHeight, resolution, Assemble(raw, new SizeD(canvasWidth, canvasHeight), remainingPixels - used, globalAngle));
    }

    /// <summary>A file with only a background: Photoshop writes no layer records, just the merged image, read as one layer.</summary>
    private static PsdDocument Flattened(ref Cursor cursor, int width, int height, int channels, double resolution, bool isPsb)
    {
        int compression = cursor.U16();
        int planes = Math.Min(Math.Max(3, channels), 4);
        var data = new ReadOnlySpan<byte>(cursor.Data, cursor.Offset, cursor.Data.Length - cursor.Offset);
        var decoded = new byte[planes][];
        int n = width * height;
        if (compression == 0)
        {
            if ((long)n * planes > data.Length) throw PsdException.Truncated();
            for (int c = 0; c < planes; c++) decoded[c] = data.Slice(c * n, n).ToArray();
        }
        else if (compression == 1)
        {
            // All channels' row byte counts come first, then all the rows, channel after channel.
            int countBytes = isPsb ? 4 : 2;
            int totalRows = Math.Max(3, channels) * height;
            if ((long)totalRows * countBytes > data.Length) throw PsdException.Truncated();
            int offset = totalRows * countBytes;
            for (int c = 0; c < planes; c++)
            {
                int size = 0;
                for (int row = 0; row < height; row++)
                {
                    int at = (c * height + row) * countBytes;
                    size += isPsb ? (int)BigEndian.U32(data, at) : data[at] << 8 | data[at + 1];
                }
                if (offset + size > data.Length) throw PsdException.Truncated();
                // Rebuild one channel's block (its counts, then its rows) for the channel decoder.
                var block = new byte[height * countBytes + size];
                data.Slice(c * height * countBytes, height * countBytes).CopyTo(block);
                data.Slice(offset, size).CopyTo(block.AsSpan(height * countBytes));
                decoded[c] = PsdChannelCoder.Decode(1, width, height, block, isPsb);
                offset += size;
            }
        }
        else throw PsdException.UnsupportedCompression();
        var rgba = new byte[n * 4];
        for (int i = 0; i < n; i++)
        {
            int a = planes == 4 ? decoded[3][i] : 255;
            rgba[i * 4] = (byte)((decoded[0][i] * a + 127) / 255);
            rgba[i * 4 + 1] = (byte)((decoded[1][i] * a + 127) / 255);
            rgba[i * 4 + 2] = (byte)((decoded[2][i] * a + 127) / 255);
            rgba[i * 4 + 3] = (byte)a;
        }
        string name = L.T("背景", "Background");
        var record = new PsdRecord { Name = name, Bounds = new RectI(0, 0, width, height), Image = RasterImage.FromRgba(rgba, width, height, name) };
        return new PsdDocument(width, height, resolution, new List<PsdRecord> { record });
    }

    private static int Length(ulong value) => value <= int.MaxValue ? (int)value : throw ImageImportException.TooLarge();

    private sealed class RawLayer
    {
        public string Name = "";
        public int Top, Left, Bottom, Right;
        public int SourceTop, SourceLeft, SourceBottom, SourceRight;
        public byte Opacity = 255, Fill = 255;
        public bool Clipping, Hidden;
        public string BlendKey = "norm";
        public List<(int Id, int Length)> Channels = new();
        public Dictionary<string, byte[]> Extra = new();
        public int MaskTop, MaskLeft, MaskBottom, MaskRight;
        public int SourceMaskTop, SourceMaskLeft, SourceMaskBottom, SourceMaskRight;
        public byte MaskDefault = 255;
        public bool MaskDisabled, MaskLinked = true, MaskFromRender, HasMask;
        public int? Section;
        public RasterImage? Image;
        public byte[]? MaskPlane;
        public PsdCrop? ImageCrop, MaskCrop;
        public bool Cropped;
    }

    private static readonly HashSet<string> PsbLargeKeys = new()
    {
        "LMsk", "Lr16", "Lr32", "Layr", "Mt16", "Mt32", "Mtrn", "Alph", "FMsk", "lnk2", "FEid", "FXid", "PxSD",
    };

    private static RawLayer ReadRecord(ref Cursor cursor, bool isPsb)
    {
        var layer = new RawLayer();
        layer.Top = cursor.I32(); layer.Left = cursor.I32(); layer.Bottom = cursor.I32(); layer.Right = cursor.I32();
        (layer.SourceTop, layer.SourceLeft, layer.SourceBottom, layer.SourceRight) = (layer.Top, layer.Left, layer.Bottom, layer.Right);
        int channelCount = cursor.U16();
        if (channelCount > 56) throw ImageImportException.TooLarge();
        for (int i = 0; i < channelCount; i++)
        {
            int id = cursor.I16();
            int length = Length(isPsb ? cursor.U64() : cursor.U32());
            layer.Channels.Add((id, length));
        }
        if (cursor.Text(4) != "8BIM") throw PsdException.Truncated();
        layer.BlendKey = cursor.Text(4);
        layer.Opacity = cursor.U8();
        layer.Clipping = cursor.U8() != 0;
        byte flags = cursor.U8();
        layer.Hidden = (flags & 2) != 0;
        cursor.Skip(1);
        int extraLength = Length(cursor.U32());
        int extraEnd = cursor.Offset + extraLength;
        int maskLength = Length(cursor.U32());
        int maskEnd = cursor.Offset + maskLength;
        if (maskLength >= 20)
        {
            layer.HasMask = true;
            layer.MaskTop = cursor.I32(); layer.MaskLeft = cursor.I32(); layer.MaskBottom = cursor.I32(); layer.MaskRight = cursor.I32();
            (layer.SourceMaskTop, layer.SourceMaskLeft, layer.SourceMaskBottom, layer.SourceMaskRight) = (layer.MaskTop, layer.MaskLeft, layer.MaskBottom, layer.MaskRight);
            layer.MaskDefault = cursor.U8();
            byte maskFlags = cursor.U8();
            layer.MaskDisabled = (maskFlags & 2) != 0;
            layer.MaskLinked = (maskFlags & 1) == 0;
            layer.MaskFromRender = (maskFlags & 8) != 0;
        }
        cursor.Offset = maskEnd;
        cursor.Skip(Length(cursor.U32()));
        int nameCount = cursor.U8();
        var nameBytes = cursor.Bytes(nameCount);
        layer.Name = Encoding.Latin1.GetString(nameBytes);
        cursor.Skip((4 - ((nameCount + 1) % 4)) % 4);
        while (cursor.Offset + 12 <= extraEnd)
        {
            string signature = cursor.Text(4);
            if (signature is not ("8BIM" or "8B64")) break;
            string key = cursor.Text(4);
            int length;
            if (signature == "8B64" || (isPsb && PsbLargeKeys.Contains(key)))
            {
                if (cursor.Offset + 8 > extraEnd) break;
                length = Length(cursor.U64());
            }
            else length = Length(cursor.U32());
            var payload = cursor.Bytes(length);
            if (length % 2 == 1) cursor.Skip(1);
            layer.Extra[key] = payload;
            if (key == "luni" && UnicodeName(payload) is { } unicode) layer.Name = unicode;
            if (key == "iOpa" && payload.Length > 0) layer.Fill = payload[0];
            if (key is "lsct" or "lsdk" && payload.Length >= 4) layer.Section = (int)BigEndian.U32(payload, 0);
        }
        cursor.Offset = extraEnd;
        return layer;
    }

    private static string? UnicodeName(byte[] data)
    {
        if (data.Length < 4) return null;
        long count = BigEndian.U32(data, 0);
        if (count <= 0 || data.Length < 4 + count * 2) return null;
        var chars = new char[count];
        for (int i = 0; i < count; i++) chars[i] = (char)(data[4 + i * 2] << 8 | data[5 + i * 2]);
        return new string(chars).Trim('\0');
    }

    /// <summary>Transparency, R, G, B, and the user mask. Spot and other extra channels are skipped.</summary>
    private static readonly HashSet<int> UnpackedChannels = new() { -1, 0, 1, 2, -2 };

    private static bool FitsBudget(List<RawLayer> layers, long remaining)
    {
        long used = 0;
        foreach (var l in layers)
        {
            int w = Math.Max(0, l.Right - l.Left), h = Math.Max(0, l.Bottom - l.Top);
            int mw = Math.Max(0, l.MaskRight - l.MaskLeft), mh = Math.Max(0, l.MaskBottom - l.MaskTop);
            if (!FitsBudget(w, h, mw, mh, l.HasMask, remaining - used)) return false;
            if (w > 0 && h > 0) used += (long)w * h;
        }
        return true;
    }

    private static bool FitsBudget(int width, int height, int maskWidth, int maskHeight, bool hasMask, long remaining)
    {
        long budget = Math.Max(0, remaining);
        if (width > 0 && height > 0 && !(width <= DocumentLimits.MaxSide && height <= DocumentLimits.MaxSide && (long)width * height <= budget)) return false;
        if (hasMask && maskWidth > 0 && maskHeight > 0
            && !(maskWidth <= DocumentLimits.MaxSide && maskHeight <= DocumentLimits.MaxSide && (long)maskWidth * maskHeight <= budget)) return false;
        return true;
    }

    private static void CropToCanvas(RawLayer layer, int width, int height)
    {
        var image = Crop(layer.Left, layer.Top, layer.Right, layer.Bottom, width, height);
        if (image.X != 0 || image.Y != 0 || image.Width != layer.Right - layer.Left || image.Height != layer.Bottom - layer.Top)
        {
            layer.Left += image.X; layer.Top += image.Y;
            layer.Right = layer.Left + image.Width; layer.Bottom = layer.Top + image.Height;
            layer.ImageCrop = image;
            layer.Cropped = true;
        }
        if (!layer.HasMask) return;
        var mask = Crop(layer.MaskLeft, layer.MaskTop, layer.MaskRight, layer.MaskBottom, width, height);
        if (mask.X != 0 || mask.Y != 0 || mask.Width != layer.MaskRight - layer.MaskLeft || mask.Height != layer.MaskBottom - layer.MaskTop)
        {
            layer.MaskLeft += mask.X; layer.MaskTop += mask.Y;
            layer.MaskRight = layer.MaskLeft + mask.Width; layer.MaskBottom = layer.MaskTop + mask.Height;
            layer.MaskCrop = mask;
            layer.Cropped = true;
        }
    }

    private static PsdCrop Crop(int left, int top, int right, int bottom, int canvasWidth, int canvasHeight)
    {
        int l = Math.Min(canvasWidth, Math.Max(0, left)), t = Math.Min(canvasHeight, Math.Max(0, top));
        int r = Math.Max(l, Math.Min(canvasWidth, right)), b = Math.Max(t, Math.Min(canvasHeight, bottom));
        return new PsdCrop(l - left, t - top, r - l, b - t);
    }

    private static void DecodeChannels(ref Cursor cursor, RawLayer layer, long remaining, bool isPsb)
    {
        var planes = new Dictionary<int, byte[]>();
        int width = Math.Max(0, layer.Right - layer.Left), height = Math.Max(0, layer.Bottom - layer.Top);
        int maskWidth = Math.Max(0, layer.MaskRight - layer.MaskLeft), maskHeight = Math.Max(0, layer.MaskBottom - layer.MaskTop);
        if (!FitsBudget(width, height, maskWidth, maskHeight, layer.HasMask, remaining)) throw ImageImportException.TooLarge();
        int sourceWidth = Math.Max(0, layer.SourceRight - layer.SourceLeft), sourceHeight = Math.Max(0, layer.SourceBottom - layer.SourceTop);
        int sourceMaskWidth = Math.Max(0, layer.SourceMaskRight - layer.SourceMaskLeft), sourceMaskHeight = Math.Max(0, layer.SourceMaskBottom - layer.SourceMaskTop);
        foreach (var (id, length) in layer.Channels)
        {
            int start = cursor.Offset;
            try
            {
                if (!UnpackedChannels.Contains(id) || length < 2) continue;
                int compression = cursor.U16();
                bool isMask = id == -2;
                int targetW = isMask ? maskWidth : width, targetH = isMask ? maskHeight : height;
                if (targetW <= 0 || targetH <= 0) continue;
                cursor.Need(length - 2);
                var payload = new ReadOnlySpan<byte>(cursor.Data, cursor.Offset, length - 2);
                planes[id] = PsdChannelCoder.Decode(compression, isMask ? sourceMaskWidth : sourceWidth, isMask ? sourceMaskHeight : sourceHeight,
                    payload, isPsb, isMask ? layer.MaskCrop : layer.ImageCrop);
            }
            finally { cursor.Offset = start + Math.Max(0, length); }
        }
        if (layer.HasMask && maskWidth > 0 && maskHeight > 0 && planes.TryGetValue(-2, out var gray) && gray.Length >= maskWidth * maskHeight)
            layer.MaskPlane = gray;
        if (width <= 0 || height <= 0) return;
        int n = width * height;
        var red = planes.GetValueOrDefault(0); var green = planes.GetValueOrDefault(1); var blue = planes.GetValueOrDefault(2);
        var alpha = planes.GetValueOrDefault(-1);
        if ((red is not null && red.Length < n) || (green is not null && green.Length < n) || (blue is not null && blue.Length < n)
            || (alpha is not null && alpha.Length < n)) throw PsdException.Truncated();
        var rgba = new byte[n * 4];
        for (int i = 0; i < n; i++)
        {
            int a = alpha?[i] ?? 255;
            rgba[i * 4] = (byte)(((red?[i] ?? 0) * a + 127) / 255);
            rgba[i * 4 + 1] = (byte)(((green?[i] ?? 0) * a + 127) / 255);
            rgba[i * 4 + 2] = (byte)(((blue?[i] ?? 0) * a + 127) / 255);
            rgba[i * 4 + 3] = (byte)a;
        }
        layer.Image = RasterImage.FromRgba(rgba, width, height, layer.Name);
    }

    private static List<PsdRecord> Assemble(List<RawLayer> raw, SizeD canvas, long remaining, double globalAngle = PsdEffects.DefaultGlobalAngle)
    {
        var result = new List<PsdRecord>();
        var groups = new List<Guid>();
        remaining = Math.Max(0, remaining);
        foreach (var layer in raw)
        {
            // Photoshop stores groups bottom-to-top: a type 3 divider, then the children, then the folder (type 1 or 2).
            if (layer.Section == 3) { groups.Add(Guid.NewGuid()); continue; }
            bool isGroup = layer.Section is 1 or 2;
            Guid id = Guid.NewGuid();
            if (isGroup && groups.Count > 0) { id = groups[^1]; groups.RemoveAt(groups.Count - 1); }
            var record = new PsdRecord
            {
                Id = id,
                Name = string.IsNullOrEmpty(layer.Name) ? "Layer" : layer.Name,
                ParentId = groups.Count > 0 ? groups[^1] : null,
                IsGroup = isGroup,
                IsVisible = !layer.Hidden,
                BlendKey = isGroup && layer.BlendKey is "pass" or "norm" ? "pass" : layer.BlendKey,
                Clipping = layer.Clipping,
                Kind = KindOf(layer, isGroup),
                CroppedToCanvas = layer.Cropped,
            };
            bool hasEffects = record.Kind == PsdLayerKind.Effects || layer.Extra.Keys.Any(k => k is "lfx2" or "lrFX" or "lmfx");
            record.Opacity = hasEffects && layer.Fill != 255 ? layer.Opacity / 255.0 : layer.Opacity / 255.0 * (layer.Fill / 255.0);
            record.Bounds = isGroup ? new RectI(0, 0, (int)canvas.Width, (int)canvas.Height)
                : new RectI(layer.Left, layer.Top, Math.Max(0, layer.Right - layer.Left), Math.Max(0, layer.Bottom - layer.Top));
            record.Image = isGroup ? null : layer.Image;
            if (!isGroup && record.Kind != PsdLayerKind.Text && PsdVector.Live(layer.Extra, canvas, remaining) is { } live)
            {
                record.Image = live.Image;
                record.Bounds = live.Bounds;
                record.Shape = live.Shape;
                record.ShapeNotes = live.Notes;
                record.Kind = PsdLayerKind.Vector;
                remaining = Math.Max(0, remaining - (long)live.Image.Width * live.Image.Height);
            }
            else if (record.Image is null && !isGroup && PsdVector.Raster(layer.Extra, canvas, remaining) is { } raster)
            {
                record.Image = raster.Image;
                record.Bounds = raster.Bounds;
                record.Kind = PsdLayerKind.Vector;
                remaining = Math.Max(0, remaining - (long)raster.Image.Width * raster.Image.Height);
            }
            record.Mask = layer.MaskFromRender ? null : layer.MaskPlane;
            record.MaskBounds = new RectI(layer.MaskLeft, layer.MaskTop, layer.MaskRight - layer.MaskLeft, layer.MaskBottom - layer.MaskTop);
            record.MaskDefault = layer.MaskDefault;
            record.MaskEnabled = !layer.MaskDisabled;
            record.MaskLinked = layer.MaskLinked;
            if (!isGroup) record.Adjustment = PsdAdjustments.Parse(layer.Extra);
            if (record.Adjustment is not null) record.Kind = PsdLayerKind.Adjustment;
            // Type and layer styles: anything unreadable simply stays pixels, as before.
            if (record.Kind == PsdLayerKind.Text)
            {
                try { record.Text = PsdText.Parse(layer.Extra); } catch { record.Text = null; }
            }
            if (!isGroup && record.Adjustment is null)
            {
                try { record.Effects = PsdEffects.Parse(layer.Extra, globalAngle); } catch { record.Effects = null; }
            }
            result.Add(record);
        }
        if (groups.Count != 0) throw PsdException.Truncated();
        return result;
    }

    private static PsdLayerKind KindOf(RawLayer layer, bool isGroup)
    {
        if (isGroup) return PsdLayerKind.Group;
        var keys = layer.Extra.Keys;
        if (keys.Any(k => k is "TySh" or "tySh" or "txt2")) return PsdLayerKind.Text;
        if (keys.Any(k => k is "vmsk" or "vsms" or "vogk")) return PsdLayerKind.Vector;
        if (keys.Any(k => k is "SoLd" or "SoLE")) return PsdLayerKind.SmartObject;
        if (keys.Any(k => k is "lfx2" or "lrFX" or "lmfx")) return PsdLayerKind.Effects;
        if (keys.Any(AdjustmentKeys.Contains)) return PsdLayerKind.Adjustment;
        return PsdLayerKind.Raster;
    }

    public static readonly HashSet<string> AdjustmentKeys = new()
    {
        "levl", "curv", "hue2", "hue ", "expA", "grdm", "brit", "blnc", "nvrt", "thrs", "post", "mixr", "selc", "blwh", "phfl", "vibA",
    };

    /// <summary>Big-endian reads over the file, failing cleanly past its end.</summary>
    private ref struct Cursor
    {
        public readonly byte[] Data;
        public int Offset;
        public Cursor(byte[] data) { Data = data; Offset = 0; }

        public void Need(int count)
        {
            if (count < 0 || Offset < 0 || (long)Offset + count > Data.Length) throw PsdException.Truncated();
        }

        public void Skip(int count) { Need(count); Offset += count; }
        public byte U8() { Need(1); return Data[Offset++]; }
        public int U16() { Need(2); int v = Data[Offset] << 8 | Data[Offset + 1]; Offset += 2; return v; }
        public short I16() => (short)U16();
        public uint U32() { Need(4); uint v = BigEndian.U32(Data, Offset); Offset += 4; return v; }
        public int I32() => (int)U32();
        public ulong U64() { Need(8); ulong v = (ulong)BigEndian.U32(Data, Offset) << 32 | BigEndian.U32(Data, Offset + 4); Offset += 8; return v; }
        public byte[] Bytes(int count) { Need(count); var b = Data.AsSpan(Offset, count).ToArray(); Offset += count; return b; }
        public string Text(int count) { Need(count); var s = Encoding.ASCII.GetString(Data, Offset, count); Offset += count; return s; }
    }
}

internal static class BigEndian
{
    public static uint U32(byte[] d, int o) => (uint)(d[o] << 24 | d[o + 1] << 16 | d[o + 2] << 8 | d[o + 3]);
    public static uint U32(ReadOnlySpan<byte> d, int o) => (uint)(d[o] << 24 | d[o + 1] << 16 | d[o + 2] << 8 | d[o + 3]);
    public static int U16(byte[] d, int o) => d[o] << 8 | d[o + 1];
    public static short I16(byte[] d, int o) => (short)U16(d, o);
    public static int I32(byte[] d, int o) => (int)U32(d, o);
}

public readonly record struct PsdCrop(int X, int Y, int Width, int Height);

/// <summary>Unpacks layer channels: compression 0 (raw) and 1 (PackBits). Mirrors PSDChannelCoder.swift.</summary>
public static class PsdChannelCoder
{
    public static byte[] Decode(int compression, int width, int height, ReadOnlySpan<byte> data, bool largeDocument = false, PsdCrop? crop = null)
    {
        if (width <= 0 || height <= 0) return Array.Empty<byte>();
        var c = crop ?? new PsdCrop(0, 0, width, height);
        if (c.X < 0 || c.Y < 0 || c.Width < 0 || c.Height < 0 || c.X + c.Width > width || c.Y + c.Height > height) throw PsdException.Truncated();
        if (c.Width == 0 || c.Height == 0) return Array.Empty<byte>();
        var plane = new byte[c.Width * c.Height];
        switch (compression)
        {
            case 0:
                if (data.Length < width * height) throw PsdException.Truncated();
                for (int row = 0; row < c.Height; row++)
                    data.Slice((c.Y + row) * width + c.X, c.Width).CopyTo(plane.AsSpan(row * c.Width));
                return plane;
            case 1:
                UnpackRle(width, height, data, largeDocument, c, plane);
                return plane;
            default:
                throw PsdException.UnsupportedCompression();
        }
    }

    private static void UnpackRle(int width, int height, ReadOnlySpan<byte> data, bool largeDocument, PsdCrop crop, byte[] plane)
    {
        int offset = 0;
        var counts = new int[height];
        int countBytes = largeDocument ? 4 : 2;
        if ((long)height * countBytes > data.Length) throw PsdException.Truncated();
        for (int row = 0; row < height; row++)
        {
            counts[row] = largeDocument ? (int)BigEndian.U32(data, offset) : data[offset] << 8 | data[offset + 1];
            offset += countBytes;
        }
        var rowBuffer = new byte[width];
        for (int row = 0; row < height; row++)
        {
            int end = offset + counts[row];
            if (end > data.Length || end < offset) throw PsdException.Truncated();
            if (row < crop.Y || row >= crop.Y + crop.Height) { offset = end; continue; }
            int written = 0;
            while (written < width)
            {
                if (offset >= end) throw PsdException.Truncated();
                int n = (sbyte)data[offset++];
                if (n >= 0)
                {
                    int count = n + 1;
                    if (written + count > width || offset + count > end) throw PsdException.Truncated();
                    data.Slice(offset, count).CopyTo(rowBuffer.AsSpan(written));
                    offset += count;
                    written += count;
                }
                else if (n != -128)
                {
                    int count = 1 - n;
                    if (written + count > width || offset >= end) throw PsdException.Truncated();
                    rowBuffer.AsSpan(written, count).Fill(data[offset++]);
                    written += count;
                }
            }
            rowBuffer.AsSpan(crop.X, crop.Width).CopyTo(plane.AsSpan((row - crop.Y) * crop.Width));
            offset = end;
        }
    }
}

/// <summary>Adjustment layers read from their additional layer info: Levels, Curves and Hue/Saturation. Mirrors PSDAdjustments.</summary>
public static class PsdAdjustments
{
    public static LayerAdjustment? Parse(Dictionary<string, byte[]> extra)
    {
        if (extra.TryGetValue("levl", out var levels)) return Levels(levels);
        if (extra.TryGetValue("curv", out var curves)) return Curves(curves);
        if (extra.TryGetValue("hue2", out var hue) || extra.TryGetValue("hue ", out hue)) return Hue(hue);
        return null;
    }

    /// <summary>'levl': a version, then input black, input white, output black, output white and gamma (in hundredths) for RGB, red, green, blue.</summary>
    public static LayerAdjustment? Levels(byte[] data)
    {
        if (data.Length < 292) return null;
        var adjustment = new LayerAdjustment(AdjustmentKind.Levels);
        for (int channel = 0; channel < 4; channel++)
        {
            int b = 2 + channel * 10;
            adjustment.Levels.Ranges[channel] = new LevelRange(BigEndian.U16(data, b), BigEndian.U16(data, b + 8) / 100.0, BigEndian.U16(data, b + 2),
                BigEndian.U16(data, b + 4), BigEndian.U16(data, b + 6)).Normalized;
        }
        return adjustment;
    }

    public static LayerAdjustment? Curves(byte[] data)
    {
        if (data.Length < 5) return null;
        int offset = data[0] == 0 ? 1 : 0;
        if (offset + 2 > data.Length) return null;
        int version = BigEndian.U16(data, offset);
        offset += 2;
        if (version is not (1 or 4) || offset + 2 > data.Length) return null;
        int count = BigEndian.U16(data, offset);
        offset += 2;
        var adjustment = new LayerAdjustment(AdjustmentKind.Curves);
        for (int channel = 0; channel < Math.Min(4, count); channel++)
        {
            if (offset + 2 > data.Length) return null;
            int points = BigEndian.U16(data, offset);
            offset += 2;
            var curve = new List<CurvePoint>();
            for (int i = 0; i < points; i++)
            {
                if (offset + 4 > data.Length) return null;
                double output = BigEndian.U16(data, offset), input = BigEndian.U16(data, offset + 2);
                offset += 4;
                curve.Add(new CurvePoint(Math.Clamp(input, 0, 255), Math.Clamp(output, 0, 255)));
            }
            if (curve.Count < 2) continue;
            curve.Sort((a, b) => a.X.CompareTo(b.X));
            if (curve[0].X != 0) curve.Insert(0, new CurvePoint(0, curve[0].Y));
            if (curve[^1].X != 255) curve.Add(new CurvePoint(255, curve[^1].Y));
            adjustment.Curves.Channels[channel] = curve;
        }
        return adjustment.Curves.IsValid ? adjustment : null;
    }

    /// <summary>
    /// 'hue2': a version, the Colorize switch, the Colorize hue/saturation/lightness, the Master's, then for Reds
    /// through Magentas the band (fade in, full, fade out, in degrees) and its hue, saturation and lightness.
    /// </summary>
    public static LayerAdjustment? Hue(byte[] data)
    {
        if (data.Length < 16) return null;
        bool colorize = data[2] != 0;
        var settings = new HueSaturationSettings { Colorize = colorize };
        RangeAdjustment Values(int at) => new(BigEndian.I16(data, at), BigEndian.I16(data, at + 2), BigEndian.I16(data, at + 4));
        settings.Adjustments[ColorRange.Master] = Values(colorize ? 4 : 10);
        if (!colorize)
        {
            int offset = 16;
            foreach (var range in ColorRanges.Colors)
            {
                if (offset + 14 > data.Length) break;
                double Degrees(int at) { double v = BigEndian.I16(data, at) % 360.0; return v < 0 ? v + 360 : v; }
                settings.Bands[range] = new HueBand(Degrees(offset), Degrees(offset + 2), Degrees(offset + 4), Degrees(offset + 6));
                settings.Adjustments[range] = Values(offset + 8);
                offset += 14;
            }
        }
        return new LayerAdjustment(AdjustmentKind.HueSaturation) { HsvSettings = settings };
    }
}
