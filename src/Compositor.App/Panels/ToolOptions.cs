using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Compositor.App.Controls;
using Compositor.Core.Editing;
using Compositor.Core.Localization;
using Compositor.Core.Model;
using Compositor.Core.Text;
using static Compositor.App.Panels.OptionsBarBuilder;

namespace Compositor.App.Panels;

internal static class Swatches
{
    public static Brush Brush(PaletteColor c, byte alpha = 255) => new SolidColorBrush(Color.FromArgb(alpha, c.R8, c.G8, c.B8));

    /// <summary>A color well that opens the picker; <paramref name="pick"/> receives the chosen color.</summary>
    public static Border Well(Func<PaletteColor> color, Action<PaletteColor> pick, string title, string tip)
    {
        var well = new Border
        {
            Width = 36, Height = 20, CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1), Cursor = Cursors.Hand, ToolTip = tip,
            Background = Brush(color()),
        };
        well.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        well.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            var dialog = new Dialogs.ColorPickerDialog(color(), title) { Owner = Window.GetWindow(well) };
            if (dialog.ShowDialog() == true) pick(dialog.Color);
            well.Background = Brush(color());
        };
        return well;
    }
}

/// <summary>The Gradient tool's bar. Mirrors GradientControls.swift.</summary>
internal sealed class GradientOptions : StackPanel, IRefreshable
{
    private readonly EditorSession _session;
    private readonly Border _swatch = new() { Width = 64, Height = 18, CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1) };
    private readonly StackPanel _pending;
    private readonly NumberField _opacity = new(L.T("不透明度", "Opacity"), 54) { Minimum = 1, Maximum = 100, Suffix = "%" };

    public GradientOptions(EditorSession session)
    {
        _session = session;
        Orientation = Orientation.Horizontal;
        _swatch.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        // "Foreground to Transparent" needs more room than the Chinese.
        var style = new ComboBox { Width = L.IsEnglish ? 190 : 150, Focusable = false };
        style.Items.Add(L.T("前景色到背景色", "Foreground to Background"));
        style.Items.Add(L.T("前景色到透明", "Foreground to Transparent"));
        style.SelectedIndex = (int)session.GradientSettings.Style;
        style.SelectionChanged += (_, _) => session.GradientSettings = session.GradientSettings with { Style = (GradientStyle)style.SelectedIndex };
        _opacity.Value = Math.Round(session.GradientSettings.Opacity * 100);
        _opacity.ValueChanged += v => session.GradientSettings = session.GradientSettings with { Opacity = v / 100 };
        _pending = Bar(Action(L.T("取消", "Cancel"), session.CancelGradient), Action(L.T("应用", "Apply"), session.CommitGradient, L.T("回车", "Enter")));
        var bar = Bar(
            Title(L.T("渐变", "Gradient")),
            Segments(new[] { L.T("线性", "Linear"), L.T("径向", "Radial") }, (int)session.GradientSettings.Shape, i => session.GradientSettings = session.GradientSettings with { Shape = (GradientShape)i }),
            _swatch, style,
            Check(L.T("反向", "Reverse"), session.GradientSettings.Reversed, v => session.GradientSettings = session.GradientSettings with { Reversed = v }),
            _opacity,
            _pending);
        Children.Add(bar);
        Refresh();
    }

    public void Refresh()
    {
        var (from, to) = _session.GradientColors(mask: false);
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(from.Alpha, from.Red, from.Green, from.Blue), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(to.Alpha, to.Red, to.Green, to.Blue), 1));
        _swatch.Background = brush;
        _pending.Visibility = _session.GradientEdit is null ? Visibility.Collapsed : Visibility.Visible;
    }
}

/// <summary>The Shape tool's bar. Mirrors ShapeControls.swift.</summary>
internal sealed class ShapeOptions : StackPanel, IRefreshable
{
    private readonly EditorSession _session;
    private readonly Border _fill;

    public ShapeOptions(EditorSession session)
    {
        _session = session;
        Orientation = Orientation.Horizontal;
        var kind = Segments(new[] { L.T("矩形", "Rectangle"), L.T("椭圆", "Ellipse"), L.T("直线", "Line") }, (int)session.ShapeKind, i => session.ShapeKind = (ShapeKind)i);
        FrameworkElement size;
        if (session.ShapeKind == ShapeKind.Line)
        {
            var width = new NumberField(L.T("粗细", "Width"), 64) { Minimum = 1, Maximum = 5000, Value = session.ShapeLineWidth, Suffix = " px" };
            width.ValueChanged += v => session.ShapeLineWidth = v;
            size = width;
        }
        else if (session.ShapeKind == ShapeKind.Rectangle)
        {
            var radius = new NumberField(L.T("圆角", "Radius"), 64) { Minimum = 0, Maximum = 5000, Value = session.ShapeCornerRadius, Suffix = " px", ToolTip = L.T("圆角半径；0 为直角", "Round the corners by this many pixels; 0 keeps them square") };
            radius.ValueChanged += v => session.ShapeCornerRadius = v;
            size = radius;
        }
        else size = new Border { Width = 0 };
        _fill = Swatches.Well(() => session.ForegroundColor, c => session.ForegroundColor = c, L.T("拾色器（前景色）", "Color Picker (Foreground Color)"), L.T("形状用前景色填充；单击更改", "Shapes fill with the foreground color; click to change it"));
        var fillLabel = Hint(L.T("填充", "Fill"));
        fillLabel.Margin = new Thickness(0, 0, 6, 0);
        Children.Add(Bar(Title(L.T("形状", "Shape")), kind, size, fillLabel, _fill, Hint(L.T("Shift 正方形/圆形 · Alt 从中心 · Shift+U 切换", "Shift: square/circle · Alt: from center · Shift+U: switch shape"))));
    }

    public void Refresh() => _fill.Background = Swatches.Brush(_session.ForegroundColor);
}

/// <summary>The Type tool's bar: face, size, color, alignment, tracking and leading. Mirrors TypeControls.swift.</summary>
internal sealed class TypeOptions : StackPanel, IRefreshable
{
    private static IReadOnlyList<string>? _families;
    private readonly EditorSession _session;
    private readonly ComboBox _font = new() { Width = 190, Focusable = false, IsTextSearchEnabled = true, MaxDropDownHeight = 420 };
    private readonly NumberField _size = new(L.T("大小", "Size"), 64) { Minimum = 1, Maximum = 2000, Suffix = " px" };
    private readonly NumberField _tracking = new(L.T("字距", "Tracking"), 44) { Minimum = -100, Maximum = 1000 };
    private readonly NumberField _leading = new(L.T("行距", "Leading"), 50) { Minimum = 0, Maximum = 5000, ToolTip = L.T("行与行基线之间的距离；0 为自动（字号的 120%）", "Line height, baseline to baseline; 0 is Auto (120% of the font size)") };
    private readonly Border _color;
    private readonly StackPanel _align;
    private readonly StackPanel _editing;
    private bool _updating;

    public TypeOptions(EditorSession session)
    {
        _session = session;
        Orientation = Orientation.Horizontal;
        _families ??= TextLayout.Families();
        foreach (var family in _families) _font.Items.Add(family);
        _font.SelectionChanged += (_, _) =>
        {
            if (_updating || _font.SelectedItem is not string name) return;
            session.ChangeTextStyle((s, start, length) => s.SettingFont(name, start, length));
        };
        _size.ValueChanged += v => session.ChangeTextStyle((s, _, _) => s with { FontSize = v });
        _tracking.ValueChanged += v => session.ChangeTextStyle((s, _, _) => s with { Tracking = v });
        _leading.ValueChanged += v => session.ChangeTextStyle((s, _, _) => s with { Leading = v });
        _color = Swatches.Well(() =>
        {
            var style = session.CurrentTextStyle;
            return session.TextDraft is { } d ? style.ColorAt(d.SelectionLength > 0 ? d.SelectionStart : Math.Max(0, d.SelectionStart - 1)) : style.Color;
        }, c => session.ChangeTextStyle((s, start, length) => s.SettingColor(c, start, length)), L.T("拾色器（文字颜色）", "Color Picker (Text Color)"), L.T("文字颜色；正在输入时只改选中的文字", "Text color; while typing, it changes only the selected text"));
        _align = Segments(new[] { L.T("左", "Left"), L.T("中", "Center"), L.T("右", "Right") }, (int)session.CurrentTextStyle.Alignment,
            i => { if (!_updating) session.ChangeTextStyle((s, _, _) => s with { Alignment = (TextAlign)i }); });
        _editing = Bar(Action(L.T("取消", "Cancel"), session.CancelText), Action(L.T("应用", "Apply"), () => session.CommitText(), L.T("Esc 或 Ctrl+回车", "Esc or Ctrl+Enter")));
        Children.Add(Bar(Title(L.T("文字", "Type")), _font, _size, _color, _align, _tracking, _leading, _editing));
        Refresh();
    }

    public void Refresh()
    {
        _updating = true;
        var style = _session.CurrentTextStyle;
        string font = style.FontName;
        if (_session.TextDraft is { } draft)
            font = draft.Style.UniformFont(draft.SelectionLength > 0 ? draft.SelectionStart : Math.Max(0, draft.SelectionStart - 1), Math.Max(1, draft.SelectionLength)) ?? style.FontName;
        if (!_font.Items.Contains(font)) _font.Items.Insert(0, font);
        _font.SelectedItem = font;
        _size.Value = style.FontSize;
        _tracking.Value = style.Tracking;
        _leading.Value = style.Leading;
        int align = (int)style.Alignment;
        for (int i = 0; i < _align.Children.Count; i++) ((RadioButton)_align.Children[i]).IsChecked = i == align;
        var current = _session.TextDraft is { } d ? style.ColorAt(d.SelectionLength > 0 ? d.SelectionStart : Math.Max(0, d.SelectionStart - 1)) : style.Color;
        _color.Background = Swatches.Brush(current);
        _editing.Visibility = _session.TextDraft is null ? Visibility.Collapsed : Visibility.Visible;
        _updating = false;
    }
}
