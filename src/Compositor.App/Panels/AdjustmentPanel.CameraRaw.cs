using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Compositor.App.Controls;
using Compositor.Core.Adjustments;
using Compositor.Core.CameraRaw;
using Compositor.Core.Editing;
using Compositor.Core.Geometry;
using Compositor.Core.Localization;
using Compositor.Core.Model;

namespace Compositor.App.Panels;

/// <summary>
/// Filter ▸ Camera Raw Filter's panel: the histogram (or vectorscope) with its clipping warnings and the color under
/// the pointer, then Basic, Curve, Detail, Color Mixer (with Point Color), Color Grading, Optics, Geometry (with Guided
/// Upright), Effects and Calibration, each a collapsible section with an eye that previews it off. Alt-dragging a Light
/// slider shows what clips; Alt-dragging Masking shows the sharpening mask; the targeted adjustment tools drag on the
/// image itself. Mirrors CameraRawControls.swift and the panels beside it.
/// </summary>
public sealed partial class AdjustmentPanel
{
    private readonly List<(SliderRow Row, Func<CameraRawSettings, double> Get)> _rawRows = new();
    private readonly List<Action> _rawRefreshers = new();
    private readonly List<ToggleButton> _gestureButtons = new();
    private CameraRawView _rawView = new();
    private CameraRawScopeView? _scopeView;
    private TextBlock? _readout;
    private bool _rawHooked;
    private int _pointIndex;

    private CameraRawSettings Raw => _filterSettings.CameraRaw;

    /// <summary>The label column of Camera Raw's rows: wider in English, whose longest label is "Vignetting Midpoint".</summary>
    private static double RawLabelWidth => L.IsEnglish ? 128 : 96;

    private void SetRaw(Func<CameraRawSettings, CameraRawSettings> change)
    {
        _filterSettings = _filterSettings with { CameraRaw = change(Raw) };
        Changed();
    }

    /// <summary>Changes what the canvas shows (never what is applied).</summary>
    private void SetView(Func<CameraRawView, CameraRawView> change)
    {
        var previous = _rawView;
        _rawView = change(_rawView);
        _session.SetCameraRawView(_rawView);
        if (previous.Hidden != _rawView.Hidden) foreach (var refresh in _rawRefreshers) refresh();
        if (_scopeView is { } scope)
        {
            scope.ShadowWarning = _rawView.ShadowWarning;
            scope.HighlightWarning = _rawView.HighlightWarning;
            scope.InvalidateVisual();
        }
    }

    /// <summary>A slider bound to one Camera Raw value.</summary>
    private SliderRow RawSlider(Panel target, string label, double min, double max, double @default,
                                Func<CameraRawSettings, double> get, Func<CameraRawSettings, double, CameraRawSettings> set,
                                int decimals = 0, string unit = "", Brush? track = null)
    {
        var row = new SliderRow(label, min, max, get(Raw), @default, decimals, unit, track: track, labelWidth: RawLabelWidth) { Width = 440 };
        row.ValueChanged += v => SetRaw(s => set(s, v));
        target.Children.Add(row);
        _rawRows.Add((row, get));
        return row;
    }

    /// <summary>Alt held as a slider's drag starts: the canvas shows <paramref name="on"/>'s view until it ends.</summary>
    private void AltView(SliderRow row, Func<CameraRawView, CameraRawView> on, Func<CameraRawView, CameraRawView> off)
    {
        bool active = false;
        row.PreviewMouseLeftButtonDown += (_, _) =>
        {
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return;
            active = true;
            SetView(on);
        };
        void End()
        {
            if (!active) return;
            active = false;
            SetView(off);
        }
        row.PreviewMouseLeftButtonUp += (_, _) => End();
        row.LostMouseCapture += (_, _) => End();
    }

    private void ClipView(SliderRow row, int clipping)
    {
        row.ToolTip = clipping == 1
            ? L.T("按住 Alt 拖动：只显示修剪到白的地方", "Alt-drag to show only the areas clipped to white.")
            : L.T("按住 Alt 拖动：只显示修剪到黑的地方", "Alt-drag to show only the areas clipped to black.");
        AltView(row, v => v with { Clipping = clipping }, v => v with { Clipping = 0 });
    }

    /// <summary>A section with its eye, which previews the section's adjustments off while closed.</summary>
    private StackPanel Section(Panel host, string title, CameraRawSection section, bool expanded = false)
    {
        var content = new StackPanel { Margin = new Thickness(4, 6, 0, 4) };
        var eye = new Button
        {
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontWeight = FontWeights.Normal, FontSize = 13,
            Style = (Style)FindResource("FlatButton"), Padding = new Thickness(5, 1, 5, 1), Focusable = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        void Update()
        {
            bool hidden = _rawView.Hidden.HasFlag(section);
            eye.Content = hidden ? "" : "";
            eye.ToolTip = hidden
                ? L.T("显示这一部分的调整", "Show this section’s adjustments.")
                : L.T("暂时隐藏这一部分的调整（只影响预览）", "Hide this section’s adjustments for now (affects the preview only).");
            eye.Visibility = hidden || Raw.Adjusts(section) ? Visibility.Visible : Visibility.Hidden;
        }
        eye.Click += (_, e) =>
        {
            SetView(v => v with { Hidden = v.Hidden ^ section });
            Update();
            e.Handled = true;
        };
        var header = new DockPanel { Width = 404, LastChildFill = true };
        DockPanel.SetDock(eye, Dock.Right);
        header.Children.Add(eye);
        header.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
        var expander = new Expander { Header = header, IsExpanded = expanded, Content = content, Margin = new Thickness(0, 0, 0, 6), FontWeight = FontWeights.SemiBold };
        content.SetValue(System.Windows.Documents.TextElement.FontWeightProperty, FontWeights.Normal);
        expander.SetResourceReference(ForegroundProperty, "TextBrush");
        host.Children.Add(expander);
        _rawRefreshers.Add(Update);
        Update();
        return content;
    }

    private static TextBlock Subhead(string text)
    {
        var block = new TextBlock { Text = text, Margin = new Thickness(0, 6, 0, 6), FontWeight = FontWeights.SemiBold };
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextDimBrush");
        return block;
    }

    private static Brush Ramp(params Color[] colors) => Theme.Gradient(colors);
    private static readonly Color Gray = Color.FromRgb(128, 128, 128);

    // MARK: Canvas tools

    /// <summary>A panel tool's drag on the canvas, through delegates.</summary>
    private sealed class PanelGesture : ICanvasGesture
    {
        public Action<PointD, PointD>? OnBegin, OnDrag, OnEnd;
        public Func<IReadOnlyList<(PointD Start, PointD End)>>? GetLines;
        public bool Crosshair { get; init; }
        public void Begin(PointD document, PointD view) => OnBegin?.Invoke(document, view);
        public void Drag(PointD document, PointD view) => OnDrag?.Invoke(document, view);
        public void End(PointD document, PointD view) => OnEnd?.Invoke(document, view);
        public IReadOnlyList<(PointD Start, PointD End)> Lines => GetLines?.Invoke() ?? Array.Empty<(PointD, PointD)>();
    }

    /// <summary>A toggle arming a canvas tool; one tool or eyedropper at a time.</summary>
    private ToggleButton GestureToggle(string text, string tip, ICanvasGesture gesture, Action<bool>? armed = null)
    {
        var button = new ToggleButton
        {
            Content = text, ToolTip = tip, Style = (Style)FindResource("FlatButton"),
            Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 4, 0), BorderThickness = new Thickness(1),
        };
        button.SetResourceReference(BorderBrushProperty, "BorderBrush");
        button.Checked += (_, _) =>
        {
            foreach (var other in _samplers) other.IsChecked = false;
            foreach (var other in _gestureButtons) if (!ReferenceEquals(other, button)) other.IsChecked = false;
            _session.CanvasGesture = gesture;
            button.Background = (Brush)FindResource("SelectionBrush");
            armed?.Invoke(true);
        };
        button.Unchecked += (_, _) =>
        {
            if (ReferenceEquals(_session.CanvasGesture, gesture)) _session.CanvasGesture = null;
            button.ClearValue(BackgroundProperty);
            armed?.Invoke(false);
        };
        _gestureButtons.Add(button);
        return button;
    }

    /// <summary>The targeted adjustment tool: pressing on the image picks what to change there, dragging up or down changes it.</summary>
    private ToggleButton TargetedTool(string tip, Func<(double Red, double Green, double Blue), Action<double>?> start)
    {
        Action<double>? apply = null;
        double startY = 0;
        var gesture = new PanelGesture
        {
            OnBegin = (doc, view) =>
            {
                apply = _session.CameraRawColorAt(doc) is { } color ? start(color) : null;
                if (apply is null) System.Media.SystemSounds.Beep.Play();
                startY = view.Y;
            },
            OnDrag = (_, view) => apply?.Invoke((startY - view.Y) * 0.5),
            OnEnd = (_, _) => apply = null,
        };
        return GestureToggle(L.T("目标调整", "Targeted Adjustment"), tip, gesture);
    }

    // MARK: Building

    private void BuildCameraRaw()
    {
        _rawRows.Clear();
        _rawRefreshers.Clear();
        _gestureButtons.Clear();
        _session.CanvasGesture = null;
        _rawView = new CameraRawView();
        _session.SetCameraRawView(_rawView);
        _pointIndex = 0;
        if (!CameraRawTables.Available)
            _body.Children.Add(Note(L.T("找不到 Camera Raw 的颜色表，基本面板的光线和颜色滑块将不起作用。",
                "Camera Raw’s color tables can’t be found, so the light and color sliders in Basic won’t have any effect.")));
        BuildRawScope();
        // A steady frame: opening and closing sections scrolls within it rather than resizing the window.
        var sections = new StackPanel { Width = 452 };
        var scroller = new ScrollViewer
        {
            Content = sections, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Height = Math.Clamp(SystemParameters.WorkArea.Height * 0.68 - 150, 300, 640), Padding = new Thickness(0, 0, 8, 0),
        };
        _body.Children.Add(scroller);
        BuildRawBasic(Section(sections, L.T("基本", "Basic"), CameraRawSection.Basic, expanded: true));
        BuildRawCurve(Section(sections, L.T("曲线", "Curve"), CameraRawSection.Curve));
        BuildRawDetail(Section(sections, L.T("细节", "Detail"), CameraRawSection.Detail));
        BuildRawMixer(Section(sections, L.T("混色器", "Color Mixer"), CameraRawSection.Mixer));
        BuildRawGrading(Section(sections, L.T("颜色分级", "Color Grading"), CameraRawSection.Grading));
        BuildRawOptics(Section(sections, L.T("光学", "Optics"), CameraRawSection.Optics));
        BuildRawGeometry(Section(sections, L.T("几何", "Geometry"), CameraRawSection.Geometry));
        BuildRawEffects(Section(sections, L.T("效果", "Effects"), CameraRawSection.Effects));
        BuildRawCalibration(Section(sections, L.T("校准", "Calibration"), CameraRawSection.Calibration));
        _refresh = () =>
        {
            foreach (var (row, get) in _rawRows) row.Value = get(Raw);
            foreach (var refresh in _rawRefreshers) refresh();
        };
    }

    // MARK: Scope and readout

    private void BuildRawScope()
    {
        var scope = _scopeView = new CameraRawScopeView { Width = 452, Height = 112, Scope = _edit.RawScope, HorizontalAlignment = HorizontalAlignment.Left };
        scope.WarningToggled += highlights => SetView(v => highlights ? v with { HighlightWarning = !v.HighlightWarning } : v with { ShadowWarning = !v.ShadowWarning });
        var histogram = new MenuItem { Header = L.T("直方图", "Histogram"), IsCheckable = true, IsChecked = true };
        var vectorscope = new MenuItem { Header = L.T("矢量示波器", "Vectorscope"), IsCheckable = true };
        void Mode(bool vectors)
        {
            scope.Vectorscope = vectors;
            histogram.IsChecked = !vectors;
            vectorscope.IsChecked = vectors;
            scope.InvalidateVisual();
        }
        histogram.Click += (_, _) => Mode(false);
        vectorscope.Click += (_, _) => Mode(true);
        scope.ContextMenu = new ContextMenu { Items = { histogram, vectorscope } };
        _body.Children.Add(scope);
        _readout = new TextBlock { Text = " ", Margin = new Thickness(0, 4, 0, 8), HorizontalAlignment = HorizontalAlignment.Center, FontFamily = new FontFamily("Consolas, Segoe UI") };
        _readout.SetResourceReference(TextBlock.ForegroundProperty, "TextDimBrush");
        _body.Children.Add(_readout);
        if (_rawHooked) return;
        _rawHooked = true;
        _session.AdjustmentPreviewChanged += OnRawPreview;
        _session.PointerMoved += OnRawPointer;
        PreviewKeyDown += OnRawKey;
        Closed += (_, _) =>
        {
            _session.AdjustmentPreviewChanged -= OnRawPreview;
            _session.PointerMoved -= OnRawPointer;
        };
    }

    private void OnRawPreview(object? sender, EventArgs e)
    {
        if (_scopeView is null || !ReferenceEquals(_session.AdjustmentEdit, _edit)) return;
        _scopeView.Scope = _edit.RawScope;
        _scopeView.InvalidateVisual();
    }

    private void OnRawPointer(PointD? point)
    {
        if (_readout is null) return;
        _readout.Text = point is { } p && _session.CameraRawColorAt(p) is { } c
            ? $"R {c.Red * 255,3:0}    G {c.Green * 255,3:0}    B {c.Blue * 255,3:0}"
            : " ";
    }

    /// <summary>U and O turn the shadow and highlight clipping warnings on and off, as in Camera Raw.</summary>
    private void OnRawKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox || Keyboard.Modifiers != ModifierKeys.None) return;
        if (e.Key == Key.U) { SetView(v => v with { ShadowWarning = !v.ShadowWarning }); e.Handled = true; }
        else if (e.Key == Key.O) { SetView(v => v with { HighlightWarning = !v.HighlightWarning }); e.Handled = true; }
    }

    // MARK: Basic

    private void BuildRawBasic(StackPanel p)
    {
        var balance = Combo(new[] { L.T("自定", "Custom"), L.T("自动", "Auto") }, (int)Raw.WhiteBalance, 110);
        var dropper = Sampler(L.T("白平衡工具", "White Balance Tool"), L.T("单击图像中应为中性灰的地方", "Click a part of the image that should be neutral gray."), color =>
        {
            if (CameraRawTables.Neutralize(color.Red, color.Green, color.Blue) is not { } solved) return;
            balance.SelectedIndex = 0;
            SetRaw(s => s with { WhiteBalance = CameraRawWhiteBalance.Custom, Temperature = solved.Temperature, Tint = solved.Tint });
        });
        balance.SelectionChanged += (_, _) =>
        {
            if (balance.SelectedIndex == 1 && _session.CameraRawAutoBalance() is { } solved)
                SetRaw(s => s with { WhiteBalance = CameraRawWhiteBalance.Auto, Temperature = solved.Temperature, Tint = solved.Tint });
            else if (balance.SelectedIndex == 0) SetRaw(s => s with { WhiteBalance = CameraRawWhiteBalance.Custom });
        };
        p.Children.Add(Line(Caption(L.T("白平衡", "White Balance"), RawLabelWidth), balance, new Border { Width = 10 }, dropper));
        RawSlider(p, L.T("色温", "Temperature"), -100, 100, 0, s => s.Temperature, (s, v) => s with { Temperature = v, WhiteBalance = CameraRawWhiteBalance.Custom },
            track: Ramp(Color.FromRgb(70, 120, 230), Gray, Color.FromRgb(235, 200, 60)));
        RawSlider(p, L.T("色调", "Tint"), -100, 100, 0, s => s.Tint, (s, v) => s with { Tint = v, WhiteBalance = CameraRawWhiteBalance.Custom },
            track: Ramp(Color.FromRgb(70, 190, 80), Gray, Color.FromRgb(210, 70, 200)));
        p.Children.Add(Subhead(L.T("光线（按住 Alt 拖动可查看修剪）", "Light (Alt-drag to see clipping)")));
        ClipView(RawSlider(p, L.T("曝光", "Exposure"), -5, 5, 0, s => s.Exposure, (s, v) => s with { Exposure = v }, 2, track: Ramp(Colors.Black, Colors.White)), 1);
        RawSlider(p, L.T("对比度", "Contrast"), -100, 100, 0, s => s.Contrast, (s, v) => s with { Contrast = v });
        ClipView(RawSlider(p, L.T("高光", "Highlights"), -100, 100, 0, s => s.Highlights, (s, v) => s with { Highlights = v }), 1);
        ClipView(RawSlider(p, L.T("阴影", "Shadows"), -100, 100, 0, s => s.Shadows, (s, v) => s with { Shadows = v }), 2);
        ClipView(RawSlider(p, L.T("白色", "Whites"), -100, 100, 0, s => s.Whites, (s, v) => s with { Whites = v }), 1);
        ClipView(RawSlider(p, L.T("黑色", "Blacks"), -100, 100, 0, s => s.Blacks, (s, v) => s with { Blacks = v }), 2);
        p.Children.Add(Subhead(L.T("偏好", "Presence")));
        RawSlider(p, L.T("纹理", "Texture"), -100, 100, 0, s => s.Texture, (s, v) => s with { Texture = v });
        RawSlider(p, L.T("清晰度", "Clarity"), -100, 100, 0, s => s.Clarity, (s, v) => s with { Clarity = v });
        RawSlider(p, L.T("去除薄雾", "Dehaze"), -100, 100, 0, s => s.Dehaze, (s, v) => s with { Dehaze = v });
        RawSlider(p, L.T("自然饱和度", "Vibrance"), -100, 100, 0, s => s.Vibrance, (s, v) => s with { Vibrance = v }, track: Ramp(Gray, Color.FromRgb(230, 120, 60)));
        RawSlider(p, L.T("饱和度", "Saturation"), -100, 100, 0, s => s.Saturation, (s, v) => s with { Saturation = v }, track: Ramp(Gray, Color.FromRgb(220, 50, 50)));
    }

    // MARK: Curve

    private void BuildRawCurve(StackPanel p)
    {
        p.Children.Add(Subhead(L.T("参数曲线", "Parametric Curve")));
        // Pressing on the image picks the region its tone falls in; dragging up or down moves that region.
        var targeted = TargetedTool(L.T("在图像上按住并上下拖动：调整该处色调所在的参数曲线区域",
            "Press on the image and drag up or down to move the parametric curve region that tone falls in."), color =>
        {
            var curve = Raw.Curve;
            double tone = CameraRawPixels.ToneOf(color.Red, color.Green, color.Blue) * 100;
            int region = tone < curve.ShadowSplit ? 0 : tone < curve.DarkSplit ? 1 : tone < curve.LightSplit ? 2 : 3;
            double from = region switch { 0 => curve.Shadows, 1 => curve.Darks, 2 => curve.Lights, _ => curve.Highlights };
            return delta =>
            {
                double v = Math.Clamp(Math.Round(from + delta), -100, 100);
                SetRaw(s => s with
                {
                    Curve = region switch
                    {
                        0 => s.Curve with { Shadows = v }, 1 => s.Curve with { Darks = v }, 2 => s.Curve with { Lights = v }, _ => s.Curve with { Highlights = v },
                    },
                });
            };
        });
        p.Children.Add(Line(new Border { Width = RawLabelWidth }, targeted));
        RawSlider(p, L.T("高光", "Highlights"), -100, 100, 0, s => s.Curve.Highlights, (s, v) => s with { Curve = s.Curve with { Highlights = v } });
        RawSlider(p, L.T("亮调", "Lights"), -100, 100, 0, s => s.Curve.Lights, (s, v) => s with { Curve = s.Curve with { Lights = v } });
        RawSlider(p, L.T("暗调", "Darks"), -100, 100, 0, s => s.Curve.Darks, (s, v) => s with { Curve = s.Curve with { Darks = v } });
        RawSlider(p, L.T("阴影", "Shadows"), -100, 100, 0, s => s.Curve.Shadows, (s, v) => s with { Curve = s.Curve with { Shadows = v } });
        RawSlider(p, L.T("阴影分界", "Shadow Split"), 5, 90, 25, s => s.Curve.ShadowSplit, (s, v) => s with { Curve = s.Curve with { ShadowSplit = v } }, unit: "%");
        RawSlider(p, L.T("中间分界", "Midtone Split"), 7, 95, 50, s => s.Curve.DarkSplit, (s, v) => s with { Curve = s.Curve with { DarkSplit = v } }, unit: "%");
        RawSlider(p, L.T("高光分界", "Highlight Split"), 9, 98, 75, s => s.Curve.LightSplit, (s, v) => s with { Curve = s.Curve with { LightSplit = v } }, unit: "%");

        p.Children.Add(Subhead(L.T("点曲线", "Point Curve")));
        var channel = Combo(ChannelNames, 0, 90);
        var preset = Combo(new[] { L.T("预设…", "Preset…"), L.T("线性", "Linear"), L.T("中对比度", "Medium Contrast"), L.T("强对比度", "Strong Contrast") }, 0, 120);
        p.Children.Add(Line(Caption(L.T("通道", "Channel"), RawLabelWidth), channel, new Border { Width = 10 }, preset));
        var graph = new CurvesGraph { Width = 260, Height = 260, Margin = new Thickness(RawLabelWidth, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Left };
        p.Children.Add(graph);
        Color[] lineColors = { Colors.White, Color.FromRgb(240, 80, 80), Color.FromRgb(90, 210, 100), Color.FromRgb(90, 140, 250) };
        CurvePoint[] Points(CameraRawCurveSettings c) => channel.SelectedIndex switch { 1 => c.Red, 2 => c.Green, 3 => c.Blue, _ => c.Rgb };
        CameraRawCurveSettings With(CameraRawCurveSettings c, CurvePoint[] points) => channel.SelectedIndex switch
        {
            1 => c with { Red = points }, 2 => c with { Green = points }, 3 => c with { Blue = points }, _ => c with { Rgb = points },
        };
        void Show()
        {
            graph.Points = Points(Raw.Curve).Select(pt => new CurvePoint(pt.X * 255, pt.Y * 255)).ToList();
            graph.LineColor = lineColors[Math.Max(0, channel.SelectedIndex)];
            graph.InvalidateVisual();
        }
        graph.PointsChanged += points =>
        {
            var unit = points.Select(pt => new CurvePoint(pt.X / 255, pt.Y / 255)).ToArray();
            SetRaw(s => s with { Curve = With(s.Curve, unit) });
        };
        channel.SelectionChanged += (_, _) => Show();
        preset.SelectionChanged += (_, _) =>
        {
            if (preset.SelectedIndex <= 0) return;
            var points = preset.SelectedIndex switch { 2 => CameraRawCurveSettings.MediumContrast, 3 => CameraRawCurveSettings.StrongContrast, _ => CameraRawCurveSettings.Linear };
            preset.SelectedIndex = 0;
            SetRaw(s => s with { Curve = With(s.Curve, points) });
            Show();
        };
        Show();
        RawSlider(p, L.T("调整饱和度", "Refine Saturation"), -100, 100, 0, s => s.Curve.RefineSaturation, (s, v) => s with { Curve = s.Curve with { RefineSaturation = v } });
    }

    // MARK: Detail

    private void BuildRawDetail(StackPanel p)
    {
        p.Children.Add(Subhead(L.T("锐化", "Sharpening")));
        RawSlider(p, L.T("数量", "Amount"), 0, 150, 0, s => s.Detail.SharpenAmount, (s, v) => s with { Detail = s.Detail with { SharpenAmount = v } });
        RawSlider(p, L.T("半径", "Radius"), 0, 100, 10, s => s.Detail.SharpenRadius, (s, v) => s with { Detail = s.Detail with { SharpenRadius = v } });
        RawSlider(p, L.T("细节", "Detail"), 0, 100, 25, s => s.Detail.SharpenDetail, (s, v) => s with { Detail = s.Detail with { SharpenDetail = v } });
        var masking = RawSlider(p, L.T("蒙版", "Masking"), 0, 100, 0, s => s.Detail.SharpenMasking, (s, v) => s with { Detail = s.Detail with { SharpenMasking = v } });
        masking.ToolTip = L.T("按住 Alt 拖动：以灰度显示锐化蒙版（白色处被锐化）", "Alt-drag to show the sharpening mask in grayscale (white areas are sharpened).");
        AltView(masking, v => v with { SharpenMask = true }, v => v with { SharpenMask = false });
        p.Children.Add(Subhead(L.T("减少杂色", "Noise Reduction")));
        RawSlider(p, L.T("明亮度", "Luminance"), 0, 100, 0, s => s.Detail.NoiseLuminance, (s, v) => s with { Detail = s.Detail with { NoiseLuminance = v } });
        RawSlider(p, L.T("明亮度细节", "Luminance Detail"), 0, 100, 50, s => s.Detail.NoiseLuminanceDetail, (s, v) => s with { Detail = s.Detail with { NoiseLuminanceDetail = v } });
        RawSlider(p, L.T("明亮度对比", "Luminance Contrast"), 0, 100, 0, s => s.Detail.NoiseLuminanceContrast, (s, v) => s with { Detail = s.Detail with { NoiseLuminanceContrast = v } });
        RawSlider(p, L.T("颜色", "Color"), 0, 100, 0, s => s.Detail.NoiseColor, (s, v) => s with { Detail = s.Detail with { NoiseColor = v } });
        RawSlider(p, L.T("颜色细节", "Color Detail"), 0, 100, 50, s => s.Detail.NoiseColorDetail, (s, v) => s with { Detail = s.Detail with { NoiseColorDetail = v } });
        RawSlider(p, L.T("颜色平滑度", "Color Smoothness"), 0, 100, 50, s => s.Detail.NoiseColorSmoothness, (s, v) => s with { Detail = s.Detail with { NoiseColorSmoothness = v } });
    }

    // MARK: Color Mixer

    private void BuildRawMixer(StackPanel p)
    {
        var tab = Combo(new[] { L.T("色相", "Hue"), L.T("饱和度", "Saturation"), L.T("明亮度", "Luminance") }, 0, 110);
        // Pressing on the image picks the color families its hue belongs to; dragging moves them, each by its share.
        var targeted = TargetedTool(L.T("在图像上按住并上下拖动：调整该处颜色所属的颜色（按当前的色相 / 饱和度 / 明亮度）",
            "Press on the image and drag up or down to adjust the color families that color belongs to (by the current Hue / Saturation / Luminance)."), color =>
        {
            var (hue, saturation, _) = CameraRawPixels.HslOf(color.Red, color.Green, color.Blue);
            if (saturation < 0.04) return null; // gray has no hue to pick
            var weights = CameraRawMixerSettings.Weights(hue);
            double top = weights.Max();
            if (top <= 0) return null;
            int page = tab.SelectedIndex;
            var from = (double[])(page switch { 1 => Raw.Mixer.Saturation, 2 => Raw.Mixer.Luminance, _ => Raw.Mixer.Hue }).Clone();
            return delta =>
            {
                var values = from.Select((v, i) => Math.Clamp(Math.Round(v + delta * weights[i] / top), -100, 100)).ToArray();
                SetRaw(s => s with
                {
                    Mixer = page switch { 1 => s.Mixer with { Saturation = values }, 2 => s.Mixer with { Luminance = values }, _ => s.Mixer with { Hue = values } },
                });
            };
        });
        p.Children.Add(Line(Caption(L.T("调整", "Adjust"), RawLabelWidth), tab, new Border { Width = 10 }, targeted));
        var rows = new List<SliderRow>();
        double[] Values(CameraRawSettings s) => tab.SelectedIndex switch { 1 => s.Mixer.Saturation, 2 => s.Mixer.Luminance, _ => s.Mixer.Hue };
        CameraRawMixerSettings With(CameraRawMixerSettings m, int index, double value)
        {
            var values = (double[])(tab.SelectedIndex switch { 1 => m.Saturation, 2 => m.Luminance, _ => m.Hue }).Clone();
            values[index] = value;
            return tab.SelectedIndex switch { 1 => m with { Saturation = values }, 2 => m with { Luminance = values }, _ => m with { Hue = values } };
        }
        Brush Track(int i)
        {
            double center = CameraRawMixerSettings.Centers[i];
            return tab.SelectedIndex switch
            {
                1 => Ramp(Gray, Theme.FromHsb(center)),
                2 => Ramp(Colors.Black, Theme.FromHsb(center), Colors.White),
                _ => Ramp(Theme.FromHsb(center - 30), Theme.FromHsb(center), Theme.FromHsb(center + 30)),
            };
        }
        for (int i = 0; i < 8; i++)
        {
            int index = i;
            rows.Add(RawSlider(p, CameraRawMixerSettings.Names[i], -100, 100, 0, s => Values(s)[index], (s, v) => s with { Mixer = With(s.Mixer, index, v) }, track: Track(i)));
        }
        tab.SelectionChanged += (_, _) =>
        {
            for (int i = 0; i < 8; i++) { rows[i].Value = Values(Raw)[i]; rows[i].Track = Track(i); }
        };
        BuildRawPointColor(p);
    }

    // MARK: Point Color

    private static Color HslColor(double hue, double saturation, double lightness)
    {
        double c = (1 - Math.Abs(2 * lightness - 1)) * saturation, h = (hue % 360 + 360) % 360 / 60, x = c * (1 - Math.Abs(h % 2 - 1)), m = lightness - c / 2;
        var (r, g, b) = h switch { < 1 => (c, x, 0.0), < 2 => (x, c, 0.0), < 3 => (0.0, c, x), < 4 => (0.0, x, c), < 5 => (x, 0.0, c), _ => (c, 0.0, x) };
        static byte B(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);
        return Color.FromRgb(B(r + m), B(g + m), B(b + m));
    }

    /// <summary>Colors picked from the image, each shifted on its own within a reach of hue, saturation and luminance.</summary>
    private void BuildRawPointColor(StackPanel p)
    {
        p.Children.Add(Subhead(L.T("点颜色", "Point Color")));
        CameraRawPointColor? Point(CameraRawSettings s) => _pointIndex < s.Mixer.Points.Length ? s.Mixer.Points[_pointIndex] : null;
        CameraRawSettings WithPoint(CameraRawSettings s, Func<CameraRawPointColor, CameraRawPointColor> change)
        {
            if (_pointIndex >= s.Mixer.Points.Length) return s;
            var points = (CameraRawPointColor[])s.Mixer.Points.Clone();
            points[_pointIndex] = change(points[_pointIndex]);
            return s with { Mixer = s.Mixer with { Points = points } };
        }
        var visualize = new CheckBox { Content = L.T("显示范围", "Visualize Range"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
                                       ToolTip = L.T("只显示受所选颜色影响的地方，其余变暗", "Show only the areas the selected color affects, dimming the rest.") };
        void ShowRange() => SetView(v => v with { VisualizePoint = visualize.IsChecked == true && Raw.Mixer.Points.Length > 0 ? _pointIndex : -1 });
        visualize.Click += (_, _) => ShowRange();
        var pick = Sampler(L.T("吸取颜色", "Sample Color"), L.T("单击图像，添加要调整的颜色（最多 8 个）", "Click the image to add a color to adjust (up to 8)."), color =>
        {
            if (Raw.Mixer.Points.Length >= 8) { System.Media.SystemSounds.Beep.Play(); return; }
            var (hue, saturation, lightness) = CameraRawPixels.HslOf(color.Red, color.Green, color.Blue);
            _pointIndex = Raw.Mixer.Points.Length;
            SetRaw(s => s with { Mixer = s.Mixer with { Points = s.Mixer.Points.Append(new CameraRawPointColor(hue, saturation, lightness)).ToArray() } });
            ShowRange();
        }, point => _session.CameraRawColorAt(point) is { } c ? new PaletteColor(c.Red, c.Green, c.Blue) : null);
        var remove = new Button { Content = L.T("删除", "Delete"), Padding = new Thickness(10, 2, 10, 2), ToolTip = L.T("删除所选颜色", "Delete the selected color.") };
        remove.Click += (_, _) =>
        {
            if (Raw.Mixer.Points.Length == 0) return;
            int index = _pointIndex;
            _pointIndex = Math.Max(0, Math.Min(index, Raw.Mixer.Points.Length - 2));
            SetRaw(s => s with { Mixer = s.Mixer with { Points = s.Mixer.Points.Where((_, i) => i != index).ToArray() } });
            ShowRange();
        };
        p.Children.Add(Line(Caption(L.T("颜色", "Color"), RawLabelWidth), pick, remove, visualize));
        var swatches = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(RawLabelWidth, 0, 0, 10), MinHeight = 24 };
        p.Children.Add(swatches);
        var sliders = new StackPanel();
        p.Children.Add(sliders);
        RawSlider(sliders, L.T("色相偏移", "Hue Shift"), -100, 100, 0, s => Point(s)?.HueShift ?? 0, (s, v) => WithPoint(s, pt => pt with { HueShift = v }), track: Theme.Spectrum());
        RawSlider(sliders, L.T("饱和度偏移", "Saturation Shift"), -100, 100, 0, s => Point(s)?.SaturationShift ?? 0, (s, v) => WithPoint(s, pt => pt with { SaturationShift = v }),
            track: Ramp(Gray, Color.FromRgb(220, 50, 50)));
        RawSlider(sliders, L.T("明亮度偏移", "Luminance Shift"), -100, 100, 0, s => Point(s)?.LuminanceShift ?? 0, (s, v) => WithPoint(s, pt => pt with { LuminanceShift = v }),
            track: Ramp(Colors.Black, Colors.White));
        RawSlider(sliders, L.T("色相范围", "Hue Range"), 5, 180, 30, s => Point(s)?.HueRange ?? 30, (s, v) => WithPoint(s, pt => pt with { HueRange = v }), unit: "°");
        RawSlider(sliders, L.T("饱和度范围", "Saturation Range"), 5, 100, 40, s => (Point(s)?.SaturationRange ?? 0.4) * 100, (s, v) => WithPoint(s, pt => pt with { SaturationRange = v / 100 }), unit: "%");
        RawSlider(sliders, L.T("明亮度范围", "Luminance Range"), 5, 100, 40, s => (Point(s)?.LuminanceRange ?? 0.4) * 100, (s, v) => WithPoint(s, pt => pt with { LuminanceRange = v / 100 }), unit: "%");
        string shown = "";
        void Update()
        {
            var points = Raw.Mixer.Points;
            if (_pointIndex >= points.Length) _pointIndex = Math.Max(0, points.Length - 1);
            sliders.IsEnabled = remove.IsEnabled = visualize.IsEnabled = points.Length > 0;
            string key = _pointIndex + ":" + string.Join(",", points.Select(pt => $"{pt.Hue:0.#}/{pt.Saturation:0.###}/{pt.Luminance:0.###}"));
            if (key == shown) return;
            shown = key;
            swatches.Children.Clear();
            if (points.Length == 0)
            {
                var hint = new TextBlock { Text = L.T("用“吸取颜色”在图像上单击添加", "Click the image with “Sample Color” to add one"), VerticalAlignment = VerticalAlignment.Center };
                hint.SetResourceReference(TextBlock.ForegroundProperty, "TextDimBrush");
                swatches.Children.Add(hint);
                return;
            }
            for (int i = 0; i < points.Length; i++)
            {
                int index = i;
                var swatch = new Border
                {
                    Width = 24, Height = 24, Margin = new Thickness(0, 0, 6, 0), CornerRadius = new CornerRadius(4), Cursor = Cursors.Hand,
                    Background = new SolidColorBrush(HslColor(points[i].Hue, points[i].Saturation, points[i].Luminance)),
                    BorderThickness = new Thickness(i == _pointIndex ? 2.5 : 1),
                    BorderBrush = i == _pointIndex ? Brushes.White : new SolidColorBrush(Color.FromRgb(90, 90, 90)),
                    ToolTip = L.T($"颜色 {i + 1}", $"Color {i + 1}"),
                };
                swatch.MouseLeftButtonDown += (_, _) =>
                {
                    _pointIndex = index;
                    _refresh?.Invoke();
                    ShowRange();
                };
                swatches.Children.Add(swatch);
            }
        }
        _rawRefreshers.Add(Update);
        Update();
    }

    // MARK: Color Grading

    private void BuildRawGrading(StackPanel p)
    {
        var wheel = Combo(new[] { L.T("阴影", "Shadows"), L.T("中间调", "Midtones"), L.T("高光", "Highlights"), L.T("全局", "Global") }, 0, 110);
        p.Children.Add(Line(Caption(L.T("色轮", "Wheel"), RawLabelWidth), wheel));
        CameraRawGradeWheel Get(CameraRawSettings s) => wheel.SelectedIndex switch
        {
            1 => s.Grading.Midtones, 2 => s.Grading.Highlights, 3 => s.Grading.Global, _ => s.Grading.Shadows,
        };
        CameraRawGradingSettings With(CameraRawGradingSettings g, CameraRawGradeWheel w) => wheel.SelectedIndex switch
        {
            1 => g with { Midtones = w }, 2 => g with { Highlights = w }, 3 => g with { Global = w }, _ => g with { Shadows = w },
        };
        var hue = RawSlider(p, L.T("色相", "Hue"), 0, 360, 0, s => Get(s).Hue, (s, v) => s with { Grading = With(s.Grading, Get(s) with { Hue = v }) }, unit: "°", track: Theme.Spectrum());
        var saturation = RawSlider(p, L.T("饱和度", "Saturation"), 0, 100, 0, s => Get(s).Saturation, (s, v) => s with { Grading = With(s.Grading, Get(s) with { Saturation = v }) });
        var luminance = RawSlider(p, L.T("明亮度", "Luminance"), -100, 100, 0, s => Get(s).Luminance, (s, v) => s with { Grading = With(s.Grading, Get(s) with { Luminance = v }) },
            track: Ramp(Colors.Black, Colors.White));
        wheel.SelectionChanged += (_, _) =>
        {
            hue.Value = Get(Raw).Hue; saturation.Value = Get(Raw).Saturation; luminance.Value = Get(Raw).Luminance;
        };
        RawSlider(p, L.T("混合", "Blending"), 0, 100, 50, s => s.Grading.Blending, (s, v) => s with { Grading = s.Grading with { Blending = v } });
        RawSlider(p, L.T("平衡", "Balance"), -100, 100, 0, s => s.Grading.Balance, (s, v) => s with { Grading = s.Grading with { Balance = v } });
    }

    // MARK: Optics

    private CheckBox RawCheck(Panel target, string text, Func<CameraRawSettings, bool> get, Func<CameraRawSettings, bool, CameraRawSettings> set)
    {
        var box = new CheckBox { Content = text, IsChecked = get(Raw), Margin = new Thickness(RawLabelWidth, 2, 0, 8) };
        box.Click += (_, _) => SetRaw(s => set(s, box.IsChecked == true));
        target.Children.Add(box);
        return box;
    }

    private void BuildRawOptics(StackPanel p)
    {
        RawCheck(p, L.T("删除色差", "Remove Chromatic Aberration"), s => s.Optics.RemoveChromaticAberration, (s, v) => s with { Optics = s.Optics with { RemoveChromaticAberration = v } });
        RawCheck(p, L.T("使用配置文件校正（通用）", "Use Profile Corrections (Generic)"), s => s.Optics.EnableLensProfile, (s, v) => s with { Optics = s.Optics with { EnableLensProfile = v } });
        RawSlider(p, L.T("配置扭曲度", "Profile Distortion"), 0, 100, 100, s => s.Optics.ProfileDistortion, (s, v) => s with { Optics = s.Optics with { ProfileDistortion = v } });
        RawSlider(p, L.T("配置晕影", "Profile Vignetting"), 0, 100, 100, s => s.Optics.ProfileVignetting, (s, v) => s with { Optics = s.Optics with { ProfileVignetting = v } });
        p.Children.Add(Subhead(L.T("手动", "Manual")));
        RawSlider(p, L.T("扭曲度", "Distortion"), -100, 100, 0, s => s.Optics.Distortion, (s, v) => s with { Optics = s.Optics with { Distortion = v } });
        RawSlider(p, L.T("镜头晕影", "Lens Vignetting"), -100, 100, 0, s => s.Optics.VignetteAmount, (s, v) => s with { Optics = s.Optics with { VignetteAmount = v } });
        RawSlider(p, L.T("晕影中点", "Vignetting Midpoint"), 0, 100, 50, s => s.Optics.VignetteMidpoint, (s, v) => s with { Optics = s.Optics with { VignetteMidpoint = v } });
        p.Children.Add(Subhead(L.T("去边", "Defringe")));
        RawSlider(p, L.T("紫色数量", "Purple Amount"), 0, 100, 0, s => s.Optics.PurpleAmount, (s, v) => s with { Optics = s.Optics with { PurpleAmount = v } },
            track: Ramp(Gray, Color.FromRgb(160, 70, 220)));
        RawSlider(p, L.T("紫色色相起", "Purple Hue Low"), 0, 360, 270, s => s.Optics.PurpleHueLow, (s, v) => s with { Optics = s.Optics with { PurpleHueLow = v } }, unit: "°", track: Theme.Spectrum());
        RawSlider(p, L.T("紫色色相止", "Purple Hue High"), 0, 360, 310, s => s.Optics.PurpleHueHigh, (s, v) => s with { Optics = s.Optics with { PurpleHueHigh = v } }, unit: "°", track: Theme.Spectrum());
        RawSlider(p, L.T("绿色数量", "Green Amount"), 0, 100, 0, s => s.Optics.GreenAmount, (s, v) => s with { Optics = s.Optics with { GreenAmount = v } },
            track: Ramp(Gray, Color.FromRgb(80, 200, 90)));
        RawSlider(p, L.T("绿色色相起", "Green Hue Low"), 0, 360, 60, s => s.Optics.GreenHueLow, (s, v) => s with { Optics = s.Optics with { GreenHueLow = v } }, unit: "°", track: Theme.Spectrum());
        RawSlider(p, L.T("绿色色相止", "Green Hue High"), 0, 360, 120, s => s.Optics.GreenHueHigh, (s, v) => s with { Optics = s.Optics with { GreenHueHigh = v } }, unit: "°", track: Theme.Spectrum());
        p.Children.Add(Line(new Border { Width = RawLabelWidth }, Sampler(L.T("去边吸管", "Fringe Selector"),
            L.T("单击彩色边缘，把紫色或绿色的色相范围对准它", "Click a purple or green fringe to set its hue range."), color =>
        {
            double hue = CameraRawPixels.HueOf(color.Red, color.Green, color.Blue);
            SetRaw(s =>
            {
                var o = s.Optics;
                o = Math.Abs(hue - 290) < Math.Abs(hue - 90)
                    ? o with { PurpleHueLow = hue - 25, PurpleHueHigh = hue + 25, PurpleAmount = o.PurpleAmount == 0 ? 50 : o.PurpleAmount }
                    : o with { GreenHueLow = hue - 25, GreenHueHigh = hue + 25, GreenAmount = o.GreenAmount == 0 ? 50 : o.GreenAmount };
                return s with { Optics = o.Normalized };
            });
            _refresh?.Invoke();
        })));
    }

    // MARK: Geometry

    private void BuildRawGeometry(StackPanel p)
    {
        var projection = Combo(new[] { L.T("透视", "Perspective"), L.T("线性", "Rectilinear") }, (int)Raw.Geometry.Projection, 110);
        projection.SelectionChanged += (_, _) => SetRaw(s => s with { Geometry = s.Geometry with { Projection = (CameraRawProjection)Math.Max(0, projection.SelectedIndex) } });
        p.Children.Add(Line(Caption(L.T("投影", "Projection"), RawLabelWidth), projection));
        RawSlider(p, L.T("垂直", "Vertical"), -100, 100, 0, s => s.Geometry.Vertical, (s, v) => s with { Geometry = s.Geometry with { Vertical = v } });
        RawSlider(p, L.T("水平", "Horizontal"), -100, 100, 0, s => s.Geometry.Horizontal, (s, v) => s with { Geometry = s.Geometry with { Horizontal = v } });
        RawSlider(p, L.T("旋转", "Rotate"), -45, 45, 0, s => s.Geometry.Rotate, (s, v) => s with { Geometry = s.Geometry with { Rotate = v } }, 1, "°");
        RawSlider(p, L.T("长宽比", "Aspect"), -100, 100, 0, s => s.Geometry.Aspect, (s, v) => s with { Geometry = s.Geometry with { Aspect = v } });
        RawSlider(p, L.T("缩放", "Scale"), -100, 100, 0, s => s.Geometry.Scale, (s, v) => s with { Geometry = s.Geometry with { Scale = v } });
        RawSlider(p, L.T("X 轴偏移", "Offset X"), -100, 100, 0, s => s.Geometry.OffsetX, (s, v) => s with { Geometry = s.Geometry with { OffsetX = v } });
        RawSlider(p, L.T("Y 轴偏移", "Offset Y"), -100, 100, 0, s => s.Geometry.OffsetY, (s, v) => s with { Geometry = s.Geometry with { OffsetY = v } });
        RawCheck(p, L.T("限制裁剪", "Constrain Crop"), s => s.Geometry.ConstrainCrop, (s, v) => s with { Geometry = s.Geometry with { ConstrainCrop = v } });
        BuildRawGuides(p);
    }

    /// <summary>
    /// Guided Upright: lines drawn on the image along what should be plumb or level (up to four). While drawing, the
    /// canvas shows the image uncorrected so the lines go where the edges are.
    /// </summary>
    private void BuildRawGuides(StackPanel p)
    {
        p.Children.Add(Subhead(L.T("引导式校正", "Guided Upright")));
        const int MaxGuides = 4;
        PointD? draftStart = null, draftEnd = null;
        PointD startView = default;
        bool geometryWasHidden = false;
        var gesture = new PanelGesture
        {
            Crosshair = true,
            OnBegin = (doc, view) => { draftStart = draftEnd = doc; startView = view; },
            OnDrag = (doc, _) => draftEnd = doc,
            OnEnd = (doc, view) =>
            {
                var start = draftStart;
                draftStart = draftEnd = null;
                double length = Math.Sqrt(Math.Pow(view.X - startView.X, 2) + Math.Pow(view.Y - startView.Y, 2));
                if (start is not { } a || length < 8) return;
                if (Raw.Geometry.Guides.Length >= MaxGuides) { System.Media.SystemSounds.Beep.Play(); return; }
                if (_session.CameraRawLayerPoint(a) is not { } from || _session.CameraRawLayerPoint(doc) is not { } to) return;
                SetRaw(s => s with { Geometry = s.Geometry with { Guides = s.Geometry.Guides.Append(new CameraRawGuide(from.X, from.Y, to.X, to.Y)).ToArray() } });
            },
            GetLines = () =>
            {
                var lines = new List<(PointD, PointD)>();
                foreach (var g in Raw.Geometry.Guides)
                    if (_session.CameraRawDocumentPoint(new PointD(g.StartX, g.StartY)) is { } a && _session.CameraRawDocumentPoint(new PointD(g.EndX, g.EndY)) is { } b)
                        lines.Add((a, b));
                if (draftStart is { } s && draftEnd is { } e) lines.Add((s, e));
                return lines;
            },
        };
        var draw = GestureToggle(L.T("绘制参考线", "Draw Guides"),
            L.T("在图像上沿应当竖直或水平的边缘拖出参考线（最多 4 条）：两条竖线校正透视，其余拉直画面",
                "Drag guides on the image along edges that should be vertical or horizontal (up to 4). Two vertical guides correct the perspective; the others straighten the picture."),
            gesture, armed =>
        {
            // Drawn on the image as it is, then corrected once the tool is put away.
            if (armed)
            {
                geometryWasHidden = _rawView.Hidden.HasFlag(CameraRawSection.Geometry);
                SetView(v => v with { Hidden = v.Hidden | CameraRawSection.Geometry });
            }
            else if (!geometryWasHidden) SetView(v => v with { Hidden = v.Hidden & ~CameraRawSection.Geometry });
            _session.Invalidate();
        });
        var clear = new Button { Content = L.T("清除参考线", "Clear Guides"), Padding = new Thickness(10, 2, 10, 2) };
        clear.Click += (_, _) =>
        {
            SetRaw(s => s with { Geometry = s.Geometry with { Guides = Array.Empty<CameraRawGuide>() } });
            _session.Invalidate();
        };
        var count = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        count.SetResourceReference(TextBlock.ForegroundProperty, "TextDimBrush");
        p.Children.Add(Line(new Border { Width = RawLabelWidth }, draw, clear, count));
        void Update()
        {
            int n = Raw.Geometry.Guides.Length;
            count.Text = n == 0 ? "" : L.T($"{n} 条参考线", n == 1 ? "1 guide" : $"{n} guides");
            clear.IsEnabled = n > 0;
        }
        _rawRefreshers.Add(Update);
        Update();
    }

    // MARK: Effects

    private void BuildRawEffects(StackPanel p)
    {
        p.Children.Add(Subhead(L.T("颗粒", "Grain")));
        RawSlider(p, L.T("数量", "Amount"), 0, 100, 0, s => s.GrainAmount, (s, v) => s with { GrainAmount = v });
        RawSlider(p, L.T("大小", "Size"), 0, 100, 25, s => s.GrainSize, (s, v) => s with { GrainSize = v });
        RawSlider(p, L.T("粗糙度", "Roughness"), 0, 100, 50, s => s.GrainRoughness, (s, v) => s with { GrainRoughness = v });
        p.Children.Add(Subhead(L.T("裁剪后晕影", "Post Crop Vignetting")));
        // English names ("Highlight Priority") need a wider box than the Chinese ones.
        var style = Combo(Enum.GetValues<CameraRawVignetteStyle>().Select(v => v.ToDisplayName()), (int)Raw.VignetteStyle, L.IsEnglish ? 140 : 120);
        style.SelectionChanged += (_, _) => SetRaw(s => s with { VignetteStyle = (CameraRawVignetteStyle)Math.Max(0, style.SelectedIndex) });
        p.Children.Add(Line(Caption(L.T("样式", "Style"), RawLabelWidth), style));
        RawSlider(p, L.T("数量", "Amount"), -100, 100, 0, s => s.VignetteAmount, (s, v) => s with { VignetteAmount = v }, track: Ramp(Colors.Black, Gray, Colors.White));
        RawSlider(p, L.T("中点", "Midpoint"), 0, 100, 50, s => s.VignetteMidpoint, (s, v) => s with { VignetteMidpoint = v });
        RawSlider(p, L.T("圆度", "Roundness"), -100, 100, 0, s => s.VignetteRoundness, (s, v) => s with { VignetteRoundness = v });
        RawSlider(p, L.T("羽化", "Feather"), 0, 100, 50, s => s.VignetteFeather, (s, v) => s with { VignetteFeather = v });
        RawSlider(p, L.T("高光", "Highlights"), 0, 100, 0, s => s.VignetteHighlights, (s, v) => s with { VignetteHighlights = v });
        p.Children.Add(Subhead(L.T("光晕", "Glow")));
        var glowStyle = Combo(Enum.GetValues<CameraRawGlowStyle>().Select(v => v.ToDisplayName()), (int)Raw.GlowStyle, 120);
        glowStyle.SelectionChanged += (_, _) => SetRaw(s => s with { GlowStyle = (CameraRawGlowStyle)Math.Max(0, glowStyle.SelectedIndex) });
        p.Children.Add(Line(Caption(L.T("样式", "Style"), RawLabelWidth), glowStyle));
        RawSlider(p, L.T("数量", "Amount"), 0, 100, 0, s => s.Glow, (s, v) => s with { Glow = v });
        RawSlider(p, L.T("范围", "Range"), -100, 100, 0, s => s.GlowRange, (s, v) => s with { GlowRange = v });
        RawSlider(p, L.T("扩散", "Spread"), -100, 100, 0, s => s.GlowSpread, (s, v) => s with { GlowSpread = v });
        RawSlider(p, L.T("暖色", "Warmth"), -100, 100, 0, s => s.GlowWarmth, (s, v) => s with { GlowWarmth = v }, track: Ramp(Color.FromRgb(70, 120, 230), Gray, Color.FromRgb(235, 170, 60)));
    }

    // MARK: Calibration

    private void BuildRawCalibration(StackPanel p)
    {
        var process = Combo(Enumerable.Range(1, 6).Select(v => L.T($"版本 {v}", $"Version {v}") + (v == 6 ? L.T("（当前）", " (Current)") : "")),
            Raw.Calibration.Process - 1, L.IsEnglish ? 150 : 130);
        process.SelectionChanged += (_, _) => SetRaw(s => s with { Calibration = s.Calibration with { Process = process.SelectedIndex + 1 } });
        p.Children.Add(Line(Caption(L.T("处理版本", "Process"), RawLabelWidth), process));
        RawSlider(p, L.T("阴影色调", "Shadow Tint"), -100, 100, 0, s => s.Calibration.ShadowTint, (s, v) => s with { Calibration = s.Calibration with { ShadowTint = v } },
            track: Ramp(Color.FromRgb(70, 190, 80), Gray, Color.FromRgb(210, 70, 200)));
        p.Children.Add(Subhead(L.T("红原色", "Red Primary")));
        RawSlider(p, L.T("色相", "Hue"), -100, 100, 0, s => s.Calibration.RedHue, (s, v) => s with { Calibration = s.Calibration with { RedHue = v } },
            track: Ramp(Color.FromRgb(230, 40, 120), Color.FromRgb(230, 40, 40), Color.FromRgb(230, 120, 40)));
        RawSlider(p, L.T("饱和度", "Saturation"), -100, 100, 0, s => s.Calibration.RedSaturation, (s, v) => s with { Calibration = s.Calibration with { RedSaturation = v } },
            track: Ramp(Gray, Color.FromRgb(230, 40, 40)));
        p.Children.Add(Subhead(L.T("绿原色", "Green Primary")));
        RawSlider(p, L.T("色相", "Hue"), -100, 100, 0, s => s.Calibration.GreenHue, (s, v) => s with { Calibration = s.Calibration with { GreenHue = v } },
            track: Ramp(Color.FromRgb(180, 220, 40), Color.FromRgb(40, 200, 60), Color.FromRgb(40, 200, 170)));
        RawSlider(p, L.T("饱和度", "Saturation"), -100, 100, 0, s => s.Calibration.GreenSaturation, (s, v) => s with { Calibration = s.Calibration with { GreenSaturation = v } },
            track: Ramp(Gray, Color.FromRgb(40, 200, 60)));
        p.Children.Add(Subhead(L.T("蓝原色", "Blue Primary")));
        RawSlider(p, L.T("色相", "Hue"), -100, 100, 0, s => s.Calibration.BlueHue, (s, v) => s with { Calibration = s.Calibration with { BlueHue = v } },
            track: Ramp(Color.FromRgb(40, 170, 230), Color.FromRgb(50, 70, 230), Color.FromRgb(140, 50, 230)));
        RawSlider(p, L.T("饱和度", "Saturation"), -100, 100, 0, s => s.Calibration.BlueSaturation, (s, v) => s with { Calibration = s.Calibration with { BlueSaturation = v } },
            track: Ramp(Gray, Color.FromRgb(50, 70, 230)));
    }
}
