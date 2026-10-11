using System.Collections.Immutable;
using Compositor.Core.Geometry;
using Compositor.Core.Localization;
using Compositor.Core.Model;
using Compositor.Core.Painting;
using Compositor.Core.Rendering;
using SkiaSharp;

namespace Compositor.Core.Editing;

public sealed partial class EditorSession
{
    private BrushStroke? _brushStroke;
    /// <summary>The stroke being painted; previews draw it in place of the layer's pixels or mask.</summary>
    public BrushStroke? BrushStroke { get => _brushStroke; private set { _brushStroke = value; Invalidate(); } }

    /// <summary>Where the brush is while Smoothing trails it behind the pointer.</summary>
    private PointD? _brushAnchor;
    private PointD? _brushPointer;
    /// <summary>Where the last stroke ended, so a Shift-click paints a straight line on from it.</summary>
    private (PointD Point, Guid LayerId, bool Mask)? _lastBrushPoint;

    /// <summary>An explicitly empty selection leaves nothing paintable, so painting never starts.</summary>
    public bool CanPaint =>
        CanEditLayers && SelectedLayerIds.Count == 1 && ActiveLayer is { } layer
        && (!layer.IsGroup || IsMaskSelected) && Selection?.IsEmpty != true
        && Document!.EffectiveVisibleIds.Contains(layer.Id)
        && (!IsMaskSelected || layer.Mask?.IsEnabled == true)
        && (IsMaskSelected || !layer.IsAdjustment);

    /// <summary>Why a stroke can't start on the target, as Photoshop explains a brush it refuses.</summary>
    public string? PaintRefusal
    {
        get
        {
            if (!CanEditLayers || ActiveLayer is not { } layer || CanPaint) return null;
            if (SelectedLayerIds.Count > 1) return L.T("选中了多个图层。只选择一个图层才能在上面绘画。", "Several layers are selected. Select just one to paint on it.");
            if (layer.IsGroup && !IsMaskSelected) return L.T($"“{layer.Name}”是文件夹，没有自己的像素。请在其中的图层上绘画，或者在文件夹的蒙版上绘画。",
                $"“{layer.Name}” is a folder, which has no pixels of its own. Paint on a layer inside it, or on the folder’s mask.");
            if (!Document!.EffectiveVisibleIds.Contains(layer.Id)) return L.T($"“{layer.Name}”是隐藏的，或者在隐藏的文件夹里。显示它才能在上面绘画。",
                $"“{layer.Name}” is hidden, or inside a hidden folder. Show it to paint on it.");
            if (IsMaskSelected && layer.Mask?.IsEnabled != true) return L.T("图层蒙版已停用。按住 Shift 单击蒙版缩略图启用它，然后再绘画。",
                "The layer mask is turned off. Shift-click its thumbnail to turn it on, then paint.");
            if (!IsMaskSelected && layer.IsAdjustment) return L.T($"“{layer.Name}”是调整图层，没有可绘画的像素。请在它的蒙版上绘画。",
                $"“{layer.Name}” is an adjustment layer, with no pixels to paint. Paint on its mask instead.");
            if (Selection?.IsEmpty == true) return L.T("没有选中任何区域，所以无处可画。选择“选择 › 取消选择”(Ctrl+D) 后可以在任意位置绘画。",
                "Nothing is selected, so there’s nowhere to paint. Choose Select ▸ Deselect (Ctrl+D) to paint anywhere.");
            return null;
        }
    }

    private RasterEdit MakeRasterEdit(Layer layer, Func<RasterEdit> make)
    {
        var edit = make();
        if (Document is { } document)
        {
            long used = document.Layers.Where(l => l.Id != layer.Id)
                .Sum(l => (IsMaskSelected ? l.Mask?.Asset : l.Asset)?.PixelCount ?? 0);
            edit.PixelLimit = Math.Max(1, DocumentLimits.DocumentPixelBudget - used);
            edit.Selection = Selection?.Clip(document.Width, document.Height);
        }
        return edit;
    }

    /// <summary>The mask is painted in gray: the foreground color's lightness, as Photoshop shows it.</summary>
    private PaletteColor PaintColor => IsMaskSelected
        ? new PaletteColor(ForegroundColor.Luminance, ForegroundColor.Luminance, ForegroundColor.Luminance)
        : ForegroundColor;

    public void BeginBrush(PointD point, bool straightLine = false)
    {
        if (!Tool.IsBrushTool() || BrushStroke is not null || WarpStroke is not null) return;
        if (!CanPaint || ActiveLayer is not { } layer || Document is not { } document)
        {
            if (PaintRefusal is { } refusal) ShowError(L.T("无法绘画", "Couldn’t paint"), refusal);
            return;
        }
        if (RetouchRefusal(Tool) is { } notHere) { ShowError(L.T("无法修饰", "Couldn’t retouch"), notHere); return; }
        if (Tool == Tool.CloneStamp && CloneSource is null)
        {
            ShowError(L.T("仿制图章", "Clone Stamp"), L.T("先按住 Alt 单击图像，设置要仿制的源点。", "Alt-click where Clone Stamp should copy from first."));
            return;
        }
        if (Tool == Tool.Smear && SmearMode != SmearMode.Blur) { BeginWarp(point); return; }
        FinishOpacityEdit();
        try
        {
            bool painting = Tool == Tool.Brush;
            var settings = BrushSettings with
            {
                Color = PaintColor,
                Erasing = painting && BrushMode == BrushToolMode.Erase && !IsMaskSelected,
            };
            if (painting && BrushMode == BrushToolMode.Erase && IsMaskSelected) settings = settings with { Color = PaletteColor.Black };
            var stroke = (BrushStroke)MakeRasterEdit(layer, () => new BrushStroke(layer, IsMaskSelected, settings, document.Width, document.Height));
            switch (Tool)
            {
                case Tool.SpotHealing:
                    stroke.Healing = true;
                    stroke.EditName = "Spot Healing";
                    break;
                case Tool.Remove:
                    stroke.Removing = true;
                    stroke.EditName = "Remove Tool";
                    break;
                case Tool.CloneStamp:
                {
                    var offset = CloneStrokeOffset(point)!.Value;
                    if (CloneSettings.Aligned) CloneOffset = offset;
                    stroke.Source = CloneSourceFor(stroke, offset);
                    stroke.EditName = "Clone Stamp";
                    if (stroke.Source is null) { stroke.Release(); return; }
                    break;
                }
                case Tool.Smear:
                    stroke.Source = BlurSourceFor(stroke);
                    stroke.EditName = "Blur";
                    if (stroke.Source is null) { stroke.Release(); ShowError(L.T("无法模糊", "Couldn’t blur"), L.T("图层太大。", "The layer is too large.")); return; }
                    break;
            }
            BrushStroke = stroke;
            if (straightLine && ShiftLineStart() is { } start)
            {
                stroke.Append(start);
                stroke.Append(point);
            }
            else stroke.Append(point);
            _brushAnchor = point;
            _brushPointer = point;
            _lastBrushPoint = (point, layer.Id, IsMaskSelected);
            Invalidate();
        }
        catch (Exception error)
        {
            CancelBrush();
            ShowError(L.T("无法绘画", "Couldn’t paint"), error.Message);
        }
    }

    public void ContinueBrush(PointD point)
    {
        if (WarpStroke is { } warp)
        {
            warp.Append(point);
            Invalidate();
            return;
        }
        if (BrushStroke is not { } stroke) return;
        _brushPointer = point;
        if (Smoothed(point) is not { } painted) return;
        try
        {
            stroke.Append(painted);
            _lastBrushPoint = (painted, stroke.Layer.Id, stroke.IsMask);
            Invalidate();
        }
        catch (Exception error)
        {
            CancelBrush();
            ShowError(L.T("无法绘画", "Couldn’t paint"), error.Message);
        }
    }

    /// <summary>
    /// Smoothing: the brush trails the pointer on a string and only moves once the pointer pulls it taut. The string's
    /// length is in screen points, so it feels the same at any zoom.
    /// </summary>
    private PointD? Smoothed(PointD point)
    {
        if (BrushSettings.Smoothing <= 0 || _brushAnchor is not { } anchor) return point;
        double radius = BrushSettings.Smoothing / Math.Max(0.01, Viewport.Zoom);
        double dx = point.X - anchor.X, dy = point.Y - anchor.Y;
        double distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance <= radius) return null;
        double step = (distance - radius) / distance;
        var moved = new PointD(anchor.X + dx * step, anchor.Y + dy * step);
        _brushAnchor = moved;
        return moved;
    }

    public PointD? ShiftLineStart() =>
        _lastBrushPoint is { } last && last.LayerId == ActiveLayerId && last.Mask == IsMaskSelected ? last.Point : null;

    public void CancelBrush()
    {
        if (_warpStroke is not null) CancelWarp();
        _brushStroke?.Release();
        BrushStroke = null;
        _brushAnchor = null;
        _brushPointer = null;
    }

    public void FinishBrush()
    {
        if (WarpStroke is not null) { FinishWarp(); return; }
        if (BrushStroke is not { } stroke) return;
        if (stroke.Removing)
        {
            // Already being filled away: the pointer's release after it changes nothing.
            if (RemoveBusy) return;
            if (_brushPointer is { } at && _brushAnchor is { } from && at != from && BrushSettings.Smoothing > 0) stroke.Append(at);
            stroke.Flush();
            _brushAnchor = null;
            _brushPointer = null;
            // The stroke stays up (its pink wash) until the fill replaces it.
            _ = RemoveAsync(stroke);
            return;
        }
        try
        {
            if (_brushPointer is { } pointer && _brushAnchor is { } anchor && pointer != anchor && BrushSettings.Smoothing > 0)
                stroke.Append(pointer);
            stroke.Flush();
            if (stroke.Healing) stroke.Heal(SpotHealingMode, (uint)Random.Shared.Next());
            if (stroke.HasPainted)
                CommitRasterEdit(stroke, stroke.EditName ?? (stroke.IsMask ? "Paint Mask" : stroke.Settings.Erasing ? "Erase" : "Brush Stroke"));
        }
        catch (Exception error)
        {
            ShowError(L.T("无法绘画", "Couldn’t paint"), error.Message);
        }
        finally
        {
            CancelBrush();
        }
    }

    /// <summary>Installs a raster edit's result as one undo step, if the layer is still as the edit found it.</summary>
    public void CommitRasterEdit(RasterEdit edit, string name, DocumentSelection? keepSelection = null, bool clearSelection = false)
    {
        if (Document is not { } document || document.Find(edit.Layer.Id) is not { } current) return;
        if (!ReferenceEquals(current.Asset, edit.Layer.Asset) || current.Transform != edit.Layer.Transform
            || !ReferenceEquals(current.Mask?.Asset, edit.Layer.Mask?.Asset)) return;
        var result = edit.Commit();
        if (!result.Transform.IsValid) { ShowError(L.T("无法完成编辑", "Couldn’t finish the edit"), L.T("结果超出了支持的范围。", "The result is outside the supported range.")); return; }
        Layer updated;
        if (edit.IsMask)
        {
            var mask = current.Mask is { } owned
                ? owned.Replacing(result.Asset) with
                {
                    Placement = owned.Placement is not null || result.Bounds != edit.SourceRect ? result.Transform : null,
                }
                : new LayerMask(result.Asset);
            updated = current with { Mask = mask };
        }
        else
        {
            var mask = current.Mask;
            if (mask is { Placement: null } && result.Bounds != edit.SourceRect && current.Asset is not null)
                mask = ExpandMask(mask, edit.SourceRect, result.Bounds);
            // Painting rasterizes live text and shapes: their pixels are what's kept.
            updated = current with { Asset = result.Asset, Transform = result.Transform, Mask = mask, Text = null, Shape = null };
        }
        BeginEdit(name);
        var next = document.ReplaceLayer(updated);
        if (clearSelection) next = next with { Selection = null };
        Document = next;
        EndEdit();
    }

    /// <summary>Brush size steps of a fifth, but at least one pixel ([ and ]).</summary>
    public void ChangeBrushSize(bool increase)
    {
        if (BrushStroke is not null) return;
        double current = BrushSettings.Diameter;
        double stepped = increase ? Math.Max(current + 1, Math.Round(current * 1.2)) : Math.Min(current - 1, Math.Round(current / 1.2));
        BrushSettings = BrushSettings with { Diameter = Math.Clamp(stepped, 1, 2000) };
    }

    /// <summary>Shift+[ / Shift+]: hardness in Photoshop's 25% steps.</summary>
    public void ChangeBrushHardness(bool increase)
    {
        if (BrushStroke is not null) return;
        double quarter = BrushSettings.Hardness * 4;
        double step = increase ? Math.Floor(quarter + 0.001) + 1 : Math.Ceiling(quarter - 0.001) - 1;
        BrushSettings = BrushSettings with { Hardness = Math.Clamp(step, 0, 4) / 4 };
    }

    private (int Digit, DateTime Time)? _pendingOpacityDigit;

    /// <summary>Photoshop's number keys: 1 = 10% … 0 = 100%; two digits typed quickly set an exact value.</summary>
    public void TypeOpacityDigit(int digit)
    {
        if (BrushStroke is not null || digit < 0 || digit > 9) return;
        int percent = digit == 0 ? 100 : digit * 10;
        var now = DateTime.UtcNow;
        if (_pendingOpacityDigit is { } pending && (now - pending.Time).TotalSeconds < 0.6)
        {
            percent = Math.Max(1, pending.Digit * 10 + digit);
            _pendingOpacityDigit = null;
        }
        else _pendingOpacityDigit = (digit, now);
        double value = percent / 100.0;
        if (Tool == Tool.Brush) BrushSettings = BrushSettings with { Opacity = value };
        else if (Tool == Tool.Move) SetLayerOpacity(value);
    }

    // MARK: Fills

    public enum FillSource { Foreground, Background }

    /// <summary>Alt+Backspace / Ctrl+Backspace: fills the selection (or the whole layer) with a palette color.</summary>
    public void FillSelection(FillSource source)
    {
        CommitTransform();
        if (!CanEditPixels || ActiveLayer is not { } layer || Document is not { } document) return;
        if (Selection?.IsEmpty == true) return;
        var color = source == FillSource.Foreground ? ForegroundColor : BackgroundColor;
        // On text, with nothing selected, Fill recolors the letters and keeps them editable.
        if (!IsMaskSelected && Selection is null && layer.Text is not null && RecolorText(layer.Id, color)) return;
        if (IsMaskSelected) color = new PaletteColor(color.Luminance, color.Luminance, color.Luminance);
        try
        {
            var edit = MakeRasterEdit(layer, () => new RasterEdit(layer, IsMaskSelected, document.Width, document.Height, grows: !IsMaskSelected));
            if (IsMaskSelected)
            {
                // A mask is coverage: blended toward the gray directly, never composited source-over.
                edit.ClearPixels(color);
            }
            else
            {
                edit.PaintDocument(document.Bounds, canvas =>
                {
                    using var paint = new SKPaint { Color = color.ToSK(), BlendMode = SKBlendMode.Src };
                    canvas.DrawRect(new SKRect(0, 0, document.Width, document.Height), paint);
                });
            }
            CommitRasterEdit(edit, "Fill");
            edit.Release();
        }
        catch (Exception error) { ShowError(L.T("无法填充", "Couldn’t fill"), error.Message); }
    }

    /// <summary>Delete/Backspace with a selection: erases the selected pixels (a mask's go black).</summary>
    public void ClearSelectedPixels()
    {
        CommitTransform();
        if (!CanEditPixels || ActiveLayer is not { } layer || Document is not { } document || Selection is not { IsEmpty: false }) return;
        try
        {
            var edit = MakeRasterEdit(layer, () => new RasterEdit(layer, IsMaskSelected, document.Width, document.Height, grows: false));
            edit.ClearPixels(IsMaskSelected ? PaletteColor.Black : null);
            CommitRasterEdit(edit, "Clear Pixels");
            edit.Release();
        }
        catch (Exception error) { ShowError(L.T("无法清除", "Couldn’t clear"), error.Message); }
    }

    public bool CanInvert => CanEditPixels && (IsMaskSelected ? ActiveLayer?.Mask is not null : ActiveLayer?.Asset is not null);

    /// <summary>Ctrl+I: inverts the layer's colors (or its mask), within the selection when there is one.</summary>
    public void InvertPixels()
    {
        CommitTransform();
        if (!CanInvert || ActiveLayer is not { } layer || Document is not { } document || Selection?.IsEmpty == true) return;
        var clip = Selection?.Clip(document.Width, document.Height);
        if (IsMaskSelected && layer.Mask is { } mask)
        {
            var source = mask.Asset;
            var map = (mask.Placement ?? layer.Transform).PixelToDocument(source.Width, source.Height);
            var inverted = PixelOps.Invert(source, clip, clip is null ? null : map);
            BeginEdit("Invert Mask");
            Document = document.UpdateLayer(layer.Id, l => l with { Mask = mask.Replacing(inverted) });
            EndEdit();
        }
        else if (layer.Asset is { } asset)
        {
            var map = layer.Transform.PixelToDocument(asset.Width, asset.Height);
            var inverted = PixelOps.Invert(asset, clip, clip is null ? null : map);
            BeginEdit("Invert");
            Document = document.UpdateLayer(layer.Id, l => l with { Asset = inverted, Text = null, Shape = null });
            EndEdit();
        }
    }

    // MARK: Eyedropper

    /// <summary>The composite's color at a document point (unpremultiplied), or null outside the canvas or where clear.</summary>
    public PaletteColor? SampleColor(PointD point, bool allLayers = true) =>
        SampleComposite(point, !allLayers && ActiveLayerId is { } active ? l => l.Id == active : null);

    private PaletteColor? SampleComposite(PointD point, Func<Layer, bool>? include)
    {
        if (Document is not { } document) return null;
        int x = (int)Math.Floor(point.X), y = (int)Math.Floor(point.Y);
        if (x < 0 || y < 0 || x >= document.Width || y >= document.Height) return null;
        using var surface = SKSurface.Create(RasterImage.ColorInfo(1, 1));
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.Translate(-x, -y);
        var options = new RenderOptions { Include = include };
        DocumentRenderer.Render(surface.Canvas, document, options);
        surface.Canvas.Flush();
        var bytes = new byte[4];
        unsafe
        {
            fixed (byte* p = bytes) surface.ReadPixels(RasterImage.ColorInfo(1, 1), (IntPtr)p, 4, 0, 0);
        }
        if (bytes[3] == 0) return null;
        var (r, g, b, _) = PixelOps.Unpremultiply(bytes[0], bytes[1], bytes[2], bytes[3]);
        return PaletteColor.FromBytes(r, g, b);
    }
}
