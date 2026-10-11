using System.Collections.Immutable;
using Compositor.Core.Geometry;
using Compositor.Core.Localization;
using Compositor.Core.Model;
using Compositor.Core.Rendering;
using SkiaSharp;

namespace Compositor.Core.Editing;

public sealed partial class EditorSession
{
    private TransformEdit? _transformEdit;
    /// <summary>A transform in progress: a drag on the Move tool, or Ctrl+T until Enter or Escape.</summary>
    public TransformEdit? TransformEdit
    {
        get => _transformEdit;
        private set
        {
            _transformEdit = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanUndo));
            Invalidate();
        }
    }

    /// <summary>Several layers selected, or a folder: they transform together in one box.</summary>
    public bool TransformsAsGroup => SelectedLayerIds.Count > 1 || (SelectedLayerIds.Count == 1 && ActiveLayer?.IsGroup == true);

    /// <summary>The visible pixel layers a group transform moves: those selected and those inside selected folders.</summary>
    public IReadOnlyList<Layer> GroupTransformMembers
    {
        get
        {
            if (!TransformsAsGroup || Document is not { } document) return Array.Empty<Layer>();
            var visible = document.EffectiveVisibleIds;
            return document.Layers.Where(layer =>
            {
                if (layer.Asset is null || layer.IsGroup || !visible.Contains(layer.Id)) return false;
                if (SelectedLayerIds.Contains(layer.Id)) return true;
                return document.AncestorIds(layer.Id).Any(SelectedLayerIds.Contains);
            }).ToList();
        }
    }

    /// <summary>The upright box around <see cref="GroupTransformMembers"/>.</summary>
    public LayerTransform? GroupTransformBox
    {
        get
        {
            var members = GroupTransformMembers;
            if (members.Count == 0) return null;
            var box = RectD.FromPoints(members.SelectMany(m => m.Transform.Corners()));
            return new LayerTransform(box.Origin, new SizeD(Math.Max(1, box.Width), Math.Max(1, box.Height)));
        }
    }

    public bool CanTransform
    {
        get
        {
            if (!CanEditLayers && TransformEdit is null) return false;
            if (Document is not { } document) return false;
            if (TransformsAsGroup) return GroupTransformMembers.Count > 0;
            return ActiveLayer is { IsGroup: false } layer && (layer.Asset is not null || (IsMaskSelected && layer.Mask is not null))
                   && document.EffectiveVisibleIds.Contains(layer.Id);
        }
    }

    public bool CanTransformSelection => CanEditPixels && !IsMaskSelected && Selection is { IsEmpty: false } && ActiveLayer?.Asset is not null;

    /// <summary>Whether the transform places only the active layer's unlinked mask.</summary>
    public bool TransformTargetsMask => TransformEdit?.Mask ?? (IsMaskSelected && ActiveLayer?.Mask is { IsLinked: false });

    /// <summary>Ctrl+T: transform the selected pixels when there's a selection, else the layer.</summary>
    public void TransformCommand()
    {
        if (TransformEdit is { Persistent: true }) return;
        if (CanTransformSelection) BeginFloatingTransform(duplicate: false, persistent: true);
        else BeginTransform(persistent: true);
    }

    public void BeginTransform(bool persistent = true)
    {
        CropRect = null;
        if (TransformEdit is not null || !CanTransform || ActiveLayer is not { } layer) return;
        if (TransformsAsGroup)
        {
            var members = GroupTransformMembers;
            if (GroupTransformBox is not { } box) return;
            TransformEdit = new TransformEdit
            {
                LayerId = layer.Id, Draft = box, Persistent = persistent,
                Group = new TransformGroup(box, members.ToDictionary(m => m.Id, m => m.Transform)),
            };
            return;
        }
        bool maskAlone = IsMaskSelected && layer.Mask is { IsLinked: false };
        TransformEdit = new TransformEdit
        {
            LayerId = layer.Id,
            Draft = maskAlone ? layer.MaskTransform : layer.Transform,
            Persistent = persistent,
            Mask = maskAlone,
        };
    }

    public void PreviewTransform(LayerTransform value)
    {
        // A distortion's box stays put: its corners are what move (see PreviewCorners and NudgeLayer).
        if (!value.IsValid || TransformEdit is not { Corners: null } edit) return;
        TransformEdit = edit with { Draft = value };
    }

    /// <summary>Where a layer shows under the pending edit; null when the edit doesn't move it.</summary>
    public LayerTransform? PendingTransform(Layer layer)
    {
        if (TransformEdit is not { Mask: false } edit) return null;
        if (edit.Floating is not null) return null;
        if (edit.Group is { } group) return group.Originals.TryGetValue(layer.Id, out var original) ? original.Following(group.Box, edit.Draft) : null;
        return edit.LayerId == layer.Id ? edit.Draft : null;
    }

    /// <summary>Where the transform box's handles sit for the active layer.</summary>
    public LayerTransform? EditedTransform()
    {
        if (TransformEdit is { } edit) return edit.Draft;
        if (ActiveLayer is not { } layer) return null;
        if (TransformsAsGroup) return GroupTransformBox;
        return TransformTargetsMask ? layer.MaskTransform : layer.Transform;
    }

    public void CommitTransform()
    {
        SnapGuides = (Array.Empty<double>(), Array.Empty<double>());
        FinishOpacityEdit();
        if (TransformEdit is not { } edit || Document is not { } document) return;
        TransformEdit = null;
        if (edit.Floating is { } floating) { CommitFloating(edit, floating); return; }
        if (edit.Corners is { } corners) { CommitDistort(edit, corners); return; }
        if (!edit.Draft.IsValid) return;
        if (edit.Mask)
        {
            if (document.Find(edit.LayerId) is not { Mask: { } mask } owner) return;
            var placement = edit.Draft.SamePlacement(owner.Transform) ? (LayerTransform?)null : edit.Draft;
            if (placement == mask.Placement) return;
            BeginEdit("Transform Layer Mask");
            Document = document.UpdateLayer(owner.Id, l => l with { Mask = mask with { Placement = placement } });
            EndEdit();
            return;
        }
        if (edit.Group is { } group)
        {
            var layers = document.Layers;
            foreach (var (id, original) in group.Originals)
            {
                int index = layers.FindIndex(l => l.Id == id);
                if (index < 0) continue;
                var moved = original.Following(group.Box, edit.Draft);
                if (!moved.IsValid) continue;
                var layer = layers[index];
                layers = layers.SetItem(index, RedrawShape(layer with
                {
                    Transform = moved,
                    Mask = layer.Mask is { } m ? m with { Placement = m.PlacementMovingLayer(original, moved) } : null,
                }));
            }
            if (layers.SequenceEqual(document.Layers)) return;
            BeginEdit("Transform Layers");
            Document = document with { Layers = layers };
            EndEdit();
            return;
        }
        if (document.Find(edit.LayerId) is not { } target || target.Transform == edit.Draft) return;
        BeginEdit("Transform Layer");
        Document = document.UpdateLayer(target.Id, l => RedrawShape(l with
        {
            Transform = edit.Draft,
            Mask = l.Mask is { } m ? m with { Placement = m.PlacementMovingLayer(l.Transform, edit.Draft) } : null,
        }));
        EndEdit();
    }

    public void CancelTransform()
    {
        SnapGuides = (Array.Empty<double>(), Array.Empty<double>());
        _distortPreviews.Clear();
        if (TransformEdit is null) return;
        TransformEdit = null;
    }

    /// <summary>Arrow keys with the Move tool: 1 px, or 10 px with Shift, one undo step each.</summary>
    public void NudgeLayer(double dx, double dy)
    {
        bool editing = TransformEdit is not null;
        if (!editing) BeginTransform(persistent: false);
        if (TransformEdit is not { } edit) return;
        if (edit.Corners is { } corners)
            TransformEdit = edit with
            {
                Draft = edit.Draft with { Origin = new PointD(edit.Draft.Origin.X + dx, edit.Draft.Origin.Y + dy) },
                Corners = corners.Select(p => new PointD(p.X + dx, p.Y + dy)).ToArray(),
            };
        else PreviewTransform(edit.Draft with { Origin = new PointD(edit.Draft.Origin.X + dx, edit.Draft.Origin.Y + dy) });
        if (!editing) CommitTransform();
    }

    /// <summary>Shows a pending transform on the canvas.</summary>
    public void ApplyTransformPreview(RenderOptions options)
    {
        if (TransformEdit is not { } edit || Document is not { } document) return;
        if (edit.Corners is { } shape) { ApplyDistortPreview(options, document, edit, shape); return; }
        if (edit.Floating is { } floating)
        {
            var o = options.Edit(floating.LayerId);
            var layer = document.Find(floating.LayerId);
            o.Image = floating.Hole.Image;
            o.Transform = floating.LayerBefore.Transform;
            var draft = edit.Draft;
            o.DrawAbove = canvas =>
            {
                using var paint = new SKPaint { Color = SKColors.Black.WithAlpha((byte)Math.Round((layer?.Opacity ?? 1) * 255)) };
                Blenders.Apply(paint, layer?.BlendMode ?? BlendMode.Normal);
                canvas.Save();
                canvas.Concat(draft.PixelToDocument(floating.Pixels.Width, floating.Pixels.Height).ToSK());
                canvas.DrawImage(floating.Pixels.Image, new SKRect(0, 0, floating.Pixels.Width, floating.Pixels.Height),
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
                canvas.Restore();
            };
            return;
        }
        if (edit.Mask)
        {
            var o = options.Edit(edit.LayerId);
            o.OverridesMaskPlacement = true;
            o.MaskPlacement = document.Find(edit.LayerId) is { } owner && edit.Draft.SamePlacement(owner.Transform) ? null : edit.Draft;
            return;
        }
        foreach (var layer in document.Layers)
        {
            if (PendingTransform(layer) is not { } pending) continue;
            var o = options.Edit(layer.Id);
            o.Transform = pending;
            if (layer.Mask is { } mask)
            {
                o.OverridesMaskPlacement = true;
                o.MaskPlacement = mask.PlacementMovingLayer(layer.Transform, pending);
            }
        }
    }

    /// <summary>The selection as shown: following floating pixels while they move.</summary>
    public DocumentSelection? DisplayedSelection
    {
        get
        {
            if (ColorRange is { } range) return range.Result;
            if (TransformEdit is { Floating: { SelectionBefore: { } before } floating } edit)
            {
                if (edit.Corners is { } corners) return DistortedSelection(before, edit, floating, corners) ?? before;
                var map = floating.Original.UnitToDocument.Invert().Concat(edit.Draft.UnitToDocument);
                var path = new SKPath(before.Path);
                path.Transform(map.ToSK());
                return new DocumentSelection(path, before.Antialiased, before.Feather);
            }
            return Selection;
        }
    }

    // MARK: Floating pixels

    /// <summary>
    /// Lifts the selected pixels of the active layer so they can move or transform on their own. With
    /// <paramref name="duplicate"/> (Alt-drag) the originals stay where they were.
    /// </summary>
    public bool BeginFloatingTransform(bool duplicate, bool persistent)
    {
        if (TransformEdit is not null || !CanTransformSelection || Document is not { } document
            || ActiveLayer is not { Asset: { } asset } layer || Selection is not { } selection) return false;
        var clip = selection.Clip(document.Width, document.Height);
        if (clip.IsNothing) return false;
        int gw = asset.Width, gh = asset.Height;
        var gridToDoc = layer.Transform.PixelToDocument(gw, gh);
        var docToGrid = gridToDoc.Invert();
        var region = RectI.From(docToGrid.Apply(clip.Rect.ToD())).Intersect(new RectI(0, 0, gw, gh));
        if (region.IsEmpty) return false;
        var source = asset.CopyPixels();
        var lifted = new byte[region.Width * region.Height * 4];
        var hole = duplicate ? null : (byte[])source.Clone();
        bool any = false;
        for (int y = region.Y; y < region.Bottom; y++)
        {
            for (int x = region.X; x < region.Right; x++)
            {
                var doc = gridToDoc.Apply(x + 0.5, y + 0.5);
                double k = clip.At(doc.X, doc.Y);
                if (k <= 0) continue;
                int s = (y * gw + x) * 4, d = ((y - region.Y) * region.Width + (x - region.X)) * 4;
                for (int c = 0; c < 4; c++)
                {
                    lifted[d + c] = (byte)Math.Round(source[s + c] * k);
                    if (hole is not null) hole[s + c] = (byte)Math.Round(source[s + c] * (1 - k));
                }
                if (source[s + 3] != 0) any = true;
            }
        }
        if (!any) return false;
        var pixels = RasterImage.FromRgba(lifted, region.Width, region.Height, layer.Name);
        var holeImage = hole is null ? asset : RasterImage.FromRgba(hole, gw, gh, layer.Name);
        var original = GridTransformFor(layer.Transform, gw, gh, region);
        TransformEdit = new TransformEdit
        {
            LayerId = layer.Id,
            Draft = original,
            Persistent = persistent,
            Floating = new FloatingTransform(layer.Id, pixels, original, layer, duplicate, holeImage, selection),
        };
        return true;
    }

    /// <summary>Where a rect of a layer's pixel grid sits on the document.</summary>
    public static LayerTransform GridTransformFor(LayerTransform transform, int gridWidth, int gridHeight, RectI rect)
    {
        var map = transform.PixelToDocument(gridWidth, gridHeight);
        var center = map.Apply(rect.X + rect.Width / 2.0, rect.Y + rect.Height / 2.0);
        var size = new SizeD(rect.Width * transform.Size.Width / gridWidth, rect.Height * transform.Size.Height / gridHeight);
        return transform with { Size = size, Origin = new PointD(center.X - size.Width / 2, center.Y - size.Height / 2) };
    }

    private void CommitFloating(TransformEdit edit, FloatingTransform floating)
    {
        if (Document is not { } document || document.Find(floating.LayerId) is not { } layer || !edit.Draft.IsValid) return;
        bool unchanged = edit.Draft == floating.Original && edit.Corners is null;
        if (unchanged && !floating.Duplicate) return;
        var before = floating.LayerBefore;
        int gw = floating.Hole.Width, gh = floating.Hole.Height;
        var gridToDoc = before.Transform.PixelToDocument(gw, gh);
        var docToGrid = gridToDoc.Invert();
        // A distorted selection is warped into its new shape first, then merged like any other.
        var pixels = floating.Pixels;
        var draft = edit.Draft;
        if (edit.Corners is { } shape)
        {
            try { (pixels, draft) = DistortedFloating(edit, floating, shape); }
            catch (Exception error) { ShowError(L.T("无法扭曲", "Couldn’t distort"), error.Message); return; }
        }
        var placed = RectI.From(docToGrid.Apply(draft.BoundingBox()).Inflate(1, 1));
        var bounds = new RectI(0, 0, gw, gh).Union(placed);
        if (bounds.Area > DocumentLimits.MaxSurfacePixels) { ShowError(L.T("无法移动像素", "Couldn’t move pixels"), L.T("结果太大。", "The result is too large.")); return; }
        using var bitmap = new SKBitmap(RasterImage.ColorInfo(bounds.Width, bounds.Height));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawImage(floating.Hole.Image, -bounds.X, -bounds.Y);
            var matrix = SKMatrix.CreateTranslation(-bounds.X, -bounds.Y)
                .PreConcat(docToGrid.ToSK())
                .PreConcat(draft.PixelToDocument(pixels.Width, pixels.Height).ToSK());
            canvas.SetMatrix(matrix);
            // Warped pixels sit on whole document pixels: one to one on an upright, unscaled layer.
            bool whole = edit.Corners is not null
                ? before.Transform.Rotation % 360 == 0 && !before.Transform.FlipX && !before.Transform.FlipY && before.Transform.Size == new SizeD(gw, gh)
                  && before.Transform.Origin == before.Transform.Origin.Rounded()
                : draft.Rotation % 360 == 0 && draft.Size == floating.Original.Size;
            using var paint = new SKPaint { IsAntialias = !whole };
            canvas.DrawImage(pixels.Image, new SKRect(0, 0, pixels.Width, pixels.Height),
                whole ? new SKSamplingOptions(SKFilterMode.Nearest) : new SKSamplingOptions(SKCubicResampler.Mitchell), paint);
            canvas.Flush();
        }
        var full = RasterImage.FromBitmap(bitmap, false, layer.Name);
        var trim = PixelOps.AlphaBounds(full);
        var keep = new RectI(-bounds.X, -bounds.Y, gw, gh).Union(trim.IsEmpty ? new RectI(-bounds.X, -bounds.Y, gw, gh) : trim);
        keep = keep.Intersect(new RectI(0, 0, bounds.Width, bounds.Height));
        var asset = keep.Width == bounds.Width && keep.Height == bounds.Height ? full : PixelOps.Crop(full, keep, layer.Name);
        var gridRect = keep.Offset(bounds.X, bounds.Y);
        var transform = GridTransformFor(before.Transform, gw, gh, gridRect);
        var mask = layer.Mask;
        if (mask is { Placement: null } && gridRect != new RectI(0, 0, gw, gh)) mask = ExpandMask(mask, new RectI(0, 0, gw, gh), gridRect);
        // The selection follows the pixels.
        DocumentSelection? selection = document.Selection;
        if (floating.SelectionBefore is { } warping && edit.Corners is { } distorted)
            selection = DistortedSelection(warping, edit, floating, distorted) ?? selection;
        else if (floating.SelectionBefore is { } moving)
        {
            var sel = moving;
            var map = floating.Original.UnitToDocument.Invert().Concat(edit.Draft.UnitToDocument);
            var path = new SKPath(sel.Path);
            path.Transform(map.ToSK());
            selection = new DocumentSelection(path, sel.Antialiased, sel.Feather);
        }
        BeginEdit(edit.Corners is not null ? "Distort" : floating.Duplicate ? "Duplicate Pixels" : edit.Draft.Size == floating.Original.Size && edit.Draft.Rotation == floating.Original.Rotation ? "Move Pixels" : "Transform Selection");
        Document = document.UpdateLayer(layer.Id, l => l with { Asset = asset, Transform = transform, Mask = mask, Text = null, Shape = null }) with { Selection = selection };
        EndEdit();
    }

    /// <summary>A mask covering a layer's grid, grown (with its background) to cover <paramref name="next"/> of that grid.</summary>
    internal static LayerMask ExpandMask(LayerMask mask, RectI old, RectI next)
    {
        if (mask.Asset.IsUniform) return mask;
        byte background = LayerMask.Background(mask.Asset);
        var bytes = new byte[next.Width * next.Height];
        if (background != 0) Array.Fill(bytes, background);
        var source = mask.Asset.CopyPixels();
        // The mask's pixels are stretched over the old grid; at the same resolution they map one to one.
        int sw = mask.Asset.Width, sh = mask.Asset.Height;
        for (int y = 0; y < old.Height; y++)
        {
            int ty = old.Y - next.Y + y;
            if (ty < 0 || ty >= next.Height) continue;
            int sy = Math.Min(sh - 1, y * sh / Math.Max(1, old.Height));
            for (int x = 0; x < old.Width; x++)
            {
                int tx = old.X - next.X + x;
                if (tx < 0 || tx >= next.Width) continue;
                int sx = Math.Min(sw - 1, x * sw / Math.Max(1, old.Width));
                bytes[ty * next.Width + tx] = source[sy * sw + sx];
            }
        }
        return mask.Replacing(RasterImage.FromMask(bytes, next.Width, next.Height));
    }

    // MARK: Snapping

    /// <summary>What a moving layer snaps to (View › Snap To): the canvas, other layers, guides, the grid.</summary>
    public (List<double> Xs, List<double> Ys) SnapTargets(ISet<Guid> excluding, bool includeCenters = true)
    {
        var xs = new List<double>();
        var ys = new List<double>();
        if (Document is not { } document || !SnapEnabled) return (xs, ys);
        if (SnapToDocumentBounds)
        {
            xs.AddRange(new[] { 0.0, document.Width });
            ys.AddRange(new[] { 0.0, document.Height });
            if (includeCenters) { xs.Add(document.Width / 2.0); ys.Add(document.Height / 2.0); }
        }
        if (SnapToLayers)
        {
            var visible = document.EffectiveVisibleIds;
            foreach (var layer in document.Layers)
            {
                if (layer.IsGroup || layer.Asset is null || excluding.Contains(layer.Id) || !visible.Contains(layer.Id)) continue;
                var box = layer.Transform.BoundingBox();
                xs.Add(box.Left); xs.Add(box.Right);
                ys.Add(box.Top); ys.Add(box.Bottom);
                if (includeCenters) { xs.Add(box.MidX); ys.Add(box.MidY); }
            }
        }
        if (SnapToGuides && ShowsGuides)
        {
            foreach (var guide in document.Guides)
            {
                if (guide.Axis == GuideAxis.Vertical) xs.Add(guide.Position); else ys.Add(guide.Position);
            }
        }
        if (SnapToGrid && ShowsGrid)
        {
            double step = GridSpacing / (double)GridSubdivisions;
            for (double x = 0; x <= document.Width; x += step) xs.Add(Math.Round(x));
            for (double y = 0; y <= document.Height; y += step) ys.Add(Math.Round(y));
        }
        return (xs, ys);
    }

    /// <summary><paramref name="draft"/> nudged so the box it places lines up with a nearby edge or center.</summary>
    public LayerTransform SnappedMove(LayerTransform draft, ISet<Guid> moving, double tolerance)
    {
        if (!SnapEnabled) { SnapGuides = (Array.Empty<double>(), Array.Empty<double>()); return draft; }
        var box = draft.BoundingBox();
        var targets = SnapTargets(moving);
        var snap = TransformSnap.Offset(box, targets.Xs, targets.Ys, tolerance);
        SnapGuides = (snap.X is { } x ? new[] { x } : Array.Empty<double>(), snap.Y is { } y ? new[] { y } : Array.Empty<double>());
        if (snap.OffsetX == 0 && snap.OffsetY == 0) return draft;
        return draft with { Origin = new PointD(draft.Origin.X + snap.OffsetX, draft.Origin.Y + snap.OffsetY) };
    }

    /// <summary>A point moved onto the nearest edge target, each axis on its own (where a marquee or crop corner goes).</summary>
    public PointD SnappedPoint(PointD point, double tolerance)
    {
        if (!SnapEnabled) { SnapGuides = (Array.Empty<double>(), Array.Empty<double>()); return point; }
        var targets = SnapTargets(new HashSet<Guid>(), includeCenters: false);
        double? Nearest(double value, List<double> lines)
        {
            double? best = null;
            foreach (var line in lines)
                if (Math.Abs(line - value) <= tolerance && (best is null || Math.Abs(line - value) < Math.Abs(best.Value - value))) best = line;
            return best;
        }
        var nx = Nearest(point.X, targets.Xs);
        var ny = Nearest(point.Y, targets.Ys);
        SnapGuides = (nx is { } a ? new[] { a } : Array.Empty<double>(), ny is { } b ? new[] { b } : Array.Empty<double>());
        return new PointD(nx ?? point.X, ny ?? point.Y);
    }

    // MARK: Flipping and rotating

    private static LayerTransform Mirrored(LayerTransform t, bool horizontally, double axis)
    {
        var result = horizontally
            ? t with { FlipX = !t.FlipX, Origin = new PointD(2 * axis - t.Center.X - t.Size.Width / 2, t.Origin.Y) }
            : t with { FlipY = !t.FlipY, Origin = new PointD(t.Origin.X, 2 * axis - t.Center.Y - t.Size.Height / 2) };
        return result with { Rotation = -t.Rotation };
    }

    /// <summary>Flips the selected layer about its middle (or several, about their box's middle).</summary>
    public void FlipLayers(bool horizontally)
    {
        CommitTransform();
        if (!CanTransform || Document is not { } document) return;
        IReadOnlyList<Layer> members;
        double axis;
        if (TransformsAsGroup)
        {
            if (GroupTransformBox is not { } box) return;
            members = GroupTransformMembers;
            axis = horizontally ? box.Center.X : box.Center.Y;
        }
        else
        {
            if (ActiveLayer is not { } layer) return;
            members = new[] { layer };
            axis = horizontally ? layer.Transform.Center.X : layer.Transform.Center.Y;
        }
        var ids = members.Select(m => m.Id).ToHashSet();
        var layers = document.Layers.Select(l =>
        {
            if (!ids.Contains(l.Id)) return l;
            var flipped = Mirrored(l.Transform, horizontally, axis);
            return l with { Transform = flipped, Mask = l.Mask is { } m ? m with { Placement = m.PlacementMovingLayer(l.Transform, flipped) } : null };
        }).ToImmutableList();
        BeginEdit(horizontally ? "Flip Horizontal" : "Flip Vertical");
        Document = document with { Layers = layers };
        EndEdit();
    }

    /// <summary>Flips the whole canvas: every layer, placed mask, the selection and the guides.</summary>
    public void FlipCanvas(bool horizontally)
    {
        CommitTransform();
        CropRect = null;
        if (!CanEditLayers || Document is not { } document) return;
        double axis = horizontally ? document.Width / 2.0 : document.Height / 2.0;
        var layers = document.Layers.Select(l => l with
        {
            Transform = Mirrored(l.Transform, horizontally, axis),
            Mask = l.Mask is { Placement: { } p } m ? m with { Placement = Mirrored(p, horizontally, axis) } : l.Mask,
        }).ToImmutableList();
        DocumentSelection? selection = document.Selection;
        if (selection is not null)
        {
            var path = new SKPath(selection.Path);
            path.Transform(horizontally ? new SKMatrix(-1, 0, document.Width, 0, 1, 0, 0, 0, 1) : new SKMatrix(1, 0, 0, 0, -1, document.Height, 0, 0, 1));
            selection = new DocumentSelection(path, selection.Antialiased, selection.Feather);
        }
        var guides = document.Guides.Select(g =>
            (horizontally && g.Axis == GuideAxis.Vertical) || (!horizontally && g.Axis == GuideAxis.Horizontal)
                ? g with { Position = 2 * axis - g.Position } : g).ToImmutableList();
        BeginEdit(horizontally ? "Flip Canvas Horizontal" : "Flip Canvas Vertical");
        Document = document with { Layers = layers, Selection = selection, Guides = guides };
        EndEdit();
    }

    private static LayerTransform QuarterTurned(LayerTransform t, bool clockwise, SizeD canvas)
    {
        var middle = clockwise ? new PointD(canvas.Height - t.Center.Y, t.Center.X) : new PointD(t.Center.Y, canvas.Width - t.Center.X);
        return t with
        {
            Origin = new PointD(middle.X - t.Size.Width / 2, middle.Y - t.Size.Height / 2),
            Rotation = (t.Rotation + (clockwise ? 90 : -90)) % 360,
        };
    }

    /// <summary>Turns the canvas a quarter turn: width and height swap; layers are placed, not resampled.</summary>
    public void RotateCanvas(bool clockwise)
    {
        CommitTransform();
        CropRect = null;
        if (!CanEditLayers || Document is not { } document) return;
        var canvas = document.Size;
        var layers = document.Layers.Select(l => l with
        {
            Transform = QuarterTurned(l.Transform, clockwise, canvas),
            Mask = l.Mask is { Placement: { } p } m ? m with { Placement = QuarterTurned(p, clockwise, canvas) } : l.Mask,
        }).ToImmutableList();
        var guides = document.Guides.Select(g => (g.Axis, clockwise) switch
        {
            (GuideAxis.Vertical, true) => g with { Axis = GuideAxis.Horizontal },
            (GuideAxis.Vertical, false) => g with { Axis = GuideAxis.Horizontal, Position = canvas.Width - g.Position },
            (GuideAxis.Horizontal, true) => g with { Axis = GuideAxis.Vertical, Position = canvas.Height - g.Position },
            _ => g with { Axis = GuideAxis.Vertical },
        }).ToImmutableList();
        DocumentSelection? selection = document.Selection;
        if (selection is not null)
        {
            var path = new SKPath(selection.Path);
            path.Transform(clockwise ? new SKMatrix(0, -1, (float)canvas.Height, 1, 0, 0, 0, 0, 1)
                                     : new SKMatrix(0, 1, 0, -1, 0, (float)canvas.Width, 0, 0, 1));
            selection = new DocumentSelection(path, selection.Antialiased, selection.Feather);
        }
        BeginEdit(clockwise ? "Rotate Canvas 90° Clockwise" : "Rotate Canvas 90° Counterclockwise");
        Document = document with { Width = document.Height, Height = document.Width, Layers = layers, Guides = guides, Selection = selection };
        EndEdit();
        Viewport.Fit(Document.Size);
    }

    // MARK: Canvas size, image size, crop

    /// <summary>
    /// Image › Canvas Size: a new canvas, the content placed by <paramref name="anchor"/> (0–8, row-major) or an
    /// explicit offset. Layers keep their pixels. A <paramref name="fill"/> color extends the canvas with a bottom layer.
    /// </summary>
    public void ResizeCanvas(int width, int height, int anchor = 4, PaletteColor? fill = null, PointD? contentOffset = null, string name = "Canvas Size")
    {
        CommitTransform();
        if (Document is not { } document || !CanvasDocument.ValidDimension(width) || !CanvasDocument.ValidDimension(height)) return;
        var offset = contentOffset ?? new PointD(Math.Floor((width - document.Width) * (anchor % 3) / 2.0),
                                                 Math.Floor((height - document.Height) * (anchor / 3) / 2.0));
        if (width == document.Width && height == document.Height && offset == PointD.Zero) return;
        var layers = document.Layers.Select(l => l with
        {
            Transform = l.Transform with { Origin = l.Transform.Origin + offset },
            Mask = l.Mask is { Placement: { } p } m ? m with { Placement = p with { Origin = p.Origin + offset } } : l.Mask,
        }).ToImmutableList();
        if (fill is { } color && (width > document.Width || height > document.Height) && contentOffset is null)
        {
            var bytes = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    bool inside = x >= offset.X && x < offset.X + document.Width && y >= offset.Y && y < offset.Y + document.Height;
                    if (inside) continue;
                    int p = (y * width + x) * 4;
                    bytes[p] = color.R8; bytes[p + 1] = color.G8; bytes[p + 2] = color.B8; bytes[p + 3] = 255;
                }
            }
            string extensionName = L.T("画布扩展", "Canvas Extension");
            var extension = Layer.FromImage(RasterImage.FromRgba(bytes, width, height, extensionName), PointD.Zero, extensionName);
            layers = layers.Insert(0, extension);
        }
        DocumentSelection? selection = document.Selection?.Offset(offset.X, offset.Y);
        var guides = document.Guides.Select(g => g with { Position = g.Position + (g.Axis == GuideAxis.Vertical ? offset.X : offset.Y) }).ToImmutableList();
        BeginEdit(name);
        Document = document with { Width = width, Height = height, Layers = layers, Selection = selection, Guides = guides };
        EndEdit();
        Viewport.Fit(Document.Size);
    }

    /// <summary>
    /// Image › Image Size: every layer resampled by the change in canvas size (each to the upright box it then covers),
    /// masks too; a resolution-only change keeps the pixels.
    /// </summary>
    public void ResizeImage(int width, int height, double resolution, LayerSampling sampling = LayerSampling.High)
    {
        CommitTransform();
        if (Document is not { } document || !CanvasDocument.ValidDimension(width) || !CanvasDocument.ValidDimension(height)) return;
        if (!double.IsFinite(resolution) || resolution < 1 || resolution > 9600) return;
        if (width == document.Width && height == document.Height)
        {
            if (resolution == document.Resolution) return;
            BeginEdit("Image Size");
            Document = document with { Resolution = resolution };
            EndEdit();
            return;
        }
        double sx = width / (double)document.Width, sy = height / (double)document.Height;
        var layers = new List<Layer>();
        foreach (var layer in document.Layers)
        {
            var corners = layer.Transform.Corners().Select(c => new PointD(c.X * sx, c.Y * sy)).ToArray();
            double left = Math.Floor(corners.Min(c => c.X)), top = Math.Floor(corners.Min(c => c.Y));
            int w = Math.Max(1, (int)(Math.Ceiling(corners.Max(c => c.X)) - left));
            int h = Math.Max(1, (int)(Math.Ceiling(corners.Max(c => c.Y)) - top));
            var transform = new LayerTransform(new PointD(left, top), new SizeD(w, h), sampling: sampling);
            if (!transform.IsValid) { ShowError(L.T("无法调整图像大小", "Couldn’t resize the image"), L.T("结果太大。", "The result is too large.")); return; }
            var resampled = layer with { Transform = layer.IsGroup || layer.Asset is null ? transform : transform, Text = null, Shape = null };
            if (layer.Asset is { } asset)
            {
                if (w > DocumentLimits.MaxSide || h > DocumentLimits.MaxSide) { ShowError(L.T("无法调整图像大小", "Couldn’t resize the image"), L.T("图层太大。", "The layer is too large.")); return; }
                resampled = resampled with { Asset = Resample(asset, layer.Transform, transform, sx, sy, isMask: false) };
                // Effects are measured in the layer's pixels, which now stand for a different size.
                if (layer.Effects is { } effects)
                    resampled = resampled with { Effects = effects.Scaled(Math.Sqrt(sx * sy) * layer.Transform.Size.Width / Math.Max(1, asset.Width)) };
            }
            if (layer.Mask is { } mask)
            {
                if (mask.Asset.IsUniform || mask.Placement is not null)
                {
                    var placement = mask.Placement is { } p ? p.Placing(p.UnitToDocument.Concat(Affine.Scale(sx, sy))) : (LayerTransform?)null;
                    resampled = resampled with { Mask = mask with { Placement = placement } };
                }
                else
                {
                    resampled = resampled with { Mask = mask.Replacing(Resample(mask.Asset, layer.Transform, transform, sx, sy, isMask: true)) };
                }
            }
            layers.Add(resampled);
        }
        var guides = document.Guides.Select(g => g with { Position = g.Position * (g.Axis == GuideAxis.Vertical ? sx : sy) }).ToImmutableList();
        DocumentSelection? selection = null;
        if (document.Selection is { } s)
        {
            var path = new SKPath(s.Path);
            path.Transform(SKMatrix.CreateScale((float)sx, (float)sy));
            selection = new DocumentSelection(path, s.Antialiased, s.Feather * Math.Sqrt(sx * sy));
        }
        BeginEdit("Image Size");
        Document = document with { Width = width, Height = height, Resolution = resolution, Layers = layers.ToImmutableList(), Guides = guides, Selection = selection };
        EndEdit();
        Viewport.Fit(Document.Size);
    }

    /// <summary>A layer's pixels drawn where the scaled document puts them, into the upright box <paramref name="target"/>.</summary>
    private static RasterImage Resample(RasterImage image, LayerTransform source, LayerTransform target, double sx, double sy, bool isMask)
    {
        int w = (int)target.Size.Width, h = (int)target.Size.Height;
        var info = isMask ? RasterImage.MaskInfo(w, h) : RasterImage.ColorInfo(w, h);
        using var bitmap = new SKBitmap(info);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Translate((float)-target.Origin.X, (float)-target.Origin.Y);
            canvas.Scale((float)sx, (float)sy);
            canvas.Concat(source.PixelToDocument(image.Width, image.Height).ToSK());
            double scale = Math.Min(sx * source.Size.Width / image.Width, sy * source.Size.Height / image.Height);
            var sampling = scale < 1 ? new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear) : new SKSamplingOptions(SKCubicResampler.Mitchell);
            using var paint = new SKPaint { IsAntialias = true };
            canvas.DrawImage(image.Image, new SKRect(0, 0, image.Width, image.Height), sampling, paint);
            canvas.Flush();
        }
        if (!isMask) PixelOps.ClampPremultiplied(bitmap);
        return RasterImage.FromBitmap(bitmap, isMask, image.Name);
    }

    private RectD? _cropRect;
    /// <summary>The crop frame being adjusted (Crop tool), in document pixels.</summary>
    public RectD? CropRect { get => _cropRect; set { _cropRect = value; OnPropertyChanged(); Invalidate(); } }

    private string _cropRatioChoice = "自由";
    /// <summary>
    /// One of <see cref="CropRatios"/>. These are identifiers, the same in every interface language (never L.T): the
    /// options bar shows each in its own words by position and writes the identifier back.
    /// </summary>
    public string CropRatioChoice { get => _cropRatioChoice; set { if (SetField(ref _cropRatioChoice, value)) ApplyCropRatio(); } }

    public static readonly string[] CropRatios = { "自由", "原始比例", "1:1", "4:3", "3:4", "16:9", "9:16" };

    public double? CropRatio => CropRatioChoice switch
    {
        "原始比例" => Document is { } d ? d.Width / (double)d.Height : null,
        "1:1" => 1,
        "4:3" => 4.0 / 3,
        "3:4" => 3.0 / 4,
        "16:9" => 16.0 / 9,
        "9:16" => 9.0 / 16,
        _ => null,
    };

    private void ApplyCropRatio()
    {
        if (CropRect is not { } rect || CropRatio is not { } ratio) return;
        double height = rect.Width / ratio;
        var next = CropGeometry.Snapped(new RectD(rect.Left, rect.MidY - height / 2, rect.Width, height));
        if (CropGeometry.Valid(next)) CropRect = next;
    }

    public void CancelCrop() => CropRect = null;

    /// <summary>Enter with the Crop tool: the canvas becomes the frame; layers keep their pixels.</summary>
    public void CommitCrop()
    {
        if (CropRect is not { } rect || !CropGeometry.Valid(rect)) return;
        CropRect = null;
        ResizeCanvas((int)rect.Width, (int)rect.Height, contentOffset: new PointD(-rect.Left, -rect.Top), name: "Crop");
        if (Tool == Tool.Crop && Document is { } document) CropRect = document.Bounds;
    }

    // MARK: Guides

    public void AddGuide(GuideAxis axis, double position)
    {
        if (Document is not { } document || !double.IsFinite(position) || document.Guides.Count >= 1000) return;
        BeginEdit("New Guide");
        Document = document with { Guides = document.Guides.Add(new CanvasGuide(Guid.NewGuid(), axis, Math.Round(position))) };
        EndEdit();
    }

    public void MoveGuide(Guid id, double position, bool remove)
    {
        if (Document is not { } document) return;
        int index = document.Guides.FindIndex(g => g.Id == id);
        if (index < 0) return;
        BeginEdit(remove ? "Delete Guide" : "Move Guide");
        Document = document with { Guides = remove ? document.Guides.RemoveAt(index) : document.Guides.SetItem(index, document.Guides[index] with { Position = Math.Round(position) }) };
        EndEdit();
    }

    public void ClearGuides()
    {
        if (Document is not { Guides.IsEmpty: false } document) return;
        BeginEdit("Clear Guides");
        Document = document with { Guides = ImmutableList<CanvasGuide>.Empty };
        EndEdit();
    }
}
