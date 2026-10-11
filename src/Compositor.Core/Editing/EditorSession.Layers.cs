using System.Collections.Immutable;
using Compositor.Core.Geometry;
using Compositor.Core.Localization;
using Compositor.Core.Model;
using Compositor.Core.Rendering;

namespace Compositor.Core.Editing;

public sealed partial class EditorSession
{
    /// <summary>Replaces the layer list as one undo step, if the result is a valid hierarchy.</summary>
    private bool CommitLayers(string name, ImmutableList<Layer> layers, Guid? active = null, bool keepSelection = false)
    {
        if (Document is not { } document || !CanvasDocument.ValidateHierarchy(layers)) return false;
        BeginEdit(name);
        Document = document with { Layers = layers };
        if (!keepSelection) ActiveLayerId = active ?? ActiveLayerId;
        EndEdit();
        return true;
    }

    private void ChangeLayer(Guid id, string name, Func<Layer, Layer> change)
    {
        if (Document is not { } document || document.Find(id) is null) return;
        BeginEdit(name);
        Document = document.UpdateLayer(id, change);
        EndEdit();
    }

    public void SelectLayer(Guid? id)
    {
        if (BrushStroke is not null) return;
        if (id != ActiveLayerId) { CommitTransform(); CommitGradient(); }
        ActiveLayerId = id;
        RevealActiveLayer();
    }

    /// <summary>Several layers selected at once (Ctrl/Shift-click in the Layers panel).</summary>
    public void SelectLayers(IEnumerable<Guid> ids, Guid? primary)
    {
        if (BrushStroke is not null || Document is not { } document) return;
        var valid = ids.Where(id => document.Find(id) is not null).ToImmutableHashSet();
        if (!valid.SetEquals(SelectedLayerIds)) { CommitTransform(); CommitGradient(); }
        var active = primary is { } p && valid.Contains(p) ? p : valid.FirstOrDefault();
        _activeLayerId = valid.IsEmpty ? null : active;
        _isMaskSelected = false;
        _selectedLayerIds = valid;
        OnPropertyChanged(nameof(ActiveLayerId));
        OnPropertyChanged(nameof(SelectedLayerIds));
        OnPropertyChanged(nameof(ActiveLayer));
        OnPropertyChanged(nameof(IsMaskSelected));
        RevealActiveLayer();
        Invalidate();
    }

    public void SelectLayerTarget(Guid id, bool mask)
    {
        if (BrushStroke is not null) return;
        if (mask != IsMaskSelected) CommitGradient();
        SelectLayer(id);
        IsMaskSelected = mask && ActiveLayer?.Mask is not null;
    }

    /// <summary>Alt-click on a mask thumbnail: shows that mask alone on the canvas, or the composite again.</summary>
    public void ToggleMaskAlone(Guid id)
    {
        bool showing = MaskAloneLayer?.Id == id;
        SelectLayerTarget(id, true);
        if (ActiveLayerId == id && IsMaskSelected) ViewsMaskAlone = !showing;
    }

    /// <summary>Opens the active layer's folders so it can be seen in the Layers panel.</summary>
    public void RevealActiveLayer()
    {
        if (CollapsedGroupIds.IsEmpty || ActiveLayerId is not { } id || Document is not { } document) return;
        CollapsedGroupIds = CollapsedGroupIds.Except(document.AncestorIds(id));
    }

    public void ToggleGroupExpansion(Guid id)
    {
        if (Document?.Find(id) is not { IsGroup: true }) return;
        if (CollapsedGroupIds.Contains(id)) CollapsedGroupIds = CollapsedGroupIds.Remove(id);
        else
        {
            if (ActiveLayerId is { } active && Document.DescendantIds(id).Contains(active)) SelectLayer(id);
            CollapsedGroupIds = CollapsedGroupIds.Add(id);
        }
    }

    private Guid? InsertionParent() => ActiveLayer is { IsGroup: true } folder ? folder.Id : ActiveLayer?.ParentId;

    private int InsertionIndex(CanvasDocument document)
    {
        int insertion = ActiveLayerId is { } id && document.IndexOf(id) is var index and >= 0 ? index + 1 : document.Layers.Count;
        // With a folder selected the new layer goes to the top of the folder.
        if (ActiveLayer is { IsGroup: true } folder)
        {
            var inside = document.DescendantIds(folder.Id);
            int topmost = document.Layers.FindLastIndex(l => inside.Contains(l.Id));
            if (topmost >= 0) insertion = Math.Max(insertion, topmost + 1);
        }
        return insertion;
    }

    public void AddBlankLayer()
    {
        if (!CanEditLayers || Document is not { } document || document.Layers.Count >= DocumentLimits.MaxLayers) return;
        var layer = Layer.Blank(NextName(L.T("图层", "Layer")), document.Size) with { ParentId = InsertionParent() };
        if (layer.ParentId is { } parent) CollapsedGroupIds = CollapsedGroupIds.Remove(parent);
        CommitLayers("New Blank Layer", document.Layers.Insert(InsertionIndex(document), layer), layer.Id);
    }

    public void AddFolder()
    {
        if (!CanEditLayers || Document is not { } document || document.Layers.Count >= DocumentLimits.MaxLayers) return;
        var folder = Layer.Folder(NextName(L.T("组", "Group")), document.Size) with { ParentId = ActiveLayer?.IsGroup == true ? ActiveLayerId : ActiveLayer?.ParentId };
        int insertion = ActiveLayerId is { } id && document.IndexOf(id) is var index and >= 0 ? index + 1 : document.Layers.Count;
        if (folder.ParentId is { } parent) CollapsedGroupIds = CollapsedGroupIds.Remove(parent);
        CommitLayers("New Folder", document.Layers.Insert(insertion, folder), folder.Id);
    }

    /// <summary>Adds a layer of pixels above the active one (import, paste).</summary>
    public void InsertLayer(Layer layer, string editName = "Import Image")
    {
        if (Document is not { } document) return;
        layer = layer with { ParentId = InsertionParent() };
        if (layer.ParentId is { } parent) CollapsedGroupIds = CollapsedGroupIds.Remove(parent);
        CommitLayers(editName, document.Layers.Insert(InsertionIndex(document), layer), layer.Id);
    }

    public void RenameLayer(Guid id, string name)
    {
        name = name.Trim();
        RenamingLayerId = null;
        if (name.Length == 0 || Document?.Find(id) is not { } layer || layer.Name == name) return;
        ChangeLayer(id, "Rename Layer", l => l with { Name = name });
    }

    public void ToggleLayerVisibility(Guid id)
    {
        if (Document?.Find(id) is not { } layer || BrushStroke is not null) return;
        CommitTransform();
        ChangeLayer(id, layer.IsVisible ? "Hide Layer" : "Show Layer", l => l with { IsVisible = !l.IsVisible });
    }

    /// <summary>Photoshop's eye swipe: dragging over eyes gives them the pressed one's new state, as one undo step.</summary>
    public bool? BeginVisibilitySwipe(Guid id)
    {
        if (Document?.Find(id) is not { } layer || BrushStroke is not null) return null;
        bool visible = !layer.IsVisible;
        BeginEdit(visible ? "Show Layer" : "Hide Layer");
        SetVisibilityInSwipe(id, visible);
        return visible;
    }

    public void SetVisibilityInSwipe(Guid id, bool visible)
    {
        if (Document?.Find(id) is not { } layer || layer.IsVisible == visible) return;
        Document = Document.UpdateLayer(id, l => l with { IsVisible = visible });
    }

    public void EndVisibilitySwipe() => EndEdit();

    // MARK: Deleting

    public void DeleteLayerOrMask()
    {
        if (IsMaskSelected && ActiveLayer?.Mask is not null) { DeleteLayerMask(); return; }
        DeleteSelectedLayers();
    }

    public void DeleteSelectedLayers()
    {
        if (!CanEditLayers || Document is not { } document) return;
        var ids = SelectedLayerIds.Count > 0 ? SelectedLayerIds : (ActiveLayerId is { } a ? ImmutableHashSet.Create(a) : ImmutableHashSet<Guid>.Empty);
        if (ids.IsEmpty) return;
        var removed = new HashSet<Guid>(ids);
        foreach (var id in ids) removed.UnionWith(document.DescendantIds(id));
        // The layer below the topmost deleted one becomes active, as in Photoshop.
        int topIndex = document.Layers.FindLastIndex(l => removed.Contains(l.Id));
        var remaining = document.Layers.RemoveAll(l => removed.Contains(l.Id));
        // Clipping to a deleted layer is released.
        remaining = remaining.Select(l => l.MaskSourceId is { } s && removed.Contains(s) ? l with { MaskSourceId = null } : l).ToImmutableList();
        Guid? next = null;
        for (int i = Math.Min(topIndex, document.Layers.Count) - 1; i >= 0; i--)
        {
            if (!removed.Contains(document.Layers[i].Id)) { next = document.Layers[i].Id; break; }
        }
        next ??= remaining.LastOrDefault()?.Id;
        CommitLayers(ids.Count > 1 ? "Delete Layers" : "Delete Layer", remaining, next);
        if (ActiveLayerId is null || removed.Contains(ActiveLayerId.Value)) ActiveLayerId = next;
    }

    // MARK: Duplicating

    /// <summary>Copies of <paramref name="ids"/> (with their folder contents) placed above the topmost of them.</summary>
    public void DuplicateLayers(IReadOnlyCollection<Guid>? ids = null)
    {
        if (!CanEditLayers || Document is not { } document) return;
        var selection = ids ?? (SelectedLayerIds.IsEmpty ? (ActiveLayerId is { } a ? new[] { a } : Array.Empty<Guid>()) : SelectedLayerIds.ToArray());
        if (selection.Count == 0) return;
        var carried = new HashSet<Guid>();
        foreach (var id in selection) carried.UnionWith(document.DescendantIds(id));
        var roots = document.Layers.Where(l => selection.Contains(l.Id) && !carried.Contains(l.Id)).ToList();
        if (roots.Count == 0) return;
        var mapping = new Dictionary<Guid, Guid>();
        var copies = new List<Layer>();
        foreach (var root in roots)
        {
            var subtree = new HashSet<Guid>(document.DescendantIds(root.Id)) { root.Id };
            foreach (var layer in document.Layers.Where(l => subtree.Contains(l.Id)))
            {
                var newId = Guid.NewGuid();
                mapping[layer.Id] = newId;
                copies.Add(layer with
                {
                    Id = newId,
                    Name = layer.Id == root.Id ? layer.Name + L.T(" 拷贝", " copy") : layer.Name,
                });
            }
        }
        copies = copies.Select(l => l with
        {
            ParentId = l.ParentId is { } p && mapping.TryGetValue(p, out var np) ? np : l.ParentId,
            MaskSourceId = l.MaskSourceId is { } s && mapping.TryGetValue(s, out var ns) ? ns : l.MaskSourceId,
        }).ToList();
        int topIndex = document.Layers.FindLastIndex(l => selection.Contains(l.Id) || carried.Contains(l.Id));
        var layers = document.Layers.InsertRange(topIndex + 1, copies);
        var newRoots = roots.Select(r => mapping[r.Id]).ToList();
        if (CommitLayers(newRoots.Count > 1 ? "Duplicate Layers" : "Duplicate Layer", layers, newRoots[^1]))
            SelectLayers(newRoots, newRoots[^1]);
    }

    // MARK: Order and folders

    public bool CanMoveActiveLayer(int offset)
    {
        if (!CanEditLayers || ActiveLayer is not { } active || Document is not { } document) return false;
        var siblings = document.Layers.Where(l => l.ParentId == active.ParentId).ToList();
        int index = siblings.FindIndex(l => l.Id == active.Id);
        return index + offset >= 0 && index + offset < siblings.Count;
    }

    /// <summary>Ctrl+] / Ctrl+[: one step up or down among the layer's siblings (a folder moves with its contents).</summary>
    public void MoveActiveLayer(int offset)
    {
        if (!CanMoveActiveLayer(offset) || ActiveLayer is not { } active || Document is not { } document) return;
        var siblings = document.Layers.Where(l => l.ParentId == active.ParentId).ToList();
        int index = siblings.FindIndex(l => l.Id == active.Id);
        var target = siblings[index + offset];
        if (offset > 0) PlaceLayer(active.Id, active.ParentId, above: target.Id, name: "Reorder Layers");
        else
        {
            int targetIndex = siblings.FindIndex(l => l.Id == target.Id);
            if (targetIndex > 0) PlaceLayer(active.Id, active.ParentId, above: siblings[targetIndex - 1].Id, name: "Reorder Layers");
            else PlaceLayer(active.Id, active.ParentId, atBottom: true, name: "Reorder Layers");
        }
    }

    public bool CanPlaceLayer(Guid id, Guid? parent)
    {
        if (!CanEditLayers || Document is not { } document || document.Find(id) is null) return false;
        if (parent is not { } p) return true;
        return p != id && !document.DescendantIds(id).Contains(p) && document.Find(p)?.IsGroup == true;
    }

    /// <summary>
    /// Moves a layer (with its folder's contents) into <paramref name="parent"/>, just above <paramref name="above"/>
    /// among its new siblings, at the bottom with <paramref name="atBottom"/>, else at the top.
    /// </summary>
    public bool PlaceLayer(Guid id, Guid? parent, Guid? above = null, bool atBottom = false, string name = "Move Layer")
    {
        if (!CanPlaceLayer(id, parent) || Document is not { } document || above == id) return false;
        var moving = new HashSet<Guid>(document.DescendantIds(id)) { id };
        var block = document.Layers.Where(l => moving.Contains(l.Id)).Select(l => l.Id == id ? l with { ParentId = parent } : l).ToList();
        var rest = document.Layers.Where(l => !moving.Contains(l.Id)).ToList();
        int insertion;
        if (above is { } target)
        {
            int targetIndex = rest.FindIndex(l => l.Id == target);
            if (targetIndex < 0) return false;
            // Past the target's own contents when it is a folder.
            var inside = document.DescendantIds(target);
            int last = targetIndex;
            for (int i = targetIndex + 1; i < rest.Count; i++) if (inside.Contains(rest[i].Id)) last = i;
            insertion = last + 1;
        }
        else if (atBottom)
        {
            if (parent is { } p)
            {
                int folderIndex = rest.FindIndex(l => l.Id == p);
                insertion = folderIndex + 1;
            }
            else insertion = 0;
        }
        else
        {
            if (parent is { } p)
            {
                var inside = document.DescendantIds(p);
                int last = rest.FindIndex(l => l.Id == p);
                for (int i = 0; i < rest.Count; i++) if (inside.Contains(rest[i].Id)) last = Math.Max(last, i);
                insertion = last + 1;
            }
            else insertion = rest.Count;
        }
        rest.InsertRange(Math.Clamp(insertion, 0, rest.Count), block);
        var layers = ReleaseDetachedClipping(rest).ToImmutableList();
        if (layers.SequenceEqual(document.Layers)) return false;
        bool ok = CommitLayers(name, layers, id);
        if (ok && parent is { } folder) CollapsedGroupIds = CollapsedGroupIds.Remove(folder);
        return ok;
    }

    /// <summary>A clipped layer must sit in its base's stack: same folder, above it with only clipped layers between.</summary>
    private static List<Layer> ReleaseDetachedClipping(List<Layer> layers)
    {
        var result = new List<Layer>(layers);
        for (int i = 0; i < result.Count; i++)
        {
            if (result[i].MaskSourceId is not { } source) continue;
            bool attached = false;
            for (int j = i - 1; j >= 0; j--)
            {
                if (result[j].ParentId != result[i].ParentId) continue;
                if (result[j].Id == source) { attached = true; break; }
                if (result[j].MaskSourceId != source) break;
            }
            if (!attached) result[i] = result[i] with { MaskSourceId = null };
        }
        return result;
    }

    public void MoveActiveLayerOutOfGroup()
    {
        if (ActiveLayer is not { ParentId: { } parent } layer || Document?.Find(parent) is not { } folder) return;
        PlaceLayer(layer.Id, folder.ParentId, above: folder.Id, name: "Move Out of Folder");
    }

    /// <summary>Ctrl+G: the selected layers wrapped in a new folder, at the topmost selected branch.</summary>
    public void GroupSelectedLayers()
    {
        if (!CanEditLayers || Document is not { } document || document.Layers.Count >= DocumentLimits.MaxLayers) return;
        var selected = SelectedLayerIds.Where(id => document.Find(id) is not null).ToHashSet();
        if (selected.Count == 0) return;
        List<Guid?> Ancestors(Guid id)
        {
            var list = new List<Guid?>();
            var parent = document.Find(id)?.ParentId;
            while (parent is { } p) { list.Add(p); parent = document.Find(p)?.ParentId; }
            list.Add(null);
            return list;
        }
        var roots = selected.Where(id => !Ancestors(id).Any(a => a is { } x && selected.Contains(x))).ToHashSet();
        var ordered = document.Hierarchy.Order.Where(roots.Contains).ToList();
        Guid? parent = null;
        if (ordered.Count > 0)
        {
            parent = Ancestors(ordered[0]).FirstOrDefault(candidate => ordered.All(o => Ancestors(o).Contains(candidate)));
        }
        var folder = Layer.Folder(NextName(L.T("组", "Group")), document.Size) with { ParentId = parent };
        var branches = ordered.Select(id =>
        {
            var branch = id;
            while (document.Find(branch)?.ParentId is { } next && next != parent) branch = next;
            return branch;
        }).ToHashSet();
        int highest = document.Layers.FindLastIndex(l => branches.Contains(l.Id));
        var subtree = new HashSet<Guid>(roots);
        foreach (var r in roots) subtree.UnionWith(document.DescendantIds(r));
        int insertion = highest >= 0 ? document.Layers.Take(highest + 1).Count(l => !subtree.Contains(l.Id)) : document.Layers.Count;
        var layers = document.Layers.Where(l => !subtree.Contains(l.Id)).ToList();
        insertion = Math.Min(insertion, layers.Count);
        // The folder, then what goes in it, in their order.
        var moved = document.Layers.Where(l => subtree.Contains(l.Id))
            .Select(l => roots.Contains(l.Id) ? l with { ParentId = folder.Id } : l).ToList();
        layers.Insert(insertion, folder);
        layers.InsertRange(insertion + 1, moved);
        if (CommitLayers("Group Layers", ReleaseDetachedClipping(layers).ToImmutableList(), folder.Id) && parent is { } p)
            CollapsedGroupIds = CollapsedGroupIds.Remove(p);
    }

    public bool CanUngroupLayers => CanEditLayers && ActiveLayer?.IsGroup == true;

    /// <summary>Ctrl+Shift+G: the folder's direct children take its place, and the folder goes.</summary>
    public void UngroupLayers()
    {
        if (!CanUngroupLayers || ActiveLayer is not { } folder || Document is not { } document) return;
        var childIds = document.Layers.Where(l => l.ParentId == folder.Id).Select(l => l.Id).ToHashSet();
        var layers = document.Layers.Where(l => l.Id != folder.Id)
            .Select(l => childIds.Contains(l.Id) ? l with { ParentId = folder.ParentId } : l).ToList();
        if (CommitLayers("Ungroup Layers", ReleaseDetachedClipping(layers).ToImmutableList(), childIds.FirstOrDefault()))
        {
            if (childIds.Count > 0) SelectLayers(childIds, document.Layers.LastOrDefault(l => childIds.Contains(l.Id))?.Id);
            CollapsedGroupIds = CollapsedGroupIds.Remove(folder.Id);
        }
    }

    // MARK: Appearance

    public bool CanEditAppearance => CanEditLayers && SelectedLayerIds.Count == 1 && ActiveLayer is { IsGroup: false };
    public bool CanEditOpacity => CanEditLayers && ActiveLayer is not null;

    /// <summary>A blend mode shown on the canvas while hovering the menu, before it is chosen.</summary>
    public (Guid LayerId, BlendMode Mode)? BlendPreview { get; private set; }

    public void PreviewBlendMode(BlendMode? mode)
    {
        BlendPreview = mode is { } m && ActiveLayerId is { } id && CanEditAppearance ? (id, m) : null;
        Invalidate();
    }

    public void SetLayerBlendMode(BlendMode mode)
    {
        BlendPreview = null;
        if (!CanEditAppearance || ActiveLayerId is not { } id || ActiveLayer?.BlendMode == mode) { Invalidate(); return; }
        ChangeLayer(id, "Layer Blend Mode", l => l with { BlendMode = mode });
    }

    /// <summary>Shift+= / Shift+-: the next or previous blend mode in the menu's order.</summary>
    public void CycleBlendMode(bool forward)
    {
        if (!CanEditAppearance || ActiveLayer is not { } layer) return;
        int index = Array.IndexOf(BlendModes.All, layer.BlendMode);
        SetLayerBlendMode(BlendModes.All[(index + (forward ? 1 : BlendModes.All.Length - 1)) % BlendModes.All.Length]);
    }

    private Guid? _opacityEditLayer;

    /// <summary>A slider drag: every change until <see cref="FinishOpacityEdit"/> is one undo step.</summary>
    public void BeginOpacityEdit()
    {
        if (!CanEditOpacity || _opacityEditLayer is not null || ActiveLayerId is not { } id) return;
        BeginEdit("Layer Opacity");
        _opacityEditLayer = id;
    }

    public void FinishOpacityEdit()
    {
        if (_opacityEditLayer is null) return;
        _opacityEditLayer = null;
        EndEdit();
    }

    public void SetLayerOpacity(double opacity)
    {
        if (!double.IsFinite(opacity) || !CanEditOpacity) return;
        var ids = _opacityEditLayer is { } editing ? new[] { editing } : SelectedLayerIds.ToArray();
        if (ids.Length == 0 || Document is not { } document) return;
        double value = Math.Clamp(opacity, 0, 1);
        bool standalone = _opacityEditLayer is null;
        if (standalone) BeginEdit("Layer Opacity");
        var layers = document.Layers;
        foreach (var id in ids)
        {
            int index = layers.FindIndex(l => l.Id == id);
            if (index >= 0) layers = layers.SetItem(index, layers[index] with { Opacity = value });
        }
        Document = document with { Layers = layers };
        if (standalone) EndEdit();
    }

    // MARK: Masks

    public bool CanEditMask => CanEditLayers && SelectedLayerIds.Count == 1 && ActiveLayer is not null;

    /// <summary>
    /// The Layers panel's Add Mask button: with no selection a reveal-all (or, Alt, hide-all) mask; with a selection,
    /// Reveal Selection (or Hide Selection). The selection is used up in the same step.
    /// </summary>
    public void AddMask(bool revealing = true)
    {
        if (!CanEditMask || ActiveLayer is not { Mask: null } layer || Document is not { } document) return;
        if (Selection is not { } selection)
        {
            ChangeLayer(layer.Id, revealing ? "Add Reveal-All Mask" : "Add Hide-All Mask",
                l => l with { Mask = new LayerMask(RasterImage.SolidMask(revealing)) });
            IsMaskSelected = true;
            return;
        }
        int width = layer.GridWidth, height = layer.GridHeight;
        if ((long)width * height > DocumentLimits.MaxSurfacePixels) { ShowError(L.T("无法添加蒙版", "Couldn’t add the mask"), L.T("图层太大。", "The layer is too large.")); return; }
        var coverage = new byte[width * height];
        var clip = selection.Clip(document.Width, document.Height);
        var map = layer.Transform.PixelToDocument(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var doc = map.Apply(x + 0.5, y + 0.5);
                double k = clip.At(doc.X, doc.Y);
                coverage[y * width + x] = (byte)Math.Round((revealing ? k : 1 - k) * 255);
            }
        }
        var mask = new LayerMask(RasterImage.FromMask(coverage, width, height));
        BeginEdit(revealing ? "Reveal Selection" : "Hide Selection");
        Document = document.UpdateLayer(layer.Id, l => l with { Mask = mask }) with { Selection = null };
        EndEdit();
        IsMaskSelected = true;
    }

    public void ToggleLayerMask()
    {
        if (!CanEditMask || ActiveLayer is not { Mask: { } mask } layer) return;
        ChangeLayer(layer.Id, mask.IsEnabled ? "Disable Layer Mask" : "Enable Layer Mask", l => l with { Mask = mask with { IsEnabled = !mask.IsEnabled } });
        Invalidate();
    }

    public void ToggleLayerMask(Guid id)
    {
        SelectLayer(id);
        ToggleLayerMask();
    }

    public void DeleteLayerMask()
    {
        if (!CanEditMask || ActiveLayer is not { Mask: not null } layer) return;
        ChangeLayer(layer.Id, "Delete Layer Mask", l => l with { Mask = null });
        IsMaskSelected = false;
    }

    /// <summary>Bakes the mask into the layer's pixels and removes it.</summary>
    public void ApplyLayerMask()
    {
        if (!CanEditMask || ActiveLayer is not { Mask: { } mask, Asset: { } asset } layer || Document is not { } document) return;
        var options = new RenderOptions();
        // The layer alone, at its own pixel grid, through its mask.
        var single = document with
        {
            Width = asset.Width, Height = asset.Height,
            Layers = ImmutableList.Create(layer with
            {
                ParentId = null, Opacity = 1, BlendMode = BlendMode.Normal, IsVisible = true, MaskSourceId = null,
                Transform = new LayerTransform(PointD.Zero, new SizeD(asset.Width, asset.Height)) with { Sampling = LayerSampling.Nearest },
                Mask = mask.Placement is { } placement
                    ? mask with { Placement = placement.Following(layer.Transform, new LayerTransform(PointD.Zero, new SizeD(asset.Width, asset.Height))) }
                    : mask with { IsEnabled = true },
            }),
        };
        using var rendered = DocumentRenderer.RenderToImage(single, options);
        var baked = RasterImage.FromImage(rendered, false, asset.Name);
        BeginEdit("Apply Layer Mask");
        Document = document.UpdateLayer(layer.Id, l => l with { Asset = baked, Mask = null });
        EndEdit();
        IsMaskSelected = false;
    }

    public void ToggleMaskLink(Guid id)
    {
        if (Document?.Find(id) is not { Mask: { } mask }) return;
        CommitTransform();
        ChangeLayer(id, mask.IsLinked ? "Unlink Layer Mask" : "Link Layer Mask", l => l with { Mask = mask with { IsLinked = !mask.IsLinked } });
    }

    // MARK: Clipping masks

    /// <summary>A layer can clip to the sibling just below it (that isn't itself a folder or adjustment).</summary>
    public bool CanToggleClippingMask(Guid id)
    {
        if (!CanEditLayers || Document is not { } document || document.Find(id) is not { } layer || layer.IsGroup) return false;
        if (layer.MaskSourceId is not null) return true;
        return ClippingBase(document, layer) is not null;
    }

    private static Layer? ClippingBase(CanvasDocument document, Layer layer)
    {
        int index = document.IndexOf(layer.Id);
        for (int i = index - 1; i >= 0; i--)
        {
            var below = document.Layers[i];
            if (below.ParentId != layer.ParentId) continue;
            if (below.IsGroup) return null;
            // Below a clipped layer, clip to that stack's base.
            return below.MaskSourceId is { } source ? document.Find(source) : below.IsAdjustment ? null : below;
        }
        return null;
    }

    /// <summary>Ctrl+Alt+G (or Alt-click between layers): clips to the layer below, or releases.</summary>
    public void ToggleClippingMask(Guid id)
    {
        if (!CanToggleClippingMask(id) || Document is not { } document || document.Find(id) is not { } layer) return;
        if (layer.MaskSourceId is not null)
        {
            // Releasing one releases those clipped above it too, as in Photoshop.
            var layers = document.Layers.ToList();
            int index = layers.FindIndex(l => l.Id == id);
            var source = layer.MaskSourceId;
            for (int i = index; i < layers.Count; i++)
            {
                if (layers[i].ParentId != layer.ParentId) continue;
                if (layers[i].MaskSourceId != source) break;
                layers[i] = layers[i] with { MaskSourceId = null };
            }
            CommitLayers("Release Clipping Mask", layers.ToImmutableList(), keepSelection: true);
        }
        else if (ClippingBase(document, layer) is { } baseLayer)
        {
            ChangeLayer(id, "Create Clipping Mask", l => l with { MaskSourceId = baseLayer.Id });
        }
    }

    // MARK: Merging

    private (List<Guid> Ids, HashSet<Guid> Removed, string Name, Guid? Parent, Guid Anchor, string Action)? MergePlan()
    {
        if (!CanEditLayers || Document is not { } document || ActiveLayer is not { } active) return null;
        var layers = document.Layers;
        if (SelectedLayerIds.Count > 1)
        {
            var picked = new HashSet<Guid>(SelectedLayerIds);
            foreach (var id in SelectedLayerIds) picked.UnionWith(document.DescendantIds(id));
            var ordered = layers.Where(l => picked.Contains(l.Id)).ToList();
            var top = ordered.LastOrDefault(l => SelectedLayerIds.Contains(l.Id));
            if (!ordered.Any(l => !l.IsGroup) || top is null) return null;
            return (ordered.Select(l => l.Id).ToList(), picked, top.Name, top.ParentId, top.Id, "Merge Layers");
        }
        if (active.IsGroup)
        {
            var inside = document.DescendantIds(active.Id);
            if (!layers.Any(l => inside.Contains(l.Id) && !l.IsGroup)) return null;
            var ids = layers.Where(l => inside.Contains(l.Id) || l.Id == active.Id).Select(l => l.Id).ToList();
            return (ids, ids.ToHashSet(), active.Name, active.ParentId, active.Id, "Merge Group");
        }
        int index = layers.FindIndex(l => l.Id == active.Id);
        var below = layers.Take(index).LastOrDefault(l => l.ParentId == active.ParentId);
        // An adjustment layer has no pixels to merge into, as in Photoshop.
        if (below is null || below.IsGroup || below.IsAdjustment) return null;
        return (new List<Guid> { below.Id, active.Id }, new HashSet<Guid> { below.Id, active.Id }, below.Name, active.ParentId, active.Id, "Merge Down");
    }

    public bool CanMergeLayers => MergePlan() is not null;
    public string MergeTitle => MergePlan()?.Action switch
    {
        "Merge Layers" => L.T("合并图层", "Merge Layers"), "Merge Group" => L.T("合并组", "Merge Group"), _ => L.T("向下合并", "Merge Down"),
    };

    /// <summary>Ctrl+E: the layers composited as the canvas shows them into one pixel layer, trimmed to what is there.</summary>
    public void MergeLayers()
    {
        CommitTransform();
        if (MergePlan() is not { } plan || Document is not { } document) return;
        var kept = plan.Ids.ToHashSet();
        var subset = document.Layers.Where(l => kept.Contains(l.Id)).Select(l => l with
        {
            ParentId = l.ParentId is { } p && !kept.Contains(p) ? null : l.ParentId,
            MaskSourceId = l.MaskSourceId is { } s && !kept.Contains(s) ? null : l.MaskSourceId,
        }).ToImmutableList();
        var flat = document with { Layers = subset, Selection = null };
        using var full = DocumentRenderer.RenderToImage(flat);
        var image = RasterImage.FromImage(full, false, plan.Name);
        var bounds = PixelOps.AlphaBounds(image);
        if (bounds.IsEmpty) bounds = new RectI(0, 0, 1, 1);
        var trimmed = PixelOps.Crop(image, bounds, plan.Name);
        var merged = Layer.FromImage(trimmed, new PointD(bounds.X, bounds.Y), plan.Name) with { ParentId = plan.Parent };
        var next = document.Layers.Where(l => !plan.Removed.Contains(l.Id))
            .Select(l => l.MaskSourceId is { } s && plan.Removed.Contains(s) ? l with { MaskSourceId = merged.Id } : l).ToList();
        int slot = document.IndexOf(plan.Anchor);
        int insertion = slot - document.Layers.Take(slot).Count(l => plan.Removed.Contains(l.Id));
        next.Insert(Math.Clamp(insertion, 0, next.Count), merged);
        CommitLayers(plan.Action, next.ToImmutableList(), merged.Id);
    }
}
