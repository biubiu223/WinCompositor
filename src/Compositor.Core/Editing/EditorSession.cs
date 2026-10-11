using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Compositor.Core.Adjustments;
using Compositor.Core.Geometry;
using Compositor.Core.History;
using Compositor.Core.Localization;
using Compositor.Core.Model;
using Compositor.Core.Painting;
using Compositor.Core.Pixels;
using Compositor.Core.Rendering;
using SkiaSharp;

namespace Compositor.Core.Editing;

public enum Tool
{
    Move, Marquee, Lasso, Wand, Crop, Brush, SpotHealing, CloneStamp, Smear, Gradient, Shape, Type, Eyedropper, Hand, Zoom,
    /// <summary>No tool (A): canvas clicks do nothing.</summary>
    Idle,
    /// <summary>The Remove tool (Shift+J): paint over something and it's filled away when the stroke ends.</summary>
    Remove,
}

public enum LassoKind { Freehand, Polygonal, Rectangle, Ellipse }
public enum SelectionMode { Replace, Add, Subtract }

public static class ToolInfo
{
    public static bool IsSelectionTool(this Tool tool) => tool is Tool.Marquee or Tool.Lasso or Tool.Wand;
    /// <summary>Tools that paint with the brush tip: Brush, Spot Healing, Clone Stamp and the Smear tool.</summary>
    public static bool IsBrushTool(this Tool tool) => tool is Tool.Brush or Tool.SpotHealing or Tool.CloneStamp or Tool.Smear or Tool.Remove;

    public static string Label(this Tool tool) => tool switch
    {
        Tool.Move => L.T("移动 / 变换 (V)", "Move / Transform (V)"),
        Tool.Marquee => L.T("选框 (M) · 工具栏切换矩形/椭圆", "Marquee (M) · Rectangle or Ellipse in the options bar"),
        Tool.Lasso => L.T("套索 (L) · 工具栏切换自由/多边形", "Lasso (L) · Freehand or Polygonal in the options bar"),
        Tool.Wand => L.T("魔棒 (W)", "Magic Wand (W)"),
        Tool.Crop => L.T("裁剪 (C)", "Crop (C)"),
        Tool.Brush => L.T("画笔 (B) · 橡皮擦 (E)", "Brush (B) · Eraser (E)"),
        Tool.SpotHealing => L.T("污点修复画笔 (J)", "Spot Healing Brush (J)"),
        Tool.Remove => L.T("抹除工具 (Shift+J) · 涂抹或圈住要去掉的东西，松开即用纹理合成抹除",
                           "Remove Tool (Shift+J) · Paint over or circle what to remove; it’s filled with texture synthesis when you release"),
        Tool.CloneStamp => L.T("仿制图章 (S) · Alt 单击设置仿制源", "Clone Stamp (S) · Alt-click sets the source"),
        Tool.Smear => L.T("涂抹工具 (R) · 液化 / 模糊 / 涂抹", "Smear Tool (R) · Liquify / Blur / Smudge"),
        Tool.Gradient => L.T("渐变 (G)", "Gradient (G)"),
        Tool.Shape => L.T("形状 (U) · Shift+U 切换矩形/椭圆/直线", "Shape (U) · Shift+U switches Rectangle/Ellipse/Line"),
        Tool.Type => L.T("文字 (T)", "Type (T)"),
        Tool.Eyedropper => L.T("吸管 (I)", "Eyedropper (I)"),
        Tool.Hand => L.T("抓手 (H)", "Hand (H)"),
        Tool.Zoom => L.T("缩放 (Z)", "Zoom (Z)"),
        _ => L.T("无工具 (A)", "No Tool (A)"),
    };
}

/// <summary>A lasso or marquee outline being drawn, in document pixels.</summary>
public sealed record LassoDraft(ImmutableList<PointD> Points, PointD? Cursor, SelectionMode Mode, LassoKind Kind, PointD? Anchor = null);

/// <summary>What the interface should show the person: an error or a notice.</summary>
public sealed record SessionMessage(string Title, string Text, bool IsError = true);

/// <summary>
/// One open project: its document, history, viewport and tool state, and every edit the interface can make. The
/// interface binds to it and calls it; it never touches pixels or files on its own. Mirrors EditorSession.swift.
/// </summary>
public sealed partial class EditorSession : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>The document changed (any edit, undo or redo).</summary>
    public event EventHandler? DocumentChanged;
    /// <summary>Something drawn on the canvas changed without the document changing (a preview, a draft, overlays).</summary>
    public event EventHandler? CanvasInvalidated;
    /// <summary>A message for the person (an error the edit hit, or why it couldn't start).</summary>
    public event EventHandler<SessionMessage>? Message;

    public DocumentHistory History { get; } = new();
    public CanvasViewport Viewport { get; } = new();

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Invalidate() => CanvasInvalidated?.Invoke(this, EventArgs.Empty);
    public void ShowError(string title, string text) => Message?.Invoke(this, new SessionMessage(title, text));
    public void ShowError(string title, Exception error) => ShowError(title, error.Message);

    public EditorSession()
    {
        History.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
            OnPropertyChanged(nameof(IsModified));
            OnPropertyChanged(nameof(UndoTitle));
            OnPropertyChanged(nameof(RedoTitle));
        };
    }

    // MARK: Document

    private CanvasDocument? _document;
    public CanvasDocument? Document
    {
        get => _document;
        set
        {
            if (ReferenceEquals(_document, value)) return;
            _document = value;
            OnPropertyChanged();
            DocumentChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private string? _projectPath;
    /// <summary>The .comp folder this project was opened from or saved to; null until saved.</summary>
    public string? ProjectPath { get => _projectPath; set { if (SetField(ref _projectPath, value)) OnPropertyChanged(nameof(Title)); } }
    public string Title => ProjectPath is null ? DocumentName ?? L.T("未命名", "Untitled") : Path.GetFileNameWithoutExtension(ProjectPath.TrimEnd('\\', '/'));

    /// <summary>The name of a document opened from a file that isn't a project (a Photoshop file), until it's saved as one.</summary>
    public string? DocumentName { get; private set; }
    public bool IsModified => History.IsModified;

    private Guid? _activeLayerId;
    public Guid? ActiveLayerId
    {
        get => _activeLayerId;
        set
        {
            if (_activeLayerId != value) IsMaskSelected = false;
            _activeLayerId = value;
            _selectedLayerIds = value is { } id ? ImmutableHashSet.Create(id) : ImmutableHashSet<Guid>.Empty;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedLayerIds));
            OnPropertyChanged(nameof(ActiveLayer));
        }
    }

    private ImmutableHashSet<Guid> _selectedLayerIds = ImmutableHashSet<Guid>.Empty;
    public ImmutableHashSet<Guid> SelectedLayerIds => _selectedLayerIds;

    public Layer? ActiveLayer => Document?.Find(ActiveLayerId);

    private bool _isMaskSelected;
    /// <summary>Painting and adjustments target the active layer's mask rather than its pixels.</summary>
    public bool IsMaskSelected
    {
        get => _isMaskSelected;
        set
        {
            if (SetField(ref _isMaskSelected, value) && !value) ViewsMaskAlone = false;
            Invalidate();
        }
    }

    private bool _viewsMaskAlone;
    /// <summary>Alt-click on a mask thumbnail: the canvas shows that mask by itself, in grayscale.</summary>
    public bool ViewsMaskAlone { get => _viewsMaskAlone; set { if (SetField(ref _viewsMaskAlone, value)) Invalidate(); } }

    public Layer? MaskAloneLayer => ViewsMaskAlone && IsMaskSelected && ActiveLayer is { Mask: not null } layer ? layer : null;

    private ImmutableHashSet<Guid> _collapsedGroupIds = ImmutableHashSet<Guid>.Empty;
    public ImmutableHashSet<Guid> CollapsedGroupIds { get => _collapsedGroupIds; set => SetField(ref _collapsedGroupIds, value); }

    private Guid? _renamingLayerId;
    public Guid? RenamingLayerId { get => _renamingLayerId; set => SetField(ref _renamingLayerId, value); }

    public DocumentSelection? Selection => Document?.Selection;

    // MARK: History

    public bool CanUndo => CanUseHistory && History.CanUndo;
    public bool CanRedo => CanUseHistory && History.CanRedo;
    public string UndoTitle => History.CanUndo ? L.T("撤销 ", "Undo ") + EditNames.Translate(History.UndoName) : L.T("撤销", "Undo");
    public string RedoTitle => History.CanRedo ? L.T("重做 ", "Redo ") + EditNames.Translate(History.RedoName) : L.T("重做", "Redo");

    public bool CanUseHistory => BrushStroke is null && WarpStroke is null && LassoDraft is null && TransformEdit?.Persistent != true && RenamingLayerId is null
                                 && AdjustmentEdit is null && EffectEdit is null && TextDraft is null && ColorRange is null;

    /// <summary>Nestable transaction boundary: a complete gesture is one undo step.</summary>
    public void BeginEdit(string name) => History.Begin(name, Document, ActiveLayerId);
    public void EndEdit() => History.End(Document, ActiveLayerId);

    public void Undo()
    {
        if (TransformEdit is not null) { CancelTransform(); return; }
        if (GradientEdit is not null) { CancelGradient(); return; }
        if (!CanUndo) return;
        if (History.Undo() is { } snapshot) Restore(snapshot);
    }

    public void Redo()
    {
        if (!CanRedo) return;
        if (History.Redo() is { } snapshot) Restore(snapshot);
    }

    private void Restore(DocumentHistory.Snapshot snapshot)
    {
        CropRect = null;
        bool changedCanvas = Document?.Id != snapshot.Document?.Id
            || Document?.Width != snapshot.Document?.Width || Document?.Height != snapshot.Document?.Height;
        bool keepMask = IsMaskSelected && ActiveLayerId == snapshot.ActiveLayerId;
        Document = snapshot.Document;
        ActiveLayerId = snapshot.ActiveLayerId;
        IsMaskSelected = keepMask && ActiveLayer?.Mask is not null;
        if (changedCanvas && Document is { } doc) Viewport.Fit(doc.Size);
    }

    // MARK: Guards

    /// <summary>Whether layer edits can start: a document, and nothing else in progress that owns the layers.</summary>
    public bool CanEditLayers => Document is not null && BrushStroke is null && WarpStroke is null && TransformEdit is null && !HasPendingCrop
                                 && RenamingLayerId is null && LassoDraft is null && AdjustmentEdit is null && EffectEdit is null
                                 && TextDraft is null && ColorRange is null;

    /// <summary>A crop frame that has been changed: the Crop tool's untouched frame (the whole canvas) is no edit.</summary>
    public bool HasPendingCrop => CropRect is { } rect && Document is { } document && rect != document.Bounds;

    public bool CanEditPixels => CanEditLayers && ActiveLayer is { } layer && SelectedLayerIds.Count == 1
                                 && (IsMaskSelected ? layer.Mask is not null : !layer.IsGroup && !layer.IsAdjustment);

    // MARK: Tools

    private Tool _tool = Tool.Move;
    public Tool Tool
    {
        get => _tool;
        set => SelectTool(value);
    }

    public void SelectTool(Tool value)
    {
        if (_tool == value) return;
        if (BrushStroke is not null || WarpStroke is not null) return;
        // Switching tools applies a pending gradient and finishes text being typed, as in Photoshop.
        CommitGradient();
        CancelShape();
        if (value != Tool.Type && TextDraft is not null && !CommitText()) return;
        CommitTransform();
        CancelLasso();
        if (_tool == Tool.Crop) CropRect = null;
        // Clone Stamp and the Smear tool keep brush tips of their own (soft to begin with); Brush and Spot Healing share one.
        int from = TipFamily(_tool), to = TipFamily(value);
        if (from != to && _parkedTips.TryGetValue(to, out var parked))
        {
            _parkedTips[from] = (BrushSettings.Diameter, BrushSettings.Hardness, BrushSettings.Opacity);
            BrushSettings = BrushSettings with { Diameter = parked.Diameter, Hardness = parked.Hardness, Opacity = parked.Opacity };
        }
        _tool = value;
        if (value == Tool.Crop && Document is { } document)
        {
            // With a selection, the crop starts at its bounds, as Photoshop's does.
            CropRatioChoice = CropRatios[0];
            var canvas = document.Bounds;
            if (Selection is { IsEmpty: false } selection)
            {
                var bounds = selection.Bounds.Integral().Intersect(canvas);
                CropRect = CropGeometry.Valid(bounds) ? bounds : canvas;
            }
            else
            {
                CropRect = canvas;
            }
        }
        OnPropertyChanged(nameof(Tool));
        Invalidate();
    }

    private readonly Dictionary<int, (double Diameter, double Hardness, double Opacity)> _parkedTips = new()
    {
        [1] = (40, 0, 1),
        [2] = (40, 0, 1),
        [3] = (80, 1, 1),
    };

    private static int TipFamily(Tool tool) => tool switch { Tool.CloneStamp => 1, Tool.Smear => 2, Tool.Remove => 3, _ => 0 };

    private BrushToolMode _brushMode = BrushToolMode.Paint;
    /// <summary>Paint lays down the foreground color, Erase clears pixels away (B and E).</summary>
    public BrushToolMode BrushMode { get => _brushMode; set { if (SetField(ref _brushMode, value)) Invalidate(); } }

    private BrushSettings _brushSettings = new();
    public BrushSettings BrushSettings { get => _brushSettings; set { if (SetField(ref _brushSettings, value)) Invalidate(); } }

    private PaletteColor _foregroundColor = PaletteColor.Black;
    public PaletteColor ForegroundColor { get => _foregroundColor; set => SetField(ref _foregroundColor, value); }

    private PaletteColor _backgroundColor = PaletteColor.White;
    public PaletteColor BackgroundColor { get => _backgroundColor; set => SetField(ref _backgroundColor, value); }

    public void SwapColors() => (ForegroundColor, BackgroundColor) = (BackgroundColor, ForegroundColor);
    public void ResetColors() { ForegroundColor = PaletteColor.Black; BackgroundColor = PaletteColor.White; }

    private SelectionMode _selectionModeChoice = SelectionMode.Replace;
    public SelectionMode SelectionModeChoice { get => _selectionModeChoice; set => SetField(ref _selectionModeChoice, value); }

    private LassoKind _marqueeKind = LassoKind.Rectangle;
    public LassoKind MarqueeKind { get => _marqueeKind; set { if (SetField(ref _marqueeKind, value)) CancelLasso(); } }

    private LassoKind _lassoKind = LassoKind.Freehand;
    public LassoKind LassoKind { get => _lassoKind; set { if (SetField(ref _lassoKind, value)) CancelLasso(); } }

    private WandSettings _wandSettings = new();
    public WandSettings WandSettings { get => _wandSettings; set => SetField(ref _wandSettings, value); }

    private bool _selectionAntialiased = true;
    public bool SelectionAntialiased { get => _selectionAntialiased; set => SetField(ref _selectionAntialiased, value); }

    private bool _autoSelect;
    /// <summary>A Move-tool press picks the layer under the pointer (Ctrl does it once without this on).</summary>
    public bool AutoSelect { get => _autoSelect; set => SetField(ref _autoSelect, value); }

    private bool _showsTransformControls = true;
    public bool ShowsTransformControls { get => _showsTransformControls; set { if (SetField(ref _showsTransformControls, value)) Invalidate(); } }

    private bool _locksTransformRatio = true;
    public bool LocksTransformRatio { get => _locksTransformRatio; set => SetField(ref _locksTransformRatio, value); }

    // MARK: View options

    private bool _showsRulers;
    public bool ShowsRulers { get => _showsRulers; set => SetField(ref _showsRulers, value); }
    private bool _showsGrid;
    public bool ShowsGrid { get => _showsGrid; set { if (SetField(ref _showsGrid, value)) Invalidate(); } }
    private bool _showsGuides = true;
    public bool ShowsGuides { get => _showsGuides; set { if (SetField(ref _showsGuides, value)) Invalidate(); } }
    private bool _showsPixelGrid = true;
    public bool ShowsPixelGrid { get => _showsPixelGrid; set { if (SetField(ref _showsPixelGrid, value)) Invalidate(); } }
    private bool _snapEnabled = true;
    public bool SnapEnabled { get => _snapEnabled; set { if (SetField(ref _snapEnabled, value)) { SnapGuides = (Array.Empty<double>(), Array.Empty<double>()); Invalidate(); } } }
    private bool _snapToGuides = true;
    public bool SnapToGuides { get => _snapToGuides; set => SetField(ref _snapToGuides, value); }
    private bool _snapToGrid;
    public bool SnapToGrid { get => _snapToGrid; set => SetField(ref _snapToGrid, value); }
    private bool _snapToLayers = true;
    public bool SnapToLayers { get => _snapToLayers; set => SetField(ref _snapToLayers, value); }
    private bool _snapToDocumentBounds = true;
    public bool SnapToDocumentBounds { get => _snapToDocumentBounds; set => SetField(ref _snapToDocumentBounds, value); }
    private bool _locksGuides;
    public bool LocksGuides { get => _locksGuides; set => SetField(ref _locksGuides, value); }
    private bool _canvasOnly;
    /// <summary>F: the canvas alone on black, with every panel put away.</summary>
    public bool CanvasOnly { get => _canvasOnly; set { if (SetField(ref _canvasOnly, value)) Invalidate(); } }

    private int _gridSpacing = 64, _gridSubdivisions = 8;
    public int GridSpacing { get => _gridSpacing; set { if (SetField(ref _gridSpacing, Math.Clamp(value, 2, 4096))) Invalidate(); } }
    public int GridSubdivisions { get => _gridSubdivisions; set { if (SetField(ref _gridSubdivisions, Math.Clamp(value, 1, Math.Min(64, GridSpacing)))) Invalidate(); } }

    /// <summary>Document positions a move has just snapped to, drawn as lines while it lasts.</summary>
    public (IReadOnlyList<double> Xs, IReadOnlyList<double> Ys) SnapGuides { get; set; } = (Array.Empty<double>(), Array.Empty<double>());

    // MARK: Creating and closing

    /// <summary>
    /// File › New: a canvas with one layer. <paramref name="background"/> fills it (named "Background", as in
    /// Photoshop); null leaves it transparent.
    /// </summary>
    public void CreateDocument(int width, int height, double resolution = 72, PaletteColor? background = null)
    {
        if (!CanvasDocument.ValidDimension(width) || !CanvasDocument.ValidDimension(height)) return;
        CommitTransform();
        ClearProject();
        var document = new CanvasDocument
        {
            Width = width,
            Height = height,
            Resolution = resolution is >= 1 and <= 9600 ? resolution : 72,
        };
        Layer layer;
        if (background is { } color)
        {
            var bytes = new byte[width * height * 4];
            byte r = color.R8, g = color.G8, b = color.B8;
            for (int i = 0; i < bytes.Length; i += 4) { bytes[i] = r; bytes[i + 1] = g; bytes[i + 2] = b; bytes[i + 3] = 255; }
            string name = L.T("背景", "Background");
            layer = Layer.FromImage(RasterImage.FromRgba(bytes, width, height, name), PointD.Zero, name);
        }
        else
        {
            layer = Layer.Blank(L.T("图层 1", "Layer 1"), document.Size);
        }
        Document = document with { Layers = ImmutableList.Create(layer) };
        ActiveLayerId = layer.Id;
        History.Reset();
        Viewport.Fit(Document.Size);
    }

    /// <summary>Installs a loaded project. Called only after the whole package validated and loaded.</summary>
    public void InstallProject(IO.ProjectSnapshot snapshot, string? path)
    {
        ClearProject();
        Document = snapshot.Document;
        ActiveLayerId = snapshot.ActiveLayerId ?? snapshot.Document.Layers.LastOrDefault()?.Id;
        ProjectPath = path;
        History.Reset();
        Viewport.Fit(Document.Size);
    }

    /// <summary>
    /// The project changed on disk: the document is replaced in place, keeping the view, the folders' open state and the
    /// layer selection where those layers still exist. History starts over. Mirrors reloadProject.
    /// </summary>
    public void ReloadProject(IO.ProjectSnapshot snapshot)
    {
        if (ProjectPath is not { } path) return;
        var viewport = Viewport.Clone();
        var collapsed = CollapsedGroupIds;
        var active = ActiveLayerId;
        var selected = SelectedLayerIds;
        InstallProject(snapshot, path);
        Viewport.CopyFrom(viewport);
        var ids = snapshot.Document.Layers.Select(l => l.Id).ToHashSet();
        CollapsedGroupIds = collapsed.Where(ids.Contains).ToImmutableHashSet();
        if (active is { } a && ids.Contains(a))
            SelectLayers(selected.Where(ids.Contains).Append(a), a);
    }

    /// <summary>A document read from another format (a Photoshop file): opened untitled, named after the file.</summary>
    public void InstallDocument(CanvasDocument document, string name)
    {
        ClearProject();
        Document = document;
        DocumentName = name;
        ActiveLayerId = document.Layers.LastOrDefault(l => !l.IsGroup)?.Id ?? document.Layers.LastOrDefault()?.Id;
        History.Reset();
        Viewport.Fit(document.Size);
        OnPropertyChanged(nameof(Title));
    }

    public IO.ProjectSnapshot? ProjectSnapshot() => Document is { } doc ? new IO.ProjectSnapshot(doc, ActiveLayerId) : null;

    public void ClearProject()
    {
        CancelBrush();
        TransformEdit = null;
        CropRect = null;
        LassoDraft = null;
        CollapsedGroupIds = ImmutableHashSet<Guid>.Empty;
        IsMaskSelected = false;
        Document = null;
        ActiveLayerId = null;
        RenamingLayerId = null;
        ProjectPath = null;
        DocumentName = null;
        History.Reset();
    }

    // MARK: Viewport

    public void Fit() { if (Document is { } d) Viewport.Fit(d.Size); }
    public void ZoomTo(double value, PointD? anchor = null) { if (Document is { } d) Viewport.SetZoom(value, anchor ?? Viewport.Center, d.Size); }
    public void ZoomKeyboard(int step)
    {
        if (Document is not { } d || step == 0) return;
        double target = Viewport.KeyboardZoomTarget(step);
        if (target != Viewport.Zoom) Viewport.SetZoom(target, Viewport.Center, d.Size);
    }

    public PointD ToDocument(PointD view) => Document is { } d ? Viewport.DocumentPoint(view, d.Size) : view;
    public PointD ToView(PointD document) => Document is { } d ? Viewport.ViewPoint(document, d.Size) : document;

    // MARK: Naming

    /// <summary>"图层 n" ("Layer n") with the first n not already taken.</summary>
    private string NextName(string prefix)
    {
        var names = Document?.Layers.Select(l => l.Name).ToHashSet() ?? new HashSet<string>();
        int number = 1;
        while (names.Contains($"{prefix} {number}")) number++;
        return $"{prefix} {number}";
    }
}

/// <summary>
/// Undo step names: stored in English like the Mac app (history keeps them that way whatever the interface language), and
/// shown in the interface language — in English, as stored.
/// </summary>
public static class EditNames
{
    private static readonly Dictionary<string, string> Names = new()
    {
        ["New Canvas"] = L.T("新建画布", "New Canvas"), ["Import Image"] = L.T("导入图像", "Import Image"), ["Import Images"] = L.T("导入图像", "Import Images"),
        ["New Blank Layer"] = L.T("新建图层", "New Blank Layer"), ["New Folder"] = L.T("新建文件夹", "New Folder"), ["Delete Layer"] = L.T("删除图层", "Delete Layer"), ["Delete Layers"] = L.T("删除图层", "Delete Layers"),
        ["Rename Layer"] = L.T("重命名图层", "Rename Layer"), ["Hide Layer"] = L.T("隐藏图层", "Hide Layer"), ["Show Layer"] = L.T("显示图层", "Show Layer"), ["Reorder Layers"] = L.T("调整图层顺序", "Reorder Layers"),
        ["Move Layer"] = L.T("移动图层", "Move Layer"), ["Group Layers"] = L.T("编组图层", "Group Layers"), ["Ungroup Layers"] = L.T("取消编组", "Ungroup Layers"), ["Duplicate Layer"] = L.T("复制图层", "Duplicate Layer"),
        ["Duplicate Layers"] = L.T("复制图层", "Duplicate Layers"), ["Merge Down"] = L.T("向下合并", "Merge Down"), ["Merge Layers"] = L.T("合并图层", "Merge Layers"), ["Merge Group"] = L.T("合并组", "Merge Group"),
        ["Layer Opacity"] = L.T("图层不透明度", "Layer Opacity"), ["Layer Blend Mode"] = L.T("混合模式", "Layer Blend Mode"), ["Transform Layer"] = L.T("变换图层", "Transform Layer"), ["Transform Layers"] = L.T("变换图层", "Transform Layers"),
        ["Transform Layer Mask"] = L.T("变换蒙版", "Transform Layer Mask"), ["Transform Selection"] = L.T("变换选区", "Transform Selection"), ["Move Pixels"] = L.T("移动像素", "Move Pixels"), ["Duplicate Pixels"] = L.T("复制像素", "Duplicate Pixels"),
        ["Flip Horizontal"] = L.T("水平翻转", "Flip Horizontal"), ["Flip Vertical"] = L.T("垂直翻转", "Flip Vertical"), ["Flip Canvas Horizontal"] = L.T("水平翻转画布", "Flip Canvas Horizontal"),
        ["Flip Canvas Vertical"] = L.T("垂直翻转画布", "Flip Canvas Vertical"), ["Rotate Canvas 90° Clockwise"] = L.T("顺时针旋转画布 90°", "Rotate Canvas 90° Clockwise"),
        ["Rotate Canvas 90° Counterclockwise"] = L.T("逆时针旋转画布 90°", "Rotate Canvas 90° Counterclockwise"), ["Canvas Size"] = L.T("画布大小", "Canvas Size"), ["Image Size"] = L.T("图像大小", "Image Size"),
        ["Crop"] = L.T("裁剪", "Crop"), ["Brush Stroke"] = L.T("画笔", "Brush Stroke"), ["Erase"] = L.T("橡皮擦", "Erase"), ["Paint Mask"] = L.T("绘制蒙版", "Paint Mask"),
        ["Fill"] = L.T("填充", "Fill"), ["Clear Pixels"] = L.T("清除像素", "Clear Pixels"), ["Invert"] = L.T("反相", "Invert"), ["Invert Mask"] = L.T("反相蒙版", "Invert Mask"),
        ["Add Reveal-All Mask"] = L.T("添加蒙版", "Add Reveal-All Mask"), ["Add Hide-All Mask"] = L.T("添加隐藏蒙版", "Add Hide-All Mask"), ["Reveal Selection"] = L.T("显示选区", "Reveal Selection"),
        ["Hide Selection"] = L.T("隐藏选区", "Hide Selection"), ["Delete Layer Mask"] = L.T("删除蒙版", "Delete Layer Mask"), ["Disable Layer Mask"] = L.T("停用蒙版", "Disable Layer Mask"),
        ["Enable Layer Mask"] = L.T("启用蒙版", "Enable Layer Mask"), ["Apply Layer Mask"] = L.T("应用蒙版", "Apply Layer Mask"), ["Link Layer Mask"] = L.T("链接蒙版", "Link Layer Mask"), ["Unlink Layer Mask"] = L.T("取消链接蒙版", "Unlink Layer Mask"),
        ["Create Clipping Mask"] = L.T("创建剪贴蒙版", "Create Clipping Mask"), ["Release Clipping Mask"] = L.T("释放剪贴蒙版", "Release Clipping Mask"),
        ["Select All"] = L.T("全选", "Select All"), ["Deselect"] = L.T("取消选择", "Deselect"), ["Inverse"] = L.T("反选", "Inverse"), ["Rectangular Marquee"] = L.T("矩形选框", "Rectangular Marquee"),
        ["Elliptical Marquee"] = L.T("椭圆选框", "Elliptical Marquee"), ["Lasso"] = L.T("套索", "Lasso"), ["Polygonal Lasso"] = L.T("多边形套索", "Polygonal Lasso"), ["Magic Wand"] = L.T("魔棒", "Magic Wand"),
        ["Move Selection"] = L.T("移动选区", "Move Selection"), ["Expand Selection"] = L.T("扩展选区", "Expand Selection"), ["Contract Selection"] = L.T("收缩选区", "Contract Selection"),
        ["Feather Selection"] = L.T("羽化选区", "Feather Selection"), ["Load Selection"] = L.T("载入选区", "Load Selection"), ["Paste"] = L.T("粘贴", "Paste"), ["Cut"] = L.T("剪切", "Cut"),
        ["Layer via Copy"] = L.T("通过拷贝的图层", "Layer via Copy"), ["Layer via Cut"] = L.T("通过剪切的图层", "Layer via Cut"), ["Paste Layer"] = L.T("粘贴图层", "Paste Layer"),
        ["New Guide"] = L.T("新建参考线", "New Guide"), ["Move Guide"] = L.T("移动参考线", "Move Guide"), ["Delete Guide"] = L.T("删除参考线", "Delete Guide"), ["Clear Guides"] = L.T("清除参考线", "Clear Guides"),
        ["Move Out of Folder"] = L.T("移出文件夹", "Move Out of Folder"),
        ["Clear Layer Style"] = L.T("清除图层样式", "Clear Layer Style"), ["Paste Layer Style"] = L.T("粘贴图层样式", "Paste Layer Style"),
        ["Gradient"] = L.T("渐变", "Gradient"), ["Gradient Mask"] = L.T("渐变蒙版", "Gradient Mask"), ["Rectangle"] = L.T("矩形", "Rectangle"), ["Ellipse"] = L.T("椭圆", "Ellipse"), ["Line"] = L.T("直线", "Line"),
        ["New Text Layer"] = L.T("新建文字图层", "New Text Layer"), ["Edit Text"] = L.T("编辑文字", "Edit Text"), ["Fill Text"] = L.T("填充文字", "Fill Text"),
        ["Spot Healing"] = L.T("污点修复", "Spot Healing"), ["Clone Stamp"] = L.T("仿制图章", "Clone Stamp"), ["Blur"] = L.T("模糊", "Blur"), ["Liquify"] = L.T("液化", "Liquify"), ["Smudge"] = L.T("涂抹", "Smudge"),
        ["Show Layer Effects"] = L.T("显示图层效果", "Show Layer Effects"), ["Hide Layer Effects"] = L.T("隐藏图层效果", "Hide Layer Effects"),
        ["Color Range"] = L.T("色彩范围", "Color Range"), ["Trim"] = L.T("裁切", "Trim"), ["Remove Tool"] = L.T("抹除", "Remove Tool"), ["Select Subject"] = L.T("选择主体", "Select Subject"), ["Object Selection"] = L.T("对象选择", "Object Selection"), ["Remove Background"] = L.T("移除背景", "Remove Background"), ["Distort"] = L.T("扭曲", "Distort"), ["Distort Layers"] = L.T("扭曲图层", "Distort Layers"), ["Distort Layer Mask"] = L.T("扭曲蒙版", "Distort Layer Mask"),
    };

    private static readonly string[] EffectVerbs = { "Add ", "Edit ", "Hide ", "Show ", "Remove " };

    private static string EffectVerb(string prefix) => prefix switch
    {
        "Add " => L.T("添加", "Add "),
        "Edit " => L.T("编辑", "Edit "),
        "Hide " => L.T("隐藏", "Hide "),
        "Show " => L.T("显示", "Show "),
        _ => L.T("删除", "Remove "),
    };

    public static string Translate(string name)
    {
        if (Names.TryGetValue(name, out var shown)) return shown;
        if (Adjustments.AdjustmentKinds.Parse(name) is { } kind) return kind.ToDisplayName();
        if (Filters.FilterKinds.Parse(name) is { } filter) return Filters.FilterKinds.ToDisplayName(filter);
        foreach (var prefix in EffectVerbs)
            if (name.StartsWith(prefix) && LayerEffectKinds.Parse(name[prefix.Length..]) is { } effect)
                return EffectVerb(prefix) + effect.ToDisplayName();
        if (name.StartsWith("New ") && name.EndsWith(" Adjustment") && Adjustments.AdjustmentKinds.Parse(name[4..^11]) is { } added)
            return L.T($"新建{added.ToDisplayName()}调整图层", name);
        if (name.StartsWith("Edit ") && name.EndsWith(" Adjustment") && Adjustments.AdjustmentKinds.Parse(name[5..^11]) is { } edited)
            return L.T($"编辑{edited.ToDisplayName()}", name);
        return name;
    }
}

/// <summary>Crop frame geometry. Mirrors CropGeometry in Crop.swift.</summary>
public static class CropGeometry
{
    public static RectD Snapped(RectD rect)
    {
        double x = Math.Round(rect.Left, MidpointRounding.AwayFromZero), y = Math.Round(rect.Top, MidpointRounding.AwayFromZero);
        return new RectD(x, y, Math.Max(1, Math.Round(rect.Right, MidpointRounding.AwayFromZero) - x),
                         Math.Max(1, Math.Round(rect.Bottom, MidpointRounding.AwayFromZero) - y));
    }

    public static bool Valid(RectD rect) =>
        !rect.IsNull && double.IsFinite(rect.X) && double.IsFinite(rect.Y) && double.IsFinite(rect.Width) && double.IsFinite(rect.Height)
        && rect.Width >= 1 && rect.Width <= DocumentLimits.MaxSide && rect.Height >= 1 && rect.Height <= DocumentLimits.MaxSide
        && Math.Abs(rect.X) <= 1_000_000 && Math.Abs(rect.Y) <= 1_000_000;

    public static RectD Create(PointD start, PointD end, double? ratio, bool symmetric = false)
    {
        double dx = end.X - start.X, dy = end.Y - start.Y;
        if (ratio is { } r)
        {
            if (Math.Abs(dx) > Math.Abs(dy) * r) dy = (dy < 0 ? -1 : 1) * Math.Abs(dx) / r;
            else dx = (dx < 0 ? -1 : 1) * Math.Abs(dy) * r;
        }
        if (symmetric) return Snapped(new RectD(start.X - Math.Abs(dx), start.Y - Math.Abs(dy), Math.Abs(dx) * 2, Math.Abs(dy) * 2));
        return Snapped(new RectD(Math.Min(start.X, start.X + dx), Math.Min(start.Y, start.Y + dy), Math.Abs(dx), Math.Abs(dy)));
    }
}
