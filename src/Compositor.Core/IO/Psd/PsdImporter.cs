using System.Collections.Immutable;
using Compositor.Core.Geometry;
using Compositor.Core.Localization;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Core.IO.Psd;

/// <summary>A Photoshop file made into a document, and what changed on the way in.</summary>
public sealed record PsdImport(CanvasDocument Document, IReadOnlyList<PsdConversion> Conversions);

/// <summary>
/// Turns a read Photoshop file into layers: pixels, folders, masks on each layer's own grid, blend modes, clipping and
/// the supported adjustment layers, listing everything converted along the way. Mirrors PSDDocumentBuilder.swift.
/// </summary>
public static class PsdImporter
{
    public static string RasterizedTextNote => L.T("可编辑的 Photoshop 文字变成了像素，无法再重新输入。", "Editable Photoshop text becomes pixels and can’t be retyped.");

    public static PsdImport Read(string path, long remainingPixels = long.MaxValue) => Make(PsdReader.Read(path, remainingPixels));

    public static PsdImport Make(PsdDocument psd)
    {
        var conversions = new List<PsdConversion>();
        var layers = new List<Layer>();
        var canvas = new SizeD(psd.Width, psd.Height);
        void Note(PsdRecord r, string message) => conversions.Add(new PsdConversion(r.Name, message));
        foreach (var record in psd.Layers)
        {
            if (record.CroppedToCanvas)
                Note(record, L.T("为了能装进内存，已裁剪到画布：画布以外的像素没有导入。",
                                 "Cropped to the canvas so the file fits in memory. Pixels outside the canvas weren’t imported."));
            // Type that maps onto editable text is drawn again from its source.
            (RasterImage Image, LayerTransform Transform)? rendered = null;
            if (record.Kind == PsdLayerKind.Text && record.Text is { } source)
            {
                try { rendered = PsdText.Render(source, record.Name); } catch { rendered = null; }
            }
            if (record.Effects is { } style)
                foreach (var note in style.Notes) Note(record, note);
            else if (record.Kind == PsdLayerKind.Effects) Note(record, L.T("图层效果已舍弃，外观可能有所不同。", "Layer effects were discarded, so the appearance may differ."));
            switch (record.Kind)
            {
                case PsdLayerKind.Text:
                    if (rendered is not null && record.Text is { } typed)
                    {
                        foreach (var note in typed.Notes) Note(record, note);
                        if (PsdText.MissingFontNote(typed.Style.FontName) is { } missing) Note(record, missing);
                    }
                    else Note(record, RasterizedTextNote);
                    break;
                case PsdLayerKind.SmartObject:
                    Note(record, L.T("智能对象已栅格化，其中链接的内容无法编辑。", "The smart object was rasterized. Linked contents can’t be edited."));
                    break;
                case PsdLayerKind.Vector:
                    if (record.Shape is not null) foreach (var note in record.ShapeNotes) Note(record, note);
                    else Note(record, L.T("矢量形状已栅格化为像素。", "Vector shape was rasterized to pixels."));
                    break;
                case PsdLayerKind.Other:
                    Note(record, L.T("不支持这种 Photoshop 图层类型，已作为像素导入。", "This Photoshop layer type isn’t supported and was imported as pixels."));
                    break;
            }
            if (record.IsGroup)
            {
                if (record.BlendKey is not ("pass" or "norm"))
                    Note(record, L.T($"不支持文件夹混合模式“{record.BlendKey.Trim()}”，文件夹将使用穿透。",
                                     $"Folder blend mode “{record.BlendKey.Trim()}” isn’t supported. The folder will be pass-through."));
            }
            else if (record.BlendMode is null && record.BlendKey != "pass")
            {
                Note(record, L.T($"不支持混合模式“{record.BlendKey.Trim()}”，将按正常模式应用。",
                                 $"Blend mode “{record.BlendKey.Trim()}” isn’t supported and will be applied as Normal."));
            }
            if (record.Kind == PsdLayerKind.Adjustment)
            {
                if (record.Adjustment is null) { Note(record, L.T("不支持这种调整类型，已跳过。", "This adjustment type isn’t supported and was skipped.")); continue; }
                Note(record, L.T("调整参数可能与 Photoshop 不完全一致。", "Adjustment parameters may not match Photoshop exactly."));
            }

            double opacity = Math.Clamp(record.Opacity, 0, 1);
            Layer layer;
            if (record.IsGroup)
            {
                // Folders carry an opacity of their own, multiplying into what's inside them as Photoshop's do.
                layer = Layer.Folder(record.Name, canvas) with { Id = record.Id, ParentId = record.ParentId, IsVisible = record.IsVisible, Opacity = opacity };
            }
            else if (record.Adjustment is { } adjustment)
            {
                layer = Layer.Blank(record.Name, canvas) with
                {
                    Id = record.Id, ParentId = record.ParentId, IsVisible = record.IsVisible, Opacity = opacity,
                    BlendMode = record.BlendMode ?? BlendMode.Normal, Adjustment = adjustment,
                };
            }
            else if (rendered is { } text && record.Text is { } textSource)
            {
                layer = Layer.FromImage(text.Image, text.Transform.Origin, record.Name) with
                {
                    Id = record.Id, ParentId = record.ParentId, IsVisible = record.IsVisible, Opacity = opacity,
                    BlendMode = record.BlendMode ?? BlendMode.Normal, Text = textSource.Style, Transform = text.Transform,
                };
            }
            else if (record.Image is { } image)
            {
                var size = record.Bounds.Width > 0 && record.Bounds.Height > 0
                    ? new SizeD(record.Bounds.Width, record.Bounds.Height) : new SizeD(image.Width, image.Height);
                layer = Layer.FromImage(image.Renamed(record.Name), new PointD(record.Bounds.X, record.Bounds.Y), record.Name) with
                {
                    Id = record.Id, ParentId = record.ParentId, IsVisible = record.IsVisible, Opacity = opacity,
                    BlendMode = record.BlendMode ?? BlendMode.Normal, Shape = record.Shape,
                    Transform = new LayerTransform(new PointD(record.Bounds.X, record.Bounds.Y), size),
                };
            }
            else
            {
                layer = Layer.Blank(record.Name, canvas) with
                {
                    Id = record.Id, ParentId = record.ParentId, IsVisible = record.IsVisible, Opacity = opacity,
                    BlendMode = record.BlendMode ?? BlendMode.Normal,
                };
            }
            if (!record.IsGroup && record.Adjustment is null && record.Effects is { Effects.IsEmpty: false } effects)
                layer = layer with { Effects = effects.Effects };
            if (record.Mask is { } patch)
            {
                if (MaskOnLayerGrid(patch, record, layer, canvas) is { } mask)
                    layer = layer with { Mask = new LayerMask(mask, record.MaskEnabled, null, record.MaskLinked) };
                else Note(record, L.T("图层蒙版无法转换，已跳过。", "The layer mask couldn’t be converted and was skipped."));
            }
            layers.Add(layer);
        }

        // Clipping: a clipped layer takes the nearest unclipped pixel layer below it in the same folder as its base.
        var byId = layers.ToDictionary(l => l.Id);
        var baseForParent = new Dictionary<Guid, Guid?>();
        Guid Key(Guid? parent) => parent ?? Guid.Empty;
        foreach (var record in psd.Layers)
        {
            if (!byId.TryGetValue(record.Id, out var layer)) continue;
            if (record.Clipping)
            {
                if (baseForParent.GetValueOrDefault(Key(record.ParentId)) is { } source && byId.TryGetValue(source, out var b) && !b.IsGroup && b.Adjustment is null)
                {
                    int index = layers.FindIndex(l => l.Id == record.Id);
                    layers[index] = layers[index] with { MaskSourceId = source };
                }
                else conversions.Add(new PsdConversion(record.Name, L.T("不支持这个剪贴蒙版的基底，已跳过剪贴。", "This clipping mask’s base isn’t supported, so clipping was skipped.")));
            }
            else baseForParent[Key(record.ParentId)] = !layer.IsGroup && layer.Adjustment is null ? record.Id : null;
        }

        var document = new CanvasDocument
        {
            Width = psd.Width, Height = psd.Height, Resolution = psd.Resolution, Layers = ImmutableList.CreateRange(layers),
        };
        return new PsdImport(document, conversions);
    }

    /// <summary>
    /// A PSD layer mask on the layer's own pixel grid, as Compositor's masks are: the stored patch where it sits on the
    /// document, and Photoshop's default value everywhere else. Folders and adjustment layers cover the canvas.
    /// </summary>
    private static RasterImage? MaskOnLayerGrid(byte[] patch, PsdRecord record, Layer layer, SizeD canvas)
    {
        var bounds = record.MaskBounds;
        if (bounds.Width <= 0 || bounds.Height <= 0 || patch.Length < bounds.Width * bounds.Height) return null;
        int gridW = layer.Asset?.Width ?? (int)canvas.Width, gridH = layer.Asset?.Height ?? (int)canvas.Height;
        var placed = layer.Transform;
        if (gridW < 1 || gridH < 1 || placed.Size.Width <= 0 || placed.Size.Height <= 0) return null;
        double sx = gridW / placed.Size.Width, sy = gridH / placed.Size.Height;
        double x = (bounds.X - placed.Origin.X) * sx, y = (bounds.Y - placed.Origin.Y) * sy;
        // Already the layer's grid: nothing to place.
        if (x == 0 && y == 0 && bounds.Width == gridW && bounds.Height == gridH) return RasterImage.FromMask(patch, gridW, gridH);
        using var bitmap = new SKBitmap(new SKImageInfo(gridW, gridH, SKColorType.Alpha8, SKAlphaType.Premul));
        bitmap.Erase(new SKColor(0, 0, 0, record.MaskDefault));
        using var source = RasterImage.FromMask(patch.AsSpan(0, bounds.Width * bounds.Height), bounds.Width, bounds.Height).Image;
        using (var canvasSk = new SKCanvas(bitmap))
        {
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
            canvasSk.DrawImage(source, new SKRect((float)x, (float)y, (float)(x + bounds.Width * sx), (float)(y + bounds.Height * sy)),
                new SKSamplingOptions(SKFilterMode.Nearest), paint);
        }
        return RasterImage.FromBitmap(bitmap, isMask: true);
    }
}
