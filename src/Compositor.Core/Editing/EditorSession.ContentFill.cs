using Compositor.Core.Geometry;
using Compositor.Core.Localization;
using Compositor.Core.Model;
using Compositor.Core.Pixels;
using Compositor.Core.Rendering;

namespace Compositor.Core.Editing;

/// <summary>
/// What Content-Aware Fill's workspace works on: the active layer's pixels (with room past its edge where the selection
/// reaches beyond it) and the selection on that grid as the area to fill.
/// </summary>
public sealed class ContentFillJob
{
    internal ContentFillJob(Guid documentId, Guid layerId, RasterImage source, LayerTransform transform, LayerTransform grid,
                            int margin, int width, int height, byte[] pixels, byte[] hole, string layerName)
    {
        DocumentId = documentId; LayerId = layerId; Source = source; Transform = transform; Grid = grid;
        Margin = margin; Width = width; Height = height; Pixels = pixels; Hole = hole; LayerName = layerName;
    }

    public Guid DocumentId { get; }
    public Guid LayerId { get; }
    public string LayerName { get; }
    public int Width { get; }
    public int Height { get; }
    /// <summary>Premultiplied RGBA, tightly packed: the layer and the clear room around it.</summary>
    public byte[] Pixels { get; }
    /// <summary>The selection on this grid (255 to fill).</summary>
    public byte[] Hole { get; }
    internal int Margin { get; }
    internal RasterImage Source { get; }
    internal LayerTransform Transform { get; }
    /// <summary>Where this grid sits on the document.</summary>
    public LayerTransform Grid { get; }
}

public sealed partial class EditorSession
{
    /// <summary>The workspace opens on a layer with pixels; with no selection, the object to remove is picked inside it.</summary>
    public bool CanOpenContentFill => CanAdjustPixels;

    /// <summary>
    /// Opens Content-Aware Fill on the active layer: its pixels and the selection to fill, for the workspace. Null (with
    /// a message when it's worth one) when there's nothing to fill.
    /// </summary>
    public ContentFillJob? BeginContentFill()
    {
        CommitTransform();
        if (!CanOpenContentFill || ActiveLayer is not { Asset: { } asset } layer || Document is not { } document) return null;
        var selection = Selection;
        // Room past the layer's edge for the part of the selection on the canvas beyond it.
        int margin = 0;
        var map = layer.Transform.PixelToDocument(asset.Width, asset.Height);
        if (map.IsInvertible && selection is not null)
        {
            var grid = map.Invert().Apply(selection.Bounds.Intersect(document.Bounds));
            double over = Math.Max(Math.Max(-grid.Left, -grid.Top), Math.Max(grid.Right - asset.Width, grid.Bottom - asset.Height));
            margin = (int)Math.Ceiling(Math.Max(0, over));
            if (margin > 0) margin += 2;
        }
        int width = asset.Width + 2 * margin, height = asset.Height + 2 * margin;
        if ((long)width * height > DocumentLimits.MaxSurfacePixels || width > DocumentLimits.MaxSide * 2 || height > DocumentLimits.MaxSide * 2)
        {
            ShowError(L.T("无法内容识别填充", "Couldn’t use Content-Aware Fill"), L.T("图层太大。", "The layer is too large."));
            return null;
        }
        var pixels = new byte[width * height * 4];
        var own = asset.CopyPixels();
        for (int y = 0; y < asset.Height; y++)
            own.AsSpan(y * asset.Width * 4, asset.Width * 4).CopyTo(pixels.AsSpan(((y + margin) * width + margin) * 4));
        var placed = margin == 0 ? layer.Transform : GridTransform(layer.Transform, asset.Width, asset.Height, new RectI(-margin, -margin, width, height));
        // Without a selection the workspace opens with nothing to fill yet: an object is picked there.
        var hole = new byte[width * height];
        if (selection is not null)
        {
            var coverage = GridCoverage(selection.Clip(document.Width, document.Height), placed.PixelToDocument(width, height), width, height);
            bool any = false;
            for (int i = 0; i < hole.Length; i++) if (coverage[i] >= 128) { hole[i] = 255; any = true; }
            if (!any) { ShowError(L.T("内容识别填充", "Content-Aware Fill"), L.T("选区没有落在这个图层上。", "The selection doesn’t fall on this layer.")); return null; }
        }
        return new ContentFillJob(document.Id, layer.Id, asset, layer.Transform, placed, margin, width, height, pixels, hole, layer.Name);
    }

    /// <summary>
    /// OK in the workspace: the filled pixels (<paramref name="filled"/>, the job's grid) where <paramref name="hole"/>
    /// is, on a new layer above or on the layer itself, as one undo step. A fill area changed in the workspace becomes
    /// the selection.
    /// </summary>
    public bool CommitContentFill(ContentFillJob job, byte[] filled, byte[] hole, bool newLayer)
    {
        if (Document is not { } document || document.Id != job.DocumentId || document.Find(job.LayerId) is not { } layer
            || !ReferenceEquals(layer.Asset, job.Source) || layer.Transform != job.Transform)
        {
            ShowError(L.T("无法内容识别填充", "Couldn’t use Content-Aware Fill"), L.T("图层在填充时被更改了。", "The layer was changed while it was being filled."));
            return false;
        }
        int width = job.Width, height = job.Height;
        DocumentSelection? selection = document.Selection;
        if (!hole.AsSpan().SequenceEqual(job.Hole))
        {
            var outline = MagicWand.Outline(hole, width, height);
            if (outline is not null)
            {
                outline.Transform(job.Grid.PixelToDocument(width, height).ToSK());
                selection = new DocumentSelection(outline, SelectionAntialiased);
            }
        }
        if (newLayer)
        {
            var only = new byte[width * height * 4];
            for (int i = 0; i < hole.Length; i++)
                if (hole[i] != 0) filled.AsSpan(i * 4, 4).CopyTo(only.AsSpan(i * 4));
            var bounds = PixelOps.AlphaBounds(only, width, height, width * 4);
            if (bounds.IsEmpty) return true;
            string fillName = L.T("内容识别填充", "Content-Aware Fill");
            var image = PixelOps.Crop(RasterImage.FromRgba(only, width, height), bounds, fillName);
            var transform = GridTransform(job.Grid, width, height, bounds);
            if (document.Layers.Count >= DocumentLimits.MaxLayers)
            {
                ShowError(L.T("无法内容识别填充", "Couldn’t use Content-Aware Fill"), L.T("图层太多。", "There are too many layers."));
                return false;
            }
            var created = Layer.FromImage(image, transform.Origin, NextName(fillName)) with { Transform = transform, ParentId = InsertionParent() };
            if (created.ParentId is { } parent) CollapsedGroupIds = CollapsedGroupIds.Remove(parent);
            BeginEdit("Content-Aware Fill");
            Document = document with { Layers = document.Layers.Insert(InsertionIndex(document), created), Selection = selection };
            ActiveLayerId = created.Id;
            EndEdit();
            return true;
        }
        var merged = (byte[])job.Pixels.Clone();
        for (int i = 0; i < hole.Length; i++)
            if (hole[i] != 0) filled.AsSpan(i * 4, 4).CopyTo(merged.AsSpan(i * 4));
        var result = RasterImage.FromRgba(merged, width, height, layer.Name);
        var placed = job.Grid;
        if (job.Margin > 0)
        {
            // The room around the layer is cut back to what the fill reached.
            var reached = PixelOps.AlphaBounds(result);
            if (reached.IsEmpty) reached = new RectI(job.Margin, job.Margin, job.Source.Width, job.Source.Height);
            if (reached != new RectI(0, 0, width, height))
            {
                result = PixelOps.Crop(result, reached, layer.Name);
                placed = GridTransform(job.Grid, width, height, reached);
            }
        }
        var mask = layer.Mask;
        if (job.Margin > 0 && mask is { Placement: null } owned && !owned.Asset.IsUniform) mask = owned with { Placement = layer.Transform };
        BeginEdit("Content-Aware Fill");
        Document = document.UpdateLayer(layer.Id, l => l with { Asset = result, Transform = placed, Mask = mask, Text = null, Shape = null })
            with { Selection = selection };
        EndEdit();
        return true;
    }
}
