using System.Collections.Immutable;
using Compositor.Core.Geometry;
using Compositor.Core.Localization;
using Compositor.Core.Model;
using Compositor.Core.Pixels;
using Compositor.Core.Rendering;
using SkiaSharp;

namespace Compositor.Core.Editing;

public sealed partial class EditorSession
{
    private LassoDraft? _lassoDraft;
    public LassoDraft? LassoDraft { get => _lassoDraft; private set { _lassoDraft = value; Invalidate(); } }

    /// <summary>The selection as it was when a drag-move began; the drag is one undo step.</summary>
    private DocumentSelection? _selectionMoveOrigin;

    public bool CanEditSelection => Document is not null && BrushStroke is null && TransformEdit is null && ColorRange is null;

    /// <summary>Shift adds, Alt (with or without Shift) subtracts; otherwise the options-bar mode.</summary>
    public SelectionMode SelectionModeFor(bool shift, bool alt) => alt ? SelectionMode.Subtract : shift ? SelectionMode.Add : SelectionModeChoice;

    public void BeginLasso(PointD point, SelectionMode mode)
    {
        if (!Tool.IsSelectionTool() || Tool == Tool.Wand || !CanEditSelection || _selectionMoveOrigin is not null) return;
        if (Tool == Tool.Marquee)
        {
            var anchor = point.Rounded();
            LassoDraft = new LassoDraft(ImmutableList.Create(anchor), null, mode, MarqueeKind, anchor);
        }
        else
        {
            LassoDraft = new LassoDraft(ImmutableList.Create(point), null, mode, LassoKind);
        }
    }

    /// <summary>Marquee drag, snapped to whole pixels. <paramref name="square"/> evens the sides; <paramref name="fromCenter"/> grows around the anchor.</summary>
    public void DragMarquee(PointD point, bool square, bool fromCenter)
    {
        if (LassoDraft is not { Anchor: { } anchor } draft || draft.Kind is not (LassoKind.Rectangle or LassoKind.Ellipse) || !point.IsFinite) return;
        var rect = DragBox(anchor, point, square, fromCenter);
        LassoDraft = draft with
        {
            Points = ImmutableList.Create(new PointD(rect.Left, rect.Top), new PointD(rect.Right, rect.Top),
                                          new PointD(rect.Right, rect.Bottom), new PointD(rect.Left, rect.Bottom)),
        };
    }

    public static RectD DragBox(PointD anchor, PointD point, bool square, bool fromCenter)
    {
        double dx = Math.Round(point.X, MidpointRounding.AwayFromZero) - anchor.X;
        double dy = Math.Round(point.Y, MidpointRounding.AwayFromZero) - anchor.Y;
        if (square)
        {
            double side = Math.Max(Math.Abs(dx), Math.Abs(dy));
            dx = dx < 0 ? -side : side;
            dy = dy < 0 ? -side : side;
        }
        return fromCenter
            ? new RectD(anchor.X - Math.Abs(dx), anchor.Y - Math.Abs(dy), Math.Abs(dx) * 2, Math.Abs(dy) * 2)
            : new RectD(Math.Min(anchor.X, anchor.X + dx), Math.Min(anchor.Y, anchor.Y + dy), Math.Abs(dx), Math.Abs(dy));
    }

    /// <summary>Adds an outline point; points closer than a quarter pixel are skipped.</summary>
    public void ExtendLasso(PointD point)
    {
        if (LassoDraft is not { } draft || !point.IsFinite) return;
        if (draft.Points.Count > 0 && draft.Points[^1].DistanceTo(point) < 0.25) return;
        LassoDraft = draft with { Points = draft.Points.Add(point) };
    }

    public void MoveLassoCursor(PointD? point)
    {
        if (LassoDraft is { } draft) LassoDraft = draft with { Cursor = point };
    }

    public void RemoveLastLassoPoint()
    {
        if (LassoDraft is not { } draft) return;
        var points = draft.Points.RemoveAt(draft.Points.Count - 1);
        LassoDraft = points.IsEmpty ? null : draft with { Points = points };
    }

    public void CancelLasso() { if (_lassoDraft is not null) LassoDraft = null; }

    /// <summary>Closes the outline and combines it with the selection. A click that encloses nothing deselects in New mode.</summary>
    public void FinishLasso()
    {
        if (LassoDraft is not { } draft) return;
        LassoDraft = null;
        var outline = new SKPath();
        if (draft.Kind == LassoKind.Ellipse && draft.Points.Count == 4)
        {
            var box = RectD.FromPoints(draft.Points);
            outline.AddOval(box.ToSK());
        }
        else
        {
            outline.MoveTo(draft.Points[0].ToSK());
            for (int i = 1; i < draft.Points.Count; i++) outline.LineTo(draft.Points[i].ToSK());
            outline.Close();
        }
        var bounds = outline.Bounds;
        if ((draft.Points.Count < 3 && draft.Kind != LassoKind.Ellipse) || bounds.Width <= 0 || bounds.Height <= 0)
        {
            if (draft.Mode == SelectionMode.Replace) Deselect();
            return;
        }
        string name = draft.Kind switch
        {
            LassoKind.Freehand => "Lasso",
            LassoKind.Polygonal => "Polygonal Lasso",
            LassoKind.Ellipse => "Elliptical Marquee",
            _ => "Rectangular Marquee",
        };
        ApplySelection(outline, draft.Mode, name);
    }

    public void ApplySelection(SKPath shape, SelectionMode mode, string name)
    {
        if (Document is not { } document || !CanEditSelection) return;
        using var canvas = DocumentSelection.CanvasPath(document.Width, document.Height);
        var clipped = DocumentSelection.Intersect(shape, canvas);
        SKPath result;
        switch (mode)
        {
            case SelectionMode.Add:
                result = Selection is { } current ? DocumentSelection.Union(current.Path, clipped) : clipped;
                break;
            case SelectionMode.Subtract:
                // Subtracting from no selection selects nothing new, so nothing changes.
                if (Selection is not { } existing) return;
                result = DocumentSelection.Subtract(existing.Path, clipped);
                break;
            default:
                result = clipped;
                break;
        }
        SetSelection(new DocumentSelection(result, SelectionAntialiased), name);
    }

    public void SetSelection(DocumentSelection? value, string name)
    {
        if (Document is not { } document || !CanEditSelection || ReferenceEquals(value, document.Selection)) return;
        BeginEdit(name);
        Document = document with { Selection = value };
        EndEdit();
    }

    /// <summary>True where dragging in New mode would move the selection outline.</summary>
    public bool CanMoveSelection(PointD point) =>
        Selection is { IsEmpty: false } selection && CanEditSelection && LassoDraft is null && selection.Contains(point);

    public bool BeginSelectionMove()
    {
        if (_selectionMoveOrigin is not null || Selection is not { IsEmpty: false } selection || !CanEditSelection) return false;
        BeginEdit("Move Selection");
        _selectionMoveOrigin = selection;
        return true;
    }

    /// <summary>Moves the outline only (never pixels), by whole pixels from where the drag started.</summary>
    public void MoveSelection(double dx, double dy)
    {
        if (_selectionMoveOrigin is not { } origin || Document is not { } document) return;
        Document = document with { Selection = origin.Offset(Math.Round(dx), Math.Round(dy)) };
    }

    public void EndSelectionMove()
    {
        if (_selectionMoveOrigin is null) return;
        _selectionMoveOrigin = null;
        EndEdit();
    }

    public DocumentSelection? SelectionMoveOrigin => _selectionMoveOrigin;

    public void NudgeSelection(double dx, double dy)
    {
        if (!BeginSelectionMove()) return;
        MoveSelection(dx, dy);
        EndSelectionMove();
    }

    public bool CanModifySelection => Selection is { IsEmpty: false } && CanEditSelection && LassoDraft is null;

    public void SelectAll()
    {
        if (Document is not { } document) return;
        SetSelection(DocumentSelection.Rectangle(document.Bounds), "Select All");
    }

    public void Deselect()
    {
        if (Selection is null) return;
        SetSelection(null, "Deselect");
    }

    public void InvertSelection()
    {
        if (Document is not { } document || Selection is not { } current) return;
        using var canvas = DocumentSelection.CanvasPath(document.Width, document.Height);
        var inverse = new DocumentSelection(DocumentSelection.Subtract(canvas, current.Path), current.Antialiased, current.Feather);
        // The inverse of everything is no selection at all, as in Photoshop.
        SetSelection(inverse.IsEmpty ? null : inverse, "Inverse");
    }

    /// <summary>Grows the outline by <paramref name="amount"/> pixels with rounded corners, clipped to the canvas.</summary>
    public void ExpandSelection(int amount) => ResizeSelection(amount, "Expand Selection");

    /// <summary>Shrinks the outline; contracting past the middle leaves an explicit empty selection.</summary>
    public void ContractSelection(int amount) => ResizeSelection(-amount, "Contract Selection");

    private void ResizeSelection(double delta, string name)
    {
        if (Document is not { } document || Selection is not { } current || !CanModifySelection || delta == 0 || Math.Abs(delta) > 500) return;
        using var band = DocumentSelection.StrokeBand(current.Path, Math.Abs(delta) * 2);
        SKPath result;
        if (delta > 0)
        {
            using var union = DocumentSelection.Union(current.Path, band);
            using var canvas = DocumentSelection.CanvasPath(document.Width, document.Height);
            result = DocumentSelection.Intersect(union, canvas);
        }
        else
        {
            result = DocumentSelection.Subtract(current.Path, band);
        }
        SetSelection(new DocumentSelection(result, current.Antialiased, current.Feather), name);
    }

    /// <summary>Softens the edge; applying it again softens further, as blurs add up.</summary>
    public void FeatherSelection(int amount)
    {
        if (!CanModifySelection || Selection is not { } current || amount <= 0) return;
        double softened = Math.Sqrt(current.Feather * current.Feather + amount * (double)amount);
        SetSelection(current.WithFeather(Math.Min(250, softened)), "Feather Selection");
    }

    // MARK: Selections from pixels

    /// <summary>
    /// What selection-from-image tools read, at document size: every visible layer as shown, or the active layer's own
    /// pixels (without its mask). Premultiplied RGBA, tightly packed.
    /// </summary>
    public byte[]? SelectionSample(bool sampleAllLayers)
    {
        if (Document is not { } document) return null;
        if ((long)document.Width * document.Height > DocumentLimits.MaxSurfacePixels) return null;
        var options = new RenderOptions();
        CanvasDocument source;
        if (sampleAllLayers)
        {
            source = document;
        }
        else if (ActiveLayer is { IsGroup: false, Asset: not null } layer)
        {
            source = document with
            {
                Layers = ImmutableList.Create(layer with { ParentId = null, Mask = null, Opacity = 1, BlendMode = BlendMode.Normal, IsVisible = true, MaskSourceId = null }),
            };
        }
        else return new byte[document.Width * document.Height * 4];
        using var image = DocumentRenderer.RenderToImage(source, options);
        using var pixmap = image.PeekPixels();
        var bytes = new byte[document.Width * document.Height * 4];
        var span = pixmap.GetPixelSpan();
        for (int y = 0; y < document.Height; y++)
            span.Slice(y * pixmap.RowBytes, document.Width * 4).CopyTo(bytes.AsSpan(y * document.Width * 4));
        return bytes;
    }

    /// <summary>The Magic Wand: pixels similar to the one at <paramref name="point"/>, combined with the selection by <paramref name="mode"/>.</summary>
    public void MagicWandAt(PointD point, SelectionMode mode)
    {
        if (!CanEditSelection || Document is not { } document) return;
        int x = (int)Math.Floor(point.X), y = (int)Math.Floor(point.Y);
        if (x < 0 || y < 0 || x >= document.Width || y >= document.Height) return;
        var sample = SelectionSample(WandSettings.SampleAllLayers);
        if (sample is null) return;
        SKPath? path;
        try { path = MagicWand.Select(sample, document.Width, document.Height, document.Width * 4, x, y, WandSettings); }
        catch (WandException error) { ShowError(L.T("魔棒", "Magic Wand"), error.Message); return; }
        if (path is null)
        {
            if (mode == SelectionMode.Replace) Deselect();
            return;
        }
        // A traced outline already lies on the canvas, so a new selection skips the clip.
        if (mode == SelectionMode.Replace) SetSelection(new DocumentSelection(path, SelectionAntialiased), "Magic Wand");
        else ApplySelection(path, mode, "Magic Wand");
    }

    /// <summary>Ctrl-click a layer thumbnail: its pixels' alpha becomes the selection (traced at document resolution).</summary>
    public void LoadLayerSelection(Guid layerId, SelectionMode mode = SelectionMode.Replace)
    {
        if (Document is not { } document || document.Find(layerId) is not { Asset: not null } layer) return;
        if ((long)document.Width * document.Height > DocumentLimits.MaxSurfacePixels) return;
        var single = document with
        {
            Layers = ImmutableList.Create(layer with { ParentId = null, Mask = null, Opacity = 1, BlendMode = BlendMode.Normal, IsVisible = true, MaskSourceId = null }),
        };
        using var image = DocumentRenderer.RenderToImage(single);
        LoadCoverageSelection(image, alpha: true, mode);
    }

    /// <summary>The black areas of a mask (Select › Mask's Black Areas) — or with <paramref name="white"/>, its white.</summary>
    public void LoadMaskSelection(Guid layerId, bool white = false)
    {
        if (Document is not { } document || document.Find(layerId) is not { Mask: { } mask } layer) return;
        var single = document with
        {
            Layers = ImmutableList.Create(Layer.Blank("mask", document.Size) with
            {
                Asset = MaskAsColor(mask.Asset, invert: !white),
                Transform = mask.Placement ?? layer.Transform,
            }),
        };
        using var image = DocumentRenderer.RenderToImage(single);
        LoadCoverageSelection(image, alpha: true, SelectionMode.Replace);
    }

    private static RasterImage MaskAsColor(RasterImage mask, bool invert)
    {
        var coverage = mask.CopyPixels();
        var rgba = new byte[coverage.Length * 4];
        for (int i = 0; i < coverage.Length; i++)
        {
            byte a = invert ? (byte)(255 - coverage[i]) : coverage[i];
            rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = a;
            rgba[i * 4 + 3] = a;
        }
        return RasterImage.FromRgba(rgba, mask.Width, mask.Height);
    }

    /// <summary>
    /// A selection from coverage: pixels at least half covered are traced along their edges, and partial coverage
    /// becomes a feather-free outline (soft coverage is approximated at the 50% level, as Photoshop's marching ants show).
    /// </summary>
    private void LoadCoverageSelection(SKImage image, bool alpha, SelectionMode mode)
    {
        if (Document is not { } document) return;
        using var pixmap = image.PeekPixels();
        var span = pixmap.GetPixelSpan();
        var mask = new byte[document.Width * document.Height];
        for (int y = 0; y < document.Height; y++)
            for (int x = 0; x < document.Width; x++)
                if (span[y * pixmap.RowBytes + x * 4 + 3] >= 128) mask[y * document.Width + x] = 255;
        SKPath? path;
        try { path = MagicWand.Outline(mask, document.Width, document.Height); }
        catch (WandException error) { ShowError(L.T("载入选区", "Load Selection"), error.Message); return; }
        if (path is null)
        {
            if (mode == SelectionMode.Replace) SetSelection(new DocumentSelection(new SKPath()), "Load Selection");
            return;
        }
        if (mode == SelectionMode.Replace) SetSelection(new DocumentSelection(path, SelectionAntialiased), "Load Selection");
        else ApplySelection(path, mode, "Load Selection");
    }
}
