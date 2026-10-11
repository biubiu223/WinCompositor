using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Compositor.App.Controls;
using Compositor.Core.Editing;
using Compositor.Core.Localization;
using Compositor.Core.Model;

namespace Compositor.App.Panels;

/// <summary>
/// One layer effect's settings, bound to the layer that opened it; changes show on the canvas as they're made. OK
/// keeps them as one undo step, Cancel or closing puts the effect back. Mirrors EffectsSheet.swift.
/// </summary>
public sealed class EffectPanel : Window, IEditPanel
{
    private static Point? _lastPosition;

    private readonly EditorSession _session;
    private readonly EffectEdit _edit;
    private LayerEffect _effect;
    private readonly StackPanel _body = new() { Margin = new Thickness(18, 14, 18, 6) };
    private readonly Button _okButton;
    private bool _finished;

    public EffectPanel(Window owner, EditorSession session)
    {
        _session = session;
        _edit = session.EffectEdit ?? throw new InvalidOperationException("No effect is being edited.");
        _effect = session.EditingEffect ?? LayerEffect.Default(_edit.Kind, session.BackgroundColor);
        Owner = owner;
        Title = L.T("图层样式 – ", "Layer Style – ") + _edit.Kind.ToDisplayName();
        SetResourceReference(StyleProperty, "DarkWindow");
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SourceInitialized += (_, _) => Services.DarkTitleBar.Apply(this);
        InputMethod.SetIsInputMethodEnabled(this, false);

        Build();

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
        root.Children.Add(_body);
        Content = root;

        Loaded += (_, _) => { Place(owner); _okButton.Focus(); };
        Closing += (_, _) => { _lastPosition = new Point(Left, Top); if (!_finished) Finish(commit: false, closing: true); };
        session.PropertyChanged += OnSessionProperty;
        Closed += (_, _) => session.PropertyChanged -= OnSessionProperty;
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
        if (e.PropertyName == nameof(EditorSession.EffectEdit) && !ReferenceEquals(_session.EffectEdit, _edit) && !_finished)
        {
            _finished = true;
            Close();
        }
    }

    public void Finish(bool commit, bool closing = false)
    {
        if (_finished) return;
        _finished = true;
        if (commit) _session.CommitEffectEdit();
        else _session.CancelEffectEdit();
        if (!closing) Close();
        Owner?.Activate();
    }

    private void Set(LayerEffect effect)
    {
        _effect = effect;
        _session.UpdateEffect(effect);
    }

    // MARK: Building

    private SliderRow Slider(string label, double min, double max, double value, double @default, Func<double, LayerEffect> change,
                             string unit = "", double? inputMaximum = null, Brush? track = null)
    {
        var row = new SliderRow(label, min, max, value, @default, 0, unit, track: track, labelWidth: 70, inputMaximum: inputMaximum) { Width = 380 };
        row.ValueChanged += v => Set(change(v));
        _body.Children.Add(row);
        return row;
    }

    private void Opacity() =>
        Slider(L.T("不透明度", "Opacity"), 0, 100, Math.Round(_effect.Opacity * 100), Math.Round(LayerEffect.Default(_edit.Kind, PaletteColor.Black).Opacity * 100),
               v => _effect with { Opacity = v / 100 }, "%");

    private void ColorRow(params UIElement[] more)
    {
        var kind = _edit.Kind.ToDisplayName();
        var well = new ColorWell(_effect.Color) { PickerTitle = L.T($"拾色器（{kind}颜色）", $"Color Picker ({kind} Color)") };
        well.ColorChanged += c => Set(_effect.WithColor(c));
        var label = new TextBlock { Text = L.T("颜色", "Color"), Width = 70, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextDimBrush");
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        row.Children.Add(label);
        row.Children.Add(well);
        foreach (var element in more) row.Children.Add(element);
        _body.Children.Add(row);
    }

    private void Build()
    {
        var defaults = LayerEffect.Default(_edit.Kind, PaletteColor.Black);
        switch (_edit.Kind)
        {
            case LayerEffectKind.Stroke:
            {
                var outside = new RadioButton { Content = L.T("外部", "Outside"), GroupName = "position" + GetHashCode(), IsChecked = !_effect.Inside, Margin = new Thickness(24, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
                var inside = new RadioButton { Content = L.T("内部", "Inside"), GroupName = "position" + GetHashCode(), IsChecked = _effect.Inside, VerticalAlignment = VerticalAlignment.Center };
                outside.Checked += (_, _) => Set(_effect with { Inside = false });
                inside.Checked += (_, _) => Set(_effect with { Inside = true });
                ColorRow(outside, inside);
                Slider(L.T("大小", "Size"), 0, 50, _effect.Size, defaults.Size, v => _effect with { Size = v }, L.T("像素", "px"), LayerEffect.MaxSize);
                Opacity();
                break;
            }
            case LayerEffectKind.Shadow:
            case LayerEffectKind.InnerShadow:
                ColorRow();
                Opacity();
                Slider(L.T("角度", "Angle"), -180, 180, _effect.Angle, 90, v => _effect with { Angle = v }, "°");
                Slider(L.T("距离", "Distance"), 0, _edit.Kind == LayerEffectKind.Shadow ? 100 : 50, _effect.Distance, defaults.Distance,
                       v => _effect with { Distance = v }, L.T("像素", "px"), LayerEffect.MaxDistance);
                Slider(L.T("大小", "Size"), 0, 100, _effect.Blur, defaults.Blur, v => _effect with { Blur = v }, L.T("像素", "px"), LayerEffect.MaxSize);
                _body.Children.Add(Note(_edit.Kind == LayerEffectKind.Shadow
                    ? L.T("角度是光线的方向：90° 时光从正上方照下，投影落在下方。", "The angle is the light's direction: at 90° it shines straight down from above, so the shadow falls below.")
                    : L.T("角度是光线的方向：90° 时光从正上方照下，阴影出现在内侧上缘。", "The angle is the light's direction: at 90° it shines straight down from above, so the shadow lines the inside top edge.")));
                break;
            case LayerEffectKind.OuterGlow:
            case LayerEffectKind.InnerGlow:
                ColorRow();
                Slider(L.T("大小", "Size"), 0, 100, _effect.Size, defaults.Size, v => _effect with { Size = v }, L.T("像素", "px"), LayerEffect.MaxSize);
                Opacity();
                break;
            default:
                ColorRow();
                Opacity();
                break;
        }
        if (_session.Document?.Find(_edit.LayerId) is { } layer)
            _body.Children.Add(Note(L.T("图层：", "Layer: ") + layer.Name));
    }

    private static TextBlock Note(string text)
    {
        var note = new TextBlock { Text = text, Margin = new Thickness(0, 2, 0, 6), TextWrapping = TextWrapping.Wrap, MaxWidth = 380 };
        note.SetResourceReference(TextBlock.ForegroundProperty, "TextDimBrush");
        return note;
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
