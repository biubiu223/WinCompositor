using System.Collections.Immutable;
using Compositor.Core.Geometry;
using Compositor.Core.IO;
using Compositor.Core.Localization;
using Compositor.Core.Model;
using Compositor.Core.Rendering;
using SkiaSharp;

namespace Compositor.Core.Editing;

/// <summary>What Copy left, shared by every open project so pasting works across tabs.</summary>
public static class SharedClipboard
{
    /// <summary>Copied pixels and the document rectangle they came from.</summary>
    public sealed record Pixels(RasterImage Image, RectD Source, Guid Token);
    /// <summary>Copied layers (with their folders' contents), bottom to top.</summary>
    public sealed record Layers(ImmutableList<Layer> Items, Guid Token);

    public static Pixels? CopiedPixels { get; set; }
    public static Layers? CopiedLayers { get; set; }
    /// <summary>The token of whatever was copied last, written to the system clipboard alongside the PNG.</summary>
    public static Guid? LastToken { get; set; }
}

public sealed partial class EditorSession
{
    public bool CanCopyPixels => Document is not null && Selection is { IsEmpty: false } && ActiveLayer is { } layer && SelectedLayerIds.Count == 1
                                 && (IsMaskSelected ? layer.Mask is not null : layer.Asset is not null && !layer.IsGroup);

    public bool CanCopyLayers => Document is not null && Selection is null && !SelectedLayerIds.IsEmpty && TransformEdit is null;

    /// <summary>
    /// Ctrl+C: the selected pixels of the active layer (or its mask, as gray), or with <paramref name="merged"/> the
    /// whole visible composite (Ctrl+Shift+C). Returns them for the system clipboard.
    /// </summary>
    public SharedClipboard.Pixels? CopyPixels(bool merged = false)
    {
        CommitTransform();
        if (Document is not { } document || Selection is not { IsEmpty: false } selection) return null;
        if (!merged && !CanCopyPixels) return null;
        var clip = selection.Clip(document.Width, document.Height);
        if (clip.IsNothing) return null;
        var region = clip.Rect;
        CanvasDocument source;
        if (merged)
        {
            source = document;
        }
        else if (IsMaskSelected && ActiveLayer is { Mask: { } mask } maskOwner)
        {
            var coverage = mask.Asset.CopyPixels();
            var rgba = new byte[coverage.Length * 4];
            for (int i = 0; i < coverage.Length; i++)
            {
                rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = coverage[i];
                rgba[i * 4 + 3] = 255;
            }
            var gray = RasterImage.FromRgba(rgba, mask.Asset.Width, mask.Asset.Height, L.T("蒙版", "Mask"));
            source = document with { Layers = ImmutableList.Create(Layer.FromImage(gray, PointD.Zero) with { Transform = mask.Placement ?? maskOwner.Transform }) };
        }
        else if (ActiveLayer is { } layer)
        {
            source = document with { Layers = ImmutableList.Create(layer with { ParentId = null, Mask = null, Opacity = 1, BlendMode = BlendMode.Normal, IsVisible = true, MaskSourceId = null }) };
        }
        else return null;

        using var surface = SKSurface.Create(RasterImage.ColorInfo(region.Width, region.Height));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.SaveLayer();
        canvas.Translate(-region.X, -region.Y);
        DocumentRenderer.Render(canvas, source with { Selection = null });
        canvas.ResetMatrix();
        using (var coverage = clip.ToImage())
        using (var dstIn = new SKPaint { BlendMode = SKBlendMode.DstIn })
            canvas.DrawImage(coverage, 0, 0, dstIn);
        canvas.Restore();
        canvas.Flush();
        using var snapshot = surface.Snapshot();
        var image = RasterImage.FromImage(snapshot, false, merged ? L.T("合并拷贝", "Copy Merged") : ActiveLayer?.Name ?? L.T("拷贝", "Copy"));
        var bounds = PixelOps.AlphaBounds(image);
        if (bounds.IsEmpty) return null;
        var trimmed = PixelOps.Crop(image, bounds);
        var copied = new SharedClipboard.Pixels(trimmed, new RectD(region.X + bounds.X, region.Y + bounds.Y, bounds.Width, bounds.Height), Guid.NewGuid());
        SharedClipboard.CopiedPixels = copied;
        SharedClipboard.CopiedLayers = null;
        SharedClipboard.LastToken = copied.Token;
        return copied;
    }

    /// <summary>Ctrl+X: copies the selected pixels and clears them.</summary>
    public SharedClipboard.Pixels? CutPixels()
    {
        var copied = CopyPixels();
        if (copied is not null) ClearSelectedPixels();
        return copied;
    }

    /// <summary>Ctrl+C with no selection: the selected layers (folders with their contents).</summary>
    public SharedClipboard.Layers? CopyLayers()
    {
        if (!CanCopyLayers || Document is not { } document) return null;
        var picked = new HashSet<Guid>(SelectedLayerIds);
        foreach (var id in SelectedLayerIds) picked.UnionWith(document.DescendantIds(id));
        var items = document.Layers.Where(l => picked.Contains(l.Id))
            .Select(l => l with
            {
                ParentId = l.ParentId is { } p && picked.Contains(p) ? p : null,
                MaskSourceId = l.MaskSourceId is { } s && picked.Contains(s) ? s : null,
            }).ToImmutableList();
        if (items.IsEmpty) return null;
        var copied = new SharedClipboard.Layers(items, Guid.NewGuid());
        SharedClipboard.CopiedLayers = copied;
        SharedClipboard.CopiedPixels = null;
        SharedClipboard.LastToken = copied.Token;
        return copied;
    }

    /// <summary>Pastes copied layers above the active one, with new ids.</summary>
    public void PasteLayers(SharedClipboard.Layers copied)
    {
        CommitTransform();
        if (Document is not { } document || document.Layers.Count + copied.Items.Count > DocumentLimits.MaxLayers) return;
        var mapping = copied.Items.ToDictionary(l => l.Id, _ => Guid.NewGuid());
        var parent = InsertionParent();
        var items = copied.Items.Select(l => l with
        {
            Id = mapping[l.Id],
            ParentId = l.ParentId is { } p && mapping.TryGetValue(p, out var np) ? np : parent,
            MaskSourceId = l.MaskSourceId is { } s && mapping.TryGetValue(s, out var ns) ? ns : null,
        }).ToList();
        var layers = document.Layers.InsertRange(InsertionIndex(document), items);
        var roots = copied.Items.Where(l => l.ParentId is null || !mapping.ContainsKey(l.ParentId.Value)).Select(l => mapping[l.Id]).ToList();
        if (CommitLayers("Paste Layer", layers, roots.LastOrDefault())) SelectLayers(roots, roots.LastOrDefault());
    }

    /// <summary>
    /// Pastes pixels as a new layer: where they were copied from when that place is on this canvas (as Photoshop does),
    /// else in the middle of the view. With no document, the pixels start one.
    /// </summary>
    public void PastePixels(RasterImage image, RectD? source = null, string? name = null)
    {
        CommitTransform();
        if (Document is null)
        {
            CreateDocument(image.Width, image.Height);
            if (Document is not { } created) return;
            var only = Layer.FromImage(image, PointD.Zero, name ?? L.T("图层 1", "Layer 1"));
            Document = created with { Layers = ImmutableList.Create(only) };
            ActiveLayerId = only.Id;
            History.Reset();
            return;
        }
        var document = Document;
        PointD origin;
        if (source is { } rect && rect.Intersects(document.Bounds)) origin = rect.Origin;
        else
        {
            var center = Viewport.ViewSize.Width > 0 ? ToDocument(Viewport.Center) : document.Bounds.Center;
            if (!document.Bounds.Contains(center)) center = document.Bounds.Center;
            origin = new PointD(Math.Floor(center.X - image.Width / 2.0), Math.Floor(center.Y - image.Height / 2.0));
        }
        var layer = Layer.FromImage(image, origin, name ?? NextName(L.T("图层", "Layer")));
        BeginEdit("Paste");
        InsertLayer(layer, "Paste");
        if (Document is { Selection: not null } withSelection) Document = withSelection with { Selection = null };
        EndEdit();
    }

    /// <summary>Ctrl+J: the selected pixels on a new layer in place (or, with no selection, a copy of the layer).</summary>
    public void LayerViaCopy(bool cut = false)
    {
        CommitTransform();
        if (Selection is null) { if (!cut) DuplicateLayers(); return; }
        if (!CanCopyPixels || IsMaskSelected) return;
        var copied = CopyPixels();
        if (copied is null) return;
        BeginEdit(cut ? "Layer via Cut" : "Layer via Copy");
        if (cut) ClearSelectedPixels();
        var layer = Layer.FromImage(copied.Image, copied.Source.Origin, NextName(L.T("图层", "Layer")));
        InsertLayer(layer, cut ? "Layer via Cut" : "Layer via Copy");
        if (Document is { } document) Document = document with { Selection = null };
        EndEdit();
    }

    // MARK: Import

    /// <summary>
    /// Adds image files as layers, centered on <paramref name="point"/> (or the canvas). With no document, the first
    /// image sets the canvas. Returns the names of files that couldn't come in, with why.
    /// </summary>
    public List<string> ImportImages(IEnumerable<string> paths, PointD? point = null)
    {
        CommitTransform();
        var failures = new List<string>();
        bool started = Document is null;
        BeginEdit("Import Images");
        foreach (var path in paths)
        {
            try
            {
                long used = Document?.Layers.Sum(l => l.Asset?.PixelCount ?? 0) ?? 0;
                var image = ImageImporter.Decode(path, DocumentLimits.DocumentPixelBudget - used, Document?.Size);
                InsertImage(image, Document is null ? null : point);
            }
            catch (Exception error)
            {
                failures.Add(L.T($"{Path.GetFileName(path)}：{error.Message}", $"{Path.GetFileName(path)}: {error.Message}"));
            }
        }
        EndEdit();
        if (started && Document is not null) History.Reset();
        return failures;
    }

    /// <summary>An image decoded elsewhere (a developed RAW) imported as a layer, one undo step; starts a document when there is none.</summary>
    public void ImportDecoded(RasterImage image, PointD? point = null)
    {
        CommitTransform();
        bool started = Document is null;
        BeginEdit("Import Image");
        InsertImage(image, started ? null : point);
        EndEdit();
        if (started && Document is not null) History.Reset();
    }

    /// <summary>Adds one image as a layer (starting a document sized to it when there is none).</summary>
    public void InsertImage(RasterImage image, PointD? center = null)
    {
        if (Document is null)
        {
            var document = new CanvasDocument { Width = image.Width, Height = image.Height };
            var first = Layer.FromImage(image, PointD.Zero);
            Document = document with { Layers = ImmutableList.Create(first) };
            ActiveLayerId = first.Id;
            Viewport.Fit(Document.Size);
            return;
        }
        var c = center ?? Document.Bounds.Center;
        var layer = Layer.FromImage(image, new PointD(Math.Floor(c.X - image.Width / 2.0), Math.Floor(c.Y - image.Height / 2.0)));
        InsertLayer(layer, "Import Image");
    }
}
