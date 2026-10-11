using Compositor.Core.Geometry;
using Compositor.Core.Localization;
using Compositor.Core.Model;
using Compositor.Core.Pixels;
using Compositor.Core.Segmentation;
using SkiaSharp;

namespace Compositor.Core.Editing;

/// <summary>The Magic Wand tool's two ways of working: similar colors, or the object under the click.</summary>
public enum WandMode { Magic, Object }

/// <summary>Object Selection's options. Mirrors ObjectSelectionSettings.</summary>
public sealed record ObjectSelectionSettings(bool SampleAllLayers = true, int EdgeOffset = 0);

/// <summary>
/// The model's answer for a layer, worked out once while Remove Background's panel is open (the preview's first render,
/// or OK), so moving a slider only redoes the refining.
/// </summary>
internal sealed class SubjectAnswer
{
    private readonly object _lock = new();
    private float[]? _answer;

    public float[] Get(ISubjectModel model, ReadOnlySpan<byte> rgba, int width, int height)
    {
        lock (_lock)
        {
            return _answer ??= SubjectMask.Infer(model, rgba, width, height);
        }
    }
}

public sealed partial class EditorSession
{
    /// <summary>
    /// Where Select Subject, Object Selection and Remove Background get their model. The app loads it (or offers to
    /// download it) the first time one of them is used; null when there is none.
    /// </summary>
    public Func<ISubjectModel?>? SubjectModelSource { get; set; }

    private WandMode _wandMode;
    public WandMode WandMode { get => _wandMode; set => SetField(ref _wandMode, value); }

    private ObjectSelectionSettings _objectSelectionSettings = new();
    public ObjectSelectionSettings ObjectSelectionSettings
    {
        get => _objectSelectionSettings;
        set => SetField(ref _objectSelectionSettings, value with { EdgeOffset = Math.Clamp(value.EdgeOffset, -10, 10) });
    }

    private bool _subjectBusy;
    /// <summary>A model is looking at the picture (Select Subject, Object Selection).</summary>
    public bool SubjectBusy { get => _subjectBusy; private set => SetField(ref _subjectBusy, value); }

    public bool CanSelectSubject => CanEditSelection && Document is not null && !SubjectBusy && LassoDraft is null && AdjustmentEdit is null;

    /// <summary>Select ▸ Subject: the foreground of the canvas as shown, outlined as a selection.</summary>
    public async Task SelectSubjectAsync(SelectionMode mode = SelectionMode.Replace)
    {
        if (!CanSelectSubject || Document is not { } document || SelectionSample(sampleAllLayers: true) is not { } sample) return;
        if (SubjectModelSource?.Invoke() is not { } model) return;
        int width = document.Width, height = document.Height;
        SubjectBusy = true;
        try
        {
            var outline = await Task.Run(() =>
            {
                var answer = SubjectMask.Infer(model, sample, width, height);
                var mask = SubjectMask.Threshold(SubjectMask.Resize(answer, model.Spec.Side, width, height));
                return MagicWand.Outline(mask, width, height);
            });
            SubjectBusy = false;
            if (Document?.Id != document.Id) { outline?.Dispose(); return; }
            if (outline is null) { ShowError(L.T("选择主体", "Select Subject"), L.T("没有在画面中找到主体。请换一张主体更分明的图像试试。", "No subject was detected in the image. Try an image with a more distinct subject.")); return; }
            if (mode == SelectionMode.Replace) SetSelection(new DocumentSelection(outline, SelectionAntialiased), "Select Subject");
            else ApplySelection(outline, mode, "Select Subject");
        }
        catch (Exception error) { ShowError(L.T("无法选择主体", "Couldn’t select the subject"), error.Message); }
        finally { SubjectBusy = false; }
    }

    /// <summary>
    /// Object Selection (the Magic Wand in Object mode): the object under <paramref name="point"/>, combined with the
    /// selection by <paramref name="mode"/>. A small object the whole picture's subject leaves out is looked for again in
    /// a window around the point.
    /// </summary>
    public async Task SelectObjectAsync(PointD point, SelectionMode mode)
    {
        if (!CanSelectSubject || Document is not { } document) return;
        int width = document.Width, height = document.Height;
        int x = (int)Math.Floor(point.X), y = (int)Math.Floor(point.Y);
        if (x < 0 || y < 0 || x >= width || y >= height) return;
        if (SelectionSample(ObjectSelectionSettings.SampleAllLayers) is not { } sample) return;
        if (SubjectModelSource?.Invoke() is not { } model) return;
        int offset = ObjectSelectionSettings.EdgeOffset;
        SubjectBusy = true;
        try
        {
            var outline = await Task.Run(() =>
            {
                var region = ObjectRegion(model, sample, width, height, x, y);
                if (region is null) return null;
                return MagicWand.Outline(SubjectMask.Offset(region, width, height, offset), width, height);
            });
            SubjectBusy = false;
            if (Document?.Id != document.Id) { outline?.Dispose(); return; }
            if (outline is null)
            {
                if (mode == SelectionMode.Replace) Deselect();
                return;
            }
            if (mode == SelectionMode.Replace) SetSelection(new DocumentSelection(outline, SelectionAntialiased), "Object Selection");
            else ApplySelection(outline, mode, "Object Selection");
        }
        catch (Exception error) { ShowError(L.T("无法选择对象", "Couldn’t select the object"), error.Message); }
        finally { SubjectBusy = false; }
    }

    /// <summary>The object (0/255) at a pixel: the subject's part there, or the subject of a window around it.</summary>
    public static byte[]? ObjectRegion(ISubjectModel model, byte[] rgba, int width, int height, int x, int y)
    {
        int side = model.Spec.Side;
        var whole = SubjectMask.Threshold(SubjectMask.Resize(SubjectMask.Infer(model, rgba, width, height), side, width, height));
        if (SubjectMask.RegionAt(whole, width, height, x, y) is { } region) return region;
        // A window a third of the picture across, centered on the point where it fits.
        int span = Math.Max(64, Math.Max(width, height) / 3);
        int cw = Math.Min(width, span), ch = Math.Min(height, span);
        int left = Math.Clamp(x - cw / 2, 0, width - cw), top = Math.Clamp(y - ch / 2, 0, height - ch);
        if (cw == width && ch == height) return null;
        var crop = new byte[cw * ch * 4];
        for (int row = 0; row < ch; row++)
            rgba.AsSpan(((top + row) * width + left) * 4, cw * 4).CopyTo(crop.AsSpan(row * cw * 4));
        var local = SubjectMask.Threshold(SubjectMask.Resize(SubjectMask.Infer(model, crop, cw, ch), side, cw, ch));
        if (SubjectMask.RegionAt(local, cw, ch, x - left, y - top) is not { } part) return null;
        var result = new byte[width * height];
        for (int row = 0; row < ch; row++) part.AsSpan(row * cw, cw).CopyTo(result.AsSpan((top + row) * width + left));
        return result;
    }

    // MARK: Remove Background

    /// <summary>
    /// Remove Background on OK: a layer mask, white over the subject and black over the background, so the background
    /// can be brought back at any time. A mask already on the layer's grid keeps hiding what it hid; with a selection,
    /// only the selected part of the mask changes. Mirrors commitBackgroundMask in Filters.swift.
    /// </summary>
    private void ApplyBackgroundMask(AdjustmentEdit edit)
    {
        if (Document is not { } document || document.Find(edit.LayerId) is not { } layer || edit.SubjectModel is not { } model
            || !SourceStillThere(edit, layer) || layer.Transform != edit.Transform || edit.Source is not { } source) return;
        int width = source.Width, height = source.Height;
        var rgba = source.CopyPixels();
        var answer = edit.Subject.Get(model, rgba, width, height);
        var mask = SubjectMask.Mask(answer, model.Spec.Side, rgba, width, height, edit.FilterSettings.RemoveBackground, limit: 4096);
        var existing = layer.Mask is { Placement: null } owned && owned.Asset.Width == width && owned.Asset.Height == height
            ? owned.Asset.CopyPixels() : null;
        if (existing is not null)
            for (int i = 0; i < mask.Length; i++) mask[i] = (byte)(mask[i] * existing[i] / 255);
        if (edit.Selection is { } clip)
        {
            // Only the selected part changes; elsewhere the mask stays as it was (or shows everything).
            var coverage = GridCoverage(clip, edit.Transform.PixelToDocument(width, height), width, height);
            for (int i = 0; i < mask.Length; i++)
            {
                int under = existing?[i] ?? 255;
                mask[i] = (byte)((mask[i] * coverage[i] + under * (255 - coverage[i]) + 127) / 255);
            }
        }
        var asset = RasterImage.FromMask(mask, width, height);
        BeginEdit("Remove Background");
        Document = document.UpdateLayer(layer.Id, l => l with
        {
            Mask = l.Mask is { } current ? current with { Asset = asset, Placement = null, IsEnabled = true } : new LayerMask(asset),
        });
        IsMaskSelected = true;
        EndEdit();
    }

    /// <summary>Remove Background's preview: the layer's pixels with the background made clear by the mask OK lays down.</summary>
    internal static Action<SKBitmap> RemoveBackgroundOperation(AdjustmentEdit edit, RemoveBackgroundSettings settings)
    {
        var model = edit.SubjectModel;
        var holder = edit.Subject;
        return bitmap =>
        {
            if (model is null) return;
            int width = bitmap.Width, height = bitmap.Height;
            var pixels = bitmap.GetPixelSpan();
            var rgba = pixels.ToArray();
            var answer = holder.Get(model, rgba, width, height);
            var mask = SubjectMask.Mask(answer, model.Spec.Side, rgba, width, height, settings, limit: 1400);
            for (int i = 0; i < mask.Length; i++)
            {
                int m = mask[i];
                if (m == 255) continue;
                int at = i * 4;
                pixels[at] = (byte)(pixels[at] * m / 255);
                pixels[at + 1] = (byte)(pixels[at + 1] * m / 255);
                pixels[at + 2] = (byte)(pixels[at + 2] * m / 255);
                pixels[at + 3] = (byte)(pixels[at + 3] * m / 255);
            }
            bitmap.NotifyPixelsChanged();
        };
    }
}
