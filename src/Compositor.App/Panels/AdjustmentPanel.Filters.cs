using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Compositor.App.Controls;
using Compositor.Core.Adjustments;
using Compositor.Core.Filters;
using Compositor.Core.Localization;

namespace Compositor.App.Panels;

/// <summary>The Filter menu's own effects (Vignette, Bloom, Tonal Contrast, Lens Correction, Dither, Scanlines, Content-Aware Fill).</summary>
public sealed partial class AdjustmentPanel
{
    private FilterSettings _filterSettings;

    private void ResetFilter(FilterKind kind)
    {
        var d = new FilterSettings();
        var s = _filterSettings;
        _filterSettings = kind switch
        {
            FilterKind.Vignette => s with
            {
                VignetteAmount = d.VignetteAmount, VignetteColor = d.VignetteColor, VignetteMidpoint = d.VignetteMidpoint,
                VignetteRoundness = d.VignetteRoundness, VignetteFeather = d.VignetteFeather, VignetteHighlights = d.VignetteHighlights,
            },
            FilterKind.BloomGlow => s with { BloomAmount = d.BloomAmount, BloomRadius = d.BloomRadius },
            FilterKind.TonalContrast => s with
            {
                TonalAmount = d.TonalAmount, TonalRadius = d.TonalRadius, TonalShadows = d.TonalShadows,
                TonalMidtones = d.TonalMidtones, TonalHighlights = d.TonalHighlights,
            },
            FilterKind.LensCorrection => s with { Distortion = 0 },
            FilterKind.Dither => s with { Dither = new DitherSettings() },
            FilterKind.Scanlines => s with { Scanlines = new ScanlinesSettings() },
            FilterKind.CameraRaw => s with { CameraRaw = new Core.CameraRaw.CameraRawSettings() },
            FilterKind.RemoveBackground => s with { RemoveBackground = new Core.Segmentation.RemoveBackgroundSettings() },
            _ => s,
        };
    }

    private void BuildFilter(FilterKind kind)
    {
        switch (kind)
        {
            case FilterKind.Vignette: BuildVignette(); break;
            case FilterKind.BloomGlow:
                FilterSlider(L.T("数量", "Amount"), 0, 100, _filterSettings.BloomAmount, 40, (s, v) => s with { BloomAmount = v }, "%");
                FilterSlider(L.T("半径", "Radius"), 1, 150, _filterSettings.BloomRadius, 24, (s, v) => s with { BloomRadius = v }, Px, log: true);
                break;
            case FilterKind.TonalContrast:
                FilterSlider(L.T("数量", "Amount"), 0, 100, _filterSettings.TonalAmount, 50, (s, v) => s with { TonalAmount = v }, "%");
                FilterSlider(L.T("阴影", "Shadows"), -100, 100, _filterSettings.TonalShadows, 40, (s, v) => s with { TonalShadows = v }, "%");
                FilterSlider(L.T("中间调", "Midtones"), -100, 100, _filterSettings.TonalMidtones, 60, (s, v) => s with { TonalMidtones = v }, "%");
                FilterSlider(L.T("高光", "Highlights"), -100, 100, _filterSettings.TonalHighlights, 30, (s, v) => s with { TonalHighlights = v }, "%");
                FilterSlider(L.T("半径", "Radius"), 1, 100, _filterSettings.TonalRadius, 16, (s, v) => s with { TonalRadius = v }, Px, log: true);
                break;
            case FilterKind.LensCorrection:
                FilterSlider(L.T("移去扭曲", "Remove Distortion"), -100, 100, _filterSettings.Distortion, 0, (s, v) => s with { Distortion = v });
                _body.Children.Add(Note(L.T("正值拉直向外鼓起的线条（桶形失真）；负值拉直向内收的线条（枕形失真）。",
                    "Positive straightens lines that bow outward (barrel); negative, lines that bow inward (pincushion).")));
                break;
            case FilterKind.Dither: BuildDither(); break;
            case FilterKind.Scanlines: BuildScanlines(); break;
            case FilterKind.CameraRaw: BuildCameraRaw(); break;
            case FilterKind.RemoveBackground: BuildRemoveBackground(); break;
            default:
                _body.Children.Add(Note(L.T("用这个图层上选区周围的像素填充选区。", "Fill the selection using surrounding pixels from this layer.")));
                break;
        }
    }

    private SliderRow FilterSlider(string label, double min, double max, double value, double @default,
                                   Func<FilterSettings, double, FilterSettings> set, string unit = "", bool log = false, Brush? track = null)
    {
        var row = new SliderRow(label, min, max, value, @default, 0, unit, log, track, LabelWidth) { Width = 430 };
        row.ValueChanged += v => { _filterSettings = set(_filterSettings, v); Changed(); };
        _body.Children.Add(row);
        return row;
    }

    private void BuildVignette()
    {
        var color = new ColorWell(_filterSettings.VignetteColor.ToPalette()) { PickerTitle = L.T("拾色器（暗角颜色）", "Color Picker (Vignette Color)") };
        color.ColorChanged += c => { _filterSettings = _filterSettings with { VignetteColor = AdjustmentColor.From(c) }; Changed(); };
        _body.Children.Add(Line(Caption(L.T("颜色", "Color")), color));
        var s = _filterSettings;
        FilterSlider(L.T("数量", "Amount"), 0, 100, s.VignetteAmount, 35, (f, v) => f with { VignetteAmount = v }, "%");
        FilterSlider(L.T("中点", "Midpoint"), 0, 100, s.VignetteMidpoint, 50, (f, v) => f with { VignetteMidpoint = v }, "%");
        FilterSlider(L.T("圆度", "Roundness"), -100, 100, s.VignetteRoundness, 100, (f, v) => f with { VignetteRoundness = v });
        FilterSlider(L.T("羽化", "Feather"), 0, 100, s.VignetteFeather, 60, (f, v) => f with { VignetteFeather = v }, "%");
        FilterSlider(L.T("高光", "Highlights"), 0, 100, s.VignetteHighlights, 25, (f, v) => f with { VignetteHighlights = v }, "%");
        if (_edit.StartedEmptyLayer) _body.Children.Add(Note(L.T("空图层：暗角会填充整个画布。", "Empty layer: the vignette fills the whole canvas.")));
    }

    // MARK: Remove Background

    /// <summary>Basic is the model's mask as it comes; Advanced refines it with the three controls below.</summary>
    private void BuildRemoveBackground()
    {
        Core.Segmentation.RemoveBackgroundSettings R() => _filterSettings.RemoveBackground;
        void SetR(Func<Core.Segmentation.RemoveBackgroundSettings, Core.Segmentation.RemoveBackgroundSettings> change)
        {
            _filterSettings = _filterSettings with { RemoveBackground = change(R()) };
            Changed();
        }
        _body.Children.Add(Note(L.T("用 AI 模型找出主体，用图层蒙版隐藏背景：像素不会被删除，随时可以在蒙版上涂抹找回。第一次识别需要几秒钟。",
            "Finds the subject with an AI model and hides the background behind a layer mask. No pixels are deleted, so the background can be painted back on the mask at any time. The first detection takes a few seconds.")));
        var quality = Combo(new[] { L.T("基本", "Basic"), L.T("高级", "Advanced") }, (int)R().Quality, 120);
        quality.SelectionChanged += (_, _) => { if (quality.SelectedIndex >= 0) SetR(x => x with { Quality = (Core.Segmentation.BackgroundQuality)quality.SelectedIndex }); };
        _body.Children.Add(Line(Caption(L.T("品质", "Quality")), quality));
        SliderRow Row(string label, double min, double max, double value, double @default,
                      Func<Core.Segmentation.RemoveBackgroundSettings, double, Core.Segmentation.RemoveBackgroundSettings> set, string unit) =>
            FilterSlider(label, min, max, value, @default, (f, v) => f with { RemoveBackground = set(f.RemoveBackground, v) }, unit);
        var refine = Row(L.T("优化边缘", "Refine Edge"), 0, 40, R().RefineEdges, 12, (x, v) => x with { RefineEdges = v }, Px);
        refine.ToolTip = L.T("把蒙版贴合到图像自身的边缘上，找回头发和毛发", "Pull the mask onto the image’s own edges, which recovers hair and fur.");
        var contrast = Row(L.T("对比度", "Contrast"), 0, 100, R().MatteContrast, 25, (x, v) => x with { MatteContrast = v }, "%");
        contrast.ToolTip = L.T("让蒙版的灰色更接近纯黑纯白，去掉半透明的残留", "Push the mask’s grays toward pure black and white, clearing semi-transparent leftovers.");
        var shift = Row(L.T("移动边缘", "Shift Edge"), -10, 10, R().ShiftEdge, 0, (x, v) => x with { ShiftEdge = v }, Px);
        shift.ToolTip = L.T("负值向内收缩，去掉边缘残留的背景色；正值向外扩展", "Negative values shrink the mask to drop the rim of background color around the subject; positive values grow it.");
        _refresh = () =>
        {
            var shown = R().Quality == Core.Segmentation.BackgroundQuality.Advanced ? Visibility.Visible : Visibility.Collapsed;
            refine.Visibility = contrast.Visibility = shift.Visibility = shown;
        };
        _refresh();
    }

    // MARK: Dither

    private static readonly string[] DitherColorNames = Enum.GetValues<DitherColors>().Select(c => c.ToDisplayName()).ToArray();

    private void BuildDither()
    {
        DitherSettings D() => _filterSettings.Dither;
        void SetD(Func<DitherSettings, DitherSettings> change) { _filterSettings = _filterSettings with { Dither = change(D()) }; Changed(); }
        var d = D();

        var style = Combo(Enum.GetValues<DitherStyle>().Select(s => s.ToDisplayName()), (int)d.Style, 220);
        style.SelectionChanged += (_, _) => { if (style.SelectedIndex >= 0) SetD(x => x with { Style = (DitherStyle)style.SelectedIndex }); };
        _body.Children.Add(Line(Caption(L.T("样式", "Style")), style));

        SliderRow Row(string label, double min, double max, double value, double @default, Func<DitherSettings, double, DitherSettings> set, string unit = "") =>
            FilterSlider(label, min, max, value, @default, (f, v) => f with { Dither = set(f.Dither, v) }, unit);

        var pixelSize = Row(L.T("像素大小", "Pixel Size"), 1, 32, d.PixelSize, 2, (x, v) => x with { PixelSize = v }, Px);
        pixelSize.ToolTip = L.T("每个仿色像素占多少个像素，做出老屏幕的粗颗粒效果", "Make each dithered pixel this many pixels across, for a chunky old-screen look.");
        var textSize = Row(L.T("文字大小", "Text Size"), 6, 64, d.TextSize, 14, (x, v) => x with { TextSize = v }, Px);
        var cellSize = Row(L.T("网格大小", "Cell Size"), 4, 64, d.CellSize, 8, (x, v) => x with { CellSize = v }, Px);
        var angle = Row(L.T("角度", "Angle"), -90, 90, d.Angle, 45, (x, v) => x with { Angle = v }, "°");
        var characters = new TextBox { Width = 220, Text = d.Characters, FontFamily = new FontFamily("Consolas") };
        characters.TextChanged += (_, _) => SetD(x => x with { Characters = characters.Text });
        var charactersLine = Line(Caption(L.T("字符", "Characters")), characters);
        _body.Children.Add(charactersLine);
        var tones = Row(L.T("色阶", "Tones"), 2, 8, d.Levels, 2, (x, v) => x with { Levels = v });
        var diffusion = Row(L.T("扩散", "Diffusion"), 0, 100, d.Diffusion, 100, (x, v) => x with { Diffusion = v }, "%");
        Row(L.T("密度", "Density"), -100, 100, d.Density, 0, (x, v) => x with { Density = v }).ToolTip =
            L.T("仿色前加深（更多墨）或减淡", "More ink (darker) or less before dithering.");
        Row(L.T("对比度", "Contrast"), -100, 100, d.Contrast, 0, (x, v) => x with { Contrast = v });

        var colors = Combo(DitherColorNames, (int)d.Colors, 120);
        colors.SelectionChanged += (_, _) => { if (colors.SelectedIndex >= 0) SetD(x => x with { Colors = (DitherColors)colors.SelectedIndex }); };
        _body.Children.Add(Line(Caption(L.T("颜色", "Colors")), colors));
        var (wells, wellsLine) = TwoColors(d.Dark, d.Light,
            c => SetD(x => x with { Dark = AdjustmentColor.From(c) }), c => SetD(x => x with { Light = AdjustmentColor.From(c) }));

        var shape = Combo(new[] { L.T("方形", "Square"), L.T("圆点", "Dot") }, (int)d.PixelShape, 120);
        shape.SelectionChanged += (_, _) => { if (shape.SelectedIndex >= 0) SetD(x => x with { PixelShape = (DitherPixelShape)shape.SelectedIndex }); };
        var shapeLine = Line(Caption(L.T("像素形状", "Pixel Shape")), shape);
        _body.Children.Add(shapeLine);
        var lightOnDark = new CheckBox { Content = L.T("暗底亮字（亮色画在暗色上）", "Light on Dark (light color drawn on dark)"), IsChecked = d.LightOnDark, Margin = new Thickness(LabelWidth, 0, 0, 8) };
        lightOnDark.Click += (_, _) => SetD(x => x with { LightOnDark = lightOnDark.IsChecked == true });
        _body.Children.Add(lightOnDark);

        static Visibility Show(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;
        _refresh = () =>
        {
            var x = D();
            bool usesPixels = x.Style != DitherStyle.Ascii;
            pixelSize.Visibility = Show(usesPixels);
            textSize.Visibility = charactersLine.Visibility = Show(x.Style == DitherStyle.Ascii);
            cellSize.Visibility = angle.Visibility = Show(x.Style.IsHalftone());
            tones.Visibility = Show(x.Style.HasTones());
            diffusion.Visibility = Show(x.Style.Diffuses());
            wellsLine.Visibility = Show(x.Colors == DitherColors.TwoColors);
            shapeLine.Visibility = Show(usesPixels && x.PixelSize > 1);
            lightOnDark.Visibility = Show(!x.Style.HasTones());
        };
        _refresh();
    }

    // MARK: Scanlines

    private void BuildScanlines()
    {
        // Not "L()": that would hide the L of L.T below.
        ScanlinesSettings Lines() => _filterSettings.Scanlines;
        void SetLines(Func<ScanlinesSettings, ScanlinesSettings> change) { _filterSettings = _filterSettings with { Scanlines = change(Lines()) }; Changed(); }
        var l = Lines();
        SliderRow Row(string label, double min, double max, double value, double @default, Func<ScanlinesSettings, double, ScanlinesSettings> set, string unit = "") =>
            FilterSlider(label, min, max, value, @default, (f, v) => f with { Scanlines = set(f.Scanlines, v) }, unit);

        Row(L.T("线间距", "Line Spacing"), 2, 32, l.LineSpacing, 4, (x, v) => x with { LineSpacing = v }, Px);
        Row(L.T("粗细", "Thickness"), 5, 100, l.Thickness, 70, (x, v) => x with { Thickness = v }, "%");
        Row(L.T("辉光", "Glow"), 0, 100, l.Glow, 0, (x, v) => x with { Glow = v }, "%").ToolTip =
            L.T("线条周围的光晕，像 CRT 的荧光粉", "Light blooming around the lines, like a CRT’s phosphors.");
        Row(L.T("网点", "Dots"), 0, 100, l.Dots, 0, (x, v) => x with { Dots = v }, "%");
        Row(L.T("位移", "Displace"), -100, 100, l.Displace, 0, (x, v) => x with { Displace = v }, Px);
        Row(L.T("平滑度", "Smoothness"), 0, 100, l.Smoothness, 50, (x, v) => x with { Smoothness = v }, "%");
        Row(L.T("阈值", "Threshold"), 0, 100, l.Threshold, 0, (x, v) => x with { Threshold = v }, "%");
        Row(L.T("抖动", "Wobble"), 0, 64, l.Wobble, 0, (x, v) => x with { Wobble = v }, Px);
        Row(L.T("色彩分离", "Color Split"), 0, 16, l.Split, 0, (x, v) => x with { Split = v }, Px);
        Row(L.T("密度", "Density"), -100, 100, l.Density, 0, (x, v) => x with { Density = v });
        Row(L.T("对比度", "Contrast"), -100, 100, l.Contrast, 0, (x, v) => x with { Contrast = v });
        Row(L.T("黑色色阶", "Black Level"), 0, 100, l.BlackLevel, 0, (x, v) => x with { BlackLevel = v }, "%");

        var colors = Combo(DitherColorNames, (int)l.Colors, 120);
        colors.SelectionChanged += (_, _) => { if (colors.SelectedIndex >= 0) SetLines(x => x with { Colors = (DitherColors)colors.SelectedIndex }); };
        _body.Children.Add(Line(Caption(L.T("颜色", "Colors")), colors));
        var (_, wellsLine) = TwoColors(l.Dark, l.Light,
            c => SetLines(x => x with { Dark = AdjustmentColor.From(c) }), c => SetLines(x => x with { Light = AdjustmentColor.From(c) }),
            L.T("屏幕颜色", "Screen Color"), L.T("线条颜色", "Line Color"));
        _refresh = () => wellsLine.Visibility = Lines().Colors == DitherColors.TwoColors ? Visibility.Visible : Visibility.Collapsed;
        _refresh();
    }

    /// <summary>The Dark and Light color wells of Dither's and Scanlines' Two Colors.</summary>
    private ((ColorWell Dark, ColorWell Light), StackPanel) TwoColors(AdjustmentColor dark, AdjustmentColor light,
        Action<Core.Model.PaletteColor> setDark, Action<Core.Model.PaletteColor> setLight, string? darkTip = null, string? lightTip = null)
    {
        darkTip ??= L.T("暗色", "Dark Color");
        lightTip ??= L.T("亮色", "Light Color");
        var darkWell = new ColorWell(dark.ToPalette()) { PickerTitle = L.T($"拾色器（{darkTip}）", $"Color Picker ({darkTip})"), ToolTip = darkTip };
        var lightWell = new ColorWell(light.ToPalette()) { PickerTitle = L.T($"拾色器（{lightTip}）", $"Color Picker ({lightTip})"), ToolTip = lightTip };
        darkWell.ColorChanged += setDark;
        lightWell.ColorChanged += setLight;
        var line = Line(Caption(L.T("暗", "Dark")), darkWell, new Border { Width = 18 }, Caption(L.T("亮", "Light"), L.IsEnglish ? 40 : 30), lightWell);
        _body.Children.Add(line);
        return ((darkWell, lightWell), line);
    }
}
