using Compositor.Core.Geometry;
using Compositor.Core.Localization;
using Compositor.Core.Model;
using Compositor.Core.Rendering;
using SkiaSharp;

namespace Compositor.Core.Editing;

/// <summary>
/// Free distortion: Ctrl-drag a transform handle (or Edit ▸ Distort) and the four corners move on their own. The
/// layer is previewed warped into the shape; on Apply its pixels (and mask) are resampled into it. Mirrors the
/// EditorSession extension in Distort.swift, the floating merge in FloatingSelection.swift and the mask distortion in
/// LayerMask.swift.
/// </summary>
public sealed partial class EditorSession
{
    private const double DistortPreviewLimit = 2048;

    /// <summary>The last warped preview of each layer, reused while its distortion and pixels are unchanged.</summary>
    private sealed record DistortPreview(PointD[] Corners, LayerTransform Transform, RasterImage Source, LayerMask? Mask,
                                         RasterImage? Image, LayerTransform Placement, RasterImage? MaskImage, bool OverridesMask,
                                         LayerTransform? MaskPlacement)
    {
        public bool Matches(PointD[] corners, LayerTransform transform, RasterImage source, LayerMask? mask) =>
            Corners.SequenceEqual(corners) && Transform == transform && ReferenceEquals(Source, source) && Equals(Mask, mask);
    }

    private readonly Dictionary<Guid, DistortPreview> _distortPreviews = new();

    /// <summary>A distortion is in progress: the transform box's corners move freely.</summary>
    public bool IsDistorting => TransformEdit?.Corners is not null;

    /// <summary>Whether a distortion can start: a transform that isn't one already, on pixels.</summary>
    public bool CanDistort => TransformEdit is { } edit ? edit.Draft.IsValid : CanTransform || CanTransformSelection;

    /// <summary>Edit ▸ Distort: starts a transform (of the selected pixels when there is a selection) with free corners.</summary>
    public void DistortCommand()
    {
        if (TransformEdit is null) TransformCommand();
        BeginDistort();
    }

    /// <summary>
    /// Ctrl-drag on a transform handle: the corners start moving freely. Each distortion resamples the pixels, so the
    /// edit then waits for Enter rather than applying on mouse-up.
    /// </summary>
    public bool BeginDistort()
    {
        if (TransformEdit is not { } edit) return false;
        if (edit.Corners is not null) return true;
        if (!edit.Draft.IsValid) return false;
        TransformEdit = edit with { Persistent = true, Corners = edit.Draft.Corners() };
        return true;
    }

    /// <summary>Moves the distortion's corners; a collapsed shape is ignored.</summary>
    public void PreviewCorners(PointD[] corners)
    {
        if (TransformEdit is not { Corners: not null } edit || !DistortWarp.IsUsable(corners)) return;
        TransformEdit = edit with { Corners = corners };
    }

    /// <summary>Where a distortion takes <paramref name="layer"/>: its transform under the edit and the corners it moves to.</summary>
    private static (LayerTransform Transform, PointD[] Corners)? DistortTarget(Layer layer, TransformEdit edit, PointD[] shape)
    {
        if (edit.Group is not { } group) return edit.LayerId == layer.Id ? (edit.Draft, shape) : null;
        if (!group.Originals.TryGetValue(layer.Id, out var original)) return null;
        var transform = original.Following(group.Box, edit.Draft);
        var corners = DistortWarp.Carried(transform, edit.Draft, shape);
        return DistortWarp.IsUsable(corners) ? (transform, corners) : null;
    }

    // MARK: Preview

    /// <summary>Shows a distortion in progress: each layer it moves warped into its shape (at preview size).</summary>
    private void ApplyDistortPreview(RenderOptions options, CanvasDocument document, TransformEdit edit, PointD[] shape)
    {
        if (edit.Floating is { } floating)
        {
            var o = options.Edit(floating.LayerId);
            var layer = document.Find(floating.LayerId);
            o.Image = floating.Hole.Image;
            o.Transform = floating.LayerBefore.Transform;
            var preview = CachedWarp(floating.LayerId, floating.Pixels, edit.Draft, shape, null, null);
            if (preview?.Image is not { } warped) return;
            var placement = preview.Placement;
            o.DrawAbove = canvas =>
            {
                using var paint = new SKPaint { Color = SKColors.Black.WithAlpha((byte)Math.Round((layer?.Opacity ?? 1) * 255)) };
                Blenders.Apply(paint, layer?.BlendMode ?? BlendMode.Normal);
                canvas.Save();
                canvas.Concat(placement.PixelToDocument(warped.Width, warped.Height).ToSK());
                canvas.DrawImage(warped.Image, new SKRect(0, 0, warped.Width, warped.Height),
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
                canvas.Restore();
            };
            return;
        }
        if (edit.Mask)
        {
            // An unlinked mask distorted on its own.
            if (document.Find(edit.LayerId) is not { Mask: { } owned } owner) return;
            var preview = CachedWarp(owner.Id, owned.Asset, edit.Draft, shape, owned, owner);
            if (preview?.MaskImage is not { } moved) return;
            var o = options.Edit(owner.Id);
            o.Mask = moved.Image;
            o.OverridesMaskPlacement = true;
            o.MaskPlacement = preview.MaskPlacement;
            return;
        }
        foreach (var layer in document.Layers)
        {
            if (layer.Asset is not { } image || DistortTarget(layer, edit, shape) is not { } target) continue;
            var preview = CachedWarp(layer.Id, image, target.Transform, target.Corners, layer.Mask, layer);
            if (preview?.Image is not { } warped) continue;
            var o = options.Edit(layer.Id);
            o.Image = warped.Image;
            o.Transform = preview.Placement;
            if (layer.Mask is not null)
            {
                o.OverridesMaskPlacement = true;
                o.MaskPlacement = preview.MaskPlacement;
                if (preview.OverridesMask && preview.MaskImage is { } mask) o.Mask = mask.Image;
            }
        }
    }

    /// <summary>
    /// The warped pixels (or, with a mask-only <paramref name="owner"/> edit, mask) of one layer, from the cache when
    /// nothing changed. <paramref name="mask"/> is the layer's mask, carried along with its pixels.
    /// </summary>
    private DistortPreview? CachedWarp(Guid id, RasterImage source, LayerTransform transform, PointD[] corners, LayerMask? mask, Layer? owner)
    {
        if (_distortPreviews.TryGetValue(id, out var cached) && cached.Matches(corners, transform, source, mask)) return cached;
        DistortPreview? result = null;
        try
        {
            if (source.IsMask && owner is not null && mask is not null)
            {
                // A mask on its own: resampled with its background outside the shape.
                var moved = DistortWarp.WarpMask(source, transform, corners, LayerMask.Background(source), DistortPreviewLimit);
                result = new DistortPreview(corners, transform, source, mask, null, moved.Transform, moved.Image, true, moved.Transform);
            }
            else
            {
                var (image, placement) = DistortWarp.Warp(source, transform, corners, DistortPreviewLimit);
                RasterImage? maskImage = null;
                bool overrides = false;
                LayerTransform? maskPlacement = null;
                if (mask is { } m && owner is not null)
                {
                    if (m.Placement is null && m.IsLinked)
                    {
                        // A mask covering the layer warps with its pixels, over the same bounds.
                        if (!m.Asset.IsUniform)
                        {
                            maskImage = DistortWarp.Warp(m.Asset, transform, corners, DistortPreviewLimit).Image;
                            overrides = true;
                        }
                    }
                    else if (m.IsLinked && m.Placement is { } placed)
                    {
                        // A linked mask placed apart takes the same perspective over its own bounds.
                        var moving = placed.Following(owner.Transform, transform);
                        var carried = DistortWarp.Carried(moving, transform, corners);
                        if (DistortWarp.IsConvex(carried))
                        {
                            var moved = DistortWarp.WarpMask(m.Asset, moving, carried, LayerMask.Background(m.Asset), DistortPreviewLimit);
                            maskImage = moved.Image;
                            overrides = true;
                            maskPlacement = moved.Transform;
                        }
                        else maskPlacement = moving;
                    }
                    else maskPlacement = m.Placement ?? owner.Transform; // an unlinked mask stays where it is
                }
                result = new DistortPreview(corners, transform, source, mask, image, placement, maskImage, overrides, maskPlacement);
            }
        }
        catch (Exception) { result = null; }
        if (result is not null) _distortPreviews[id] = result;
        return result;
    }

    // MARK: Apply

    /// <summary>Apply for a distortion of layers: each one's pixels and mask resampled into its shape, as one undo step.</summary>
    private void CommitDistort(TransformEdit edit, PointD[] shape)
    {
        _distortPreviews.Clear();
        if (Document is not { } document) return;
        try
        {
            if (edit.Mask)
            {
                if (document.Find(edit.LayerId) is not { Mask: { } mask } owner) return;
                var moved = DistortWarp.WarpMask(mask.Asset, edit.Draft, shape, LayerMask.Background(mask.Asset));
                var placement = moved.Transform.SamePlacement(owner.Transform) ? (LayerTransform?)null : moved.Transform;
                BeginEdit("Distort Layer Mask");
                Document = document.UpdateLayer(owner.Id, l => l with { Mask = mask with { Asset = moved.Image, Placement = placement } });
                EndEdit();
                return;
            }
            var layers = document.Layers;
            var ids = edit.Group is { } group ? group.Originals.Keys.ToList() : new List<Guid> { edit.LayerId };
            foreach (var id in ids)
            {
                int index = layers.FindIndex(l => l.Id == id);
                if (index < 0 || layers[index].Asset is null || DistortTarget(layers[index], edit, shape) is not { } target) continue;
                layers = layers.SetItem(index, Distorted(layers[index], target.Transform, target.Corners));
            }
            if (layers.SequenceEqual(document.Layers)) return;
            BeginEdit(edit.Group is null ? "Distort" : "Distort Layers");
            Document = document with { Layers = layers };
            EndEdit();
        }
        catch (Exception error) { ShowError(L.T("无法扭曲", "Couldn’t distort"), error.Message); }
    }

    /// <summary><paramref name="layer"/>, shown by <paramref name="transform"/>, resampled so its corners land on <paramref name="corners"/>.</summary>
    private static Layer Distorted(Layer layer, LayerTransform transform, PointD[] corners)
    {
        var warped = DistortWarp.WarpTrimmed(layer.Asset!, transform, corners);
        var mask = layer.Mask;
        if (mask is { Placement: null, IsLinked: true } covering)
        {
            // A uniform mask passes through; any other is warped and cropped with the pixels.
            if (!covering.Asset.IsUniform)
            {
                var full = DistortWarp.Warp(covering.Asset, transform, corners).Image;
                bool whole = warped.Crop == new RectI(0, 0, full.Width, full.Height);
                mask = covering with { Asset = whole ? full : PixelOps.Crop(full, warped.Crop, covering.Asset.Name) };
            }
        }
        else if (mask is { IsLinked: true, Placement: { } placed } linked)
        {
            var moving = placed.Following(layer.Transform, transform);
            var carried = DistortWarp.Carried(moving, transform, corners);
            if (DistortWarp.IsConvex(carried))
            {
                var moved = DistortWarp.WarpMask(linked.Asset, moving, carried, LayerMask.Background(linked.Asset));
                mask = linked with { Asset = moved.Image, Placement = moved.Transform };
            }
            else mask = linked with { Placement = moving };
        }
        else if (mask is { } unlinked)
        {
            // An unlinked mask keeps its place on the document.
            mask = unlinked with { Placement = unlinked.Placement ?? layer.Transform };
        }
        return layer with { Asset = warped.Image, Transform = warped.Transform, Mask = mask, Text = null, Shape = null };
    }

    /// <summary>The floating pixels warped into a distortion's shape (full size), for merging back into their layer.</summary>
    private static (RasterImage Pixels, LayerTransform Placement) DistortedFloating(TransformEdit edit, FloatingTransform floating, PointD[] corners)
    {
        var warped = DistortWarp.WarpTrimmed(floating.Pixels, edit.Draft, corners);
        return (warped.Image, warped.Transform);
    }

    /// <summary>A selection moved with distorted floating pixels; null when the shape is folded.</summary>
    private static DocumentSelection? DistortedSelection(DocumentSelection selection, TransformEdit edit, FloatingTransform floating, PointD[] corners)
    {
        var placement = floating.Original.PixelToDocument(floating.Pixels.Width, floating.Pixels.Height);
        return DistortWarp.MapPath(selection.Path, placement, floating.PixelSize, edit.Draft, corners) is { } path
            ? new DocumentSelection(path, selection.Antialiased, selection.Feather)
            : null;
    }
}
