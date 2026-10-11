using Compositor.Core.Adjustments;
using Compositor.Core.Filters;
using Compositor.Core.Geometry;
using Compositor.Core.Localization;
using Compositor.Core.Model;
using Compositor.Core.Rendering;
using SkiaSharp;

namespace Compositor.Core.Editing;

/// <summary>
/// An Image-menu adjustment or filter being set in its panel (previewed on the canvas until OK), or an adjustment
/// layer's settings being edited (applied live). Mirrors LevelsEdit, HueSaturationEdit, FilterEdit and
/// AdjustmentEditing.swift, in one place.
/// </summary>
public sealed class AdjustmentEdit
{
    internal AdjustmentEdit(Guid layerId, LayerAdjustment settings, bool editsLayer)
    {
        LayerId = layerId;
        Settings = settings;
        EditsLayer = editsLayer;
    }

    /// <summary>A Filter-menu effect on a layer's pixels.</summary>
    internal AdjustmentEdit(Guid layerId, Filters.FilterKind filter, Filters.FilterSettings settings)
        : this(layerId, new LayerAdjustment(AdjustmentKind.Invert), editsLayer: false)
    {
        Filter = filter;
        FilterSettings = settings;
    }

    /// <summary>Set for Filter-menu effects (vignette, dither…); the adjustment kinds use <see cref="Settings"/>.</summary>
    public Filters.FilterKind? Filter { get; }
    public Filters.FilterSettings FilterSettings { get; internal set; } = new();
    public string Title => Filter is { } f ? Filters.FilterKinds.ToDisplayName(f) : Kind.ToDisplayName();

    /// <summary>Vignette on an empty layer: the layer had no pixels; the vignette frames and fills the canvas.</summary>
    internal bool StartedEmpty;
    public bool StartedEmptyLayer => StartedEmpty;
    internal SizeD CanvasSize;
    /// <summary>Content-Aware Fill: room to reach the part of the selection past the layer's edge.</summary>
    internal int FillMargin;
    /// <summary>Changes nothing as set: OK closes as Cancel does.</summary>
    internal bool DoesNothing => Filter is { } f ? FilterSettings.DoesNothing(f) : Settings.IsIdentity;
    internal int NeededMargin() =>
        Math.Max(FillMargin, (int)Math.Ceiling(Filter is { } f ? FilterSettings.Margin(f) : Settings.SamplingMargin));
    internal double PreviewLimit => Filter switch
    {
        { } f when Filters.FilterKinds.PreviewsFullSize(f) => double.MaxValue,
        Filters.FilterKind.Vignette => 8000,
        { } => 2048,
        _ => Kind switch
        {
            AdjustmentKind.GaussianBlur or AdjustmentKind.MotionBlur => 2048,
            AdjustmentKind.Grain or AdjustmentKind.AddNoise => double.MaxValue,
            _ => 8000,
        },
    };

    /// <summary>
    /// What the edit does to a bitmap of the layer's pixels made <paramref name="scale"/> times their size, placed on the
    /// document by <paramref name="bitmapToDocument"/>; <paramref name="coverage"/> is the selection on that bitmap.
    /// </summary>
    internal Action<SKBitmap> Operation(double scale, byte[]? coverage, Affine bitmapToDocument, bool preview = false)
    {
        if (Filter is { } filter)
        {
            var settings = FilterSettings.Normalized;
            if (filter == Filters.FilterKind.RemoveBackground) return EditorSession.RemoveBackgroundOperation(this, settings.RemoveBackground);
            if (preview && filter == Filters.FilterKind.CameraRaw)
            {
                // The panel's view: sections switched off, the clipping or mask views, a point color's range.
                var view = RawView;
                var raw = settings.CameraRaw.Showing(view.Hidden);
                return bitmap =>
                {
                    CameraRaw.CameraRawProcessor.Apply(bitmap, raw, scale, 0, view.Clipping, view.SharpenMask, null, view.VisualizePoint);
                    bitmap.NotifyPixelsChanged();
                };
            }
            SKRect? frame = null;
            if (StartedEmpty && bitmapToDocument.IsInvertible)
                frame = bitmapToDocument.Invert().Apply(new RectD(0, 0, CanvasSize.Width, CanvasSize.Height)).ToSK();
            return bitmap => Filters.FilterPixels.Apply(filter, settings, bitmap, scale, frame, coverage);
        }
        var adjustment = Settings.Clone();
        var placement = new AdjustmentPlacement(scale, 0, 0, 0, 0);
        return bitmap => AdjustmentPixels.Apply(adjustment, bitmap, placement);
    }

    public Guid LayerId { get; }
    /// <summary>Editing an adjustment layer's settings rather than changing a layer's pixels.</summary>
    public bool EditsLayer { get; }
    public AdjustmentKind Kind => Settings.Kind;
    /// <summary>The panel's working settings. Change them through <see cref="EditorSession.UpdateAdjustmentEdit"/>.</summary>
    public LayerAdjustment Settings { get; internal set; }
    public bool Preview { get; internal set; } = true;
    /// <summary>Levels: RGB (mean of the three), red, green, blue; 256 bins each. Null for other kinds.</summary>
    public double[][]? Histogram { get; internal set; }
    /// <summary>Camera Raw: what the canvas shows besides the grade (never applied).</summary>
    public CameraRaw.CameraRawView RawView { get; internal set; } = new();
    /// <summary>Camera Raw: the histogram and vectorscope of what the canvas last showed.</summary>
    public CameraRaw.CameraRawScope? RawScope { get; internal set; }

    // An adjustment layer: its settings when editing began, restored by Cancel.
    internal LayerAdjustment? Original;

    // A layer's pixels: what they were, where, and the selection limiting the change.
    internal RasterImage? Source;
    internal LayerTransform Transform = LayerTransform.Canvas(1, 1);
    internal SelectionClip? Selection;
    /// <summary>Room around the layer for a blur to spread into, in layer pixels; only ever grows.</summary>
    internal int Margin;
    internal SKBitmap? PreviewSource;
    internal double PreviewScale = 1;
    internal byte[]? PreviewCoverage;
    internal SKImage? PreviewImage;
    internal LayerTransform? PreviewTransform;
    internal PreviewJob? Pending;
    internal bool Rendering;
    internal int Generation, ClearedAt;
    /// <summary>Remove Background: the model, and its answer for this layer once worked out.</summary>
    internal Segmentation.ISubjectModel? SubjectModel;
    internal readonly SubjectAnswer Subject = new();
    internal bool Finished;

    internal int SourceWidth => Source!.Width + 2 * Margin;
    internal int SourceHeight => Source!.Height + 2 * Margin;
    internal LayerTransform GrownTransform =>
        Margin == 0 ? Transform : EditorSession.GridTransform(Transform, Source!.Width, Source.Height, new RectI(-Margin, -Margin, SourceWidth, SourceHeight));

    internal void ReleasePreview()
    {
        PreviewImage?.Dispose();
        PreviewImage = null;
        if (!Rendering)
        {
            PreviewSource?.Dispose();
            PreviewSource = null;
        }
    }
}

/// <summary>A preview render: works on its own copy, so it can run off the interface thread.</summary>
internal sealed record PreviewJob(SKBitmap Source, Action<SKBitmap> Operation, byte[]? Coverage, LayerTransform Transform,
                                  CameraRaw.CameraRawView? RawView = null)
{
    /// <summary>Camera Raw: the rendered grade's histogram and vectorscope, measured before any warning is painted over it.</summary>
    public CameraRaw.CameraRawScope? Scope { get; private set; }
    public int Generation { get; init; }

    public SKImage Run()
    {
        var adjusted = Source.Copy();
        try
        {
            Operation(adjusted);
            if (Coverage is not null)
            {
                // Within the selection only: the adjusted pixels over the originals, through its coverage.
                var output = Source.Copy();
                AdjustmentPixels.Mix(output, adjusted, Coverage, 1);
                adjusted.Dispose();
                adjusted = output;
            }
            if (RawView is { } view)
            {
                if (view.ShowsGrade) Scope = CameraRaw.CameraRawScope.Make(adjusted, CameraRaw.CameraRawScope.StepFor(adjusted));
                if (view.ShadowWarning || view.HighlightWarning)
                {
                    CameraRaw.CameraRawPixels.ClipOverlay(adjusted, view.ShadowWarning, view.HighlightWarning);
                    adjusted.NotifyPixelsChanged();
                }
            }
            adjusted.SetImmutable();
            return SKImage.FromBitmap(adjusted);
        }
        finally { adjusted.Dispose(); }
    }
}

public sealed partial class EditorSession
{
    private AdjustmentEdit? _adjustmentEdit;
    /// <summary>The adjustment panel's edit, while one is open.</summary>
    public AdjustmentEdit? AdjustmentEdit
    {
        get => _adjustmentEdit;
        private set
        {
            if (ReferenceEquals(_adjustmentEdit, value)) return;
            _adjustmentEdit = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
        }
    }

    private Action<PointD>? _canvasSampler;
    /// <summary>
    /// Set while an adjustment panel's eyedropper is armed: a click on the canvas goes to it (with the document point)
    /// instead of the tool.
    /// </summary>
    public Action<PointD>? CanvasSampler { get => _canvasSampler; set { _canvasSampler = value; OnPropertyChanged(); } }

    /// <summary>Renders adjustment previews off the interface thread (when there is one to come back to).</summary>
    public bool PreviewsInBackground { get; set; } = true;

    /// <summary>Changes whenever the canvas preview of an adjustment changes (for redraw caching).</summary>
    public int AdjustmentPreviewVersion { get; private set; }

    /// <summary>A pixel edit's canvas preview was redrawn (Camera Raw's scope follows it).</summary>
    public event EventHandler? AdjustmentPreviewChanged;

    /// <summary>Settings each filter was last applied with, so its panel opens where it was left, as on the Mac.</summary>
    private readonly Dictionary<AdjustmentKind, LayerAdjustment> _lastAdjustments = new();

    /// <summary>The last filter applied, for Filter ▸ Last Filter (Ctrl+F).</summary>
    public AdjustmentKind? LastFilter { get; private set; }

    /// <summary>
    /// Color adjustments and filters need one visible layer with pixels (not a folder, an adjustment layer or a mask)
    /// and a non-empty selection if there is one.
    /// </summary>
    public bool CanAdjustPixels => CanEditLayers && ActiveLayer is { IsGroup: false, IsAdjustment: false, Asset: not null } layer
                                   && SelectedLayerIds.Count == 1 && !IsMaskSelected && Selection?.IsEmpty != true
                                   && Document!.EffectiveVisibleIds.Contains(layer.Id);

    public bool CanRepeatLastFilter => LastFilterKind is { } filter
        ? (filter == Filters.FilterKind.Vignette ? CanVignette : CanAdjustPixels)
        : LastFilter is not null && CanAdjustPixels;

    public static bool IsFilter(AdjustmentKind kind) => kind.SamplesNeighbors();

    // MARK: Image adjustments and filters

    /// <summary>Opens an Image ▸ Adjustments command or a filter on the active layer's pixels, previewing on the canvas.</summary>
    public bool BeginImageAdjustment(AdjustmentKind kind, bool preview = true)
    {
        CommitTransform();
        if (!CanAdjustPixels || ActiveLayer is not { Asset: { } asset } layer || Document is not { } document) return false;
        if (kind == AdjustmentKind.Invert) { InvertPixels(); return false; }
        var settings = StartingSettings(kind, remembered: true);
        var edit = new AdjustmentEdit(layer.Id, settings, editsLayer: false)
        {
            Source = asset,
            Transform = layer.Transform,
            Selection = Selection?.Clip(document.Width, document.Height),
            Preview = preview,
        };
        try
        {
            PreparePreviewSource(edit);
            if (kind is AdjustmentKind.Levels or AdjustmentKind.Curves) edit.Histogram = PixelHistogram(edit);
        }
        catch (Exception error)
        {
            edit.ReleasePreview();
            ShowError(L.T("无法调整", "Couldn’t adjust"), error.Message);
            return false;
        }
        AdjustmentEdit = edit;
        SchedulePreview(edit);
        return true;
    }

    /// <summary>Filter ▸ Last Filter: the last filter again, with the same settings, without its panel.</summary>
    public void RepeatLastFilter()
    {
        if (!CanRepeatLastFilter) return;
        bool started = LastFilterKind is { } filter ? BeginFilter(filter, preview: false) : LastFilter is { } kind && BeginImageAdjustment(kind, preview: false);
        if (started) CommitAdjustmentEdit();
    }

    // MARK: The Filter menu's own effects

    private Filters.FilterSettings _filterSettings = new();

    /// <summary>The last Filter-menu effect applied (Vignette, Dither…), when it was one of those.</summary>
    public Filters.FilterKind? LastFilterKind { get; private set; }

    /// <summary>What Filter ▸ Last Filter repeats, for its menu title.</summary>
    public string? LastFilterTitle => LastFilterKind is { } f ? Filters.FilterKinds.ToDisplayName(f) : LastFilter?.ToDisplayName();

    /// <summary>Content-Aware Fill needs a selection to fill on a layer with pixels.</summary>
    public bool CanContentAwareFill => CanAdjustPixels && Selection is { IsEmpty: false };

    /// <summary>Vignette also works on an empty layer: it frames and fills the canvas.</summary>
    public bool CanVignette => CanAdjustPixels || (CanEditLayers && ActiveLayer is { IsGroup: false, IsAdjustment: false, Asset: null } layer
        && SelectedLayerIds.Count == 1 && !IsMaskSelected && Selection?.IsEmpty != true && Document!.EffectiveVisibleIds.Contains(layer.Id));

    /// <summary>Opens a Filter-menu effect on the active layer's pixels, previewing on the canvas.</summary>
    public bool BeginFilter(Filters.FilterKind kind, bool preview = true)
    {
        CommitTransform();
        CommitGradient();
        bool allowed = kind switch
        {
            Filters.FilterKind.Vignette => CanVignette,
            Filters.FilterKind.ContentAwareFill => CanContentAwareFill,
            _ => CanAdjustPixels,
        };
        if (!allowed || ActiveLayer is not { } layer || Document is not { } document) return false;
        Segmentation.ISubjectModel? subjectModel = null;
        if (kind == Filters.FilterKind.RemoveBackground && (subjectModel = SubjectModelSource?.Invoke()) is null) return false;
        var asset = layer.Asset;
        bool empty = asset is null;
        if (empty)
        {
            // An empty layer has no pixels until something is put on it; Vignette starts it with clear ones.
            int w = Math.Max(1, (int)Math.Round(layer.Transform.Size.Width)), h = Math.Max(1, (int)Math.Round(layer.Transform.Size.Height));
            asset = RasterImage.FromRgba(new byte[w * h * 4], w, h, layer.Name);
        }
        var edit = new AdjustmentEdit(layer.Id, kind, _filterSettings)
        {
            Source = asset,
            Transform = layer.Transform,
            Selection = Selection?.Clip(document.Width, document.Height),
            Preview = preview,
            StartedEmpty = empty,
            CanvasSize = document.Size,
            SubjectModel = subjectModel,
        };
        if (kind == Filters.FilterKind.ContentAwareFill && Selection is { } selection)
        {
            // Content-Aware Fill extends the layer over the part of the selection on the canvas past its edge.
            var map = layer.Transform.PixelToDocument(asset!.Width, asset.Height);
            if (map.IsInvertible)
            {
                var grid = map.Invert().Apply(selection.Bounds.Intersect(document.Bounds));
                double over = Math.Max(Math.Max(-grid.Left, -grid.Top), Math.Max(grid.Right - asset.Width, grid.Bottom - asset.Height));
                edit.FillMargin = (int)Math.Ceiling(Math.Max(0, over)) + 2;
            }
        }
        try { PreparePreviewSource(edit); }
        catch (Exception error)
        {
            edit.ReleasePreview();
            ShowError(L.T("无法应用滤镜", "Couldn’t apply the filter"), error.Message);
            return false;
        }
        AdjustmentEdit = edit;
        SchedulePreview(edit);
        return true;
    }

    /// <summary>A Filter-menu panel's settings changed (or its Preview box).</summary>
    public void UpdateFilterEdit(Filters.FilterSettings settings, bool? preview = null)
    {
        if (AdjustmentEdit is not { Filter: not null } edit) return;
        edit.FilterSettings = settings.Normalized;
        if (preview is { } p) edit.Preview = p;
        if (edit.NeededMargin() > edit.Margin)
        {
            try { PreparePreviewSource(edit); }
            catch (Exception error) { ShowError(L.T("无法预览", "Couldn’t preview"), error.Message); return; }
        }
        SchedulePreview(edit);
    }

    /// <summary>Camera Raw's White Balance › Auto: the Temperature and Tint that neutralize the layer's average color.</summary>
    public (double Temperature, double Tint)? CameraRawAutoBalance()
    {
        if (AdjustmentEdit is not { Filter: Filters.FilterKind.CameraRaw } edit) return null;
        if (edit.PreviewSource is { } preview) return CameraRaw.CameraRawProcessor.AutoBalance(preview);
        if (edit.Source is not { } source) return null;
        using var bitmap = SKBitmap.FromImage(source.Image);
        return CameraRaw.CameraRawProcessor.AutoBalance(bitmap);
    }

    // MARK: Adjustment layers

    /// <summary>Layer ▸ New Adjustment Layer: adds one above the active layer and opens its settings (Invert has none).</summary>
    public void AddAdjustmentLayer(AdjustmentKind kind)
    {
        CommitTransform();
        if (!CanEditLayers || Document is not { } document || document.Layers.Count >= DocumentLimits.MaxLayers) return;
        var adjustment = StartingSettings(kind, remembered: false);
        var layer = Layer.Blank(NextName(kind.ToDisplayName()), document.Size) with { Adjustment = adjustment, ParentId = InsertionParent() };
        if (layer.ParentId is { } parent) CollapsedGroupIds = CollapsedGroupIds.Remove(parent);
        if (!CommitLayers($"New {kind.ToName()} Adjustment", document.Layers.Insert(InsertionIndex(document), layer), layer.Id)) return;
        if (kind.IsEditable()) EditAdjustmentLayer(layer.Id);
    }

    /// <summary>Opens an adjustment layer's settings (double-click in the Layers panel). Changes show live; OK is one undo step.</summary>
    public bool EditAdjustmentLayer(Guid id)
    {
        CommitTransform();
        if (!CanEditLayers || Document is not { } document || document.Find(id) is not { Adjustment: { } original } || !original.Kind.IsEditable())
            return false;
        var edit = new AdjustmentEdit(id, original.Clone(), editsLayer: true) { Original = original };
        if (original.Kind is AdjustmentKind.Levels or AdjustmentKind.Curves)
        {
            try { edit.Histogram = CompositeHistogram(document, id); }
            catch { edit.Histogram = null; }
        }
        BeginEdit($"Edit {original.Kind.ToName()} Adjustment");
        AdjustmentEdit = edit;
        return true;
    }

    // MARK: Changing, OK and Cancel

    /// <summary>The panel's settings changed (or its Preview box): shows them on the canvas.</summary>
    public void UpdateAdjustmentEdit(LayerAdjustment settings, bool? preview = null)
    {
        if (AdjustmentEdit is not { Filter: null } edit || settings.Kind != edit.Kind) return;
        edit.Settings = settings.Clone();
        if (preview is { } p) edit.Preview = p;
        if (edit.EditsLayer)
        {
            if (!settings.IsValid) return;
            ShowLayerAdjustment(edit, edit.Preview ? edit.Settings.Clone() : edit.Original!);
            return;
        }
        // A bigger blur needs more room around the layer than it was given.
        if (edit.NeededMargin() > edit.Margin)
        {
            try { PreparePreviewSource(edit); }
            catch (Exception error) { ShowError(L.T("无法预览", "Couldn’t preview"), error.Message); return; }
        }
        SchedulePreview(edit);
    }

    /// <summary>OK: an adjustment layer keeps its new settings; a layer's pixels are changed at full size. One undo step.</summary>
    public void CommitAdjustmentEdit()
    {
        if (AdjustmentEdit is not { } edit) return;
        if (edit.EditsLayer)
        {
            var value = edit.Settings.IsValid ? edit.Settings.Clone() : edit.Original!;
            ShowLayerAdjustment(edit, value);
            EndEdit();
            CloseAdjustmentEdit(edit);
            return;
        }
        try
        {
            if (!edit.DoesNothing) ApplyToPixels(edit);
        }
        catch (Exception error) { ShowError(L.T("无法应用", "Couldn’t apply ") + edit.Title, error.Message); }
        finally { CloseAdjustmentEdit(edit); }
    }

    /// <summary>Cancel (or closing the panel): puts everything back as it was.</summary>
    public void CancelAdjustmentEdit()
    {
        if (AdjustmentEdit is not { } edit) return;
        if (edit.EditsLayer)
        {
            ShowLayerAdjustment(edit, edit.Original!);
            EndEdit();
        }
        CloseAdjustmentEdit(edit);
    }

    /// <summary>Shows what an adjustment layer's panel previews or a pixel edit's canvas preview, while one is open.</summary>
    public void ApplyAdjustmentPreview(RenderOptions options)
    {
        if (AdjustmentEdit is not { EditsLayer: false, Preview: true, PreviewImage: { } image } edit) return;
        if (Document?.Find(edit.LayerId) is not { } layer || !SourceStillThere(edit, layer)) return;
        var o = options.Edit(edit.LayerId);
        o.Image = image;
        o.Transform = edit.PreviewTransform;
        if (edit.Margin > 0 && layer.Mask is { Placement: null } mask && !mask.Asset.IsUniform)
        {
            // The layer grew for a blur; its mask stays where it was.
            o.OverridesMaskPlacement = true;
            o.MaskPlacement = layer.Transform;
        }
    }

    /// <summary>The layer still has the pixels the edit started from (none, for a vignette begun on an empty layer).</summary>
    private static bool SourceStillThere(AdjustmentEdit edit, Layer layer) =>
        edit.StartedEmpty ? layer.Asset is null : ReferenceEquals(layer.Asset, edit.Source);

    /// <summary>
    /// The color an adjustment panel's eyedropper picks up (unpremultiplied): the layer's own pixels for an Image
    /// adjustment, what is below an adjustment layer for one. Null outside or where clear.
    /// </summary>
    public PaletteColor? SampleAdjustmentSource(PointD point)
    {
        if (AdjustmentEdit is not { } edit || Document is not { } document) return null;
        if (edit.EditsLayer)
        {
            var below = document.Entries(topFirst: false).TakeWhile(e => e.Layer.Id != edit.LayerId).Select(e => e.Layer.Id).ToHashSet();
            return SampleComposite(point, l => below.Contains(l.Id));
        }
        var source = edit.Source!;
        var pixel = edit.Transform.PixelToDocument(source.Width, source.Height).Invert().Apply(point);
        int x = (int)Math.Floor(pixel.X), y = (int)Math.Floor(pixel.Y);
        if (x < 0 || y < 0 || x >= source.Width || y >= source.Height) return null;
        using var pixmap = source.PeekPixels();
        var bytes = pixmap.GetPixelSpan();
        int i = y * pixmap.RowBytes + x * 4;
        if (bytes[i + 3] == 0) return null;
        var (r, g, b, _) = PixelOps.Unpremultiply(bytes[i], bytes[i + 1], bytes[i + 2], bytes[i + 3]);
        return PaletteColor.FromBytes(r, g, b);
    }

    private void CloseAdjustmentEdit(AdjustmentEdit edit)
    {
        CanvasSampler = null;
        CanvasGesture = null;
        edit.Finished = true;
        edit.Pending = null;
        edit.ReleasePreview();
        if (ReferenceEquals(AdjustmentEdit, edit)) AdjustmentEdit = null;
        AdjustmentPreviewVersion++;
        Invalidate();
    }

    private void ShowLayerAdjustment(AdjustmentEdit edit, LayerAdjustment value)
    {
        if (Document is not { } document || document.Find(edit.LayerId) is not { IsAdjustment: true }) return;
        Document = document.UpdateLayer(edit.LayerId, l => l with { Adjustment = value });
    }

    // MARK: Settings

    private LayerAdjustment StartingSettings(AdjustmentKind kind, bool remembered)
    {
        // Levels and Hue/Saturation open fresh each time, as in Photoshop; the others where they were left.
        var settings = remembered && kind is not (AdjustmentKind.Levels or AdjustmentKind.HueSaturation)
                       && _lastAdjustments.TryGetValue(kind, out var last) ? last.Clone() : new LayerAdjustment(kind);
        // Gradient Map runs from the foreground to the background color; Grain and Noise each get a pattern of their own.
        if (kind == AdjustmentKind.GradientMap)
            settings.GradientMapSettings = new GradientMapSettings(AdjustmentColor.From(ForegroundColor), AdjustmentColor.From(BackgroundColor));
        if (kind == AdjustmentKind.Grain) settings.GrainSettings = settings.Grain with { Seed = (uint)Random.Shared.NextInt64(0, uint.MaxValue) };
        if (kind == AdjustmentKind.AddNoise) settings.NoiseSeed = (uint)Random.Shared.NextInt64(0, uint.MaxValue);
        if (kind == AdjustmentKind.HueSaturation) settings.HsvSettings = new HueSaturationSettings();
        return settings;
    }


    // MARK: Pixels

    /// <summary>The layer's pixels (padded for a blur) at preview size, and the selection's coverage on that grid.</summary>
    private static void PreparePreviewSource(AdjustmentEdit edit)
    {
        var source = edit.Source!;
        int margin = Math.Max(edit.Margin, edit.NeededMargin());
        if (margin > 0)
        {
            // Generous rounding so dragging the radius up doesn't regrow on every step.
            margin = (int)Math.Ceiling(margin / 16.0) * 16;
            long w = source.Width + 2L * margin, h = source.Height + 2L * margin;
            if (w > DocumentLimits.MaxSide * 2 || h > DocumentLimits.MaxSide * 2 || w * h > DocumentLimits.MaxSurfacePixels)
                throw new InvalidOperationException(L.T("图层太大，无法应用这个滤镜。", "The layer is too large for this filter."));
        }
        edit.Margin = margin;
        int width = edit.SourceWidth, height = edit.SourceHeight;
        double scale = Math.Min(1, edit.PreviewLimit / Math.Max(width, height));
        var old = edit.PreviewSource;
        edit.PreviewSource = Padded(source, margin, scale);
        if (!edit.Rendering) old?.Dispose();
        edit.PreviewScale = edit.PreviewSource.Width / (double)width;
        var placed = edit.GrownTransform;
        edit.PreviewTransform = placed;
        edit.PreviewCoverage = edit.Selection is { } clip
            ? GridCoverage(clip, placed.PixelToDocument(edit.PreviewSource.Width, edit.PreviewSource.Height),
                           edit.PreviewSource.Width, edit.PreviewSource.Height)
            : null;
    }

    /// <summary>A layer's pixels with <paramref name="margin"/> clear pixels around them, scaled by <paramref name="scale"/>.</summary>
    private static SKBitmap Padded(RasterImage source, int margin, double scale)
    {
        int width = source.Width + 2 * margin, height = source.Height + 2 * margin;
        int w = Math.Max(1, (int)Math.Round(width * scale)), h = Math.Max(1, (int)Math.Round(height * scale));
        var bitmap = new SKBitmap(RasterImage.ColorInfo(w, h));
        if (bitmap.GetPixels() == IntPtr.Zero) { bitmap.Dispose(); throw new InvalidOperationException(L.T("内存不足。", "Not enough memory.")); }
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        if (w == width && h == height)
        {
            canvas.DrawImage(source.Image, margin, margin);
        }
        else
        {
            canvas.Scale(w / (float)width, h / (float)height);
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
            canvas.DrawImage(source.Image, SKRect.Create(margin, margin, source.Width, source.Height),
                             new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
        }
        canvas.Flush();
        return bitmap;
    }

    /// <summary>A selection's coverage (0–255) for each pixel of a grid placed by <paramref name="pixelToDocument"/>.</summary>
    internal static byte[] GridCoverage(SelectionClip clip, Affine pixelToDocument, int width, int height)
    {
        var bytes = new byte[width * height];
        if (clip.IsNothing) return bytes;
        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                var d = pixelToDocument.Apply(x + 0.5, y + 0.5);
                bytes[y * width + x] = clip.At((int)Math.Floor(d.X), (int)Math.Floor(d.Y));
            }
        });
        return bytes;
    }

    /// <summary>Where a rectangle of a layer's pixel grid (possibly reaching past it) sits on the document.</summary>
    internal static LayerTransform GridTransform(LayerTransform transform, int gridWidth, int gridHeight, RectI rect)
    {
        var map = transform.PixelToDocument(gridWidth, gridHeight);
        var center = map.Apply(rect.X + rect.Width / 2.0, rect.Y + rect.Height / 2.0);
        var size = new SizeD(rect.Width * transform.Size.Width / gridWidth, rect.Height * transform.Size.Height / gridHeight);
        return transform with { Size = size, Origin = new PointD(center.X - size.Width / 2, center.Y - size.Height / 2) };
    }

    private void SchedulePreview(AdjustmentEdit edit)
    {
        if (edit.EditsLayer || edit.PreviewSource is not { } source) return;
        bool raw = edit.Filter == Filters.FilterKind.CameraRaw;
        if (!edit.Preview || (edit.DoesNothing && !(raw && edit.RawView.Paints)))
        {
            edit.Pending = null;
            edit.PreviewImage?.Dispose();
            edit.PreviewImage = null;
            // The canvas shows the layer as it is, and so does the scope.
            edit.ClearedAt = ++edit.Generation;
            if (raw) edit.RawScope = CameraRaw.CameraRawScope.Make(source, CameraRaw.CameraRawScope.StepFor(source));
            AdjustmentPreviewVersion++;
            AdjustmentPreviewChanged?.Invoke(this, EventArgs.Empty);
            Invalidate();
            return;
        }
        var placed = edit.PreviewTransform ?? edit.GrownTransform;
        var operation = edit.Operation(edit.PreviewScale, edit.PreviewCoverage, placed.PixelToDocument(source.Width, source.Height), preview: true);
        edit.Pending = new PreviewJob(source, operation, edit.PreviewCoverage, placed, raw ? edit.RawView : null) { Generation = ++edit.Generation };
        var context = PreviewsInBackground ? SynchronizationContext.Current : null;
        if (context is null)
        {
            // No interface thread to come back to (tests, scripts): render straight away.
            var job = edit.Pending;
            edit.Pending = null;
            ShowPreview(edit, job, job.Run());
            return;
        }
        if (!edit.Rendering) RenderPending(edit, context);
    }

    /// <summary>
    /// Renders the newest settings off the interface thread. Changes that arrive mid-render wait for it rather than
    /// cancel it, so dragging a slider keeps the canvas updating.
    /// </summary>
    private void RenderPending(AdjustmentEdit edit, SynchronizationContext context)
    {
        if (edit.Pending is not { } job) return;
        edit.Pending = null;
        edit.Rendering = true;
        Task.Run(() =>
        {
            try { return job.Run(); }
            catch { return null; }
        }).ContinueWith(task => context.Post(_ =>
        {
            edit.Rendering = false;
            var image = task.Result;
            if (edit.Finished || !ReferenceEquals(AdjustmentEdit, edit))
            {
                image?.Dispose();
                edit.PreviewSource?.Dispose();
                edit.PreviewSource = null;
                return;
            }
            // A render the panel has since cleared (settings back to nothing) is stale.
            if (image is not null && job.Generation < edit.ClearedAt) { image.Dispose(); image = null; }
            if (image is not null) ShowPreview(edit, job, image);
            RenderPending(edit, context);
        }, null), TaskScheduler.Default);
    }

    private void ShowPreview(AdjustmentEdit edit, PreviewJob job, SKImage image)
    {
        if (!edit.Preview) { image.Dispose(); return; }
        edit.PreviewImage?.Dispose();
        edit.PreviewImage = image;
        edit.PreviewTransform = job.Transform;
        if (job.Scope is { } scope) edit.RawScope = scope;
        AdjustmentPreviewVersion++;
        AdjustmentPreviewChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    /// <summary>OK on a pixel edit: the adjustment at full size, within the selection; a blur's spread trimmed to what is there.</summary>
    private void ApplyToPixels(AdjustmentEdit edit)
    {
        if (Document is not { } document || document.Find(edit.LayerId) is not { } layer
            || !SourceStillThere(edit, layer) || layer.Transform != edit.Transform) return;
        if (edit.Filter == Filters.FilterKind.RemoveBackground)
        {
            ApplyBackgroundMask(edit);
            _filterSettings = edit.FilterSettings;
            return;
        }
        var settings = edit.Settings;
        int margin = edit.Margin;
        int width = edit.SourceWidth, height = edit.SourceHeight;
        var grown = edit.GrownTransform;
        using var original = Padded(edit.Source!, margin, 1);
        var adjusted = original.Copy();
        try
        {
            var coverage = edit.Selection is { } clip ? GridCoverage(clip, grown.PixelToDocument(width, height), width, height) : null;
            edit.Operation(1, coverage, grown.PixelToDocument(width, height))(adjusted);
            if (coverage is not null)
            {
                AdjustmentPixels.Mix(original, adjusted, coverage, 1);
                adjusted.Dispose();
                adjusted = original.Copy();
            }
            var image = RasterImage.FromBitmap(adjusted, name: layer.Name);
            var transform = grown;
            if (margin > 0)
            {
                // Whatever the blur left empty is cut away again.
                var bounds = PixelOps.AlphaBounds(image);
                if (bounds.IsEmpty) bounds = new RectI(margin, margin, edit.Source!.Width, edit.Source.Height);
                if (bounds != new RectI(0, 0, width, height))
                {
                    image = PixelOps.Crop(image, bounds, layer.Name);
                    transform = GridTransform(grown, width, height, bounds);
                }
            }
            var mask = layer.Mask;
            if (margin > 0 && mask is { Placement: null } owned && !owned.Asset.IsUniform) mask = owned with { Placement = layer.Transform };
            BeginEdit(edit.Filter?.ToName() ?? settings.Kind.ToName());
            Document = document.UpdateLayer(layer.Id, l => l with { Asset = image, Transform = transform, Mask = mask, Text = null, Shape = null });
            EndEdit();
            if (edit.Filter is { } filter)
            {
                _filterSettings = edit.FilterSettings!;
                if (filter != Filters.FilterKind.ContentAwareFill)
                {
                    LastFilterKind = filter; LastFilter = null;
                    OnPropertyChanged(nameof(LastFilter)); OnPropertyChanged(nameof(LastFilterKind));
                }
            }
            else
            {
                _lastAdjustments[settings.Kind] = settings.Clone();
                if (IsFilter(settings.Kind))
                {
                    LastFilter = settings.Kind; LastFilterKind = null;
                    OnPropertyChanged(nameof(LastFilter)); OnPropertyChanged(nameof(LastFilterKind));
                }
            }
        }
        finally { adjusted.Dispose(); }
    }

    // MARK: Histograms

    private const int HistogramSide = 1024;

    /// <summary>The layer's pixels within the selection, weighted by coverage.</summary>
    private static double[][] PixelHistogram(AdjustmentEdit edit)
    {
        var source = edit.Source!;
        double scale = Math.Min(1, HistogramSide / (double)Math.Max(source.Width, source.Height));
        using var bitmap = Padded(source, 0, scale);
        var coverage = edit.Selection is { } clip
            ? GridCoverage(clip, edit.Transform.PixelToDocument(bitmap.Width, bitmap.Height), bitmap.Width, bitmap.Height)
            : null;
        return AdjustmentPixels.Histogram(bitmap, coverage);
    }

    /// <summary>What an adjustment layer works on: everything below it, composited.</summary>
    private static double[][] CompositeHistogram(CanvasDocument document, Guid id)
    {
        var below = document.Entries(topFirst: false).TakeWhile(e => e.Layer.Id != id).Select(e => e.Layer.Id).ToHashSet();
        double scale = Math.Min(1, HistogramSide / (double)Math.Max(document.Width, document.Height));
        using var image = DocumentRenderer.RenderToImage(document, new RenderOptions { Include = l => below.Contains(l.Id) }, scale);
        using var bitmap = SKBitmap.FromImage(image);
        return AdjustmentPixels.Histogram(bitmap, null);
    }
}
