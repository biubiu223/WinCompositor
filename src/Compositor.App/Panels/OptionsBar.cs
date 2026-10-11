using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Compositor.App.Controls;
using Compositor.App.Services;
using Compositor.Core.Editing;
using Compositor.Core.Geometry;
using Compositor.Core.Localization;
using Compositor.Core.Model;
using SelectionMode = Compositor.Core.Editing.SelectionMode;
using Compositor.Core.Painting;
using Compositor.Core.Pixels;

namespace Compositor.App.Panels;

/// <summary>The bar above the canvas with the active tool's settings. Mirrors the Mac version's tool headers.</summary>
public static class OptionsBarBuilder
{
    public static FrameworkElement Build(MainWindow window, EditorSession session) => session.Tool switch
    {
        Tool.Move => new MoveOptions(window, session),
        Tool.Marquee => SelectionOptions(window, session, marquee: true),
        Tool.Lasso => SelectionOptions(window, session, marquee: false),
        Tool.Wand => WandOptions(window, session),
        Tool.Crop => new CropOptions(window, session),
        Tool.Brush or Tool.SpotHealing or Tool.CloneStamp or Tool.Smear or Tool.Remove => new BrushOptions(window, session),
        Tool.Gradient => new GradientOptions(session),
        Tool.Shape => new ShapeOptions(session),
        Tool.Type => new TypeOptions(session),
        Tool.Eyedropper => Bar(Title(L.T("吸管", "Eyedropper")), Hint(L.T("单击或拖动以吸取前景色 · 画笔工具下按住 Alt 也能吸取", "Click or drag to pick the foreground color · With a brush tool, hold Alt to pick too"))),
        Tool.Hand or Tool.Zoom => NavigationOptions(window, session),
        _ => Bar(Title(L.T("选择一个工具", "Choose a Tool")), Hint(L.T("按工具的快捷键（例如 V、M、B）即可选用", "Press a tool's shortcut key (such as V, M or B) to choose it"))),
    };

    internal static StackPanel Bar(params UIElement[] children)
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Height = 40 };
        foreach (var child in children)
        {
            if (child is FrameworkElement f && f.Margin == default) f.Margin = new Thickness(0, 0, 16, 0);
            f_Center(child);
            bar.Children.Add(child);
        }
        return bar;
    }

    private static void f_Center(UIElement child)
    {
        if (child is FrameworkElement f) f.VerticalAlignment = VerticalAlignment.Center;
    }

    internal static TextBlock Title(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 13 };
    internal static TextBlock Hint(string text) => new() { Text = text, Foreground = (Brush)Application.Current.FindResource("TextDimBrush") };
    internal static Border Divider() => new() { Width = 1, Height = 20, Background = (Brush)Application.Current.FindResource("BorderBrush"), Margin = new Thickness(0, 0, 16, 0) };

    /// <summary>A segmented choice; <paramref name="selected"/> is the index chosen.</summary>
    internal static StackPanel Segments(string[] titles, int selected, Action<int> choose, string? group = null)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        group ??= Guid.NewGuid().ToString();
        for (int i = 0; i < titles.Length; i++)
        {
            int index = i;
            var button = new RadioButton { Content = titles[i], GroupName = group, IsChecked = i == selected, Style = (Style)Application.Current.FindResource("SegmentButton") };
            button.Checked += (_, _) => choose(index);
            panel.Children.Add(button);
        }
        return panel;
    }

    internal static CheckBox Check(string title, bool value, Action<bool> set)
    {
        var box = new CheckBox { Content = title, IsChecked = value, Focusable = false };
        box.Checked += (_, _) => set(true);
        box.Unchecked += (_, _) => set(false);
        return box;
    }

    internal static Button Action(string title, Action action, string? tip = null)
    {
        var button = new Button { Content = title, ToolTip = tip, Focusable = false, Padding = new Thickness(10, 2, 10, 2) };
        button.Click += (_, _) => action();
        return button;
    }

    private static readonly string[] ModeTitles = { L.T("新选区", "New"), L.T("添加", "Add"), L.T("减去", "Subtract") };

    private static FrameworkElement SelectionModes(EditorSession session) =>
        Segments(ModeTitles, (int)session.SelectionModeChoice, i => session.SelectionModeChoice = (SelectionMode)i);

    private static FrameworkElement SelectionOptions(MainWindow window, EditorSession session, bool marquee)
    {
        var shape = marquee
            ? Segments(new[] { L.T("矩形", "Rectangle"), L.T("椭圆", "Ellipse") }, session.MarqueeKind == LassoKind.Ellipse ? 1 : 0, i => session.MarqueeKind = i == 1 ? LassoKind.Ellipse : LassoKind.Rectangle)
            : Segments(new[] { L.T("自由", "Freehand"), L.T("多边形", "Polygonal") }, session.LassoKind == LassoKind.Polygonal ? 1 : 0, i => session.LassoKind = i == 1 ? LassoKind.Polygonal : LassoKind.Freehand);
        return Bar(
            Title(marquee ? L.T("选框", "Marquee") : L.T("套索", "Lasso")), shape, Divider(), SelectionModes(session),
            Check(L.T("消除锯齿", "Anti-alias"), session.SelectionAntialiased, v => session.SelectionAntialiased = v), Divider(),
            Action(L.T("扩展…", "Expand…"), () => window.SelectionAmountDialog(L.T("扩展选区", "Expand Selection"), 1, 500, session.ExpandSelection)),
            Action(L.T("收缩…", "Contract…"), () => window.SelectionAmountDialog(L.T("收缩选区", "Contract Selection"), 1, 500, session.ContractSelection)),
            Action(L.T("羽化…", "Feather…"), () => window.SelectionAmountDialog(L.T("羽化选区", "Feather Selection"), 2, 250, session.FeatherSelection)));
    }

    private static FrameworkElement WandOptions(MainWindow window, EditorSession session)
    {
        // The segments stay short in English (as in the Mac version), so the bar still fits beside the title.
        var mode = Segments(new[] { L.T("魔棒", "Wand"), L.T("对象选择", "Object") }, session.WandMode == WandMode.Object ? 1 : 0,
            i => session.WandMode = i == 1 ? WandMode.Object : WandMode.Magic);
        if (session.WandMode == WandMode.Object)
        {
            var edge = new NumberField(L.T("边缘", "Edge"), 40) { Minimum = -10, Maximum = 10, Value = session.ObjectSelectionSettings.EdgeOffset, ToolTip = L.T("正值向内收缩选区，负值向外扩展（像素）", "Positive values tighten the selection inward; negative values expand it outward (pixels)") };
            edge.ValueChanged += v => session.ObjectSelectionSettings = session.ObjectSelectionSettings with { EdgeOffset = (int)Math.Round(v) };
            return Bar(
                Title(L.T("对象选择", "Object Selection")), mode, Divider(), SelectionModes(session), Divider(),
                Check(L.T("对所有图层取样", "Sample All Layers"), session.ObjectSelectionSettings.SampleAllLayers,
                    v => session.ObjectSelectionSettings = session.ObjectSelectionSettings with { SampleAllLayers = v }),
                edge,
                Check(L.T("消除锯齿", "Anti-alias"), session.SelectionAntialiased, v => session.SelectionAntialiased = v),
                Hint(L.T("单击对象即可选中它 · Shift 添加 · Alt 减去", "Click an object to select it · Shift adds · Alt subtracts")));
        }
        var tolerance = new NumberField(L.T("容差", "Tolerance"), 50) { Minimum = 0, Maximum = 255, Value = session.WandSettings.Tolerance };
        tolerance.ValueChanged += v => session.WandSettings = session.WandSettings with { Tolerance = (int)v };
        var sample = new ComboBox { Width = 120, Focusable = false };
        foreach (var t in new[] { L.T("取样点", "Point Sample"), L.T("3 × 3 平均", "3 by 3 Average"), L.T("5 × 5 平均", "5 by 5 Average") }) sample.Items.Add(t);
        sample.SelectedIndex = (int)session.WandSettings.SampleSize;
        sample.SelectionChanged += (_, _) => session.WandSettings = session.WandSettings with { SampleSize = (WandSampleSize)sample.SelectedIndex };
        return Bar(
            Title(L.T("魔棒", "Magic Wand")), mode, Divider(), SelectionModes(session), Divider(), tolerance, sample,
            Check(L.T("连续", "Contiguous"), session.WandSettings.Contiguous, v => session.WandSettings = session.WandSettings with { Contiguous = v }),
            Check(L.T("对所有图层取样", "Sample All Layers"), session.WandSettings.SampleAllLayers, v => session.WandSettings = session.WandSettings with { SampleAllLayers = v }),
            Check(L.T("消除锯齿", "Anti-alias"), session.SelectionAntialiased, v => session.SelectionAntialiased = v));
    }

    private static FrameworkElement NavigationOptions(MainWindow window, EditorSession session) => Bar(
        Title(session.Tool == Tool.Hand ? L.T("抓手", "Hand") : L.T("缩放", "Zoom")),
        Action(L.T("按屏幕大小", "Fit Screen"), () => session.Fit()),
        Action("100%", () => session.ZoomTo(1)),
        Action(L.T("放大", "Zoom In"), () => session.ZoomKeyboard(1)),
        Action(L.T("缩小", "Zoom Out"), () => session.ZoomKeyboard(-1)),
        Hint(session.Tool == Tool.Zoom
            ? L.T("单击放大 · Alt 单击缩小 · 左右拖动连续缩放", "Click to zoom in · Alt-click to zoom out · Drag left or right to zoom smoothly")
            : L.T("拖动以平移画布", "Drag to pan the canvas")));
}

/// <summary>The Move tool's bar: auto-select, transform controls, and exact position, size and angle.</summary>
internal sealed class MoveOptions : StackPanel, IRefreshable
{
    private readonly EditorSession _session;
    private readonly NumberField _x = new("X", 60), _y = new("Y", 60), _w = new(L.T("宽", "W"), 60), _h = new(L.T("高", "H"), 60), _angle = new(L.T("角度", "Angle"), 48);
    private readonly StackPanel _fields;
    private readonly StackPanel _confirm;

    public MoveOptions(MainWindow window, EditorSession session)
    {
        _session = session;
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        Height = 40;
        _angle.Suffix = "°";
        _angle.Format = "0.#";
        _angle.Minimum = -360;
        _angle.Maximum = 360;
        foreach (var f in new[] { _w, _h }) { f.Minimum = 1; f.Maximum = 300_000; }
        foreach (var f in new[] { _x, _y }) { f.Minimum = -1_000_000; f.Maximum = 1_000_000; }
        _x.ValueChanged += v => Apply(t => t with { Origin = new PointD(v, t.Origin.Y) });
        _y.ValueChanged += v => Apply(t => t with { Origin = new PointD(t.Origin.X, v) });
        _w.ValueChanged += v => Apply(t =>
        {
            double h = session.LocksTransformRatio ? t.Size.Height * v / t.Size.Width : t.Size.Height;
            return t with { Size = new SizeD(v, h), Origin = new PointD(t.Center.X - v / 2, t.Center.Y - h / 2) };
        });
        _h.ValueChanged += v => Apply(t =>
        {
            double w = session.LocksTransformRatio ? t.Size.Width * v / t.Size.Height : t.Size.Width;
            return t with { Size = new SizeD(w, v), Origin = new PointD(t.Center.X - w / 2, t.Center.Y - v / 2) };
        });
        _angle.ValueChanged += v => Apply(t => t with { Rotation = v });

        Children.Add(Spaced(OptionsBarBuilder.Title(L.T("移动", "Move"))));
        Children.Add(Spaced(OptionsBarBuilder.Check(L.T("自动选择", "Auto-Select"), session.AutoSelect, v => session.AutoSelect = v)));
        Children.Add(Spaced(OptionsBarBuilder.Check(L.T("显示变换控件", "Show Transform Controls"), session.ShowsTransformControls, v => session.ShowsTransformControls = v)));
        Children.Add(OptionsBarBuilder.Divider());
        _fields = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var f in new UIElement[] { _x, _y, _w, _h }) _fields.Children.Add(Spaced((FrameworkElement)f, 10));
        _fields.Children.Add(Spaced(OptionsBarBuilder.Check(L.T("锁定比例", "Lock Ratio"), session.LocksTransformRatio, v => session.LocksTransformRatio = v), 10));
        _fields.Children.Add(Spaced(_angle, 16));
        Children.Add(_fields);
        // Short labels in English (as in the Mac version), so the whole bar still fits; the full name is the tooltip.
        var flipH = OptionsBarBuilder.Action(L.T("水平翻转", "Flip H"), () => session.FlipLayers(true), L.IsEnglish ? "Flip Horizontal" : null);
        var flipV = OptionsBarBuilder.Action(L.T("垂直翻转", "Flip V"), () => session.FlipLayers(false), L.IsEnglish ? "Flip Vertical" : null);
        _fields.Children.Add(Spaced(flipH, 6));
        _fields.Children.Add(Spaced(flipV, 16));
        _confirm = new StackPanel { Orientation = Orientation.Horizontal };
        var apply = OptionsBarBuilder.Action(L.T("✓ 应用", "✓ Apply"), () => session.CommitTransform(), L.T("回车", "Enter"));
        var cancel = OptionsBarBuilder.Action(L.T("✕ 取消", "✕ Cancel"), () => session.CancelTransform(), "Esc");
        _confirm.Children.Add(Spaced(apply, 6));
        _confirm.Children.Add(cancel);
        Children.Add(_confirm);
        Refresh();
    }

    private static FrameworkElement Spaced(FrameworkElement element, double right = 16)
    {
        element.Margin = new Thickness(0, 0, right, 0);
        element.VerticalAlignment = VerticalAlignment.Center;
        return element;
    }

    private void Apply(Func<LayerTransform, LayerTransform> change)
    {
        var session = _session;
        bool editing = session.TransformEdit is not null;
        if (!editing) session.BeginTransform(persistent: false);
        if (session.TransformEdit is not { } edit) return;
        var next = change(edit.Draft);
        if (next.IsValid) session.PreviewTransform(next);
        if (!editing) session.CommitTransform();
        Refresh();
    }

    public void Refresh()
    {
        var transform = _session.EditedTransform();
        _fields.IsEnabled = transform is not null && (_session.CanTransform || _session.TransformEdit is not null);
        if (transform is { } t)
        {
            _x.Value = Math.Round(t.Origin.X, 1);
            _y.Value = Math.Round(t.Origin.Y, 1);
            _w.Value = Math.Round(t.Size.Width, 1);
            _h.Value = Math.Round(t.Size.Height, 1);
            _angle.Value = Math.Round(t.Rotation, 1);
        }
        _confirm.Visibility = _session.TransformEdit is { Persistent: true } ? Visibility.Visible : Visibility.Collapsed;
    }
}

/// <summary>The Crop tool's bar: ratio, size, apply and cancel.</summary>
internal sealed class CropOptions : StackPanel, IRefreshable
{
    private readonly EditorSession _session;
    private readonly TextBlock _size = OptionsBarBuilder.Hint("");

    public CropOptions(MainWindow window, EditorSession session)
    {
        _session = session;
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        Height = 40;
        // The ratios are the session's own names (it reads them back), so each item shows a translated name and keeps
        // the session's name as its tag.
        var ratio = new ComboBox { Width = 120, Focusable = false };
        foreach (var r in EditorSession.CropRatios)
        {
            var item = new ComboBoxItem { Content = CropRatioName(r), Tag = r };
            ratio.Items.Add(item);
            if (r == session.CropRatioChoice) ratio.SelectedItem = item;
        }
        ratio.SelectionChanged += (_, _) => { if (ratio.SelectedItem is ComboBoxItem { Tag: string choice }) session.CropRatioChoice = choice; };
        var ratioLabel = new TextBlock { Text = L.T("比例", "Ratio"), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        foreach (var child in new FrameworkElement[]
        {
            OptionsBarBuilder.Title(L.T("裁剪", "Crop")), ratioLabel, ratio, _size,
            OptionsBarBuilder.Action(L.T("✓ 裁剪", "✓ Crop"), () => session.CommitCrop(), L.T("回车", "Enter")),
            OptionsBarBuilder.Action(L.T("复位", "Reset"), () => { if (session.Document is { } d) session.CropRect = d.Bounds; }),
        })
        {
            child.Margin = new Thickness(0, 0, child == ratioLabel ? 0 : 14, 0);
            child.VerticalAlignment = VerticalAlignment.Center;
            Children.Add(child);
        }
        Refresh();
    }

    /// <summary>The first two ratios are words (Free, Original); the rest are numbers that read the same in any language.</summary>
    private static string CropRatioName(string ratio) =>
        ratio == EditorSession.CropRatios[0] ? L.T("自由", "Free")
        : ratio == EditorSession.CropRatios[1] ? L.T("原始比例", "Original")
        : ratio;

    public void Refresh()
    {
        _size.Text = _session.CropRect is { } r ? L.T($"{r.Width:0} × {r.Height:0} 像素", $"{r.Width:0} × {r.Height:0} px") : "";
    }
}

/// <summary>The Brush's bar: mode, size, hardness, opacity and smoothing.</summary>
internal sealed class BrushOptions : StackPanel, IRefreshable
{
    private readonly EditorSession _session;
    private readonly NumberField _size = new(L.T("大小", "Size"), 64), _hardness = new(L.T("硬度", "Hardness"), 54), _opacity = new(L.T("不透明度", "Opacity"), 54), _smoothing = new(L.T("平滑", "Smoothing"), 54);
    private readonly Slider _sizeSlider = new() { Width = 110, Minimum = 0, Maximum = 1 };
    private bool _updating;

    public BrushOptions(MainWindow window, EditorSession session)
    {
        _session = session;
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        Height = 40;
        _size.Minimum = 1; _size.Maximum = 2000; _size.Suffix = " px";
        _hardness.Minimum = 0; _hardness.Maximum = 100; _hardness.Suffix = "%";
        _opacity.Minimum = 1; _opacity.Maximum = 100; _opacity.Suffix = "%";
        _smoothing.Minimum = 0; _smoothing.Maximum = 100; _smoothing.Suffix = "%";
        _size.ValueChanged += v => session.BrushSettings = session.BrushSettings with { Diameter = v };
        _hardness.ValueChanged += v => session.BrushSettings = session.BrushSettings with { Hardness = v / 100 };
        _opacity.ValueChanged += v => session.BrushSettings = session.BrushSettings with { Opacity = v / 100 };
        _smoothing.ValueChanged += v => session.BrushSettings = session.BrushSettings with { Smoothing = v };
        // The size slider runs on a curve, so small brushes get most of its length.
        _sizeSlider.ValueChanged += (_, _) =>
        {
            if (_updating) return;
            double diameter = Math.Round(1 + Math.Pow(_sizeSlider.Value, 2.5) * 1999);
            session.BrushSettings = session.BrushSettings with { Diameter = diameter };
            Refresh();
        };
        // What sits before the brush tip's settings depends on the tool.
        var header = new List<FrameworkElement>();
        switch (session.Tool)
        {
            case Tool.SpotHealing:
                header.Add(OptionsBarBuilder.Title(L.T("污点修复画笔", "Spot Healing Brush")));
                header.Add(OptionsBarBuilder.Segments(Enum.GetValues<Compositor.Core.Pixels.SpotHealingMode>().Select(m => m.ToDisplayName()).ToArray(),
                    (int)session.SpotHealingMode, i => session.SpotHealingMode = (Compositor.Core.Pixels.SpotHealingMode)i));
                break;
            case Tool.Remove:
                header.Add(OptionsBarBuilder.Title(L.T("抹除工具", "Remove Tool")));
                bool hasInpaintModel = InpaintModels.Find() is not null;
                var neural = OptionsBarBuilder.Check(L.T("AI 神经重建", "Neural Reconstruction"), session.RemoveSettings.Neural && hasInpaintModel, v => session.RemoveSettings = session.RemoveSettings with { Neural = v });
                neural.ToolTip = hasInpaintModel
                    ? L.T($"先用 AI 修补模型（{InpaintModels.Name}）补全结构，再用纹理合成补回图像自身的细节——与内容识别填充 / 创造式生成里的“纹理合成 + 神经重建”相同",
                          $"The AI inpainting model ({InpaintModels.Name}) rebuilds the structure first, then Texture Synthesis brings back the image's own detail. The same as Texture Synthesis + Neural Reconstruction in Content-Aware Fill and Generative Fill.")
                    : L.T($"找不到 AI 修补模型 {InpaintModels.LamaFile}（应在程序旁的 Models 文件夹里）",
                          $"Can't find the AI inpainting model {InpaintModels.LamaFile} (it belongs in the Models folder next to WinCompositor).");
                neural.IsEnabled = hasInpaintModel;
                header.Add(neural);
                var snap = OptionsBarBuilder.Check(L.T("AI 识别对象", "Detect Objects (AI)"), session.RemoveSettings.SnapToObjects, v => session.RemoveSettings = session.RemoveSettings with { SnapToObjects = v });
                snap.ToolTip = L.T("涂抹覆盖了对象的大部分时，用 AI 找出整个对象的轮廓一起抹除，不必涂得很仔细",
                                   "When a stroke covers most of an object, AI finds the whole object's outline and removes all of it, so you needn't paint carefully.");
                header.Add(snap);
                // Kept short in English so the bar still fits; the checkboxes' tooltips say the rest.
                header.Add(OptionsBarBuilder.Hint(L.T("涂抹或圈住要去掉的东西，松开即用纹理合成抹除（与创造式生成里的纹理合成相同）", "Paint over or circle what to remove, then release")));
                break;
            case Tool.CloneStamp:
                header.Add(OptionsBarBuilder.Title(L.T("仿制图章", "Clone Stamp")));
                header.Add(OptionsBarBuilder.Check(L.T("对齐", "Aligned"), session.CloneSettings.Aligned, v => session.CloneSettings = session.CloneSettings with { Aligned = v }));
                header.Add(OptionsBarBuilder.Check(L.T("对所有图层取样", "Sample All Layers"), session.CloneSettings.SampleAllLayers, v => session.CloneSettings = session.CloneSettings with { SampleAllLayers = v }));
                header.Add(OptionsBarBuilder.Hint(session.CloneSource is null ? L.T("Alt 单击设置仿制源", "Alt-click to set the source") : L.T("Alt 单击更换仿制源", "Alt-click to change the source")));
                break;
            case Tool.Smear:
                header.Add(OptionsBarBuilder.Title(L.T("涂抹工具", "Smear Tool")));
                header.Add(OptionsBarBuilder.Segments(Enum.GetValues<SmearMode>().Select(m => m.ToDisplayName()).ToArray(), (int)session.SmearMode,
                    i => { session.SmearMode = (SmearMode)i; window.Dispatcher.BeginInvoke(window.RebuildOptionsNow); }));
                if (session.SmearMode == SmearMode.Blur)
                {
                    var radius = new NumberField(L.T("模糊半径", "Radius"), 64) { Minimum = 0.5, Maximum = 50, Step = 0.5, Format = "0.#", Suffix = " px", Value = session.BlurRadius };
                    radius.ValueChanged += v => session.BlurRadius = v;
                    header.Add(radius);
                }
                _opacity.Label = L.T("强度", "Strength");
                break;
            default:
                header.Add(OptionsBarBuilder.Title(session.BrushMode == BrushToolMode.Erase ? L.T("橡皮擦", "Eraser") : L.T("画笔", "Brush")));
                header.Add(OptionsBarBuilder.Segments(new[] { L.T("画笔", "Brush"), L.T("橡皮擦", "Eraser") }, session.BrushMode == BrushToolMode.Erase ? 1 : 0,
                    i => session.BrushMode = i == 1 ? BrushToolMode.Erase : BrushToolMode.Paint));
                break;
        }
        header.Add(OptionsBarBuilder.Divider());
        // The Remove tool's stroke only marks an area: hardness and opacity mean nothing to it.
        var tip = session.Tool == Tool.Remove
            ? new List<FrameworkElement> { _size, _sizeSlider, _smoothing }
            : new List<FrameworkElement> { _size, _sizeSlider, _hardness, _opacity };
        if (session.Tool is not (Tool.Smear or Tool.Remove)) tip.Add(_smoothing);
        foreach (var child in header.Concat(tip))
        {
            child.Margin = new Thickness(0, 0, child == _size ? 8 : 16, 0);
            child.VerticalAlignment = VerticalAlignment.Center;
            Children.Add(child);
        }
        Refresh();
    }

    public void Refresh()
    {
        _updating = true;
        var s = _session.BrushSettings;
        _size.Value = s.Diameter;
        _sizeSlider.Value = Math.Pow((s.Diameter - 1) / 1999, 1 / 2.5);
        _hardness.Value = Math.Round(s.Hardness * 100);
        _opacity.Value = Math.Round(s.Opacity * 100);
        _smoothing.Value = s.Smoothing;
        _updating = false;
    }
}
