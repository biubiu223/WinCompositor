using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Compositor.App.Controls;
using Compositor.Core.Adjustments;
using Compositor.Core.Editing;
using Compositor.Core.Filters;
using Compositor.Core.Geometry;
using Compositor.Core.Localization;
using Compositor.Core.Model;

namespace Compositor.App.Panels;

/// <summary>A non-modal panel editing something live, answered by OK or Cancel (Enter and Escape in the main window too).</summary>
public interface IEditPanel
{
    void Finish(bool commit, bool closing = false);
}

/// <summary>
/// The movable, non-modal panel for an Image adjustment, a filter, or an adjustment layer's settings: every change
/// previews on the canvas, OK keeps it (one undo step), Cancel or closing puts things back. The canvas stays usable
/// for zooming, panning and the panel's eyedroppers. Mirrors LevelsSheet, CurvesControls, HueSaturationSheet and
/// FilterSheet.
/// </summary>
public sealed partial class AdjustmentPanel : Window, IEditPanel
{
    private static Point? _lastPosition;

    private readonly EditorSession _session;
    private readonly AdjustmentEdit _edit;
    private readonly LayerAdjustment _settings;
    private readonly StackPanel _body = new() { Margin = new Thickness(18, 14, 18, 6) };
    private readonly CheckBox _preview = new() { Content = L.T("预览", "Preview"), IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly List<ToggleButton> _samplers = new();
    private Action? _refresh;
    private bool _finished;
    private readonly Button _okButton;

    public AdjustmentPanel(Window owner, EditorSession session)
    {
        _session = session;
        _edit = session.AdjustmentEdit ?? throw new InvalidOperationException("No adjustment is being edited.");
        _settings = _edit.Settings.Clone();
        _filterSettings = _edit.FilterSettings;
        Owner = owner;
        Title = _edit.EditsLayer
            ? L.T($"{_edit.Kind.ToDisplayName()}（调整图层）", $"{_edit.Kind.ToDisplayName()} (Adjustment Layer)")
            : _edit.Title;
        SetResourceReference(StyleProperty, "DarkWindow");
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SourceInitialized += (_, _) => Services.DarkTitleBar.Apply(this);
        InputMethod.SetIsInputMethodEnabled(this, false);

        Build();

        var ok = _okButton = new Button { Content = L.T("确定", "OK"), IsDefault = true, MinWidth = 84, Margin = new Thickness(8, 0, 0, 0) };
        var cancel = new Button { Content = L.T("取消", "Cancel"), IsCancel = true, MinWidth = 84 };
        var reset = new Button { Content = L.T("复位", "Reset"), MinWidth = 64, Margin = new Thickness(12, 0, 0, 0), ToolTip = L.T("恢复默认设置", "Restore the default settings.") };
        ok.Click += (_, _) => Finish(commit: true);
        cancel.Click += (_, _) => Finish(commit: false);
        reset.Click += (_, _) => Reset();
        _preview.Click += (_, _) => Changed();
        var footer = new DockPanel { Margin = new Thickness(18, 4, 18, 16), LastChildFill = false };
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(_preview);
        if (_edit.Filter is { } filter ? !filter.IsAutomatic() : _edit.Kind.IsEditable()) left.Children.Add(reset);
        footer.Children.Add(left);
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(cancel);
        right.Children.Add(ok);
        DockPanel.SetDock(right, Dock.Right);
        footer.Children.Add(right);
        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(_body);
        Content = root;

        Loaded += (_, _) => { Place(owner); _okButton.Focus(); };
        Closing += (_, _) => { _lastPosition = new Point(Left, Top); if (!_finished) Finish(commit: false, closing: true); };
        session.PropertyChanged += OnSessionProperty;
        Closed += (_, _) => session.PropertyChanged -= OnSessionProperty;
    }

    private void Place(Window owner)
    {
        if (_lastPosition is { } p && p.X > -10000)
        {
            Left = p.X;
            Top = p.Y;
        }
        else
        {
            // Beside the Layers panel, near the top: out of the way of the canvas's middle.
            Left = owner.Left + Math.Max(40, owner.ActualWidth - ActualWidth - 330);
            Top = owner.Top + 110;
        }
        var area = SystemParameters.WorkArea;
        Left = Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - ActualWidth));
        Top = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - ActualHeight));
    }

    private void OnSessionProperty(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // The edit ended elsewhere (another tab, the window closing): the panel goes with it.
        if (e.PropertyName == nameof(EditorSession.AdjustmentEdit) && !ReferenceEquals(_session.AdjustmentEdit, _edit) && !_finished)
        {
            _finished = true;
            Close();
        }
    }

    /// <summary>OK or Cancel, from the buttons, the keyboard, or the main window.</summary>
    public void Finish(bool commit, bool closing = false)
    {
        if (_finished) return;
        _finished = true;
        _session.CanvasSampler = null;
        if (commit)
        {
            Send();
            // Full-size work (and Remove Background still finding the subject) can take a moment.
            Mouse.OverrideCursor = Cursors.Wait;
            try { _session.CommitAdjustmentEdit(); }
            finally { Mouse.OverrideCursor = null; }
        }
        else _session.CancelAdjustmentEdit();
        if (!closing) Close();
        Owner?.Activate();
    }

    private void Changed()
    {
        Send();
        _refresh?.Invoke();
    }

    private void Send()
    {
        if (_edit.Filter is not null) _session.UpdateFilterEdit(_filterSettings, _preview.IsChecked == true);
        else _session.UpdateAdjustmentEdit(_settings, _preview.IsChecked == true);
    }

    private void Reset()
    {
        if (_edit.Filter is { } kind) ResetFilter(kind);
        var fresh = new LayerAdjustment(_edit.Kind);
        switch (_edit.Kind)
        {
            case AdjustmentKind.Levels: _settings.Levels = fresh.Levels; break;
            case AdjustmentKind.Curves: _settings.Curves = fresh.Curves; break;
            case AdjustmentKind.HueSaturation: _settings.HsvSettings = new HueSaturationSettings(); break;
            case AdjustmentKind.Exposure: _settings.ExposureSettings = new ExposureSettings(); break;
            case AdjustmentKind.GradientMap:
                _settings.GradientMapSettings = new GradientMapSettings(AdjustmentColor.From(_session.ForegroundColor), AdjustmentColor.From(_session.BackgroundColor));
                break;
            case AdjustmentKind.Grain: _settings.GrainSettings = new GrainSettings(Seed: _settings.Grain.Seed); break;
            case AdjustmentKind.BlackWhite: _settings.BlackWhiteSettings = new BlackWhiteSettings(); break;
            case AdjustmentKind.ColorBalance: _settings.ColorBalanceSettings = new ColorBalanceSettings(); break;
            case AdjustmentKind.GaussianBlur: _settings.BlurRadius = null; break;
            case AdjustmentKind.MotionBlur: _settings.MotionAngle = null; _settings.MotionDistance = null; break;
            case AdjustmentKind.AddNoise: _settings.NoiseAmount = null; _settings.NoiseGaussian = null; _settings.NoiseMonochromatic = null; break;
        }
        _body.Children.Clear();
        _samplers.Clear();
        _session.CanvasSampler = null;
        Build();
        Changed();
    }

    // MARK: Building

    private void Build()
    {
        if (_edit.Filter is { } kind) BuildFilter(kind);
        else switch (_edit.Kind)
        {
            case AdjustmentKind.Levels: BuildLevels(); break;
            case AdjustmentKind.Curves: BuildCurves(); break;
            case AdjustmentKind.HueSaturation: BuildHueSaturation(); break;
            case AdjustmentKind.Exposure: BuildExposure(); break;
            case AdjustmentKind.GradientMap: BuildGradientMap(); break;
            case AdjustmentKind.Grain: BuildGrain(); break;
            case AdjustmentKind.BlackWhite: BuildBlackWhite(); break;
            case AdjustmentKind.ColorBalance: BuildColorBalance(); break;
            case AdjustmentKind.GaussianBlur: BuildGaussianBlur(); break;
            case AdjustmentKind.MotionBlur: BuildMotionBlur(); break;
            case AdjustmentKind.AddNoise: BuildAddNoise(); break;
            default: _body.Children.Add(Note(L.T("这个调整没有可设置的选项。", "This adjustment has no options."))); break;
        }
        if (!_edit.EditsLayer && _session.Selection is not null) _body.Children.Add(Note(L.T("只作用于选区内。", "Limited to the selection.")));
    }

    private static TextBlock Note(string text)
    {
        var note = new TextBlock { Text = text, Margin = new Thickness(0, 2, 0, 8), TextWrapping = TextWrapping.Wrap, MaxWidth = 420 };
        note.SetResourceReference(TextBlock.ForegroundProperty, "TextDimBrush");
        return note;
    }

    /// <summary>The label column of the slider rows and the captions beside them: English labels run longer.</summary>
    private static double LabelWidth => L.IsEnglish ? 120 : 92;

    /// <summary>The pixels unit after a slider's field.</summary>
    private static string Px => L.T("像素", "px");

    /// <summary>
    /// A row's caption, <paramref name="width"/> (the label column) wide at least: a longer caption, as English ones can
    /// be, widens its row rather than being cut off.
    /// </summary>
    private static TextBlock Caption(string text, double? width = null)
    {
        var label = new TextBlock { Text = text, MinWidth = width ?? LabelWidth, Padding = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextDimBrush");
        return label;
    }

    private static StackPanel Line(params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        foreach (var child in children) row.Children.Add(child);
        return row;
    }

    private SliderRow Slider(string label, double min, double max, double value, double @default, Action<double> set,
                             int decimals = 0, string unit = "", bool log = false, Brush? track = null)
    {
        var row = new SliderRow(label, min, max, value, @default, decimals, unit, log, track, LabelWidth) { Width = 430 };
        row.ValueChanged += v => { set(v); Changed(); };
        _body.Children.Add(row);
        return row;
    }

    private static ComboBox Combo(IEnumerable<string> items, int selected, double width = 150)
    {
        var combo = new ComboBox { Width = width };
        foreach (var item in items) combo.Items.Add(item);
        combo.SelectedIndex = selected;
        return combo;
    }

    /// <summary>
    /// An eyedropper toggle: while on, a click on the canvas samples for <paramref name="sample"/> (from
    /// <paramref name="source"/>, or the pixels the adjustment works on).
    /// </summary>
    private ToggleButton Sampler(string text, string tip, Action<PaletteColor> sample, Func<PointD, PaletteColor?>? source = null)
    {
        var button = new ToggleButton
        {
            Content = text,
            ToolTip = tip,
            Style = (Style)FindResource("FlatButton"),
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(0, 0, 4, 0),
            BorderThickness = new Thickness(1),
        };
        button.SetResourceReference(BorderBrushProperty, "BorderBrush");
        button.Checked += (_, _) =>
        {
            foreach (var other in _samplers) if (!ReferenceEquals(other, button)) other.IsChecked = false;
            foreach (var gesture in _gestureButtons) gesture.IsChecked = false;
            _session.CanvasSampler = point =>
            {
                if ((source is null ? _session.SampleAdjustmentSource(point) : source(point)) is { } color) sample(color);
                else System.Media.SystemSounds.Beep.Play();
            };
            button.Background = (Brush)FindResource("SelectionBrush");
        };
        button.Unchecked += (_, _) =>
        {
            if (_samplers.All(b => b.IsChecked != true)) _session.CanvasSampler = null;
            button.ClearValue(BackgroundProperty);
        };
        _samplers.Add(button);
        return button;
    }

    private static readonly string[] ChannelNames = { "RGB", L.T("红", "Red"), L.T("绿", "Green"), L.T("蓝", "Blue") };
    private static readonly Color[] ChannelColors =
    {
        Color.FromRgb(150, 150, 150), Color.FromRgb(225, 70, 70), Color.FromRgb(80, 200, 90), Color.FromRgb(80, 130, 240),
    };

    // MARK: Levels

    private void BuildLevels()
    {
        var levels = _settings.Levels;
        var channel = Combo(ChannelNames, (int)levels.Channel, 120);
        var auto = Combo(new[] { L.T("自动…", "Auto…") }.Concat(Enum.GetValues<LevelsAuto>().Select(m => m.ToDisplayName())), 0, 220);
        auto.Margin = new Thickness(12, 0, 0, 0);
        auto.IsEnabled = _edit.Histogram is not null;
        _body.Children.Add(Line(Caption(L.T("通道", "Channel"), 52), channel, auto));

        var input = new LevelsGraph { Width = 420, Height = 160, Margin = new Thickness(0, 0, 0, 4) };
        var black = new NumberField(L.T("输入黑场", "Input Black"), 46) { Minimum = 0, Maximum = 254 };
        var gamma = new NumberField(L.T("灰度", "Gamma"), 46) { Minimum = 0.1, Maximum = 9.99, Step = 0.01, ScrubRate = 0.01, Format = "0.00" };
        var white = new NumberField(L.T("白场", "Input White"), 46) { Minimum = 1, Maximum = 255 };
        var output = new LevelsGraph { Output = true, Width = 420, Height = 34, Margin = new Thickness(0, 6, 0, 4) };
        var outBlack = new NumberField(L.T("输出黑场", "Output Black"), 46) { Minimum = 0, Maximum = 255 };
        var outWhite = new NumberField(L.T("白场", "Output White"), 46) { Minimum = 0, Maximum = 255 };
        _body.Children.Add(input);
        var inputs = Line(black, gamma, white);
        gamma.Margin = new Thickness(26, 0, 26, 0);
        _body.Children.Add(inputs);
        _body.Children.Add(output);
        outWhite.Margin = new Thickness(26, 0, 0, 0);
        _body.Children.Add(Line(outBlack, outWhite));

        void Set(LevelRange range) { levels.Current = range; Changed(); }
        input.RangeChanged += Set;
        output.RangeChanged += Set;
        black.ValueChanged += v => Set(levels.Current with { Black = v });
        gamma.ValueChanged += v => Set(levels.Current with { Gamma = v });
        white.ValueChanged += v => Set(levels.Current with { White = v });
        outBlack.ValueChanged += v => Set(levels.Current with { OutputBlack = v });
        outWhite.ValueChanged += v => Set(levels.Current with { OutputWhite = v });
        channel.SelectionChanged += (_, _) => { levels.Channel = (LevelsChannel)Math.Max(0, channel.SelectedIndex); _refresh?.Invoke(); };
        auto.SelectionChanged += (_, _) =>
        {
            if (auto.SelectedIndex <= 0 || _edit.Histogram is not { } histogram) return;
            var mode = (LevelsAuto)(auto.SelectedIndex - 1);
            auto.SelectedIndex = 0;
            var result = mode.Settings(histogram);
            result.Channel = levels.Channel;
            _settings.Levels = levels = result;
            Changed();
        };

        var samplers = Line(Caption(L.T("取样", "Sample"), 52),
            Sampler(L.T("黑场", "Black Point"), L.T("在图像中单击以设置黑场", "Click in the image to set the black point."),
                c => { _settings.Levels = levels = levels.Sampling(c.Red, c.Green, c.Blue, LevelsSample.Black); Changed(); }),
            Sampler(L.T("灰场", "Gray Point"), L.T("在图像中单击以设置灰场（中性色）", "Click in the image to set the gray point (a neutral color)."),
                c => { _settings.Levels = levels = levels.Sampling(c.Red, c.Green, c.Blue, LevelsSample.Gray); Changed(); }),
            Sampler(L.T("白场", "White Point"), L.T("在图像中单击以设置白场", "Click in the image to set the white point."),
                c => { _settings.Levels = levels = levels.Sampling(c.Red, c.Green, c.Blue, LevelsSample.White); Changed(); }));
        _body.Children.Add(samplers);
        _body.Children.Add(Note(_edit.EditsLayer
            ? L.T("直方图：下方图层的合成（按透明度加权）。", "Histogram: the layers below, composited (alpha-weighted).")
            : L.T("直方图：图层原始像素（按选区和透明度加权）。", "Histogram: the layer’s original pixels (weighted by the selection and alpha).")));

        _refresh = () =>
        {
            var range = levels.Current.Normalized;
            int c = (int)levels.Channel;
            input.Bins = _edit.Histogram?[c];
            input.BinColor = ChannelColors[c];
            input.Range = range;
            output.Range = range;
            input.InvalidateVisual();
            output.InvalidateVisual();
            black.Value = range.Black; gamma.Value = range.Gamma; white.Value = range.White;
            outBlack.Value = range.OutputBlack; outWhite.Value = range.OutputWhite;
        };
        _refresh();
    }

    // MARK: Curves

    private void BuildCurves()
    {
        var curves = _settings.Curves;
        var channel = Combo(ChannelNames, (int)curves.Channel, 120);
        _body.Children.Add(Line(Caption(L.T("通道", "Channel"), 52), channel));
        var graph = new CurvesGraph { Width = 320, Height = 320, Margin = new Thickness(0, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Left };
        _body.Children.Add(graph);
        var input = new NumberField(L.T("输入", "Input"), 46) { Minimum = 0, Maximum = 255 };
        var output = new NumberField(L.T("输出", "Output"), 46) { Minimum = 0, Maximum = 255, Margin = new Thickness(18, 0, 0, 0) };
        var remove = new Button { Content = L.T("删除点", "Remove Point"), Margin = new Thickness(18, 0, 0, 0), MinWidth = 70 };
        var resetCurve = new Button { Content = L.T("复位曲线", "Reset Curve"), Margin = new Thickness(8, 0, 0, 0), MinWidth = 80 };
        _body.Children.Add(Line(input, output, remove, resetCurve));
        _body.Children.Add(Note(L.T("单击曲线添加点，拖动调整；将中间的点拖出图表或按 Delete 删除。",
            "Click the curve to add a point and drag to adjust it. Drag a middle point off the graph or press Delete to remove it.")));

        List<CurvePoint> Points() => curves.Channels[(int)curves.Channel];
        void SetPoints(List<CurvePoint> points)
        {
            if (points.Count < 2) return;
            curves.Channels[(int)curves.Channel] = points;
            Changed();
        }
        graph.PointsChanged += SetPoints;
        graph.SelectionChanged += () => _refresh?.Invoke();
        void MoveSelected(double? x, double? y)
        {
            if (graph.Selected is not { } i) return;
            var p = new List<CurvePoint>(Points());
            if (i >= p.Count) return;
            double nx = x ?? p[i].X;
            if (i == 0 || i == p.Count - 1) nx = p[i].X;
            else nx = Math.Clamp(nx, p[i - 1].X + 1, p[i + 1].X - 1);
            p[i] = new CurvePoint(nx, Math.Clamp(y ?? p[i].Y, 0, 255));
            SetPoints(p);
        }
        input.ValueChanged += v => MoveSelected(v, null);
        output.ValueChanged += v => MoveSelected(null, v);
        remove.Click += (_, _) => graph.RemoveSelected();
        resetCurve.Click += (_, _) => { graph.ClearSelection(); SetPoints(CurvesSettings.Identity()); };
        channel.SelectionChanged += (_, _) => { curves.Channel = (LevelsChannel)Math.Max(0, channel.SelectedIndex); graph.ClearSelection(); _refresh?.Invoke(); };

        _refresh = () =>
        {
            int c = (int)curves.Channel;
            graph.Points = new List<CurvePoint>(Points());
            graph.Bins = _edit.Histogram?[c];
            graph.LineColor = c == 0 ? Colors.White : ChannelColors[c];
            graph.InvalidateVisual();
            bool has = graph.Selected is { } i && i < graph.Points.Count;
            input.IsEnabled = output.IsEnabled = has;
            remove.IsEnabled = has && graph.Selected > 0 && graph.Selected < graph.Points.Count - 1;
            if (has) { input.Value = graph.Points[graph.Selected!.Value].X; output.Value = graph.Points[graph.Selected.Value].Y; }
        };
        _refresh();
    }

    // MARK: Hue/Saturation

    private void BuildHueSaturation()
    {
        var hsv = _settings.HsvSettings ??= _settings.ResolvedHsv.Clone();
        var range = Combo(ColorRanges.All.Select(r => r.ToDisplayName()), (int)hsv.Range, 120);
        var sample = Sampler(L.T("取样", "Sample"), L.T("在图像中单击，使此范围以该颜色为中心", "Click in the image to center this range on that color."),
            c => SampleBand(c, b => b.CenteredOn));
        var add = Sampler(L.T("添加", "Add"), L.T("在图像中单击，将该颜色加入此范围", "Click in the image to add that color to this range."),
            c => SampleBand(c, b => b.Including));
        var subtract = Sampler(L.T("减去", "Subtract"), L.T("在图像中单击，将该颜色移出此范围", "Click in the image to remove that color from this range."),
            c => SampleBand(c, b => b.Excluding));
        var top = Line(Caption(L.T("范围", "Range"), 52), range);
        var samplerRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 0, 0) };
        samplerRow.Children.Add(sample); samplerRow.Children.Add(add); samplerRow.Children.Add(subtract);
        top.Children.Add(samplerRow);
        _body.Children.Add(top);

        var hue = Slider(L.T("色相", "Hue"), -180, 180, 0, 0, v => hsv.Current = hsv.Current with { Hue = v });
        var saturation = Slider(L.T("饱和度", "Saturation"), -100, 100, 0, 0, v => hsv.Current = hsv.Current with { Saturation = v });
        Slider(L.T("明度", "Lightness"), -100, 100, hsv.Current.Lightness, 0, v => hsv.Current = hsv.Current with { Lightness = v },
               track: Theme.Gradient(Colors.Black, Color.FromRgb(128, 128, 128), Colors.White));
        var spectrum = new SpectrumBar { Width = 430, Margin = new Thickness(0, 4, 0, 6) };
        spectrum.BandChanged += band => { hsv.Band = band; Changed(); };
        _body.Children.Add(spectrum);
        var outside = new CheckBox { Content = L.T("应用到此范围之外", "Apply outside this range instead"), IsChecked = hsv.InvertRange, Margin = new Thickness(0, 0, 0, 8) };
        outside.Click += (_, _) => { hsv.InvertRange = outside.IsChecked == true; Changed(); };
        _body.Children.Add(outside);
        var colorize = new CheckBox { Content = L.T("着色", "Colorize"), IsChecked = hsv.Colorize, Margin = new Thickness(0, 0, 0, 8) };
        colorize.Click += (_, _) =>
        {
            // Photoshop starts colorizing at hue 0, saturation 25.
            var fresh = new HueSaturationSettings();
            if (colorize.IsChecked == true) { fresh.Colorize = true; fresh.Adjustments[ColorRange.Master] = new RangeAdjustment(0, 25, 0); }
            _settings.HsvSettings = hsv = fresh;
            _body.Children.Clear();
            _samplers.Clear();
            _session.CanvasSampler = null;
            Build();
            Changed();
        };
        _body.Children.Add(colorize);
        range.SelectionChanged += (_, _) => { hsv.Range = (ColorRange)Math.Max(0, range.SelectedIndex); Changed(); };

        void SampleBand(PaletteColor color, Func<HueBand, Func<double, HueBand>> change)
        {
            if (hsv.Range == ColorRange.Master || hsv.Colorize) return;
            var (h, s, _) = color.ToHsb();
            if (s <= 0.02) { System.Media.SystemSounds.Beep.Play(); return; }
            hsv.Band = change(hsv.Band)(h);
            Changed();
        }

        _refresh = () =>
        {
            bool colorizing = hsv.Colorize;
            bool ranged = hsv.Range != ColorRange.Master && !colorizing;
            range.IsEnabled = !colorizing;
            foreach (var b in _samplers) b.Visibility = ranged ? Visibility.Visible : Visibility.Collapsed;
            if (!ranged) foreach (var b in _samplers) b.IsChecked = false;
            spectrum.Visibility = outside.Visibility = ranged ? Visibility.Visible : Visibility.Collapsed;
            spectrum.Settings = hsv;
            spectrum.InvalidateVisual();
            var current = hsv.Current;
            hue.Value = current.Hue;
            saturation.Value = current.Saturation;
            double center = Math.Max(0, Array.IndexOf(ColorRanges.Colors, hsv.Range)) * 60;
            hue.Track = Theme.Spectrum(colorizing ? 180 : center);
            saturation.Track = Theme.Gradient(Color.FromRgb(128, 128, 128), Theme.FromHsb(colorizing ? current.Hue : center));
        };
        if (hsv.Colorize)
        {
            // Colorize picks an absolute hue and an amount of color.
            _body.Children.Remove(hue);
            _body.Children.Remove(saturation);
            hue = Slider(L.T("色相", "Hue"), 0, 360, hsv.Current.Hue, 0, v => hsv.Current = hsv.Current with { Hue = v });
            saturation = Slider(L.T("饱和度", "Saturation"), 0, 100, hsv.Current.Saturation, 25, v => hsv.Current = hsv.Current with { Saturation = v });
            _body.Children.Remove(hue); _body.Children.Remove(saturation);
            _body.Children.Insert(1, hue); _body.Children.Insert(2, saturation);
        }
        _refresh();
    }

    // MARK: The slider panels

    private void BuildExposure()
    {
        var e = _settings.Exposure;
        Slider(L.T("曝光度", "Exposure"), -20, 20, e.Exposure, 0, v => _settings.ExposureSettings = _settings.Exposure with { Exposure = v }, 2,
               track: Theme.Gradient(Colors.Black, Colors.White));
        Slider(L.T("位移", "Offset"), -0.5, 0.5, e.Offset, 0, v => _settings.ExposureSettings = _settings.Exposure with { Offset = v }, 4);
        Slider(L.T("灰度系数校正", "Gamma Correction"), 0.01, 9.99, e.Gamma, 1, v => _settings.ExposureSettings = _settings.Exposure with { Gamma = v }, 2, log: true);
    }

    private void BuildGradientMap()
    {
        var g = _settings.GradientMap;
        var preview = new Border { Width = 300, Height = 20, CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 10) };
        preview.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        var shadows = new ColorWell(g.Shadows.ToPalette()) { PickerTitle = L.T("拾色器（暗部）", "Color Picker (Shadows)") };
        var highlights = new ColorWell(g.Highlights.ToPalette()) { PickerTitle = L.T("拾色器（亮部）", "Color Picker (Highlights)"), Margin = new Thickness(0) };
        var reversed = new CheckBox { Content = L.T("反向", "Reverse"), IsChecked = g.Reversed, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 0, 0) };
        _body.Children.Add(preview);
        _body.Children.Add(Line(Caption(L.T("暗部", "Shadows"), 40), shadows, new Border { Width = 18 }, Caption(L.T("亮部", "Highlights"), 40), highlights, reversed));
        shadows.ColorChanged += c => { _settings.GradientMapSettings = _settings.GradientMap with { Shadows = AdjustmentColor.From(c) }; Changed(); };
        highlights.ColorChanged += c => { _settings.GradientMapSettings = _settings.GradientMap with { Highlights = AdjustmentColor.From(c) }; Changed(); };
        reversed.Click += (_, _) => { _settings.GradientMapSettings = _settings.GradientMap with { Reversed = reversed.IsChecked == true }; Changed(); };
        static Color Wpf(AdjustmentColor c) => Color.FromRgb((byte)Math.Round(c.Red * 255), (byte)Math.Round(c.Green * 255), (byte)Math.Round(c.Blue * 255));
        _refresh = () =>
        {
            var (dark, light) = _settings.GradientMap.Ends;
            preview.Background = Theme.Gradient(Wpf(dark), Wpf(light));
        };
        _refresh();
    }

    private void BuildGrain()
    {
        var g = _settings.Grain;
        Slider(L.T("数量", "Amount"), 0, 100, g.Amount, 25, v => _settings.GrainSettings = _settings.Grain with { Amount = v });
        Slider(L.T("大小", "Size"), 0.5, 20, g.Size, 1.5, v => _settings.GrainSettings = _settings.Grain with { Size = v }, 1, Px, log: true);
        Slider(L.T("粗糙度", "Roughness"), 0, 100, g.Roughness, 50, v => _settings.GrainSettings = _settings.Grain with { Roughness = v });
    }

    private void BuildBlackWhite()
    {
        var b = _settings.BlackWhite;
        LinearGradientBrush Luma(double hue) => Theme.Gradient(Colors.Black, Theme.FromHsb(hue), Colors.White);
        Slider(L.T("红色", "Reds"), -200, 300, b.Reds, 40, v => _settings.BlackWhiteSettings = _settings.BlackWhite with { Reds = v }, unit: "%", track: Luma(0));
        Slider(L.T("黄色", "Yellows"), -200, 300, b.Yellows, 60, v => _settings.BlackWhiteSettings = _settings.BlackWhite with { Yellows = v }, unit: "%", track: Luma(60));
        Slider(L.T("绿色", "Greens"), -200, 300, b.Greens, 40, v => _settings.BlackWhiteSettings = _settings.BlackWhite with { Greens = v }, unit: "%", track: Luma(120));
        Slider(L.T("青色", "Cyans"), -200, 300, b.Cyans, 60, v => _settings.BlackWhiteSettings = _settings.BlackWhite with { Cyans = v }, unit: "%", track: Luma(180));
        Slider(L.T("蓝色", "Blues"), -200, 300, b.Blues, 20, v => _settings.BlackWhiteSettings = _settings.BlackWhite with { Blues = v }, unit: "%", track: Luma(240));
        Slider(L.T("洋红", "Magentas"), -200, 300, b.Magentas, 80, v => _settings.BlackWhiteSettings = _settings.BlackWhite with { Magentas = v }, unit: "%", track: Luma(300));
        var tint = new CheckBox { Content = L.T("色调", "Tint"), IsChecked = b.Tint, Margin = new Thickness(0, 4, 0, 8) };
        _body.Children.Add(tint);
        var hue = Slider(L.T("色相", "Hue"), 0, 360, b.TintHue, 40, v => _settings.BlackWhiteSettings = _settings.BlackWhite with { TintHue = v }, unit: "°", track: Theme.Spectrum());
        var saturation = Slider(L.T("饱和度", "Saturation"), 0, 100, b.TintSaturation, 20, v => _settings.BlackWhiteSettings = _settings.BlackWhite with { TintSaturation = v }, unit: "%");
        tint.Click += (_, _) => { _settings.BlackWhiteSettings = _settings.BlackWhite with { Tint = tint.IsChecked == true }; Changed(); };
        _refresh = () =>
        {
            hue.IsEnabled = saturation.IsEnabled = _settings.BlackWhite.Tint;
            saturation.Track = Theme.Gradient(Color.FromRgb(128, 128, 128), Theme.FromHsb(_settings.BlackWhite.TintHue));
        };
        _refresh();
    }

    private void BuildColorBalance()
    {
        var tones = new[] { L.T("阴影", "Shadows"), L.T("中间调", "Midtones"), L.T("高光", "Highlights") };
        var tone = new StackPanel { Orientation = Orientation.Horizontal };
        var buttons = tones.Select((t, i) => new RadioButton { Content = t, GroupName = "tone" + GetHashCode(), IsChecked = i == 1, Margin = new Thickness(0, 0, 16, 0) }).ToList();
        foreach (var b in buttons) tone.Children.Add(b);
        _body.Children.Add(Line(Caption(L.T("色调", "Tone"), 52), tone));
        int selected = 1;
        double Get(int axis)
        {
            var c = _settings.ColorBalance;
            return (selected, axis) switch
            {
                (0, 0) => c.ShadowCyanRed, (0, 1) => c.ShadowMagentaGreen, (0, _) => c.ShadowYellowBlue,
                (1, 0) => c.MidCyanRed, (1, 1) => c.MidMagentaGreen, (1, _) => c.MidYellowBlue,
                (_, 0) => c.HighlightCyanRed, (_, 1) => c.HighlightMagentaGreen, _ => c.HighlightYellowBlue,
            };
        }
        void Set(int axis, double v)
        {
            var c = _settings.ColorBalance;
            _settings.ColorBalanceSettings = (selected, axis) switch
            {
                (0, 0) => c with { ShadowCyanRed = v }, (0, 1) => c with { ShadowMagentaGreen = v }, (0, _) => c with { ShadowYellowBlue = v },
                (1, 0) => c with { MidCyanRed = v }, (1, 1) => c with { MidMagentaGreen = v }, (1, _) => c with { MidYellowBlue = v },
                (_, 0) => c with { HighlightCyanRed = v }, (_, 1) => c with { HighlightMagentaGreen = v }, _ => c with { HighlightYellowBlue = v },
            };
        }
        var rows = new[]
        {
            Slider(L.T("青色 – 红色", "Cyan / Red"), -100, 100, Get(0), 0, v => Set(0, v), track: Theme.Gradient(Color.FromRgb(0, 200, 220), Color.FromRgb(128, 128, 128), Color.FromRgb(230, 50, 50))),
            Slider(L.T("洋红 – 绿色", "Magenta / Green"), -100, 100, Get(1), 0, v => Set(1, v), track: Theme.Gradient(Color.FromRgb(220, 60, 220), Color.FromRgb(128, 128, 128), Color.FromRgb(60, 200, 60))),
            Slider(L.T("黄色 – 蓝色", "Yellow / Blue"), -100, 100, Get(2), 0, v => Set(2, v), track: Theme.Gradient(Color.FromRgb(230, 210, 40), Color.FromRgb(128, 128, 128), Color.FromRgb(50, 90, 235))),
        };
        var preserve = new CheckBox { Content = L.T("保持明度", "Preserve Luminosity"), IsChecked = _settings.ColorBalance.PreserveLuminosity, Margin = new Thickness(0, 2, 0, 8) };
        preserve.Click += (_, _) => { _settings.ColorBalanceSettings = _settings.ColorBalance with { PreserveLuminosity = preserve.IsChecked == true }; Changed(); };
        _body.Children.Add(preserve);
        for (int i = 0; i < buttons.Count; i++)
        {
            int index = i;
            buttons[i].Checked += (_, _) => { selected = index; for (int a = 0; a < 3; a++) rows[a].Value = Get(a); };
        }
    }

    private void BuildGaussianBlur() =>
        Slider(L.T("半径", "Radius"), 0.1, 250, _settings.GaussianRadius, 10, v => _settings.BlurRadius = v, 1, Px, log: true);

    private void BuildMotionBlur()
    {
        Slider(L.T("角度", "Angle"), -90, 90, _settings.ResolvedMotionAngle, 0, v => _settings.MotionAngle = v, unit: "°");
        Slider(L.T("距离", "Distance"), 1, 2000, _settings.ResolvedMotionDistance, 10, v => _settings.MotionDistance = v, unit: Px, log: true);
    }

    private void BuildAddNoise()
    {
        Slider(L.T("数量", "Amount"), 0.1, 400, _settings.ResolvedNoiseAmount, 10, v => _settings.NoiseAmount = v, 1, "%", log: true);
        var uniform = new RadioButton { Content = L.T("平均分布", "Uniform"), GroupName = "noise" + GetHashCode(), IsChecked = !_settings.ResolvedNoiseGaussian, Margin = new Thickness(0, 0, 16, 0) };
        var gaussian = new RadioButton { Content = L.T("高斯分布", "Gaussian"), GroupName = "noise" + GetHashCode(), IsChecked = _settings.ResolvedNoiseGaussian };
        uniform.Checked += (_, _) => { _settings.NoiseGaussian = false; Changed(); };
        gaussian.Checked += (_, _) => { _settings.NoiseGaussian = true; Changed(); };
        _body.Children.Add(Line(Caption(L.T("分布", "Distribution")), uniform, gaussian));
        var mono = new CheckBox { Content = L.T("单色", "Monochromatic"), IsChecked = _settings.ResolvedNoiseMonochromatic, Margin = new Thickness(LabelWidth, 0, 0, 8) };
        mono.Click += (_, _) => { _settings.NoiseMonochromatic = mono.IsChecked == true; Changed(); };
        _body.Children.Add(mono);
    }

    // MARK: Keys

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (Keyboard.FocusedElement is ComboBox { IsDropDownOpen: true }) return;
        if (e.Key == Key.Escape)
        {
            // A field being typed in goes back to its value first; otherwise Escape is Cancel.
            if (Keyboard.FocusedElement is TextBox) { _okButton.Focus(); e.Handled = true; return; }
            Finish(commit: false);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            // A value still being typed counts: moving focus commits it.
            if (Keyboard.FocusedElement is TextBox) _okButton.Focus();
            Finish(commit: true);
            e.Handled = true;
        }
    }
}
