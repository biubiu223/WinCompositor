using Compositor.Core.Geometry;
using Compositor.Core.Localization;
using Compositor.Core.Model;

namespace Compositor.Core.Editing;

/// <summary>A shape being dragged out with the Shape tool, in whole document pixels. Mirrors ShapeDraft.</summary>
public sealed record ShapeDraft(ShapeKind Kind, PointD Anchor, RectD Rect, PointD? End = null, double CornerRadius = 0);

public sealed partial class EditorSession
{
    private ShapeKind _shapeKind = ShapeKind.Rectangle;
    public ShapeKind ShapeKind { get => _shapeKind; set { CancelShape(); SetField(ref _shapeKind, value); } }

    private double _shapeCornerRadius;
    /// <summary>Rectangles' corner radius in document pixels; 0 keeps them square.</summary>
    public double ShapeCornerRadius { get => _shapeCornerRadius; set => SetField(ref _shapeCornerRadius, Math.Clamp(value, 0, 5000)); }

    private double _shapeLineWidth = 4;
    public double ShapeLineWidth { get => _shapeLineWidth; set => SetField(ref _shapeLineWidth, Math.Clamp(value, 1, 5000)); }

    private ShapeDraft? _shapeDraft;
    public ShapeDraft? ShapeDraft { get => _shapeDraft; private set { _shapeDraft = value; Invalidate(); } }

    public void BeginShape(PointD point)
    {
        if (Tool != Tool.Shape || !CanEditLayers || !point.IsFinite) return;
        var anchor = point.Rounded();
        ShapeDraft = new ShapeDraft(ShapeKind, anchor, new RectD(anchor.X, anchor.Y, 0, 0),
            CornerRadius: ShapeKind == ShapeKind.Rectangle ? ShapeCornerRadius : 0);
    }

    /// <summary>Shift makes a square or circle (a line snaps to eighths of a turn); Alt grows it from its center.</summary>
    public void DragShape(PointD point, bool square, bool fromCenter)
    {
        if (ShapeDraft is not { } draft || !point.IsFinite) return;
        if (draft.Kind == ShapeKind.Line)
        {
            var end = point;
            if (square)
            {
                double dx = point.X - draft.Anchor.X, dy = point.Y - draft.Anchor.Y;
                double angle = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4), length = Math.Sqrt(dx * dx + dy * dy);
                end = new PointD(draft.Anchor.X + Math.Cos(angle) * length, draft.Anchor.Y + Math.Sin(angle) * length);
            }
            ShapeDraft = draft with { End = end, Rect = DragBox(draft.Anchor, end, false, fromCenter) };
            return;
        }
        ShapeDraft = draft with { Rect = DragBox(draft.Anchor, point, square, fromCenter).Integral() };
    }

    public void CancelShape() { if (_shapeDraft is not null) ShapeDraft = null; }

    /// <summary>Shift+U (and Tab): Rectangle, Ellipse, Line in turn.</summary>
    public void ToggleShapeKind() => ShapeKind = (ShapeKind)(((int)ShapeKind + 1) % 3);

    /// <summary>
    /// The dragged shape, filled (or a line stroked) with the foreground color, on a new layer above the active one, in
    /// one undo step. A click without a drag makes nothing; the selection is left alone.
    /// </summary>
    public void FinishShape()
    {
        if (ShapeDraft is not { } draft) return;
        ShapeDraft = null;
        if (!CanEditLayers || Document is not { } document || document.Layers.Count >= DocumentLimits.MaxLayers) return;
        var rect = draft.Rect;
        double thickness = ShapeLineWidth;
        PointD? start = null, end = null;
        if (draft.Kind == ShapeKind.Line)
        {
            var from = draft.Anchor;
            var to = draft.End ?? draft.Anchor;
            if (from.DistanceTo(to) < 1) return;
            // The two ends' box, with room for the stroke's thickness and round ends around it.
            rect = RectD.FromLTRB(Math.Min(from.X, to.X), Math.Min(from.Y, to.Y), Math.Max(from.X, to.X), Math.Max(from.Y, to.Y))
                .Inflate(thickness / 2, thickness / 2);
            rect = RectD.FromLTRB(Math.Floor(rect.Left), Math.Floor(rect.Top), Math.Ceiling(rect.Right), Math.Ceiling(rect.Bottom));
            PointD Unit(PointD p) => new(rect.Width > 0 ? (p.X - rect.X) / rect.Width : 0.5, rect.Height > 0 ? (p.Y - rect.Y) / rect.Height : 0.5);
            start = Unit(from);
            end = Unit(to);
        }
        if (rect.Width < 1 || rect.Height < 1) return;
        int w = (int)Math.Round(rect.Width), h = (int)Math.Round(rect.Height);
        if ((long)w * h > DocumentLimits.MaxSurfacePixels || w > DocumentLimits.MaxSide || h > DocumentLimits.MaxSide)
        {
            ShowError(L.T("无法绘制形状", "Couldn’t draw the shape"), L.T("这个形状太大了。", "That shape is too large."));
            return;
        }
        try
        {
            var color = ForegroundColor;
            var shape = new LayerShape(draft.Kind, color.Red, color.Green, color.Blue, draft.CornerRadius,
                draft.Kind == ShapeKind.Line ? thickness : null, start, end);
            string name = NextName(draft.Kind.ToDisplayName());
            var image = shape.Render(w, h, name);
            InsertLayer(Layer.FromImage(image, rect.Origin, name) with { Shape = shape }, draft.Kind.ToName());
        }
        catch (Exception error) { ShowError(L.T("无法绘制形状", "Couldn’t draw the shape"), error.Message); }
    }

    /// <summary>
    /// A shape layer scaled to a new size draws its shape again at that size, so a rounded corner keeps its radius and
    /// edges stay crisp. Part of the edit that changed the size.
    /// </summary>
    internal static Layer RedrawShape(Layer layer)
    {
        if (layer.Shape is not { } shape || layer.Asset is not { } asset) return layer;
        int w = Math.Max(1, (int)Math.Round(layer.Transform.Size.Width)), h = Math.Max(1, (int)Math.Round(layer.Transform.Size.Height));
        if ((w == asset.Width && h == asset.Height) || (long)w * h > DocumentLimits.MaxSurfacePixels
            || w > DocumentLimits.MaxSide || h > DocumentLimits.MaxSide) return layer;
        try { return layer with { Asset = shape.Render(w, h, asset.Name) }; }
        catch { return layer; }
    }
}
