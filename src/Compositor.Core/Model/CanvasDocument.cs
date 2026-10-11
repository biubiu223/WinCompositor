using System.Collections.Immutable;
using Compositor.Core.Geometry;

namespace Compositor.Core.Model;

/// <summary>
/// The document: canvas size, layers bottom to top, guides, and the selection (part of the document so undo covers
/// it; never saved). Mirrors CanvasDocument in EditorSession.swift.
/// </summary>
public sealed record CanvasDocument
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required int Width { get; init; }
    public required int Height { get; init; }
    public double Resolution { get; init; } = 72;
    /// <summary>Bottom to top.</summary>
    public ImmutableList<Layer> Layers { get; init; } = ImmutableList<Layer>.Empty;
    public ImmutableList<CanvasGuide> Guides { get; init; } = ImmutableList<CanvasGuide>.Empty;
    public DocumentSelection? Selection { get; init; }

    public SizeD Size => new(Width, Height);
    public RectD Bounds => new(0, 0, Width, Height);

    public static bool ValidDimension(int value) => value >= 1 && value <= DocumentLimits.MaxSide;

    public Layer? Find(Guid? id) => id is { } value ? Layers.Find(l => l.Id == value) : null;
    public int IndexOf(Guid id) => Layers.FindIndex(l => l.Id == id);

    public CanvasDocument ReplaceLayer(Layer layer)
    {
        int index = IndexOf(layer.Id);
        return index < 0 ? this : this with { Layers = Layers.SetItem(index, layer) };
    }

    public CanvasDocument UpdateLayer(Guid id, Func<Layer, Layer> change)
    {
        int index = IndexOf(id);
        return index < 0 ? this : this with { Layers = Layers.SetItem(index, change(Layers[index])) };
    }

    public bool Equals(CanvasDocument? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null) return false;
        return Id == other.Id && Width == other.Width && Height == other.Height && Resolution == other.Resolution
            && Layers.SequenceEqual(other.Layers) && Guides.SequenceEqual(other.Guides) && Equals(Selection, other.Selection);
    }

    public override int GetHashCode() => HashCode.Combine(Id, Width, Height, Layers.Count);

    // MARK: Hierarchy

    /// <summary>Every layer and folder in drawing order (each folder before what's inside it), and which of them show.</summary>
    public LayerOrder Hierarchy => LayerOrder.Resolve(Layers);
    public IReadOnlySet<Guid> EffectiveVisibleIds => Hierarchy.Visible;

    /// <summary>The layers that show (folders left out), bottom to top.</summary>
    public IReadOnlyList<Layer> RenderLayers()
    {
        var byId = Layers.ToDictionary(l => l.Id);
        return Hierarchy.Drawn.Select(id => byId[id]).ToList();
    }

    /// <summary>A folder's opacity multiplies into everything inside it (folders are pass-through).</summary>
    public double EffectiveOpacity(Layer layer)
    {
        double opacity = layer.Opacity;
        var parent = layer.ParentId;
        for (int depth = 0; parent is { } id && depth < 64; depth++)
        {
            var folder = Find(id);
            if (folder is null) break;
            opacity *= folder.Opacity;
            parent = folder.ParentId;
        }
        return opacity;
    }

    public ISet<Guid> DescendantIds(Guid id)
    {
        var children = Layers.Where(l => l.ParentId is not null).ToLookup(l => l.ParentId!.Value);
        var result = new HashSet<Guid>();
        var pending = new Stack<Guid>();
        pending.Push(id);
        while (pending.Count > 0)
        {
            var parent = pending.Pop();
            foreach (var child in children[parent])
            {
                if (result.Add(child.Id)) pending.Push(child.Id);
            }
        }
        return result;
    }

    public IEnumerable<Guid> AncestorIds(Guid id)
    {
        var parent = Find(id)?.ParentId;
        for (int depth = 0; parent is { } p && depth < 64; depth++)
        {
            yield return p;
            parent = Find(p)?.ParentId;
        }
    }

    /// <summary>Rows for the Layers panel: top first, with depth and inherited visibility; collapsed folders hide their contents.</summary>
    public List<HierarchyEntry> Entries(bool topFirst, IReadOnlySet<Guid>? collapsed = null)
    {
        var children = Layers.GroupBy(l => l.ParentId ?? Guid.Empty).ToDictionary(g => g.Key, g => g.ToList());
        var result = new List<HierarchyEntry>();
        void Visit(Guid parent, int depth, bool visible)
        {
            if (depth > 64 || !children.TryGetValue(parent, out var siblings)) return;
            IEnumerable<Layer> ordered = topFirst ? Enumerable.Reverse(siblings) : siblings;
            foreach (var layer in ordered)
            {
                bool effective = visible && layer.IsVisible;
                result.Add(new HierarchyEntry(layer, depth, effective));
                if (layer.IsGroup && (collapsed is null || !collapsed.Contains(layer.Id)))
                    Visit(layer.Id, depth + 1, effective);
            }
        }
        Visit(Guid.Empty, 0, true);
        return result;
    }

    /// <summary>Validates the folder tree: unique ids, parents that are folders, no cycles, at most 64 levels.</summary>
    public static bool ValidateHierarchy(IReadOnlyList<Layer> layers)
    {
        var byId = new Dictionary<Guid, Layer>();
        foreach (var layer in layers)
        {
            if (!byId.TryAdd(layer.Id, layer)) return false;
            if (layer.IsGroup && layer.Asset is not null) return false;
        }
        foreach (var layer in layers)
        {
            var seen = new HashSet<Guid> { layer.Id };
            var parent = layer.ParentId;
            while (parent is { } id)
            {
                if (seen.Count > 64 || !seen.Add(id) || !byId.TryGetValue(id, out var node) || !node.IsGroup) return false;
                parent = node.ParentId;
            }
        }
        return true;
    }
}

public sealed record HierarchyEntry(Layer Layer, int Depth, bool Visible);

/// <summary>The layer hierarchy worked out from each layer's id, folder and visibility. Mirrors LayerOrder.</summary>
public sealed class LayerOrder
{
    public required IReadOnlyList<Guid> Order { get; init; }
    public required IReadOnlySet<Guid> Visible { get; init; }
    public required IReadOnlyList<Guid> Drawn { get; init; }

    private static readonly object Lock = new();
    private static (ImmutableList<Layer> Layers, LayerOrder Result)? _last;

    public static LayerOrder Resolve(ImmutableList<Layer> layers)
    {
        lock (Lock)
        {
            if (_last is { } known && ReferenceEquals(known.Layers, layers)) return known.Result;
        }
        var children = layers.GroupBy(l => l.ParentId ?? Guid.Empty).ToDictionary(g => g.Key, g => g.ToList());
        var order = new List<Guid>();
        var visible = new HashSet<Guid>();
        var drawn = new List<Guid>();
        void Visit(Guid parent, int depth, bool shown)
        {
            if (depth > 64 || !children.TryGetValue(parent, out var nodes)) return;
            foreach (var node in nodes)
            {
                bool effective = shown && node.IsVisible;
                order.Add(node.Id);
                if (effective)
                {
                    visible.Add(node.Id);
                    if (!node.IsGroup) drawn.Add(node.Id);
                }
                if (node.IsGroup) Visit(node.Id, depth + 1, effective);
            }
        }
        Visit(Guid.Empty, 0, true);
        var result = new LayerOrder { Order = order, Visible = visible, Drawn = drawn };
        lock (Lock) { _last = (layers, result); }
        return result;
    }
}
