using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Compositor.App.Services;
using Compositor.Core.Localization;
using Compositor.Core.Model;

namespace Compositor.App.Dialogs;

/// <summary>Widens the label column of rows built with <see cref="DialogBase"/>'s Row, for labels that run longer in English.</summary>
file static class RowLabels
{
    public static void SetWidth(double width, params StackPanel[] rows)
    {
        foreach (var row in rows)
            if (row.Children.Count > 0 && row.Children[0] is TextBlock label) label.Width = width;
    }
}

/// <summary>The messages the size dialogs share.</summary>
file static class SizeMessages
{
    public static string SizeRange => L.T($"宽度和高度必须在 1 到 {DocumentLimits.MaxSide:N0} 像素之间。",
        $"Width and height must be between 1 and {DocumentLimits.MaxSide:N0} pixels.");

    public static string ResolutionRange => L.T("分辨率必须在 1 到 9600 像素/英寸之间。", "Resolution must be between 1 and 9,600 pixels/inch.");
}

/// <summary>The New Canvas fields, shared by the dialog and the welcome screen. Mirrors NewCanvasSheet.swift.</summary>
public sealed class NewCanvasForm : StackPanel
{
    private readonly TextBox _width, _height, _resolution;
    private readonly ComboBox _background, _preset;
    private static readonly (string Name, int W, int H, double Ppi)[] Presets =
    {
        (L.T("自定义", "Custom"), 0, 0, 0),
        (L.T("全高清 1920 × 1080", "Full HD 1920 × 1080"), 1920, 1080, 72),
        ("4K UHD 3840 × 2160", 3840, 2160, 72),
        (L.T("方形 1080 × 1080", "Square 1080 × 1080"), 1080, 1080, 72),
        (L.T("竖屏 1080 × 1920", "Portrait 1080 × 1920"), 1080, 1920, 72),
        (L.T("手机 1170 × 2532", "Phone 1170 × 2532"), 1170, 2532, 72),
        ("A4 300 ppi 2480 × 3508", 2480, 3508, 300),
        ("A3 300 ppi 3508 × 4961", 3508, 4961, 300),
        (L.T("网页横幅 1200 × 628", "Web Banner 1200 × 628"), 1200, 628, 72),
    };

    public NewCanvasForm()
    {
        var settings = Settings.Current;
        _preset = new ComboBox { Width = 220 };
        foreach (var p in Presets) _preset.Items.Add(p.Name);
        _preset.SelectedIndex = 0;
        _width = new TextBox { Text = settings.NewWidth.ToString(), Width = 90 };
        _height = new TextBox { Text = settings.NewHeight.ToString(), Width = 90 };
        _resolution = new TextBox { Text = settings.NewResolution.ToString(CultureInfo.InvariantCulture), Width = 90 };
        _background = new ComboBox { Width = 140 };
        foreach (var name in new[] { L.T("白色", "White"), L.T("黑色", "Black"), L.T("透明", "Transparent") }) _background.Items.Add(name);
        _background.SelectedIndex = settings.NewBackground switch { "black" => 1, "transparent" => 2, _ => 0 };
        _preset.SelectionChanged += (_, _) =>
        {
            var p = Presets[Math.Max(0, _preset.SelectedIndex)];
            if (p.W == 0) return;
            _width.Text = p.W.ToString();
            _height.Text = p.H.ToString();
            _resolution.Text = p.Ppi.ToString(CultureInfo.InvariantCulture);
        };
        Children.Add(Row(L.T("预设", "Preset"), _preset));
        Children.Add(Row(L.T("宽度", "Width"), _width, Unit(L.T("像素", "pixels"))));
        Children.Add(Row(L.T("高度", "Height"), _height, Unit(L.T("像素", "pixels"))));
        Children.Add(Row(L.T("分辨率", "Resolution"), _resolution, Unit(L.T("像素/英寸", "pixels/inch"))));
        Children.Add(Row(L.T("背景", "Background"), _background));
    }

    private static TextBlock Unit(string text) => new() { Text = text, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)Application.Current.FindResource("TextDimBrush") };

    private static StackPanel Row(string label, params UIElement[] controls)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        // "Background" fills the Chinese column's 70 pixels: English gets a little more.
        row.Children.Add(new TextBlock { Text = label, Width = L.IsEnglish ? 84 : 70, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)Application.Current.FindResource("TextDimBrush") });
        foreach (var c in controls) row.Children.Add(c);
        return row;
    }

    /// <summary>The values, or an error message when they are out of range.</summary>
    public (int Width, int Height, double Resolution, PaletteColor? Background)? Read(out string? error)
    {
        error = null;
        if (!int.TryParse(_width.Text.Trim(), out var w) || !int.TryParse(_height.Text.Trim(), out var h)
            || w < 1 || h < 1 || w > DocumentLimits.MaxSide || h > DocumentLimits.MaxSide)
        {
            error = SizeMessages.SizeRange;
            return null;
        }
        if ((long)w * h > DocumentLimits.DocumentPixelBudget)
        {
            error = L.T($"画布超过了 {DocumentLimits.DocumentBudgetMegapixels} 百万像素的上限。",
                $"The canvas is over the {DocumentLimits.DocumentBudgetMegapixels}-megapixel limit.");
            return null;
        }
        if (!double.TryParse(_resolution.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var r) || r < 1 || r > 9600)
        {
            error = SizeMessages.ResolutionRange;
            return null;
        }
        PaletteColor? background = _background.SelectedIndex switch { 1 => PaletteColor.Black, 2 => null, _ => PaletteColor.White };
        var settings = Settings.Current;
        settings.NewWidth = w;
        settings.NewHeight = h;
        settings.NewResolution = r;
        settings.NewBackground = _background.SelectedIndex switch { 1 => "black", 2 => "transparent", _ => "white" };
        settings.Save();
        return (w, h, r, background);
    }
}

public sealed class NewCanvasDialog : DialogBase
{
    private readonly NewCanvasForm _form = new();
    public int CanvasWidth { get; private set; }
    public int CanvasHeight { get; private set; }
    public double Resolution { get; private set; }
    public PaletteColor? BackgroundColor { get; private set; }

    public NewCanvasDialog() : base(L.T("新建画布", "New Canvas"), L.T("创建", "Create"))
    {
        Body.Children.Add(_form);
    }

    protected override bool Validate()
    {
        if (_form.Read(out var error) is not { } values) { Warn(error!); return false; }
        (CanvasWidth, CanvasHeight, Resolution, BackgroundColor) = values;
        return true;
    }
}

/// <summary>Image › Canvas Size. Mirrors CanvasSizeSheet.swift.</summary>
public sealed class CanvasSizeDialog : DialogBase
{
    private readonly CanvasDocument _document;
    private readonly TextBox _width, _height;
    private readonly ComboBox _unit, _fill;
    private readonly CheckBox _relative;
    private readonly RadioButton[] _anchors = new RadioButton[9];
    private readonly PaletteColor _foreground, _background;
    /// <summary>The units the fields are in, by their place in the Units menu (whatever its language).</summary>
    private enum SizeUnit { Pixels, Percent, Inches, Centimeters }
    private SizeUnit _lastUnit = SizeUnit.Pixels;

    public int CanvasWidth { get; private set; }
    public int CanvasHeight { get; private set; }
    public int Anchor { get; private set; } = 4;
    public PaletteColor? Fill { get; private set; }

    public CanvasSizeDialog(CanvasDocument document, PaletteColor foreground, PaletteColor background) : base(L.T("画布大小", "Canvas Size"))
    {
        _document = document;
        _foreground = foreground;
        _background = background;
        Body.Children.Add(Note(L.T($"当前大小：{document.Width} × {document.Height} 像素", $"Current size: {document.Width} × {document.Height} pixels")));
        _width = new TextBox { Text = document.Width.ToString(), Width = 90 };
        _height = new TextBox { Text = document.Height.ToString(), Width = 90 };
        _unit = new ComboBox { Width = L.IsEnglish ? 110 : 90, Margin = new Thickness(8, 0, 0, 0) };
        foreach (var u in new[] { L.T("像素", "Pixels"), L.T("百分比", "Percent"), L.T("英寸", "Inches"), L.T("厘米", "Centimeters") }) _unit.Items.Add(u);
        _unit.SelectedIndex = 0;
        _unit.SelectionChanged += (_, _) => ConvertUnits();
        _relative = new CheckBox { Content = L.T("相对", "Relative"), Margin = new Thickness(0, 0, 0, 10) };
        _relative.Checked += (_, _) => ToggleRelative(true);
        _relative.Unchecked += (_, _) => ToggleRelative(false);
        var widthRow = Row(L.T("宽度", "Width"), _width, _unit);
        var heightRow = Row(L.T("高度", "Height"), _height);
        Body.Children.Add(_relative);
        var grid = new UniformGrid { Rows = 3, Columns = 3, Width = 84, Height = 84 };
        for (int i = 0; i < 9; i++)
        {
            _anchors[i] = new RadioButton { GroupName = "anchor", IsChecked = i == 4, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            grid.Children.Add(_anchors[i]);
        }
        var anchorRow = Row(L.T("定位", "Anchor"), grid);
        _fill = new ComboBox { Width = 140 };
        foreach (var f in new[] { L.T("透明", "Transparent"), L.T("前景色", "Foreground"), L.T("背景色", "Background"), L.T("白色", "White"), L.T("黑色", "Black") })
            _fill.Items.Add(f);
        _fill.SelectedIndex = 0;
        var fillRow = Row(L.T("画布扩展颜色", "Canvas Extension Color"), _fill);
        // "Canvas Extension Color" is wider than the 90-pixel label column.
        if (L.IsEnglish) RowLabels.SetWidth(144, widthRow, heightRow, anchorRow, fillRow);
    }

    private double Resolution => _document.Resolution;

    private double ToPixels(double value, bool widthAxis, SizeUnit unit) => unit switch
    {
        SizeUnit.Percent => value / 100 * (widthAxis ? _document.Width : _document.Height),
        SizeUnit.Inches => value * Resolution,
        SizeUnit.Centimeters => value / 2.54 * Resolution,
        _ => value,
    };

    private double FromPixels(double pixels, bool widthAxis, SizeUnit unit) => unit switch
    {
        SizeUnit.Percent => pixels / (widthAxis ? _document.Width : _document.Height) * 100,
        SizeUnit.Inches => pixels / Resolution,
        SizeUnit.Centimeters => pixels / Resolution * 2.54,
        _ => pixels,
    };

    private static double Parse(TextBox box) =>
        double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;

    private void ConvertUnits()
    {
        var next = (SizeUnit)Math.Max(0, _unit.SelectedIndex);
        double w = ToPixels(Parse(_width), true, _lastUnit), h = ToPixels(Parse(_height), false, _lastUnit);
        _lastUnit = next;
        _width.Text = Math.Round(FromPixels(w, true, next), 2).ToString(CultureInfo.InvariantCulture);
        _height.Text = Math.Round(FromPixels(h, false, next), 2).ToString(CultureInfo.InvariantCulture);
    }

    private void ToggleRelative(bool relative)
    {
        double w = ToPixels(Parse(_width), true, _lastUnit), h = ToPixels(Parse(_height), false, _lastUnit);
        w += relative ? -_document.Width : _document.Width;
        h += relative ? -_document.Height : _document.Height;
        _width.Text = Math.Round(FromPixels(w, true, _lastUnit), 2).ToString(CultureInfo.InvariantCulture);
        _height.Text = Math.Round(FromPixels(h, false, _lastUnit), 2).ToString(CultureInfo.InvariantCulture);
    }

    protected override bool Validate()
    {
        double w = ToPixels(Parse(_width), true, _lastUnit), h = ToPixels(Parse(_height), false, _lastUnit);
        if (_relative.IsChecked == true) { w += _document.Width; h += _document.Height; }
        int wi = (int)Math.Round(w), hi = (int)Math.Round(h);
        if (double.IsNaN(w) || double.IsNaN(h) || !CanvasDocument.ValidDimension(wi) || !CanvasDocument.ValidDimension(hi))
        {
            Warn(SizeMessages.SizeRange);
            return false;
        }
        CanvasWidth = wi;
        CanvasHeight = hi;
        Anchor = Array.FindIndex(_anchors, a => a.IsChecked == true);
        if (Anchor < 0) Anchor = 4;
        Fill = _fill.SelectedIndex switch
        {
            1 => _foreground,
            2 => _background,
            3 => PaletteColor.White,
            4 => PaletteColor.Black,
            _ => null,
        };
        return true;
    }
}

/// <summary>Image › Image Size. Mirrors ImageSizeSheet.swift.</summary>
public sealed class ImageSizeDialog : DialogBase
{
    private readonly CanvasDocument _document;
    private readonly TextBox _width, _height, _resolution;
    private readonly CheckBox _constrain;
    private readonly ComboBox _sampling;
    private bool _updating;

    public int ImageWidth { get; private set; }
    public int ImageHeight { get; private set; }
    public double Resolution { get; private set; }
    public LayerSampling Sampling { get; private set; } = LayerSampling.High;

    public ImageSizeDialog(CanvasDocument document) : base(L.T("图像大小", "Image Size"))
    {
        _document = document;
        Body.Children.Add(Note(L.T($"当前大小：{document.Width} × {document.Height} 像素，{document.Resolution:0.##} 像素/英寸",
            $"Current size: {document.Width} × {document.Height} pixels, {document.Resolution:0.##} pixels/inch")));
        _width = new TextBox { Text = document.Width.ToString(), Width = 90 };
        _height = new TextBox { Text = document.Height.ToString(), Width = 90 };
        _resolution = new TextBox { Text = document.Resolution.ToString("0.##", CultureInfo.InvariantCulture), Width = 90 };
        _constrain = new CheckBox { Content = L.T("约束比例", "Constrain Proportions"), IsChecked = true, Margin = new Thickness(0, 0, 0, 10) };
        // "Nearest Neighbor (Hard Edges)" needs more than the Chinese 160 pixels.
        _sampling = new ComboBox { Width = L.IsEnglish ? 220 : 160 };
        foreach (var s in new[] { L.T("高品质（两次立方）", "High Quality (Bicubic)"), L.T("平滑（两次线性）", "Smooth (Bilinear)"), L.T("邻近（硬边缘）", "Nearest Neighbor (Hard Edges)") })
            _sampling.Items.Add(s);
        _sampling.SelectedIndex = 0;
        Row(L.T("宽度", "Width"), _width, Unit(L.T("像素", "pixels")));
        Row(L.T("高度", "Height"), _height, Unit(L.T("像素", "pixels")));
        Body.Children.Add(_constrain);
        Row(L.T("分辨率", "Resolution"), _resolution, Unit(L.T("像素/英寸", "pixels/inch")));
        Row(L.T("重新采样", "Resample"), _sampling);
        _width.TextChanged += (_, _) => Follow(true);
        _height.TextChanged += (_, _) => Follow(false);
    }

    private static TextBlock Unit(string text) => new() { Text = text, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)Application.Current.FindResource("TextDimBrush") };

    private void Follow(bool fromWidth)
    {
        if (_updating || _constrain.IsChecked != true) return;
        _updating = true;
        if (fromWidth && int.TryParse(_width.Text, out var w))
            _height.Text = Math.Max(1, (int)Math.Round(w * (double)_document.Height / _document.Width)).ToString();
        else if (!fromWidth && int.TryParse(_height.Text, out var h))
            _width.Text = Math.Max(1, (int)Math.Round(h * (double)_document.Width / _document.Height)).ToString();
        _updating = false;
    }

    protected override bool Validate()
    {
        if (!TryInt(_width, 1, DocumentLimits.MaxSide, out var w) || !TryInt(_height, 1, DocumentLimits.MaxSide, out var h))
        {
            Warn(SizeMessages.SizeRange);
            return false;
        }
        if (!double.TryParse(_resolution.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var r) || r < 1 || r > 9600)
        {
            Warn(SizeMessages.ResolutionRange);
            return false;
        }
        ImageWidth = w;
        ImageHeight = h;
        Resolution = r;
        Sampling = _sampling.SelectedIndex switch { 1 => LayerSampling.Smooth, 2 => LayerSampling.Nearest, _ => LayerSampling.High };
        return true;
    }
}

/// <summary>One number: Expand, Contract and Feather Selection.</summary>
public sealed class AmountDialog : DialogBase
{
    private readonly TextBox _box;
    private readonly int _min, _max;
    public int Value { get; private set; }

    /// <param name="label">The field's name ("Feather Radius"); the title when null.</param>
    public AmountDialog(string title, string unit, int initial, int min, int max, string? label = null) : base(title)
    {
        _min = min;
        _max = max;
        _box = IntBox(initial);
        var row = Row(label ?? title, _box,
            new TextBlock { Text = unit, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        // "Feather Radius" all but fills the 90-pixel label column.
        if (L.IsEnglish) RowLabels.SetWidth(104, row);
        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }

    protected override bool Validate()
    {
        if (!TryInt(_box, _min, _max, out var value)) { Warn(L.T($"请输入 {_min} 到 {_max} 之间的整数。", $"Enter a whole number from {_min} to {_max}.")); return false; }
        Value = value;
        return true;
    }
}

/// <summary>Export JPEG's quality.</summary>
public sealed class JpegDialog : DialogBase
{
    private readonly Slider _slider;
    private readonly TextBlock _value;
    public int Quality { get; private set; }

    public JpegDialog(int quality) : base(L.T("导出 JPEG", "Export JPEG"), L.T("导出…", "Export…"))
    {
        _slider = new Slider { Minimum = 1, Maximum = 100, Value = quality, Width = 200, IsSnapToTickEnabled = true, TickFrequency = 1 };
        _value = new TextBlock { Width = 40, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        _slider.ValueChanged += (_, _) => _value.Text = ((int)_slider.Value).ToString();
        _value.Text = quality.ToString();
        Row(L.T("品质", "Quality"), _slider, _value);
        Body.Children.Add(Note(L.T("JPEG 不支持透明度：透明区域会以白色导出。", "JPEG doesn’t support transparency: transparent areas are exported as white.")));
    }

    protected override bool Validate()
    {
        Quality = (int)_slider.Value;
        return true;
    }
}

/// <summary>View › Grid Settings: spacing and subdivisions, shown live while editing.</summary>
public sealed class GridDialog : DialogBase
{
    private readonly TextBox _spacing, _subdivisions;
    public Action<int, int>? Preview { get; set; }

    public GridDialog(int spacing, int subdivisions) : base(L.T("网格设置", "Grid Settings"))
    {
        _spacing = IntBox(spacing);
        _subdivisions = IntBox(subdivisions);
        var spacingRow = Row(L.T("网格线间隔", "Gridline Every"), _spacing,
            new TextBlock { Text = L.T("像素", "pixels"), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        var subdivisionsRow = Row(L.T("子网格", "Subdivisions"), _subdivisions);
        // "Gridline Every" all but fills the 90-pixel label column.
        if (L.IsEnglish) RowLabels.SetWidth(104, spacingRow, subdivisionsRow);
        _spacing.TextChanged += (_, _) => Push();
        _subdivisions.TextChanged += (_, _) => Push();
    }

    private void Push()
    {
        if (TryInt(_spacing, 2, 4096, out var s) && TryInt(_subdivisions, 1, 64, out var d)) Preview?.Invoke(s, Math.Min(d, s));
    }

    protected override bool Validate()
    {
        if (!TryInt(_spacing, 2, 4096, out var s) || !TryInt(_subdivisions, 1, 64, out var d))
        {
            Warn(L.T("网格线间隔为 2–4096 像素，子网格为 1–64。", "Use gridlines every 2–4,096 pixels and 1–64 subdivisions."));
            return false;
        }
        Preview?.Invoke(s, Math.Min(d, s));
        return true;
    }
}

/// <summary>What opening a Photoshop file will convert, listed before anything is opened. Mirrors PSDConversionSheet.swift.</summary>
public sealed class PsdConversionDialog : DialogBase
{
    public PsdConversionDialog(string fileName, IReadOnlyList<Compositor.Core.IO.Psd.PsdConversion> conversions)
        : base(L.T($"打开“{fileName}”", $"Open “{fileName}”"), L.T("继续", "Continue"))
    {
        Body.Children.Add(Note(L.T("WinCompositor 会这样转换这些 Photoshop 功能。在你继续之前，什么都不会应用。",
            "WinCompositor will convert these Photoshop features. Nothing is applied until you continue.")));
        var list = new StackPanel();
        foreach (var item in conversions)
        {
            var name = new TextBlock { Text = item.LayerName, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 2) };
            var message = new TextBlock { Text = item.Message, TextWrapping = TextWrapping.Wrap };
            message.SetResourceReference(TextBlock.ForegroundProperty, "TextDimBrush");
            list.Children.Add(name);
            list.Children.Add(message);
        }
        var scroller = new ScrollViewer
        {
            Content = list, MaxHeight = 320, Width = 480, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(10, 4, 10, 8), Margin = new Thickness(0, 4, 0, 6),
        };
        var frame = new Border { Child = scroller, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4) };
        frame.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        Body.Children.Add(frame);
    }
}

/// <summary>Image ▸ Trim: what to measure against and which sides to cut. Mirrors TrimSheet.swift.</summary>
public sealed class TrimDialog : DialogBase
{
    private readonly RadioButton[] _based;
    private readonly CheckBox _top, _bottom, _left, _right;
    public Compositor.Core.Editing.TrimOptions Options { get; private set; } = new();

    public TrimDialog() : base(L.T("裁切", "Trim"))
    {
        Body.Children.Add(new TextBlock { Text = L.T("基于", "Based On"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        _based = new[] { L.T("透明像素", "Transparent Pixels"), L.T("左上角像素颜色", "Top Left Pixel Color"), L.T("右下角像素颜色", "Bottom Right Pixel Color") }
            .Select((text, i) => new RadioButton { Content = text, GroupName = "trim" + GetHashCode(), IsChecked = i == 0, Margin = new Thickness(0, 0, 0, 6) })
            .ToArray();
        foreach (var radio in _based) Body.Children.Add(radio);
        Body.Children.Add(new TextBlock { Text = L.T("裁掉", "Trim Away"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 6) });
        _top = new CheckBox { Content = L.T("顶", "Top"), IsChecked = true, Width = 90 };
        _bottom = new CheckBox { Content = L.T("底", "Bottom"), IsChecked = true };
        _left = new CheckBox { Content = L.T("左", "Left"), IsChecked = true, Width = 90 };
        _right = new CheckBox { Content = L.T("右", "Right"), IsChecked = true };
        var first = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        first.Children.Add(_top); first.Children.Add(_bottom);
        var second = new StackPanel { Orientation = Orientation.Horizontal };
        second.Children.Add(_left); second.Children.Add(_right);
        Body.Children.Add(first);
        Body.Children.Add(second);
        foreach (var box in new[] { _top, _bottom, _left, _right })
            box.Click += (_, _) => OkButton.IsEnabled = new[] { _top, _bottom, _left, _right }.Any(b => b.IsChecked == true);
    }

    protected override bool Validate()
    {
        int based = Array.FindIndex(_based, r => r.IsChecked == true);
        Options = new Compositor.Core.Editing.TrimOptions((Compositor.Core.Editing.TrimBasedOn)Math.Max(0, based),
            _top.IsChecked == true, _bottom.IsChecked == true, _left.IsChecked == true, _right.IsChecked == true);
        return Options.TrimsAny;
    }
}
