using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Compositor.Core.Adjustments;
using Compositor.Core.Geometry;
using Compositor.Core.Localization;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Core.IO;

public sealed class ProjectException : Exception
{
    public ProjectException(string message, Exception? inner = null) : base(message, inner) { }

    public static ProjectException Invalid() =>
        new(L.T("这不是有效的 Compositor 项目，或者它的元数据已损坏。", "This is not a valid Compositor project, or its metadata is damaged."));
    public static ProjectException Version(int version) =>
        new(L.T($"此项目使用格式版本 {version}。本程序支持版本 {ProjectManifest.FirstSupported}–{ProjectManifest.Current}。",
                $"This project uses format version {version}. This app supports versions {ProjectManifest.FirstSupported}–{ProjectManifest.Current}."));
    public static ProjectException MissingImage() =>
        new(L.T("项目中的某个图像丢失或已损坏。当前文档没有被替换。",
                "An image inside the project is missing or damaged. The current document has not been replaced."));
    public static ProjectException TooLarge() =>
        new(L.T($"此项目超出了支持的画布、图层、文件大小或 {DocumentLimits.DocumentBudgetMegapixels} 百万像素的文档上限。",
                $"This project exceeds the supported canvas, layer, file-size, or {DocumentLimits.DocumentBudgetMegapixels}-megapixel document limit."));
    public static ProjectException Encode() =>
        new(L.T("有图像无法保存。原来的项目没有被替换。", "An image could not be saved. The previous project has not been replaced."));
}

/// <summary>The manifest's constants. Mirrors ProjectManifest in ProjectStore.swift.</summary>
public static class ProjectManifest
{
    public const string Format = "com.compositor.project";
    public const int Current = 11;
    public const int FirstSupported = 1;
    public const string ManifestName = "manifest.json";
    public const string ImagesFolder = "images";
    public const int MaxManifestBytes = 4 * 1024 * 1024;
    public const long MaxAssetBytes = 512L * 1024 * 1024;
}

/// <summary>What a project package holds, decoded: the document and the active layer.</summary>
public sealed record ProjectSnapshot(CanvasDocument Document, Guid? ActiveLayerId);

/// <summary>
/// Reads and writes Compositor projects: a folder named *.comp holding manifest.json and images/&lt;UUID&gt;.png
/// (plus &lt;UUID&gt;.mask.png). The same package the Mac app writes, so projects move between the two.
/// </summary>
public static class ProjectStore
{
    public const string Extension = ".comp";

    // MARK: Save

    public static void Save(ProjectSnapshot snapshot, string path, SKImage? preview = null)
    {
        var document = snapshot.Document;
        var manifest = BuildManifest(document, snapshot.ActiveLayerId);
        Validate(manifest, out _);

        string full = Path.GetFullPath(path);
        string parent = Path.GetDirectoryName(full) ?? throw ProjectException.Invalid();
        Directory.CreateDirectory(parent);
        string staging = Path.Combine(parent, "." + Path.GetFileName(full) + ".saving-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(staging);
        try
        {
            string images = Path.Combine(staging, ProjectManifest.ImagesFolder);
            Directory.CreateDirectory(images);
            long pixels = 0, maskPixels = 0;
            foreach (var layer in document.Layers)
            {
                if (layer.Asset is { } asset)
                {
                    CheckSize(asset.Width, asset.Height, ref pixels);
                    WriteAsset(Path.Combine(images, FileName(layer.Id, false)), asset, mask: false);
                }
                if (layer.Mask is { } mask)
                {
                    CheckSize(mask.Asset.Width, mask.Asset.Height, ref maskPixels);
                    WriteAsset(Path.Combine(images, FileName(layer.Id, true)), mask.Asset, mask: true);
                }
            }
            // Unicode names written as they are (UTF-8), as the Mac app's encoder writes them.
            var json = manifest.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
            var bytes = new UTF8Encoding(false).GetBytes(json);
            if (bytes.Length > ProjectManifest.MaxManifestBytes) throw ProjectException.TooLarge();
            File.WriteAllBytes(Path.Combine(staging, ProjectManifest.ManifestName), bytes);
            if (preview is not null)
            {
                // The Mac's Quick Look preview; loading ignores it.
                string quickLook = Path.Combine(staging, "QuickLook");
                Directory.CreateDirectory(quickLook);
                using var data = preview.Encode(SKEncodedImageFormat.Jpeg, 85);
                if (data is not null) File.WriteAllBytes(Path.Combine(quickLook, "Preview.jpg"), data.ToArray());
            }

            // Replace the destination only once the complete package has been written.
            if (Directory.Exists(full))
            {
                string backup = staging + ".old";
                Directory.Move(full, backup);
                try { Directory.Move(staging, full); }
                catch
                {
                    Directory.Move(backup, full);
                    throw;
                }
                TryDelete(backup);
            }
            else
            {
                if (File.Exists(full)) File.Delete(full);
                Directory.Move(staging, full);
            }
        }
        catch
        {
            TryDelete(staging);
            throw;
        }
    }

    private static void TryDelete(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch { /* best effort */ }
    }

    private static void WriteAsset(string file, RasterImage asset, bool mask)
    {
        SKData? data;
        if (mask)
        {
            // Masks are stored as 8-bit grayscale without alpha (white reveals, black hides).
            var gray = new SKImageInfo(asset.Width, asset.Height, SKColorType.Gray8, SKAlphaType.Opaque);
            using var pixmap = asset.PeekPixels();
            using var grayPixmap = new SKPixmap(gray, pixmap.GetPixels(), pixmap.RowBytes);
            data = grayPixmap.Encode(SKEncodedImageFormat.Png, 100);
        }
        else
        {
            data = asset.Image.Encode(SKEncodedImageFormat.Png, 100);
        }
        if (data is null) throw ProjectException.Encode();
        using (data)
        {
            if (data.Size > ProjectManifest.MaxAssetBytes) throw ProjectException.TooLarge();
            using var stream = File.Create(file);
            data.SaveTo(stream);
        }
    }

    public static string FileName(Guid id, bool mask) => GuidText(id) + (mask ? ".mask.png" : ".png");
    public static string GuidText(Guid id) => id.ToString("D").ToUpperInvariant();

    public static JsonObject BuildManifest(CanvasDocument document, Guid? activeLayerId)
    {
        var layers = new JsonArray();
        foreach (var layer in document.Layers) layers.Add(LayerRecord(layer));
        var root = new SortedJson
        {
            ["format"] = ProjectManifest.Format,
            ["version"] = ProjectManifest.Current,
            ["colorSpace"] = "sRGB",
            ["resolution"] = document.Resolution,
            ["documentID"] = GuidText(document.Id),
            ["width"] = document.Width,
            ["height"] = document.Height,
            ["layers"] = layers,
        };
        if (activeLayerId is { } active && document.Find(active) is not null) root["activeLayerID"] = GuidText(active);
        if (!document.Guides.IsEmpty)
        {
            var guides = new JsonArray();
            foreach (var guide in document.Guides)
            {
                guides.Add(new SortedJson
                {
                    ["id"] = GuidText(guide.Id),
                    ["axis"] = guide.Axis == GuideAxis.Horizontal ? "horizontal" : "vertical",
                    ["position"] = guide.Position,
                }.Build());
            }
            root["guides"] = guides;
        }
        return root.Build();
    }

    private static JsonObject LayerRecord(Layer layer)
    {
        var record = new SortedJson
        {
            ["id"] = GuidText(layer.Id),
            ["name"] = layer.Name,
            ["isVisible"] = layer.IsVisible,
            ["transform"] = TransformJson(layer.Transform),
            ["isGroup"] = layer.IsGroup,
            ["opacity"] = layer.Opacity,
            ["blendMode"] = layer.BlendMode.ToName(),
        };
        if (layer.Asset is not null) record["imageFile"] = FileName(layer.Id, false);
        if (layer.ParentId is { } parent) record["parentID"] = GuidText(parent);
        if (layer.Mask is { } mask)
        {
            record["maskFile"] = FileName(layer.Id, true);
            record["maskEnabled"] = mask.IsEnabled;
            record["maskLinked"] = mask.IsLinked;
            if (mask.Placement is { } placement) record["maskPlacement"] = TransformJson(placement);
        }
        if (layer.MaskSourceId is { } source) record["maskSourceID"] = GuidText(source);
        if (layer.Adjustment is not null) record["adjustment"] = Sorted(layer.Adjustment.ToJson());
        if (layer.Shape is not null) record["shape"] = Sorted(layer.Shape.ToJson());
        if (layer.Effects is { IsEmpty: false }) record["effects"] = Sorted(layer.Effects.ToJson());
        if (layer.Text is not null) record["text"] = Sorted(layer.Text.ToJson());
        return record.Build();
    }

    /// <summary>A copy of a JSON tree with every object's keys in ordinal order, as the Mac's sortedKeys encoder writes.</summary>
    public static JsonNode? Sorted(JsonNode? node) => node switch
    {
        JsonObject o => SortObject(o),
        JsonArray a => new JsonArray(a.Select(Sorted).ToArray()),
        null => null,
        _ => node.DeepClone(),
    };

    private static JsonObject SortObject(JsonObject o)
    {
        var sorted = new JsonObject();
        foreach (var (key, value) in o.OrderBy(p => p.Key, StringComparer.Ordinal)) sorted[key] = Sorted(value);
        return sorted;
    }

    public static JsonObject TransformJson(LayerTransform t) => new SortedJson
    {
        ["origin"] = new JsonArray(t.Origin.X, t.Origin.Y),
        ["size"] = new JsonArray(t.Size.Width, t.Size.Height),
        ["rotation"] = t.Rotation,
        ["flipX"] = t.FlipX,
        ["flipY"] = t.FlipY,
        ["sampling"] = t.Sampling.ToName(),
    }.Build();

    /// <summary>Builds a JsonObject with keys in ordinal order, as the Mac app's sortedKeys encoder writes them.</summary>
    private sealed class SortedJson : Dictionary<string, JsonNode?>
    {
        public JsonObject Build()
        {
            var obj = new JsonObject();
            foreach (var key in Keys.OrderBy(k => k, StringComparer.Ordinal)) obj[key] = this[key];
            return obj;
        }
    }

    // MARK: Load

    public static ProjectSnapshot Load(string path)
    {
        string root = Path.GetFullPath(path);
        if (File.Exists(root) && string.Equals(Path.GetFileName(root), ProjectManifest.ManifestName, StringComparison.OrdinalIgnoreCase))
            root = Path.GetDirectoryName(root)!;
        if (!Directory.Exists(root)) throw ProjectException.Invalid();
        string manifestPath = Path.Combine(root, ProjectManifest.ManifestName);
        CheckFile(manifestPath, root, ProjectManifest.MaxManifestBytes);
        JsonObject manifest;
        try
        {
            manifest = JsonNode.Parse(File.ReadAllBytes(manifestPath)) as JsonObject ?? throw ProjectException.Invalid();
        }
        catch (JsonException) { throw ProjectException.Invalid(); }

        if (Str(manifest, "format") != ProjectManifest.Format) throw ProjectException.Invalid();
        int version = Int(manifest, "version") ?? throw ProjectException.Invalid();
        if (version < ProjectManifest.FirstSupported || version > ProjectManifest.Current) throw ProjectException.Version(version);

        var records = Validate(manifest, out var parsed);
        string images = Path.Combine(root, ProjectManifest.ImagesFolder);
        long pixels = 0, maskPixels = 0;
        var layers = new List<Layer>();
        foreach (var record in records)
        {
            RasterImage? asset = null;
            LayerMask? mask = null;
            if (record.ImageFile is { } imageFile)
            {
                string file = Path.Combine(images, imageFile);
                CheckFile(file, root, ProjectManifest.MaxAssetBytes);
                asset = ReadPng(file, isMask: false, record.Name, ref pixels);
            }
            if (record.MaskFile is { } maskFile)
            {
                string file = Path.Combine(images, maskFile);
                CheckFile(file, root, ProjectManifest.MaxAssetBytes);
                var maskAsset = ReadPng(file, isMask: true, "Layer Mask", ref maskPixels);
                mask = new LayerMask(maskAsset, record.MaskEnabled ?? true, record.MaskPlacement, record.MaskLinked ?? true);
            }
            layers.Add(new Layer
            {
                Id = record.Id,
                Name = record.Name,
                Asset = asset,
                Transform = record.Transform,
                IsVisible = record.IsVisible,
                ParentId = record.ParentId,
                IsGroup = record.IsGroup,
                Opacity = record.Opacity,
                BlendMode = record.BlendMode,
                MaskSourceId = record.MaskSourceId,
                Mask = mask,
                Adjustment = record.Adjustment,
                Shape = record.Shape,
                Effects = record.Effects,
                Text = record.Text,
            });
        }
        var document = new CanvasDocument
        {
            Id = parsed.DocumentId,
            Width = parsed.Width,
            Height = parsed.Height,
            Resolution = parsed.Resolution,
            Layers = layers.ToImmutableListCompat(),
            Guides = parsed.Guides.ToImmutableListCompat(),
        };
        return new ProjectSnapshot(document, parsed.ActiveLayerId);
    }

    private static RasterImage ReadPng(string file, bool isMask, string name, ref long used)
    {
        // Decoded from the file's bytes in memory, so nothing stays tied to a file the next save replaces.
        var bytes = File.ReadAllBytes(file);
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data) ?? throw ProjectException.MissingImage();
        if (codec.EncodedFormat != SKEncodedImageFormat.Png) throw ProjectException.MissingImage();
        var info = codec.Info;
        CheckSize(info.Width, info.Height, ref used);
        if (isMask)
        {
            // Read as 8-bit gray: what the Mac writes. A color PNG is reduced to its luminance.
            if (info.ColorType == SKColorType.Gray8)
            {
                var gray = new SKImageInfo(info.Width, info.Height, SKColorType.Gray8, SKAlphaType.Opaque);
                using var bitmap = new SKBitmap(gray);
                var result = codec.GetPixels(gray, bitmap.GetPixels());
                if (result == SKCodecResult.Success || result == SKCodecResult.IncompleteInput)
                    return RasterImage.FromMask(bitmap.GetPixelSpan(), info.Width, info.Height, name, bitmap.RowBytes);
            }
            var rgba = new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            using var color = new SKBitmap(rgba);
            var decoded = codec.GetPixels(rgba, color.GetPixels());
            if (decoded != SKCodecResult.Success && decoded != SKCodecResult.IncompleteInput) throw ProjectException.MissingImage();
            var source = color.GetPixelSpan();
            var coverage = new byte[info.Width * info.Height];
            for (int i = 0, p = 0; i < coverage.Length; i++, p += 4)
                coverage[i] = (byte)((source[p] * 77 + source[p + 1] * 150 + source[p + 2] * 29 + 128) >> 8);
            return RasterImage.FromMask(coverage, info.Width, info.Height, name);
        }
        else
        {
            var target = RasterImage.ColorInfo(info.Width, info.Height);
            using var bitmap = new SKBitmap(target);
            var result = codec.GetPixels(target, bitmap.GetPixels());
            if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput) throw ProjectException.MissingImage();
            return RasterImage.FromBitmap(bitmap, false, name);
        }
    }

    private static void CheckFile(string file, string root, long maximumBytes)
    {
        string full = Path.GetFullPath(file);
        string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw ProjectException.Invalid();
        var info = new FileInfo(full);
        if (!info.Exists) throw ProjectException.MissingImage();
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw ProjectException.Invalid();
        if (info.Length > maximumBytes) throw ProjectException.TooLarge();
    }

    private static void CheckSize(int width, int height, ref long used)
    {
        if (width < 1 || width > DocumentLimits.MaxSide || height < 1 || height > DocumentLimits.MaxSide
            || (long)width * height > DocumentLimits.DocumentPixelBudget - used) throw ProjectException.TooLarge();
        used += (long)width * height;
    }

    // MARK: Validation

    public sealed record LayerRecordData(
        Guid Id, string Name, bool IsVisible, LayerTransform Transform, string? ImageFile, Guid? ParentId, bool IsGroup,
        double Opacity, BlendMode BlendMode, string? MaskFile, bool? MaskEnabled, Guid? MaskSourceId,
        LayerTransform? MaskPlacement, bool? MaskLinked, LayerAdjustment? Adjustment, LayerShape? Shape, LayerEffects? Effects, LayerText? Text);

    public sealed record ManifestData(int Version, Guid DocumentId, int Width, int Height, double Resolution,
                                      Guid? ActiveLayerId, List<CanvasGuide> Guides);

    /// <summary>Checks everything ProjectStore.validate checks on the Mac; throws on the first problem.</summary>
    public static List<LayerRecordData> Validate(JsonObject manifest, out ManifestData data)
    {
        if (Str(manifest, "format") != ProjectManifest.Format) throw ProjectException.Invalid();
        int version = Int(manifest, "version") ?? throw ProjectException.Invalid();
        if (version < ProjectManifest.FirstSupported || version > ProjectManifest.Current) throw ProjectException.Version(version);
        if (Str(manifest, "colorSpace") != "sRGB") throw ProjectException.Invalid();
        double resolution = 72;
        if (manifest["resolution"] is not null)
        {
            resolution = Num(manifest, "resolution") ?? throw ProjectException.Invalid();
            if (!double.IsFinite(resolution) || resolution < 1 || resolution > 9600) throw ProjectException.Invalid();
        }
        var documentId = Uuid(manifest, "documentID") ?? throw ProjectException.Invalid();
        int width = Int(manifest, "width") ?? throw ProjectException.Invalid();
        int height = Int(manifest, "height") ?? throw ProjectException.Invalid();
        if (!CanvasDocument.ValidDimension(width) || !CanvasDocument.ValidDimension(height)) throw ProjectException.TooLarge();
        if (manifest["layers"] is not JsonArray layerArray) throw ProjectException.Invalid();
        if (layerArray.Count > DocumentLimits.MaxLayers) throw ProjectException.TooLarge();

        var records = new List<LayerRecordData>();
        foreach (var node in layerArray)
        {
            if (node is not JsonObject layer) throw ProjectException.Invalid();
            records.Add(ParseLayer(layer, version));
        }

        // Hierarchy: unique ids, parents are folders, no cycles, at most 64 levels; folders hold no image.
        var byId = new Dictionary<Guid, LayerRecordData>();
        foreach (var r in records)
        {
            if (!byId.TryAdd(r.Id, r)) throw ProjectException.Invalid();
            if (r.IsGroup && r.ImageFile is not null) throw ProjectException.Invalid();
        }
        foreach (var r in records)
        {
            var seen = new HashSet<Guid> { r.Id };
            var parent = r.ParentId;
            while (parent is { } id)
            {
                if (seen.Count > 64 || !seen.Add(id) || !byId.TryGetValue(id, out var node) || !node.IsGroup) throw ProjectException.Invalid();
                parent = node.ParentId;
            }
            if (r.IsGroup && seen.Count > 64) throw ProjectException.Invalid();
        }
        // Clipping (live mask) links: existing, non-group endpoints, no self-links or cycles, chains under 256.
        foreach (var r in records)
        {
            if (r.MaskSourceId is null) continue;
            if (r.IsGroup) throw ProjectException.Invalid();
            var seen = new HashSet<Guid> { r.Id };
            var source = r.MaskSourceId;
            while (source is { } id)
            {
                if (seen.Count > 256 || !seen.Add(id) || !byId.TryGetValue(id, out var node) || node.IsGroup) throw ProjectException.Invalid();
                source = node.MaskSourceId;
            }
        }
        if (version < 5 && records.Any(r => r.MaskSourceId is not null)) throw ProjectException.Invalid();
        if (version == 1 && records.Any(r => r.ParentId is not null || r.IsGroup)) throw ProjectException.Invalid();

        Guid? active = null;
        if (manifest["activeLayerID"] is not null)
        {
            active = Uuid(manifest, "activeLayerID") ?? throw ProjectException.Invalid();
            if (!byId.ContainsKey(active.Value)) throw ProjectException.Invalid();
        }

        var guides = new List<CanvasGuide>();
        if (manifest["guides"] is JsonArray guideArray)
        {
            if (version < 8 && guideArray.Count > 0) throw ProjectException.Invalid();
            if (guideArray.Count > 1000) throw ProjectException.TooLarge();
            var ids = new HashSet<Guid>();
            foreach (var g in guideArray)
            {
                if (g is not JsonObject guide) throw ProjectException.Invalid();
                var id = Uuid(guide, "id") ?? throw ProjectException.Invalid();
                var axis = Str(guide, "axis") switch
                {
                    "horizontal" => GuideAxis.Horizontal,
                    "vertical" => GuideAxis.Vertical,
                    _ => throw ProjectException.Invalid(),
                };
                double position = Num(guide, "position") ?? throw ProjectException.Invalid();
                if (!ids.Add(id) || !double.IsFinite(position) || Math.Abs(position) > 1_000_000) throw ProjectException.Invalid();
                guides.Add(new CanvasGuide(id, axis, position));
            }
        }
        else if (manifest["guides"] is not null) throw ProjectException.Invalid();

        data = new ManifestData(version, documentId, width, height, resolution, active, guides);
        return records;
    }

    private static LayerRecordData ParseLayer(JsonObject layer, int version)
    {
        var id = Uuid(layer, "id") ?? throw ProjectException.Invalid();
        string name = Str(layer, "name") ?? throw ProjectException.Invalid();
        if (string.IsNullOrWhiteSpace(name) || Encoding.UTF8.GetByteCount(name) > 16_384) throw ProjectException.Invalid();
        bool isVisible = Bool(layer, "isVisible") ?? throw ProjectException.Invalid();
        var transform = ParseTransform(layer["transform"]) ?? throw ProjectException.Invalid();
        if (!transform.IsValid) throw ProjectException.Invalid();
        string? imageFile = layer["imageFile"] is null ? null : Str(layer, "imageFile") ?? throw ProjectException.Invalid();
        if (imageFile is not null && imageFile != FileName(id, false)) throw ProjectException.Invalid();
        Guid? parentId = layer["parentID"] is null ? null : Uuid(layer, "parentID") ?? throw ProjectException.Invalid();
        bool isGroup = Bool(layer, "isGroup") ?? false;
        double opacity = layer["opacity"] is null ? 1 : Num(layer, "opacity") ?? throw ProjectException.Invalid();
        var blend = layer["blendMode"] is null ? BlendMode.Normal
            : BlendModes.Parse(Str(layer, "blendMode")) ?? throw ProjectException.Invalid();
        string? maskFile = layer["maskFile"] is null ? null : Str(layer, "maskFile") ?? throw ProjectException.Invalid();
        bool? maskEnabled = layer["maskEnabled"] is null ? null : Bool(layer, "maskEnabled") ?? throw ProjectException.Invalid();
        Guid? maskSource = layer["maskSourceID"] is null ? null : Uuid(layer, "maskSourceID") ?? throw ProjectException.Invalid();
        LayerTransform? maskPlacement = null;
        if (layer["maskPlacement"] is not null)
        {
            maskPlacement = ParseTransform(layer["maskPlacement"]) ?? throw ProjectException.Invalid();
            if (!maskPlacement.Value.IsValid || maskFile is null) throw ProjectException.Invalid();
        }
        bool? maskLinked = layer["maskLinked"] is null ? null : Bool(layer, "maskLinked") ?? throw ProjectException.Invalid();
        LayerAdjustment? adjustment = null;
        if (layer["adjustment"] is not null) adjustment = LayerAdjustment.FromJson(layer["adjustment"]) ?? throw ProjectException.Invalid();
        // A shape the Windows version can't read is dropped (the pixels stay); the Mac does the same with a stale one.
        var shape = layer["shape"] is { } shapeNode ? LayerShape.FromJson(shapeNode) : null;
        LayerEffects? effects = null;
        if (layer["effects"] is not null) effects = LayerEffects.FromJson(layer["effects"]) ?? throw ProjectException.Invalid();
        LayerText? text = null;

        if (layer["text"] is { } textNode)
        {
            if (textNode is not JsonObject textObject) throw ProjectException.Invalid();
            if (textObject["colorRuns"] is not null && version < 10) throw ProjectException.Invalid();
            if (textObject["fontRuns"] is not null && version < 11) throw ProjectException.Invalid();
            if (imageFile is null || isGroup || adjustment is not null) throw ProjectException.Invalid();
            // Like LayerText.loaded on the Mac: text that isn't valid is kept as plain pixels.
            text = LayerText.FromJson(textObject) is { IsValid: true } parsed ? parsed : null;
        }
        if (adjustment is not null)
        {
            if (version < 7 || isGroup || imageFile is not null || !adjustment.IsValid) throw ProjectException.Invalid();
            if (adjustment.Kind.SamplesNeighbors() && version < 9) throw ProjectException.Invalid();
        }
        if (maskFile is not null)
        {
            if (version < (isGroup ? 6 : 4) || maskFile != FileName(id, true)) throw ProjectException.Invalid();
        }
        if (maskEnabled is not null && maskFile is null) throw ProjectException.Invalid();
        if (!double.IsFinite(opacity) || opacity < 0 || opacity > 1) throw ProjectException.Invalid();
        if (version < 3 && (opacity != 1 || blend != BlendMode.Normal)) throw ProjectException.Invalid();
        if (isGroup && (blend != BlendMode.Normal || (version < 8 && opacity != 1))) throw ProjectException.Invalid();

        return new LayerRecordData(id, name, isVisible, transform, imageFile, parentId, isGroup, opacity, blend, maskFile,
            maskEnabled, maskSource, maskPlacement, maskLinked, adjustment, shape, effects, text);
    }

    public static LayerTransform? ParseTransform(JsonNode? node)
    {
        if (node is not JsonObject t) return null;
        if (t["origin"] is not JsonArray origin || origin.Count != 2 || t["size"] is not JsonArray size || size.Count != 2) return null;
        double? ox = NumOf(origin[0]), oy = NumOf(origin[1]), w = NumOf(size[0]), h = NumOf(size[1]);
        if (ox is null || oy is null || w is null || h is null) return null;
        double rotation = t["rotation"] is null ? 0 : Num(t, "rotation") ?? double.NaN;
        bool flipX = Bool(t, "flipX") ?? false;
        bool flipY = Bool(t, "flipY") ?? false;
        var sampling = t["sampling"] is null ? LayerSampling.High : LayerSamplingNames.Parse(Str(t, "sampling"));
        if (sampling is null || double.IsNaN(rotation)) return null;
        return new LayerTransform(new PointD(ox.Value, oy.Value), new SizeD(w.Value, h.Value), rotation, flipX, flipY, sampling.Value);
    }

    // MARK: JSON helpers

    private static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    private static bool? Bool(JsonObject o, string key)
    {
        if (o[key] is not JsonValue v) return null;
        return v.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static double? NumOf(JsonNode? node)
    {
        if (node is not JsonValue v || v.GetValueKind() != JsonValueKind.Number) return null;
        // Parsed numbers read as any type; ones built in memory only as the type they were made from.
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<float>(out var f)) return f;
        if (v.TryGetValue<decimal>(out var m)) return (double)m;
        return null;
    }

    private static double? Num(JsonObject o, string key) => NumOf(o[key]);

    private static int? Int(JsonObject o, string key)
    {
        var d = Num(o, key);
        if (d is null || d != Math.Floor(d.Value) || d < int.MinValue || d > int.MaxValue) return null;
        return (int)d.Value;
    }

    private static Guid? Uuid(JsonObject o, string key) =>
        Str(o, key) is { } s && Guid.TryParseExact(s, "D", out var g) ? g : null;
}

internal static class ImmutableCompat
{
    public static System.Collections.Immutable.ImmutableList<T> ToImmutableListCompat<T>(this IEnumerable<T> items) =>
        System.Collections.Immutable.ImmutableList.CreateRange(items);
}
