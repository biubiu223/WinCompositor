using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Compositor.App.Controls;
using Compositor.Core.Editing;
using Compositor.Core.Localization;

namespace Compositor.App.Panels;

/// <summary>
/// Select ▸ Color Range's panel: the eyedroppers, Fuzziness and Invert, with the selection updating on the canvas as
/// colors are clicked there. OK keeps it as one undo step; Cancel puts back the selection there was. Mirrors
/// ColorRangeSheet.swift.
/// </summary>
public sealed class ColorRangePanel : Window, IEditPanel
{
    private static Point? _lastPosition;

    private readonly EditorSession _session;
    private readonly ColorRangeEdit _edit;
    private readonly List<(ToggleButton Button, ColorRangeSampleMode Mode)> _modes = new();
    private readonly Border _previewFrame;
    private readonly Image _preview = new() { Stretch = Stretch.Fill };
    private readonly TextBlock _hint;
    private readonly TextBlock _error;
    private readonly SliderRow _fuzziness;
    private readonly CheckBox _invert;
    private readonly Button _okButton;
    private bool _finished;

    public ColorRangePanel(Window owner, EditorSession session)
    {
        _session = session;
        _edit = session.ColorRange ?? throw new InvalidOperationException("Color Range is not open.");
        Owner = owner;
        Title = L.T("色彩范围", "Color Range");
        SetResourceReference(StyleProperty, "DarkWindow");
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SourceInitialized += (_, _) => Services.DarkTitleBar.Apply(this);
        InputMethod.SetIsInputMethodEnabled(this, false);

        // The three eyedropper buttons' English names are longer: the panel is a little wider for them.
        double width = L.IsEnglish ? 370 : 340;
        var body = new StackPanel { Margin = new Thickness(18, 14, 18, 6), Width = width };

        // The eyedroppers: what a click on the image does (Shift adds and Alt takes away, whichever is chosen).
        var modes = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        foreach (var (mode, text, tip) in new[]
                 {
                     (ColorRangeSampleMode.Replace, L.T("取样", "Sample"), L.T("单击图像，选择那种颜色", "Click the image to select that color")),
                     (ColorRangeSampleMode.Add, L.T("添加到取样 +", "Add to Sample +"), L.T("单击图像，把那种颜色加入选区（或按住 Shift 单击）", "Click the image to add that color to the selection (or Shift-click)")),
                     (ColorRangeSampleMode.Remove, L.T("从取样中减去 −", "Subtract from Sample −"), L.T("单击图像，把那种颜色从选区中去掉（或按住 Alt 单击）", "Click the image to take that color out of the selection (or Alt-click)")),
                 })
        {
            var button = new ToggleButton
            {
                Content = text, ToolTip = tip, Style = (Style)FindResource("FlatButton"),
                Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 4, 0), BorderThickness = new Thickness(1),
            };
            button.SetResourceReference(BorderBrushProperty, "BorderBrush");
            var m = mode;
            button.Click += (_, _) => { _edit.SampleMode = m; ShowMode(); };
            _modes.Add((button, mode));
            modes.Children.Add(button);
        }
        body.Children.Add(modes);

        // The selection in black and white, shaped like the canvas.
        double scale = Math.Min(ColorRangeEdit.PreviewWidth / (double)Math.Max(1, session.Document!.Width),
                                ColorRangeEdit.PreviewHeight / (double)Math.Max(1, session.Document.Height));
        _previewFrame = new Border
        {
            Width = Math.Max(1, session.Document.Width * scale), Height = Math.Max(1, session.Document.Height * scale),
            Background = Brushes.Black, BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 10), Child = _preview,
        };
        RenderOptions.SetBitmapScalingMode(_preview, BitmapScalingMode.HighQuality);
        body.Children.Add(_previewFrame);

        _hint = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        _hint.SetResourceReference(TextBlock.ForegroundProperty, "TextDimBrush");
        body.Children.Add(_hint);

        _fuzziness = new SliderRow(L.T("颜色容差", "Fuzziness"), 0, ColorRangeEdit.MaxFuzziness, _edit.Fuzziness, 40, labelWidth: 70) { Width = width };
        _fuzziness.ToolTip = L.T("颜色与取样颜色相差多少仍会被选中", "How far a color may be from the picked ones and still be selected");
        _fuzziness.ValueChanged += v => _session.SetColorRangeOptions(v, _invert!.IsChecked == true);
        body.Children.Add(_fuzziness);

        _invert = new CheckBox { Content = L.T("反相", "Invert"), IsChecked = _edit.Invert, Margin = new Thickness(0, 2, 0, 8), ToolTip = L.T("选择取样颜色以外的一切，比如绿幕以外的部分", "Select everything except those colors, such as all but a green screen") };
        _invert.Click += (_, _) => _session.SetColorRangeOptions(_fuzziness.Value, _invert.IsChecked == true);
        body.Children.Add(_invert);

        _error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Orange, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 8) };
        body.Children.Add(_error);

        _okButton = new Button { Content = L.T("确定", "OK"), IsDefault = true, MinWidth = 84, Margin = new Thickness(8, 0, 0, 0) };
        var cancel = new Button { Content = L.T("取消", "Cancel"), IsCancel = true, MinWidth = 84 };
        _okButton.Click += (_, _) => Finish(commit: true);
        cancel.Click += (_, _) => Finish(commit: false);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(18, 4, 18, 16) };
        footer.Children.Add(cancel);
        footer.Children.Add(_okButton);
        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(body);
        Content = root;

        // Clicks on the canvas pick colors while the panel is open.
        session.CanvasSampler = point =>
        {
            var modifiers = Keyboard.Modifiers;
            _session.SampleColorRange(point, modifiers.HasFlag(ModifierKeys.Shift), modifiers.HasFlag(ModifierKeys.Alt));
        };
        ShowMode();
        Refresh();

        Loaded += (_, _) => { Place(owner); _okButton.Focus(); };
        Closing += (_, _) => { _lastPosition = new Point(Left, Top); if (!_finished) Finish(commit: false, closing: true); };
        session.PropertyChanged += OnSessionProperty;
        session.ColorRangeChanged += OnColorRangeChanged;
        Closed += (_, _) =>
        {
            session.PropertyChanged -= OnSessionProperty;
            session.ColorRangeChanged -= OnColorRangeChanged;
        };
    }

    private void Place(Window owner)
    {
        if (_lastPosition is { } p) { Left = p.X; Top = p.Y; }
        else
        {
            Left = owner.Left + Math.Max(40, owner.ActualWidth - ActualWidth - 330);
            Top = owner.Top + 110;
        }
        var area = SystemParameters.WorkArea;
        Left = Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - ActualWidth));
        Top = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - ActualHeight));
    }

    private void OnSessionProperty(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorSession.ColorRange) && !ReferenceEquals(_session.ColorRange, _edit) && !_finished)
        {
            _finished = true;
            if (_session.CanvasSampler is not null) _session.CanvasSampler = null;
            Close();
        }
    }

    private void OnColorRangeChanged(object? sender, EventArgs e) => Refresh();

    private void ShowMode()
    {
        foreach (var (button, mode) in _modes)
        {
            bool on = mode == _edit.SampleMode;
            button.IsChecked = on;
            if (on) button.Background = (Brush)FindResource("SelectionBrush");
            else button.ClearValue(BackgroundProperty);
        }
    }

    private void Refresh()
    {
        _hint.Text = _edit.HasColors
            ? L.T("按住 Shift 单击添加颜色，按住 Alt 单击去掉颜色。", "Shift-click adds a color, Alt-click takes one away.")
            : L.T("单击图像，选取要选择的颜色。", "Click the image to pick the color to select.");
        _error.Text = _edit.Error ?? "";
        _error.Visibility = _edit.Error is null ? Visibility.Collapsed : Visibility.Visible;
        if (_edit.Preview is { } pixels && _edit.PreviewPixelWidth > 0)
        {
            var bitmap = BitmapSource.Create(_edit.PreviewPixelWidth, _edit.PreviewPixelHeight, 96, 96, PixelFormats.Gray8, null, pixels, _edit.PreviewPixelWidth);
            bitmap.Freeze();
            _preview.Source = bitmap;
        }
        else _preview.Source = null;
    }

    public void Finish(bool commit, bool closing = false)
    {
        if (_finished) return;
        _finished = true;
        _session.CanvasSampler = null;
        if (commit) _session.CommitColorRange();
        else _session.CancelColorRange();
        if (!closing) Close();
        Owner?.Activate();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Escape)
        {
            if (Keyboard.FocusedElement is TextBox) { _okButton.Focus(); e.Handled = true; return; }
            Finish(commit: false);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            if (Keyboard.FocusedElement is TextBox) _okButton.Focus();
            Finish(commit: true);
            e.Handled = true;
        }
    }
}
