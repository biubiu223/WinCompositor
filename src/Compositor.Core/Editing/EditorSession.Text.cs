using Compositor.Core.Geometry;
using Compositor.Core.Localization;
using Compositor.Core.Model;
using Compositor.Core.Rendering;
using Compositor.Core.Text;
using SkiaSharp;

namespace Compositor.Core.Editing;

/// <summary>
/// Text being typed: a new text layer, or an existing one opened again. The canvas shows it as it will be; nothing
/// changes in the document until it is applied. Mirrors TextDraft in TypeTool.swift.
/// </summary>
public sealed class TextDraft
{
    internal TextDraft(Guid documentId, Guid? layerId, PointD origin, LayerTransform? transform, LayerText style)
    {
        DocumentId = documentId;
        LayerId = layerId;
        Origin = origin;
        Transform = transform;
        Style = style;
    }

    public Guid DocumentId { get; }
    /// <summary>The text layer being edited; null for new text.</summary>
    public Guid? LayerId { get; }
    /// <summary>New text: the box's top-left corner on the document.</summary>
    public PointD Origin { get; internal set; }
    /// <summary>An existing layer: where it is (its scale, rotation and flips are kept).</summary>
    public LayerTransform? Transform { get; internal set; }
    public LayerText Style { get; internal set; }
    /// <summary>What is selected in the editor, in UTF-16 offsets; color and font changes apply to it.</summary>
    public int SelectionStart { get; internal set; }
    public int SelectionLength { get; internal set; }
    public int Revision { get; internal set; }

    private int _laidOut = -1, _rendered = -1;
    private TextLayout? _layout;
    private RasterImage? _image;

    public TextLayout Layout
    {
        get
        {
            if (_layout is null || _laidOut != Revision) { _layout = TextLayout.Make(Style); _laidOut = Revision; }
            return _layout;
        }
    }

    public RasterImage Image
    {
        get
        {
            if (_image is null || _rendered != Revision) { _image = Layout.Render(LayerText.LayerName(Style.Content)); _rendered = Revision; }
            return _image;
        }
    }

    /// <summary>
    /// Where the text shows: new text at its origin, one pixel per layer pixel; an existing layer keeps its top-left
    /// corner and its own scale, rotation and flips while its box changes size.
    /// </summary>
    public LayerTransform Placement(Layer? layer)
    {
        var size = Layout.Size;
        if (Transform is not { } transform || layer?.Asset is not { } asset) return new LayerTransform(Origin, size);
        double sx = transform.Size.Width / Math.Max(1, asset.Width), sy = transform.Size.Height / Math.Max(1, asset.Height);
        var anchor = transform.Point(0, 0);
        var resized = transform with { Size = new SizeD(Math.Ceiling(size.Width) * sx, Math.Ceiling(size.Height) * sy) };
        var moved = resized.Point(0, 0);
        return resized with { Origin = new PointD(resized.Origin.X + anchor.X - moved.X, resized.Origin.Y + anchor.Y - moved.Y) };
    }
}

public sealed partial class EditorSession
{
    private LayerText _textDefaults = new() { Content = "", FontName = "Microsoft YaHei", FontSize = 72 };
    /// <summary>The style new text starts with: what the last applied text used.</summary>
    public LayerText TextDefaults { get => _textDefaults; set => SetField(ref _textDefaults, value); }

    private TextDraft? _textDraft;
    public TextDraft? TextDraft
    {
        get => _textDraft;
        private set
        {
            _textDraft = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CurrentTextStyle));
            Invalidate();
        }
    }

    /// <summary>The style the options bar shows: the text being typed, the selected text layer, or the defaults.</summary>
    public LayerText CurrentTextStyle => TextDraft?.Style ?? ActiveLayer?.Text ?? TextDefaults;

    private void TextChanged(TextDraft draft)
    {
        draft.Revision++;
        OnPropertyChanged(nameof(TextDraft));
        OnPropertyChanged(nameof(CurrentTextStyle));
        Invalidate();
    }

    /// <summary>
    /// A click with the Type tool: on a text layer it opens that text; elsewhere new point text starts with its first
    /// baseline on the pointer, as Photoshop's does.
    /// </summary>
    public void BeginText(PointD point, bool newLayer = false)
    {
        if (!CanEditLayers || TextDraft is not null || Document is not { } document || !point.IsFinite) return;
        var visible = document.EffectiveVisibleIds;
        var target = newLayer ? null : document.Layers.Reverse().FirstOrDefault(l => visible.Contains(l.Id) && l.Text is not null && l.Transform.Contains(point));
        if (target is not null) SelectLayer(target.Id);
        var style = target?.Text ?? TextDefaults with { Content = "", ColorRuns = null, FontRuns = null, BoxSize = null };
        if (target is null && !IsMaskSelected)
            style = style with { Red = ForegroundColor.Red, Green = ForegroundColor.Green, Blue = ForegroundColor.Blue };
        if (Tool != Tool.Type) SelectTool(Tool.Type);
        double baseline = LayerText.Padding + style.LineHeight - TextLayout.Descent(style);
        var origin = target?.Origin ?? new PointD(point.X - LayerText.Padding, point.Y - baseline);
        var draft = new TextDraft(document.Id, target?.Id, origin, target?.Transform, style);
        draft.SelectionStart = target is null ? 0 : style.Content.Length;
        TextDraft = draft;
    }

    /// <summary>Dragging with the Type tool: paragraph text in a box of that size, exactly where it was drawn.</summary>
    public void BeginTextBox(RectD rect)
    {
        if (!CanEditLayers || TextDraft is not null || rect.IsNull) return;
        var box = new SizeD(Math.Max(16, Math.Round(rect.Width)), Math.Max(16, Math.Round(rect.Height)));
        if (box.Width > DocumentLimits.MaxSide || box.Height > DocumentLimits.MaxSide) { ShowError(L.T("无法创建文字框", "Couldn’t create the text box"), L.T("文字框太大。", "The text box is too large.")); return; }
        BeginText(rect.Origin, newLayer: true);
        if (TextDraft is not { } draft) return;
        draft.Origin = rect.Origin;
        draft.Style = draft.Style with { BoxSize = box };
        TextChanged(draft);
    }

    /// <summary>Opens the selected text layer for editing (Enter with the Type tool, or a double-click).</summary>
    public void EditActiveText()
    {
        if (!CanEditLayers || TextDraft is not null || Document is not { } document || ActiveLayer is not { Text: { } text } layer) return;
        if (Tool != Tool.Type) SelectTool(Tool.Type);
        TextDraft = new TextDraft(document.Id, layer.Id, layer.Origin, layer.Transform, text) { SelectionStart = 0, SelectionLength = text.Content.Length };
    }

    /// <summary>The editor replaced a range of the text (typing, deleting, pasting); new letters take on their neighbor's look.</summary>
    public void ReplaceText(int location, int length, string replacement)
    {
        if (TextDraft is not { } draft) return;
        var next = draft.Style.Replacing(location, length, replacement);
        if (next.Content.Length > LayerText.MaxLength) return;
        draft.Style = next;
        TextChanged(draft);
    }

    public void SetTextSelection(int start, int length)
    {
        if (TextDraft is not { } draft) return;
        start = Math.Clamp(start, 0, draft.Style.Content.Length);
        length = Math.Clamp(length, 0, draft.Style.Content.Length - start);
        if (draft.SelectionStart == start && draft.SelectionLength == length) return;
        draft.SelectionStart = start;
        draft.SelectionLength = length;
        OnPropertyChanged(nameof(TextDraft));
        Invalidate();
    }

    /// <summary>
    /// The options bar changed the font, size, color or layout: the text being typed takes it (color and font only
    /// its selection, when there is one); a selected text layer is opened to take it; otherwise new text will.
    /// </summary>
    public void ChangeTextStyle(Func<LayerText, int, int, LayerText> change)
    {
        if (TextDraft is null && ActiveLayer?.Text is not null && Tool == Tool.Type) EditActiveText();
        if (TextDraft is { } draft)
        {
            var next = change(draft.Style, draft.SelectionStart, draft.SelectionLength);
            if (!next.IsValid || next == draft.Style) return;
            draft.Style = next;
            TextChanged(draft);
        }
        else
        {
            var next = change(TextDefaults, 0, 0);
            if (next.IsValid) TextDefaults = next;
            OnPropertyChanged(nameof(CurrentTextStyle));
        }
    }

    public void CancelText()
    {
        if (TextDraft is null) return;
        TextDraft = null;
    }

    /// <summary>
    /// Applies the text: a new text layer above the active one, or the edited layer redrawn (keeping its place,
    /// scale and rotation), as one undo step. New text with nothing but spaces makes nothing.
    /// </summary>
    public bool CommitText()
    {
        if (TextDraft is not { } draft) return true;
        if (Document is not { } document || document.Id != draft.DocumentId) { TextDraft = null; return false; }
        if (!draft.Style.IsValid) return false;
        if (draft.LayerId is null && string.IsNullOrWhiteSpace(draft.Style.Content)) { TextDraft = null; return true; }
        RasterImage image;
        try { image = draft.Image; }
        catch (Exception error) { ShowError(L.T("无法应用文字", "Couldn’t apply the text"), error.Message); return false; }
        TextDraft = null;
        if (draft.LayerId is { } id)
        {
            if (document.Find(id) is not { Text: not null } layer) return false;
            if (layer.Text == draft.Style) return true;
            var placement = draft.Placement(layer);
            if (!placement.IsValid) { ShowError(L.T("无法应用文字", "Couldn’t apply the text"), L.T("结果超出了支持的范围。", "The result is outside the supported range.")); return false; }
            BeginEdit("Edit Text");
            Document = document.UpdateLayer(id, l => l with
            {
                Asset = image,
                Text = draft.Style,
                Transform = placement,
                Name = l.Name,
                // A mask that follows the layer's grid stays where it is while the grid changes size.
                Mask = l.Mask is { Placement: null } m && !m.Asset.IsUniform ? m with { Placement = l.Transform } : l.Mask,
            });
            EndEdit();
        }
        else
        {
            if (document.Layers.Count >= DocumentLimits.MaxLayers) return false;
            var layer = Layer.FromImage(image, draft.Origin, LayerText.LayerName(draft.Style.Content)) with { Text = draft.Style };
            InsertLayer(layer, "New Text Layer");
        }
        TextDefaults = draft.Style with { Content = "", ColorRuns = null, FontRuns = null, BoxSize = null };
        return true;
    }

    /// <summary>Shows an edited text layer as it is being typed, in its place among the layers.</summary>
    public void ApplyTextPreview(RenderOptions options)
    {
        if (TextDraft is not { LayerId: { } id } draft || Document?.Find(id) is not { } layer) return;
        try
        {
            var o = options.Edit(id);
            o.Image = draft.Image.Image;
            o.Transform = draft.Placement(layer);
            if (layer.Mask is { } mask)
            {
                o.OverridesMaskPlacement = true;
                o.MaskPlacement = mask.Placement ?? layer.Transform;
            }
        }
        catch { /* too large to show: the layer as it was */ }
    }

    /// <summary>Fill with a color on a text layer (no selection): the letters change color and stay editable text.</summary>
    public bool RecolorText(Guid id, PaletteColor color)
    {
        if (!CanEditLayers || Document is not { } document || document.Find(id) is not { Text: { } text, Asset: { } asset } layer) return false;
        var style = text.SettingColor(color, 0, 0);
        if (style == text) return true;
        try
        {
            var layout = TextLayout.Make(style);
            var image = layout.Render(asset.Name);
            BeginEdit("Fill Text");
            Document = document.UpdateLayer(id, l => l with { Asset = image, Text = style });
            EndEdit();
            return true;
        }
        catch { return false; }
    }
}
