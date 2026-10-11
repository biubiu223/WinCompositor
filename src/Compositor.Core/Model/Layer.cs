using System.Text.Json.Nodes;
using Compositor.Core.Adjustments;
using Compositor.Core.Geometry;

namespace Compositor.Core.Model;

/// <summary>
/// Immutable, normalized layer-local coverage. Mirrors LayerMask.swift: white reveals, black hides.
/// <see cref="Placement"/> is set once the mask has been moved apart from its layer.
/// </summary>
public sealed record LayerMask(RasterImage Asset, bool IsEnabled = true, LayerTransform? Placement = null, bool IsLinked = true)
{
    public RasterImage? EnabledImage => IsEnabled ? Asset : null;

    public LayerMask Replacing(RasterImage asset) => this with { Asset = asset };

    /// <summary>
    /// Where the mask sits once its layer moves from <paramref name="old"/> to <paramref name="next"/>: carried along
    /// when linked, left where it was when unlinked. Null when it covers the layer's own grid.
    /// </summary>
    public LayerTransform? PlacementMovingLayer(LayerTransform old, LayerTransform next)
    {
        if (Asset.IsUniform) return null;
        LayerTransform? moved = IsLinked ? Placement?.Following(old, next) : (Placement ?? old);
        if (moved is { } m && m.SamePlacement(next)) return null;
        return moved;
    }

    /// <summary>
    /// What a mask shows beyond its pixels once placed apart from its layer: white or black, whichever most of its
    /// edge is, so a reveal-all mask keeps revealing and a hide-all mask hiding.
    /// </summary>
    public static byte Background(RasterImage mask)
    {
        using var pixmap = mask.PeekPixels();
        var bytes = pixmap.GetPixelSpan();
        int w = mask.Width, h = mask.Height, stride = pixmap.RowBytes;
        long total = 0, count = 0;
        for (int y = 0; y < h; y++)
        {
            bool edgeRow = y == 0 || y == h - 1;
            for (int x = 0; x < w; x++)
            {
                if (!edgeRow && x != 0 && x != w - 1) { x = Math.Max(x, w - 2); continue; }
                total += bytes[y * stride + x];
                count++;
            }
        }
        return count == 0 || total * 2 >= count * 255 ? (byte)255 : (byte)0;
    }

    public bool Equals(LayerMask? other) =>
        other is not null && ReferenceEquals(Asset, other.Asset) && IsEnabled == other.IsEnabled
        && Placement == other.Placement && IsLinked == other.IsLinked;

    public override int GetHashCode() => HashCode.Combine(Asset, IsEnabled, Placement, IsLinked);
}

/// <summary>
/// One layer, folder or adjustment layer. Value semantics: edits make a new record, so history snapshots share
/// everything that did not change. Mirrors ImageLayer in EditorSession.swift.
/// </summary>
public sealed record Layer
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public RasterImage? Asset { get; init; }
    public required LayerTransform Transform { get; init; }
    public bool IsVisible { get; init; } = true;
    public Guid? ParentId { get; init; }
    public bool IsGroup { get; init; }
    public double Opacity { get; init; } = 1;
    public BlendMode BlendMode { get; init; } = BlendMode.Normal;
    /// <summary>A clipping mask: the layer whose coverage limits this one.</summary>
    public Guid? MaskSourceId { get; init; }
    public LayerMask? Mask { get; init; }

    // Features carried through the project format but not edited yet in this version. They are kept verbatim so
    // that opening and saving a project made on the Mac never loses them.
    /// <summary>Set on adjustment layers: what they do to everything below them.</summary>
    public LayerAdjustment? Adjustment { get; init; }
    /// <summary>Set on a shape layer whose pixels are still the shape it drew.</summary>
    public LayerShape? Shape { get; init; }
    /// <summary>Layer style: drop shadow, stroke and the rest, drawn around the pixels as they render.</summary>
    public LayerEffects? Effects { get; init; }
    /// <summary>Set on a text layer whose pixels are still the text it drew: it can be edited again.</summary>
    public LayerText? Text { get; init; }

    /// <summary>The effects that show, or null when there are none.</summary>
    public LayerEffects? VisibleEffects => Effects is { IsEmpty: false } e && e.Visible is { IsEmpty: false } v && v.IsValid ? v : null;

    public PointD Origin => Transform.Origin;
    public SizeD Size => Transform.Size;
    public bool IsAdjustment => Adjustment is not null;

    /// <summary>Where the mask's pixels sit on the document: its own placement, else the layer's.</summary>
    public LayerTransform MaskTransform => Mask?.Placement ?? Transform;

    /// <summary>The pixel grid a mask or paint covers: the image's own size, else the layer's size.</summary>
    public int GridWidth => Asset?.Width ?? Math.Max(1, (int)Math.Round(Size.Width));
    public int GridHeight => Asset?.Height ?? Math.Max(1, (int)Math.Round(Size.Height));

    public static Layer FromImage(RasterImage asset, PointD origin, string? name = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = name ?? asset.Name,
        Asset = asset,
        Transform = new LayerTransform(origin, new SizeD(asset.Width, asset.Height)),
    };

    public static Layer Blank(string name, SizeD size) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Transform = new LayerTransform(PointD.Zero, size),
    };

    public static Layer Folder(string name, SizeD size) => Blank(name, size) with { IsGroup = true };

    /// <summary>
    /// Pixel content, masks and attached data are compared by identity (as the original compares CGImages),
    /// everything else by value.
    /// </summary>
    public bool Equals(Layer? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null) return false;
        return Id == other.Id && Name == other.Name && ReferenceEquals(Asset, other.Asset) && Transform == other.Transform
            && IsVisible == other.IsVisible && ParentId == other.ParentId && IsGroup == other.IsGroup
            && Opacity == other.Opacity && BlendMode == other.BlendMode && MaskSourceId == other.MaskSourceId
            && Equals(Mask, other.Mask) && ReferenceEquals(Adjustment, other.Adjustment) && ReferenceEquals(Shape, other.Shape)
            && ReferenceEquals(Effects, other.Effects) && ReferenceEquals(Text, other.Text);
    }

    public override int GetHashCode() => HashCode.Combine(Id, Name, Transform, IsVisible, Opacity, BlendMode);
}
