using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Compositor.App.Controls;
using Compositor.App.Drawing;
using Compositor.App.Services;
using Compositor.Core.Editing;
using Compositor.Core.Inpainting;
using Compositor.Core.Localization;
using Compositor.Core.Pixels;
using SkiaSharp;

namespace Compositor.App.Dialogs;

/// <summary>
/// Edit ▸ Content-Aware Fill's workspace, as in Photoshop: the picture with its sampling area in green beside a live
/// preview of the fill. The sampling brush adds or removes sampling area; the fill brush and AI object picking change
/// what gets filled; automatic rebuilding stretches smooth surroundings and synthesizes textured ones, with the neural
/// model rebuilding structure first. OK puts the fill on a new layer or on the layer itself.
/// </summary>
public sealed class ContentFillWindow : Window
{
    private enum BrushTool { Sample, Fill, Object }

    // Kept from one opening to the next, as Photoshop does.
    private static ContentFillSettings _settings = new();
    private static bool _newLayer = true, _showOverlay = true;
    private static double _overlayOpacity = 0.45, _brushSize = 60, _grow = 8;
    private static FillSampling _lastMode = FillSampling.Auto;

    private readonly EditorSession _session;
    private readonly ContentFillJob _job;
    private readonly int _w, _h;
    private byte[] _hole, _sample;
    private FillSampling _mode;
    private BrushTool _tool = BrushTool.Sample;
    private bool _subtract;
    private readonly List<(byte[] Hole, byte[] Sample, FillSampling Mode)> _undo = new(), _redo = new();

    private readonly FillViewport _viewport = new();
    private readonly FillView _left, _right;
    private readonly SKImage _picture;
    private readonly SKBitmap _overlay;
    private SKImage? _resultImage;
    private byte[]? _result;
    private int _version, _resultVersion = -1;
    private CancellationTokenSource? _cancel;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private SKPoint? _last;
    private bool _strokeChanged, _picking, _committing;

    private readonly List<(ToggleButton Button, BrushTool Tool)> _tools = new();
    private readonly List<(ToggleButton Button, FillSampling Mode)> _modes = new();
    private readonly ToggleButton _subtractButton, _addButton;
    private readonly Slider _sizeSlider;
    private readonly TextBlock _sizeLabel = new() { Width = 56, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _undoButton, _redoButton;
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    private readonly TextBlock _zoomLabel = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
    private readonly TextBlock _description = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly CheckBox _preview = new() { Content = L.T("预览", "Preview"), IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _ok;
    private readonly bool _hasGenerator = InpaintModels.FindGenerative() is not null;

    /// <param name="generate">Opened as Edit ▸ Generative Fill: Texture to start with, AI generation one pick away.</param>
    public ContentFillWindow(Window owner, EditorSession session, ContentFillJob job, bool generate = false)
    {
        _session = session;
        _job = job;
        // AI generation holds gigabytes of graphics memory, so the workspace never opens with it: Generative Fill starts
        // on Texture (with the inpainting model), and generation loads only when it's picked from the list.
        if (generate) _settings = _settings with { Method = FillMethod.Texture };
        else if (_settings.Method == FillMethod.Generate) _settings = _settings with { Method = FillMethod.Auto };
        _w = job.Width;
        _h = job.Height;
        _hole = (byte[])job.Hole.Clone();
        _mode = _lastMode == FillSampling.Custom ? FillSampling.Auto : _lastMode;
        _sample = SampleFor(_mode);
        Owner = owner;
        Title = generate ? L.T("创造式生成", "Generative Fill") : L.T("内容识别填充", "Content-Aware Fill");
        SetResourceReference(StyleProperty, "DarkWindow");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        var area = SystemParameters.WorkArea;
        Width = Math.Min(1500, area.Width * 0.9);
        Height = Math.Min(1000, area.Height * 0.9);
        MinWidth = 900;
        MinHeight = 600;
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        InputMethod.SetIsInputMethodEnabled(this, false);

        var info = new SKImageInfo(_w, _h, SKColorType.Rgba8888, SKAlphaType.Premul);
        _picture = SKImage.FromPixelCopy(info, job.Pixels) ?? throw new InvalidOperationException(L.T("无法显示图层。", "Can't display the layer."));
        _overlay = new SKBitmap(new SKImageInfo(_w, _h, SKColorType.Alpha8, SKAlphaType.Premul));
        _left = new FillView(_viewport) { Paints = true, Image = _picture, Overlay = _overlay };
        _right = new FillView(_viewport) { Image = _picture };
        _left.Stroke += OnStroke;
        _viewport.Changed += () => _zoomLabel.Text = L.T($"{_viewport.Zoom * 100:0}% 视图", $"Zoom: {_viewport.Zoom * 100:0}%");

        // Row 1: the tool, add or remove, size, undo; fit and 100%.
        _subtractButton = Toggle(L.T("减去 −", "Subtract −"), L.T("从区域中去掉（按住 Alt 临时切换）", "Remove from the area (hold Alt to switch for a moment)"));
        _addButton = Toggle(L.T("增加 +", "Add +"), L.T("添加到区域（按住 Alt 临时切换）", "Add to the area (hold Alt to switch for a moment)"));
        _subtractButton.Click += (_, _) => SetSubtract(true);
        _addButton.Click += (_, _) => SetSubtract(false);
        _sizeSlider = new Slider { Minimum = 1, Maximum = 500, Value = _brushSize, Width = 160, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
        _sizeSlider.ValueChanged += (_, _) => SetBrushSize(_sizeSlider.Value);
        _undoButton = IconButton("↶", L.T("还原 (Ctrl+Z)", "Undo (Ctrl+Z)"), Undo);
        _redoButton = IconButton("↷", L.T("重做 (Ctrl+Shift+Z)", "Redo (Ctrl+Shift+Z)"), Redo);
        var tools = Segments(
            (Toggle(L.T("取样画笔", "Sampling Brush"), L.T("在左侧图像上涂抹，增减可以取样的区域（绿色）", "Paint on the left image to add to or take away from the area that can be sampled (green)")), BrushTool.Sample),
            (Toggle(L.T("填充画笔", "Fill Brush"), L.T("在左侧图像上涂抹，增减要填充的区域（虚线框内）", "Paint on the left image to add to or take away from the area to fill (inside the dashed outline)")), BrushTool.Fill),
            (Toggle(L.T("AI 选择对象", "Select Object (AI)"), L.T("单击左侧图像中的对象，用 AI 识别它并作为填充区域（Shift 添加 · Alt 减去）", "Click an object in the left image: AI detects it and makes it the area to fill (Shift adds · Alt subtracts)")), BrushTool.Object));
        foreach (ToggleButton b in tools.Children) _tools.Add((b, _tools.Count switch { 0 => BrushTool.Sample, 1 => BrushTool.Fill, _ => BrushTool.Object }));
        foreach (var (button, tool) in _tools) button.Click += (_, _) => SetTool(tool);
        var addRemove = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 0, 0, 0) };
        addRemove.Children.Add(_subtractButton);
        addRemove.Children.Add(_addButton);
        var row1 = new DockPanel { Margin = new Thickness(0, 0, 0, 10), LastChildFill = false };
        var row1Left = Line(Caption(L.T("工具", "Tool"), CaptionWidth), tools, addRemove, Caption(L.T("大小", "Size"), 44, 18), _sizeSlider, _sizeLabel, _undoButton, _redoButton);
        row1.Children.Add(row1Left);
        var fit = new Button { Content = L.T("适合窗口", "Fit on Screen"), Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(6, 0, 0, 0) };
        fit.Click += (_, _) => Fit();
        var actual = new Button { Content = "100%", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(6, 0, 0, 0) };
        actual.Click += (_, _) => _viewport.Set(1, _viewport.CenterX, _viewport.CenterY);
        var row1Right = Line(fit, actual);
        DockPanel.SetDock(row1Right, Dock.Right);
        row1.Children.Insert(0, row1Right);

        // Row 2: the sampling area, its overlay, how far a picked object grows; the hints.
        var modes = Segments(
            (Toggle(L.T("自动", "Auto"), L.T("填充区域周围、与它大小相称的一圈", "A band around the fill area, in proportion to its size")), FillSampling.Auto),
            (Toggle(L.T("全部", "All"), L.T("图层上填充区域以外的全部像素", "Every pixel on the layer outside the fill area")), FillSampling.All),
            (Toggle(L.T("自定义", "Custom"), L.T("用取样画笔涂出来的区域", "The area painted with the Sampling Brush")), FillSampling.Custom));
        foreach (ToggleButton b in modes.Children) _modes.Add((b, _modes.Count switch { 0 => FillSampling.Auto, 1 => FillSampling.All, _ => FillSampling.Custom }));
        foreach (var (button, mode) in _modes) button.Click += (_, _) => SetMode(mode);
        var showOverlay = new CheckBox { Content = L.T("显示绿色取样区域", "Show Sampling Area"), IsChecked = _showOverlay, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 8, 0) };
        var opacity = new Slider { Minimum = 0.1, Maximum = 0.9, Value = _overlayOpacity, Width = 110, VerticalAlignment = VerticalAlignment.Center };
        showOverlay.Click += (_, _) => { _showOverlay = showOverlay.IsChecked == true; _left.ShowsOverlay = _showOverlay; _left.InvalidateVisual(); };
        opacity.ValueChanged += (_, _) => { _overlayOpacity = opacity.Value; _left.OverlayAlpha = (byte)(_overlayOpacity * 255); _left.InvalidateVisual(); };
        var grow = new NumberField(L.T("对象扩展", "Expand Object"), 56) { Minimum = 0, Maximum = 60, Value = _grow, Suffix = " px", ToolTip = L.T("AI 选择对象时向外多扩几个像素，盖住对象边缘残留的颜色", "How many pixels Select Object (AI) grows the object by, to cover the color left around its edge") };
        grow.ValueChanged += v => _grow = v;
        var row2 = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        row2.Children.Add(Line(Caption(L.T("取样区域", "Sampling Area"), CaptionWidth), modes, showOverlay, opacity, new Border { Width = 18 }, grow));
        // The hints fill what's left at the right; in a narrow window they're cut short rather than the controls.
        var hint = new TextBlock
        {
            Text = L.T("Alt 临时切换增减 · [ ] 调大小 · 滚轮缩放 · 空格拖动", "Alt: switch add/subtract · [ ]: size · Wheel: zoom · Space: pan"),
            VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(12, 0, 0, 0),
        };
        hint.ToolTip = hint.Text;
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextDimBrush");
        row2.Children.Add(hint);

        // The two views.
        var views = new Grid();
        views.ColumnDefinitions.Add(new ColumnDefinition());
        views.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        views.ColumnDefinitions.Add(new ColumnDefinition());
        views.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        views.RowDefinitions.Add(new RowDefinition());
        var leftTitle = Header(L.T("原图与取样区域", "Original and Sampling Area"));
        var rightTitle = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(_preview, Dock.Right);
        rightTitle.Children.Add(_preview);
        var rightHeader = new StackPanel { Orientation = Orientation.Horizontal };
        rightHeader.Children.Add(Header(L.T("填充预览", "Fill Preview"), margin: 0));
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextDimBrush");
        rightHeader.Children.Add(_status);
        rightTitle.Children.Add(rightHeader);
        Grid.SetColumn(rightTitle, 2);
        views.Children.Add(leftTitle);
        views.Children.Add(rightTitle);
        var leftFrame = Frame(_left);
        var rightFrame = Frame(_right);
        Grid.SetRow(leftFrame, 1);
        Grid.SetRow(rightFrame, 1);
        Grid.SetColumn(rightFrame, 2);
        views.Children.Add(leftFrame);
        views.Children.Add(rightFrame);
        _preview.Click += (_, _) => ShowResult();

        // Below: how to rebuild, the neural model, another result, where it goes.
        // "Texture Synthesis" needs more room than the Chinese.
        var method = new ComboBox { Width = L.IsEnglish ? 160 : 130, VerticalAlignment = VerticalAlignment.Center };
        foreach (var m in Enum.GetValues<FillMethod>())
            method.Items.Add(new ComboBoxItem
            {
                Content = m.ToDisplayName(), IsEnabled = m != FillMethod.Generate || _hasGenerator,
                ToolTip = m == FillMethod.Generate && !_hasGenerator
                    ? L.T("找不到 Stable Diffusion 模型（应在程序旁的 Models\\sd-inpainting 文件夹里）", "Can't find the Stable Diffusion model (it belongs in the Models\\sd-inpainting folder next to WinCompositor).")
                    : null,
            });
        method.SelectedIndex = (int)_settings.Method;
        bool hasModel = InpaintModels.Find() is not null;
        var neural = new CheckBox
        {
            Content = L.T("神经重建", "Neural Reconstruction"), IsChecked = _settings.Neural && hasModel, IsEnabled = hasModel, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 6, 0),
            ToolTip = hasModel
                ? L.T($"用 AI 修补模型（{InpaintModels.Name}）补全结构，再用图像自身的纹理补回细节", $"The AI inpainting model ({InpaintModels.Name}) rebuilds the structure, then the image's own texture brings back the detail")
                : L.T($"找不到 AI 修补模型 {InpaintModels.LamaFile}（应在程序旁的 Models 文件夹里）", $"Can't find the AI inpainting model {InpaintModels.LamaFile} (it belongs in the Models folder next to WinCompositor)."),
        };
        neural.Click += (_, _) => { _settings = _settings with { Neural = neural.IsChecked == true }; Describe(); Changed(); };
        var neuralHint = new TextBlock { Text = L.T("AI 识别并补全结构", "AI recognizes and rebuilds the structure"), VerticalAlignment = VerticalAlignment.Center };
        neuralHint.SetResourceReference(TextBlock.ForegroundProperty, "TextDimBrush");
        var another = new Button { Content = L.T("换一个结果", "Try Another"), Padding = new Thickness(14, 3, 14, 3), Margin = new Thickness(16, 0, 0, 0), ToolTip = L.T("同样的设置，换一组随机选择", "The same settings with a different random choice") };
        another.Click += (_, _) =>
        {
            _settings = _settings with { Seed = _settings.Seed + 1 };
            if (_settings.Method == FillMethod.Generate) GenerateNow(); else Changed();
        };
        // Generative Fill paints what fits the surroundings; it takes seconds, so after the first result it waits for
        // this button instead of following every brush stroke.
        var generateButton = new Button { Content = L.T("生成", "Generate"), MinWidth = 84, Padding = new Thickness(14, 3, 14, 3), Margin = new Thickness(18, 0, 0, 0), ToolTip = L.T("用 AI 按周围内容生成填充区域", "Use AI to generate the area to fill from its surroundings") };
        generateButton.Click += (_, _) => GenerateNow();
        var generateHint = new TextBlock { Text = L.T("按周围内容生成，约 10 秒", "Generates from the surroundings in about 10 seconds"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        generateHint.SetResourceReference(TextBlock.ForegroundProperty, "TextDimBrush");
        void ShowMethod()
        {
            bool generating = _settings.Method == FillMethod.Generate;
            generateButton.Visibility = generateHint.Visibility = generating ? Visibility.Visible : Visibility.Collapsed;
            neural.Visibility = neuralHint.Visibility = generating ? Visibility.Collapsed : Visibility.Visible;
        }
        method.SelectionChanged += (_, _) =>
        {
            _settings = _settings with { Method = (FillMethod)Math.Max(0, method.SelectedIndex) };
            ShowMethod();
            Describe();
            Changed();
            // Chosen on purpose: generate straight away, rather than leave another method's preview standing.
            if (_settings.Method == FillMethod.Generate) GenerateNow();
        };
        ShowMethod();
        var newLayer = new CheckBox { Content = L.T("输出到新图层", "Output to New Layer"), IsChecked = _newLayer, VerticalAlignment = VerticalAlignment.Center };
        newLayer.Click += (_, _) => _newLayer = newLayer.IsChecked == true;
        var options = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = false };
        DockPanel.SetDock(newLayer, Dock.Right);
        options.Children.Add(newLayer);
        options.Children.Add(Line(method, neural, neuralHint, generateButton, generateHint, another));
        _description.SetResourceReference(TextBlock.ForegroundProperty, "TextDimBrush");

        var cancel = new Button { Content = L.T("取消", "Cancel"), IsCancel = true, MinWidth = 84 };
        _ok = new Button { Content = L.T("确定", "OK"), IsDefault = true, MinWidth = 84 };
        _ok.Click += async (_, _) => await Commit();
        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0), LastChildFill = false };
        footer.Children.Add(cancel);
        var right = Line(_zoomLabel, _ok);
        right.Margin = new Thickness(0);
        DockPanel.SetDock(right, Dock.Right);
        footer.Children.Insert(0, right);
        var divider = new Border { Height = 1, Margin = new Thickness(0, 12, 0, 0) };
        divider.SetResourceReference(Border.BackgroundProperty, "BorderBrush");

        var bottom = new StackPanel();
        bottom.Children.Add(options);
        bottom.Children.Add(_description);
        bottom.Children.Add(divider);
        bottom.Children.Add(footer);
        var top = new StackPanel();
        top.Children.Add(row1);
        top.Children.Add(row2);
        var root = new DockPanel { Margin = new Thickness(18, 14, 18, 14) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(views);
        Content = root;

        _left.ShowsOverlay = _showOverlay;
        _left.OverlayAlpha = (byte)(_overlayOpacity * 255);
        // Opened without a selection: the object to remove is picked first.
        SetTool(HasHole ? BrushTool.Sample : BrushTool.Object);
        SetSubtract(false);
        SetBrushSize(_brushSize);
        ShowMode();
        RefreshOverlay();
        RefreshOutline();
        Describe();
        UpdateUndoButtons();
        _debounce.Tick += (_, _) => { _debounce.Stop(); Render(); };
        Loaded += (_, _) => { Fit(); Changed(); };
        Closed += (_, _) =>
        {
            _cancel?.Cancel();
            _debounce.Stop();
            // The picture, its overlay and the fill are big: let them go now, and give the memory back.
            _left.Release();
            _right.Release();
            _picture.Dispose();
            _overlay.Dispose();
            _resultImage?.Dispose();
            _resultImage = null;
            _result = null;
            _undo.Clear();
            _redo.Clear();
            // The models (LaMa alone holds hundreds of megabytes) go with the workspace; they load again when needed.
            // Off the interface thread, since a model still finishing its last run holds on until it's done.
            Task.Run(() => { ModelIdle.ReleaseAll(); MemoryRelease.Compact(); });
        };
        PreviewKeyDown += OnKey;
        PreviewKeyUp += OnKeyUp;
    }

    // MARK: Building

    private ToggleButton Toggle(string text, string tip)
    {
        var button = new ToggleButton
        {
            Content = text, ToolTip = tip, Style = (Style)FindResource("FlatButton"), Padding = new Thickness(12, 4, 12, 4),
            BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 2, 0), Focusable = false,
        };
        button.SetResourceReference(BorderBrushProperty, "BorderBrush");
        return button;
    }

    private static StackPanel Segments<T>(params (ToggleButton Button, T Value)[] items)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (button, _) in items) panel.Children.Add(button);
        return panel;
    }

    private void Mark(ToggleButton button, bool on)
    {
        button.IsChecked = on;
        if (on) button.Background = (Brush)FindResource("SelectionBrush");
        else button.ClearValue(BackgroundProperty);
    }

    private Button IconButton(string text, string tip, Action action)
    {
        var button = new Button { Content = text, ToolTip = tip, Style = (Style)FindResource("FlatButton"), Padding = new Thickness(10, 2, 10, 2), FontSize = 15, Focusable = false };
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>The width of the two rows' leading captions (Tool, Sampling Area), so the rows line up.</summary>
    private static double CaptionWidth => L.IsEnglish ? 100 : 72;

    private static TextBlock Caption(string text, double width = 72, double left = 0)
    {
        var label = new TextBlock { Text = text, Width = width, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold, Margin = new Thickness(left, 0, 0, 0) };
        return label;
    }

    private static TextBlock Header(string text, double margin = 6)
    {
        var block = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, margin), VerticalAlignment = VerticalAlignment.Center };
        return block;
    }

    private static StackPanel Line(params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var child in children) row.Children.Add(child);
        return row;
    }

    private static Border Frame(FillView view)
    {
        var border = new Border { Child = view, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        return border;
    }

    // MARK: State

    private void SetTool(BrushTool tool)
    {
        _tool = tool;
        foreach (var (button, t) in _tools) Mark(button, t == tool);
        _subtractButton.IsEnabled = _addButton.IsEnabled = _sizeSlider.IsEnabled = tool != BrushTool.Object;
        _left.Crosshair = tool == BrushTool.Object;
        _left.InvalidateVisual();
    }

    private void SetSubtract(bool subtract)
    {
        _subtract = subtract;
        Mark(_subtractButton, subtract);
        Mark(_addButton, !subtract);
    }

    private void SetBrushSize(double size)
    {
        _brushSize = Math.Clamp(Math.Round(size), 1, 500);
        if (Math.Abs(_sizeSlider.Value - _brushSize) > 0.5) _sizeSlider.Value = _brushSize;
        _sizeLabel.Text = $"{_brushSize:0} px";
        _left.BrushRadius = _brushSize / 2;
        _left.InvalidateVisual();
    }

    private void SetMode(FillSampling mode)
    {
        if (mode == _mode) { ShowMode(); return; }
        Remember();
        _mode = mode;
        _lastMode = mode;
        if (mode != FillSampling.Custom) _sample = SampleFor(mode);
        ShowMode();
        RefreshOverlay();
        Changed();
    }

    private void ShowMode()
    {
        foreach (var (button, m) in _modes) Mark(button, m == _mode);
    }

    private byte[] SampleFor(FillSampling mode) => mode switch
    {
        FillSampling.All => FillMasks.All(_job.Pixels, _hole),
        _ => FillMasks.Auto(_job.Pixels, _hole, _w, _h),
    };

    private void RefreshOverlay()
    {
        var pixels = _overlay.GetPixelSpan();
        for (int i = 0; i < _sample.Length; i++) pixels[i] = _hole[i] != 0 ? (byte)0 : _sample[i];
        _left.OverlayVersion++;
        _left.InvalidateVisual();
    }

    private void RefreshOutline()
    {
        var outline = MagicWand.Outline(_hole, _w, _h);
        _left.Outline = outline;
        _right.Outline = outline;
        _left.InvalidateVisual();
        _right.InvalidateVisual();
    }

    private void Describe()
    {
        bool neural = _settings.Neural && InpaintModels.Find() is not null;
        _description.Text = _settings.Method switch
        {
            FillMethod.Texture => neural
                ? L.T("纹理合成：AI 先补全结构，再从取样区域里找最相似的图块覆盖，补回图像自身的纹理细节。",
                      "Texture Synthesis: AI rebuilds the structure first, then the closest-matching patches from the sampling area cover it, bringing back the image's own texture.")
                : L.T("纹理合成：从取样区域里找最相似的图块，由粗到细拼合出填充内容，适合有纹理、有重复结构的画面。",
                      "Texture Synthesis: finds the closest-matching patches in the sampling area and pieces the fill together from coarse to fine. Suits textured pictures and repeating structures."),
            FillMethod.Smooth => L.T("平滑延展：把边缘的颜色平滑地延伸进来，并加上与周围一致的颗粒，适合天空、渐变和纯色背景。",
                                     "Smooth Fill: draws the colors at the edge smoothly inward and adds grain to match the surroundings. Suits skies, gradients and plain backgrounds."),
            FillMethod.Generate => L.T("AI 生成：用 Stable Diffusion 按周围内容在填充区域里画出新内容，颜色会和周围对齐。每次约 10 秒，可以“换一个结果”多试几次。",
                                       "AI Generation: Stable Diffusion paints new content into the fill area to suit its surroundings, with the colors matched to them. Each try takes about 10 seconds; click Try Another for more."),
            _ => neural
                ? L.T("自动：周围平滑（天空、渐变、纯色）时平滑延展；有纹理时由 AI 补全结构，再用图像自身的纹理补回细节。",
                      "Auto: Smooth Fill where the surroundings are smooth (sky, gradients, plain color); where they're textured, AI rebuilds the structure and the image's own texture brings back the detail.")
                : L.T("自动：周围平滑（天空、渐变、纯色）时平滑延展；有纹理时用纹理合成。",
                      "Auto: Smooth Fill where the surroundings are smooth (sky, gradients, plain color); Texture Synthesis where they're textured."),
        };
    }

    // MARK: Undo

    private void Remember()
    {
        _undo.Add(((byte[])_hole.Clone(), (byte[])_sample.Clone(), _mode));
        // Snapshots of a big picture add up; keep fewer of them.
        int limit = (long)_w * _h > 8_000_000 ? 8 : 30;
        if (_undo.Count > limit) _undo.RemoveAt(0);
        _redo.Clear();
        UpdateUndoButtons();
    }

    private void Undo() => Step(_undo, _redo);
    private void Redo() => Step(_redo, _undo);

    private void Step(List<(byte[] Hole, byte[] Sample, FillSampling Mode)> from, List<(byte[] Hole, byte[] Sample, FillSampling Mode)> to)
    {
        if (from.Count == 0) return;
        to.Add(((byte[])_hole.Clone(), (byte[])_sample.Clone(), _mode));
        var (hole, sample, mode) = from[^1];
        from.RemoveAt(from.Count - 1);
        bool holeChanged = !hole.AsSpan().SequenceEqual(_hole);
        _hole = hole;
        _sample = sample;
        _mode = mode;
        ShowMode();
        RefreshOverlay();
        if (holeChanged) RefreshOutline();
        UpdateUndoButtons();
        Changed();
    }

    private void UpdateUndoButtons()
    {
        _undoButton.IsEnabled = _undo.Count > 0;
        _redoButton.IsEnabled = _redo.Count > 0;
    }

    // MARK: Painting

    private void OnStroke(StrokePhase phase, SKPoint point)
    {
        if (_committing) return;
        if (_tool == BrushTool.Object)
        {
            if (phase == StrokePhase.Down) _ = PickObject(point);
            return;
        }
        bool subtract = _subtract ^ Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        switch (phase)
        {
            case StrokePhase.Down:
                Remember();
                _strokeChanged = false;
                _last = point;
                if (_tool == BrushTool.Fill) { _left.LiveStroke = new SKPath(); _left.LiveStrokeRemoves = subtract; }
                Stamp(point, point, subtract);
                break;
            case StrokePhase.Move when _last is { } last:
                Stamp(last, point, subtract);
                _last = point;
                break;
            case StrokePhase.Up:
                _last = null;
                FinishStroke();
                break;
        }
    }

    /// <summary>Round brush stamps along a segment, into the sampling area or the fill area.</summary>
    private void Stamp(SKPoint from, SKPoint to, bool subtract)
    {
        double radius = _brushSize / 2;
        double length = Math.Sqrt((to.X - from.X) * (to.X - from.X) + (to.Y - from.Y) * (to.Y - from.Y));
        int steps = Math.Max(1, (int)Math.Ceiling(length / Math.Max(1, radius / 3)));
        var target = _tool == BrushTool.Fill ? _hole : _sample;
        byte value = subtract ? (byte)0 : (byte)255;
        var overlay = _tool == BrushTool.Sample ? _overlay.GetPixelSpan() : default;
        for (int s = 0; s <= steps; s++)
        {
            double cx = from.X + (to.X - from.X) * s / steps, cy = from.Y + (to.Y - from.Y) * s / steps;
            int x0 = Math.Max(0, (int)Math.Floor(cx - radius)), x1 = Math.Min(_w - 1, (int)Math.Ceiling(cx + radius));
            int y0 = Math.Max(0, (int)Math.Floor(cy - radius)), y1 = Math.Min(_h - 1, (int)Math.Ceiling(cy + radius));
            double r2 = radius * radius;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    double dx = x + 0.5 - cx, dy = y + 0.5 - cy;
                    if (dx * dx + dy * dy > r2) continue;
                    int i = y * _w + x;
                    if (target[i] == value) continue;
                    // Only usable pixels can be sampled.
                    if (_tool == BrushTool.Sample && !subtract && (_hole[i] != 0 || _job.Pixels[i * 4 + 3] != 255)) continue;
                    target[i] = value;
                    if (_tool == BrushTool.Sample) overlay[i] = value;
                    _strokeChanged = true;
                }
            _left.LiveStroke?.AddCircle((float)cx, (float)cy, (float)radius);
        }
        if (_tool == BrushTool.Sample) _left.OverlayVersion++;
        _left.InvalidateVisual();
    }

    private void FinishStroke()
    {
        _left.LiveStroke?.Dispose();
        _left.LiveStroke = null;
        if (!_strokeChanged)
        {
            // Nothing painted: no undo step for it.
            if (_undo.Count > 0) _undo.RemoveAt(_undo.Count - 1);
            UpdateUndoButtons();
            _left.InvalidateVisual();
            return;
        }
        if (_tool == BrushTool.Sample)
        {
            // Painting the sampling area makes it the user's own.
            _mode = FillSampling.Custom;
            ShowMode();
        }
        else HoleChanged();
        Changed();
    }

    /// <summary>The fill area changed: its outline, and an automatic sampling area to suit it.</summary>
    private void HoleChanged()
    {
        if (_mode != FillSampling.Custom) _sample = SampleFor(_mode);
        else for (int i = 0; i < _sample.Length; i++) if (_hole[i] != 0) _sample[i] = 0;
        RefreshOverlay();
        RefreshOutline();
    }

    /// <summary>AI 选择对象: the object under the click, found by the subject model, grown a little, as the fill area.</summary>
    private async Task PickObject(SKPoint point)
    {
        if (_picking) return;
        int x = (int)Math.Floor(point.X), y = (int)Math.Floor(point.Y);
        if (x < 0 || y < 0 || x >= _w || y >= _h) return;
        var model = SubjectModels.Current();
        if (model is null)
        {
            if (new SubjectModelDialog { Owner = this }.ShowDialog() != true || (model = SubjectModels.Current()) is null) return;
        }
        var modifiers = Keyboard.Modifiers;
        _picking = true;
        _left.Busy = L.T("正在识别对象…", "Detecting the object…");
        _left.InvalidateVisual();
        Mouse.OverrideCursor = Cursors.AppStarting;
        try
        {
            var pixels = _job.Pixels;
            int w = _w, h = _h;
            double grow = _grow;
            var region = await Task.Run(() =>
            {
                var found = EditorSession.ObjectRegion(model, pixels, w, h, x, y);
                return found is null ? null : FillMasks.Grow(found, w, h, grow);
            });
            if (region is null)
            {
                _status.Text = L.T("这里没有识别到对象，换个位置单击试试", "No object found here. Try clicking somewhere else.");
                System.Media.SystemSounds.Beep.Play();
                return;
            }
            Remember();
            if (modifiers.HasFlag(ModifierKeys.Alt)) for (int i = 0; i < region.Length; i++) { if (region[i] != 0) _hole[i] = 0; }
            else if (modifiers.HasFlag(ModifierKeys.Shift)) for (int i = 0; i < region.Length; i++) { if (region[i] != 0) _hole[i] = 255; }
            else _hole = region;
            if (_hole.AsSpan().IndexOfAnyExcept((byte)0) < 0)
            {
                // Taking everything away leaves nothing to fill: put it back.
                Undo();
                _redo.Clear();
                UpdateUndoButtons();
                return;
            }
            HoleChanged();
            Changed();
        }
        catch (Exception error) { MessageDialog.Show(this, error.Message, L.T("AI 选择对象", "Select Object (AI)"), MessageKind.Warning); }
        finally
        {
            _picking = false;
            _left.Busy = null;
            _left.InvalidateVisual();
            Mouse.OverrideCursor = null;
        }
    }

    // MARK: Preview

    /// <summary>Something changed: a new fill is worked out shortly after (a burst of changes makes one).</summary>
    private bool HasHole => _hole.AsSpan().IndexOfAnyExcept((byte)0) >= 0;

    private void Changed()
    {
        _ok.IsEnabled = HasHole && !_committing;
        if (!HasHole)
        {
            _version++;
            _cancel?.Cancel();
            _debounce.Stop();
            _result = null;
            _resultVersion = -1;
            _right.Busy = null;
            _status.Text = L.T("单击左侧图像中要移除的对象（AI 选择对象），或用填充画笔涂出要填充的区域", "Click the object to remove in the left image, or paint the area to fill with the Fill Brush");
            ShowResult();
            return;
        }
        _version++;
        _cancel?.Cancel();
        _debounce.Stop();
        if (_settings.Method == FillMethod.Generate && _result is not null)
        {
            // Generating takes seconds: once there's a result, it waits for the button rather than following every change.
            _right.Busy = null;
            _status.Text = L.T("填充区域或取样区域已更改，点击“生成”更新预览", "The fill or sampling area has changed. Click Generate to update the preview.");
            _right.InvalidateVisual();
            return;
        }
        _debounce.Start();
        _right.Busy = _settings.Method == FillMethod.Generate ? L.T("AI 生成中…", "Generating…") : L.T("正在计算…", "Working…");
        _right.InvalidateVisual();
    }

    /// <summary>Generative Fill's button (and Another Result, and choosing AI generation).</summary>
    private void GenerateNow()
    {
        if (!HasHole || _committing) return;
        _version++;
        _cancel?.Cancel();
        _debounce.Stop();
        Render();
    }

    private void Render()
    {
        var cancel = _cancel = new CancellationTokenSource();
        int version = _version;
        var hole = (byte[])_hole.Clone();
        var sample = (byte[])_sample.Clone();
        var settings = _settings;
        var model = settings.Neural ? InpaintModels.Current() : null;
        var generator = settings.Method == FillMethod.Generate ? InpaintModels.Generative() : null;
        var job = _job;
        IProgress<double>? progress = null;
        if (generator is not null)
        {
            _right.Busy = L.T("AI 生成中…（首次要先载入模型）", "Generating… (the first run loads the model)");
            _status.Text = L.T("正在生成…", "Generating…");
            _right.InvalidateVisual();
            progress = new Progress<double>(p =>
            {
                if (version != _version || cancel.IsCancellationRequested) return;
                _right.Busy = L.T($"AI 生成中… {p:P0}", $"Generating… {p:P0}");
                _right.InvalidateVisual();
            });
        }
        _ = Task.Run(() => ContentFill.Run(job.Pixels, job.Width, job.Height, hole, sample, settings, model, cancel.Token, generator, progress), cancel.Token)
            .ContinueWith(task => Dispatcher.BeginInvoke(() =>
            {
                if (cancel.IsCancellationRequested || version != _version) return;
                if (task.IsFaulted)
                {
                    _right.Busy = null;
                    _status.Text = task.Exception?.InnerException?.Message ?? L.T("无法填充", "Can't fill the area");
                    _result = null;
                    _resultVersion = -1;
                    ShowResult();
                    return;
                }
                if (task.IsCanceled) return;
                var outcome = task.Result;
                _result = outcome.Rgba;
                _resultVersion = version;
                _status.Text = outcome.Method == FillMethod.Generate
                    ? L.T("已用：AI 生成（按周围内容补全）", "Used: AI Generation (from the surroundings)")
                    : L.T("已用：", "Used: ") + outcome.Method.ToDisplayName() + (outcome.Neural ? L.T($" + AI 神经重建（{InpaintModels.Name}）", $" + Neural Reconstruction ({InpaintModels.Name})") : "");
                _right.Busy = null;
                ShowResult();
            }), TaskScheduler.Default);
    }

    private void ShowResult()
    {
        _resultImage?.Dispose();
        _resultImage = null;
        if (_preview.IsChecked == true && _result is { } result)
            _resultImage = SKImage.FromPixelCopy(new SKImageInfo(_w, _h, SKColorType.Rgba8888, SKAlphaType.Premul), result);
        _right.Image = _resultImage ?? _picture;
        _right.InvalidateVisual();
    }

    private void Fit()
    {
        double zoom = Math.Min(_left.FitZoom, _right.FitZoom);
        _viewport.Set(zoom > 0 ? zoom : 1, _w / 2.0, _h / 2.0);
    }

    // MARK: Keys

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control), shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (key)
        {
            case Key.OemOpenBrackets: SetBrushSize(_brushSize / 1.2); e.Handled = true; break;
            case Key.OemCloseBrackets: SetBrushSize(_brushSize * 1.2); e.Handled = true; break;
            case Key.Space:
                _left.SpaceHeld = _right.SpaceHeld = true;
                _left.UpdateCursor(); _right.UpdateCursor();
                e.Handled = true;
                break;
            case Key.Z when ctrl: if (shift) Redo(); else Undo(); e.Handled = true; break;
            case Key.Y when ctrl: Redo(); e.Handled = true; break;
            case Key.D0 when ctrl: Fit(); e.Handled = true; break;
            case Key.D1 when ctrl: _viewport.Set(1, _viewport.CenterX, _viewport.CenterY); e.Handled = true; break;
            case Key.LeftAlt or Key.RightAlt: e.Handled = true; break; // Alt only flips add and remove here
        }
    }

    private void OnKeyUp(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Space)
        {
            _left.SpaceHeld = _right.SpaceHeld = false;
            _left.UpdateCursor(); _right.UpdateCursor();
        }
        if (key is Key.LeftAlt or Key.RightAlt) e.Handled = true;
    }

    // MARK: OK

    private async Task Commit()
    {
        if (_committing) return;
        _committing = true;
        _ok.IsEnabled = false;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            _debounce.Stop();
            var result = _resultVersion == _version ? _result : null;
            if (result is null)
            {
                _cancel?.Cancel();
                var hole = (byte[])_hole.Clone();
                var sample = (byte[])_sample.Clone();
                var settings = _settings;
                var model = settings.Neural ? InpaintModels.Current() : null;
                var generator = settings.Method == FillMethod.Generate ? InpaintModels.Generative() : null;
                result = (await Task.Run(() => ContentFill.Run(_job.Pixels, _w, _h, hole, sample, settings, model, default, generator))).Rgba;
            }
            if (_session.CommitContentFill(_job, result, _hole, _newLayer)) DialogResult = true;
        }
        catch (Exception error)
        {
            MessageDialog.Show(this, error.Message, L.T("无法内容识别填充", "Couldn't Complete Content-Aware Fill"), MessageKind.Warning);
        }
        finally
        {
            Mouse.OverrideCursor = null;
            _committing = false;
            _ok.IsEnabled = HasHole;
        }
    }
}
