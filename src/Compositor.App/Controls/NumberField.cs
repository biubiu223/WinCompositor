using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Compositor.App.Controls;

/// <summary>
/// A labeled number: type a value, step it with Up/Down (Shift for ten times as much), or drag the label left and
/// right to scrub it, as in Photoshop. Mirrors NumericScrub.swift.
/// </summary>
public sealed class NumberField : StackPanel
{
    private readonly TextBlock _label;
    private readonly TextBox _box;
    private double _value;
    private Point? _scrubStart;
    private double _scrubValue;
    private bool _scrubbing;

    public double Minimum { get; set; } = double.MinValue;
    public double Maximum { get; set; } = double.MaxValue;
    public double Step { get; set; } = 1;
    /// <summary>Value change per point dragged on the label.</summary>
    public double ScrubRate { get; set; } = 1;
    public string Format { get; set; } = "0";
    public string Suffix { get; set; } = "";

    /// <summary>A new value was committed (typed and confirmed, stepped, or scrubbed).</summary>
    public event Action<double>? ValueChanged;
    /// <summary>Scrubbing started and ended, so callers can make the whole drag one undo step.</summary>
    public event Action? ScrubStarted;
    public event Action? ScrubEnded;

    public NumberField(string label, double width = 52)
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        _label = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            Cursor = Cursors.SizeWE,
            Background = Brushes.Transparent,
        };
        _label.SetResourceReference(TextBlock.ForegroundProperty, "TextDimBrush");
        _box = new TextBox { Width = width, TextAlignment = TextAlignment.Right };
        Children.Add(_label);
        Children.Add(_box);
        _label.MouseLeftButtonDown += (_, e) =>
        {
            _scrubStart = e.GetPosition(this);
            _scrubValue = _value;
            _scrubbing = false;
            _label.CaptureMouse();
            e.Handled = true;
        };
        _label.MouseMove += (_, e) =>
        {
            if (_scrubStart is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
            double dx = e.GetPosition(this).X - start.X;
            if (!_scrubbing && Math.Abs(dx) < 2) return;
            if (!_scrubbing) { _scrubbing = true; ScrubStarted?.Invoke(); }
            double rate = ScrubRate * (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1);
            Commit(_scrubValue + Math.Round(dx) * rate);
        };
        _label.MouseLeftButtonUp += (_, _) =>
        {
            _label.ReleaseMouseCapture();
            _scrubStart = null;
            if (_scrubbing) { _scrubbing = false; ScrubEnded?.Invoke(); }
        };
        _box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitText(); Keyboard.ClearFocus(); e.Handled = true; }
            else if (e.Key == Key.Escape) { Show(); }
        };
        _box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Up or Key.Down)) return;
            CommitText();
            double amount = Step * (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1);
            Commit(_value + (e.Key == Key.Up ? amount : -amount));
            _box.SelectAll();
            e.Handled = true;
        };
        _box.LostKeyboardFocus += (_, _) => CommitText();
        _box.GotKeyboardFocus += (_, _) => _box.SelectAll();
        _box.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (_box.IsKeyboardFocusWithin) return;
            _box.Focus();
            e.Handled = true;
        };
    }

    public string Label { get => _label.Text; set => _label.Text = value; }

    public double Value
    {
        get => _value;
        set
        {
            _value = Math.Clamp(value, Minimum, Maximum);
            if (!_box.IsKeyboardFocusWithin) Show();
        }
    }

    private void Show() => _box.Text = _value.ToString(Format, CultureInfo.InvariantCulture) + Suffix;

    private void CommitText()
    {
        var text = _box.Text.Trim();
        if (Suffix.Length > 0 && text.EndsWith(Suffix)) text = text[..^Suffix.Length];
        text = text.TrimEnd('%', '°', ' ', 'p', 'x');
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) || double.TryParse(text, out parsed))
            Commit(parsed);
        else Show();
    }

    private void Commit(double value)
    {
        value = Math.Clamp(value, Minimum, Maximum);
        bool changed = value != _value;
        _value = value;
        Show();
        if (changed) ValueChanged?.Invoke(value);
    }
}
