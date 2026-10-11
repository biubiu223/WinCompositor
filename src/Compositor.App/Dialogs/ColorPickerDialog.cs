using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Compositor.Core.Localization;
using Compositor.Core.Model;

namespace Compositor.App.Dialogs;

/// <summary>Photoshop's color picker: a saturation/brightness square for the chosen hue, and exact fields. Mirrors ColorPickerSheet.swift.</summary>
public sealed class ColorPickerDialog : DialogBase
{
    private const int Side = 256;
    private double _h, _s, _b;
    private readonly PaletteColor _original;
    private readonly Image _square = new() { Width = Side, Height = Side, Stretch = Stretch.Fill };
    private readonly Image _hueStrip = new() { Width = 22, Height = Side, Stretch = Stretch.Fill };
    private readonly Ellipse _marker = new() { Width = 12, Height = 12, Stroke = Brushes.White, StrokeThickness = 1.5, IsHitTestVisible = false };
    private readonly Rectangle _hueMarker = new() { Width = 26, Height = 4, Stroke = Brushes.White, StrokeThickness = 1.2, Fill = Brushes.Transparent, IsHitTestVisible = false };
    private readonly Rectangle _newSwatch = new() { Width = 64, Height = 32 };
    private readonly Rectangle _oldSwatch = new() { Width = 64, Height = 32 };
    private readonly TextBox _hBox = new() { Width = 56 }, _sBox = new() { Width = 56 }, _bBox = new() { Width = 56 };
    private readonly TextBox _rBox = new() { Width = 56 }, _gBox = new() { Width = 56 }, _blBox = new() { Width = 56 };
    private readonly TextBox _hexBox = new() { Width = 80 };
    private readonly Canvas _squareHost = new() { Width = Side, Height = Side, ClipToBounds = true };
    private readonly Canvas _hueHost = new() { Width = 26, Height = Side };
    private bool _updating;
    private WriteableBitmap? _squareBitmap;

    public PaletteColor Color { get; private set; }

    public ColorPickerDialog(PaletteColor color, string title) : base(title)
    {
        _original = color;
        Color = color;
        (_h, _s, _b) = color.ToHsb();
        _oldSwatch.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(color.R8, color.G8, color.B8));

        _squareHost.Children.Add(_square);
        _squareHost.Children.Add(_marker);
        _hueHost.Children.Add(_hueStrip);
        Canvas.SetLeft(_hueStrip, 2);
        _hueHost.Children.Add(_hueMarker);
        _hueStrip.Source = HueStrip();

        _squareHost.MouseLeftButtonDown += (_, e) => { _squareHost.CaptureMouse(); PickSquare(e.GetPosition(_squareHost)); };
        _squareHost.MouseMove += (_, e) => { if (_squareHost.IsMouseCaptured) PickSquare(e.GetPosition(_squareHost)); };
        _squareHost.MouseLeftButtonUp += (_, _) => _squareHost.ReleaseMouseCapture();
        _hueHost.MouseLeftButtonDown += (_, e) => { _hueHost.CaptureMouse(); PickHue(e.GetPosition(_hueHost)); };
        _hueHost.MouseMove += (_, e) => { if (_hueHost.IsMouseCaptured) PickHue(e.GetPosition(_hueHost)); };
        _hueHost.MouseLeftButtonUp += (_, _) => _hueHost.ReleaseMouseCapture();
        _hueHost.Background = Brushes.Transparent;
        _squareHost.Background = Brushes.Transparent;

        var swatches = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        swatches.Children.Add(new TextBlock { Text = L.T("新的", "new"), Foreground = (Brush)FindResource("TextDimBrush"), HorizontalAlignment = HorizontalAlignment.Center });
        swatches.Children.Add(_newSwatch);
        swatches.Children.Add(_oldSwatch);
        swatches.Children.Add(new TextBlock { Text = L.T("当前", "current"), Foreground = (Brush)FindResource("TextDimBrush"), HorizontalAlignment = HorizontalAlignment.Center });
        _oldSwatch.MouseLeftButtonDown += (_, _) => SetColor(_original);

        var fields = new StackPanel { Margin = new Thickness(18, 0, 0, 0) };
        fields.Children.Add(swatches);
        fields.Children.Add(FieldRow("H", _hBox, "°"));
        fields.Children.Add(FieldRow("S", _sBox, "%"));
        fields.Children.Add(FieldRow("B", _bBox, "%"));
        fields.Children.Add(new Border { Height = 8 });
        fields.Children.Add(FieldRow("R", _rBox, ""));
        fields.Children.Add(FieldRow("G", _gBox, ""));
        fields.Children.Add(FieldRow("B", _blBox, ""));
        fields.Children.Add(new Border { Height = 8 });
        fields.Children.Add(FieldRow("#", _hexBox, ""));

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Border { Child = _squareHost, BorderBrush = (Brush)FindResource("BorderBrush"), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Top });
        row.Children.Add(new Border { Child = _hueHost, Margin = new Thickness(10, 1, 0, 0), VerticalAlignment = VerticalAlignment.Top });
        row.Children.Add(fields);
        Body.Children.Add(row);

        foreach (var box in new[] { _hBox, _sBox, _bBox }) Hook(box, FromHsbFields);
        foreach (var box in new[] { _rBox, _gBox, _blBox }) Hook(box, FromRgbFields);
        Hook(_hexBox, () => { if (PaletteColor.FromHex(_hexBox.Text) is { } c) SetColor(c); });
        Update();
    }

    private static StackPanel FieldRow(string label, TextBox box, string unit)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 5) };
        row.Children.Add(new TextBlock { Text = label, Width = 18, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(box);
        row.Children.Add(new TextBlock { Text = unit, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    private void Hook(TextBox box, Action apply)
    {
        box.LostKeyboardFocus += (_, _) => { if (!_updating) apply(); };
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter && !_updating) { apply(); box.SelectAll(); e.Handled = false; } };
    }

    private static double Num(TextBox box) => double.TryParse(box.Text.Trim().TrimStart('#'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;

    private void FromHsbFields()
    {
        double h = Num(_hBox), s = Num(_sBox), b = Num(_bBox);
        if (double.IsNaN(h) || double.IsNaN(s) || double.IsNaN(b)) { Update(); return; }
        _h = ((h % 360) + 360) % 360;
        _s = Math.Clamp(s / 100, 0, 1);
        _b = Math.Clamp(b / 100, 0, 1);
        Color = PaletteColor.FromHsb(_h, _s, _b);
        Update();
    }

    private void FromRgbFields()
    {
        double r = Num(_rBox), g = Num(_gBox), b = Num(_blBox);
        if (double.IsNaN(r) || double.IsNaN(g) || double.IsNaN(b)) { Update(); return; }
        SetColor(PaletteColor.FromBytes((byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255), (byte)Math.Clamp(b, 0, 255)));
    }

    private void SetColor(PaletteColor color)
    {
        Color = color;
        var (h, s, b) = color.ToHsb();
        // Grays have no hue of their own: keep the one shown.
        if (s > 0 && b > 0) _h = h;
        _s = s;
        _b = b;
        Update();
    }

    private void PickSquare(Point p)
    {
        _s = Math.Clamp(p.X / Side, 0, 1);
        _b = Math.Clamp(1 - p.Y / Side, 0, 1);
        Color = PaletteColor.FromHsb(_h, _s, _b);
        Update(redrawSquare: false);
    }

    private void PickHue(Point p)
    {
        _h = Math.Clamp(p.Y / Side, 0, 0.9999) * 360;
        Color = PaletteColor.FromHsb(_h, _s, _b);
        Update();
    }

    private double _squareHue = -1;

    private void Update(bool redrawSquare = true)
    {
        _updating = true;
        if (redrawSquare && Math.Abs(_squareHue - _h) > 0.01) { _square.Source = SquareBitmap(_h); _squareHue = _h; }
        Canvas.SetLeft(_marker, _s * Side - 6);
        Canvas.SetTop(_marker, (1 - _b) * Side - 6);
        _marker.Stroke = _b > 0.6 && _s < 0.4 ? Brushes.Black : Brushes.White;
        Canvas.SetTop(_hueMarker, _h / 360 * Side - 2);
        _newSwatch.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(Color.R8, Color.G8, Color.B8));
        _hBox.Text = Math.Round(_h).ToString(CultureInfo.InvariantCulture);
        _sBox.Text = Math.Round(_s * 100).ToString(CultureInfo.InvariantCulture);
        _bBox.Text = Math.Round(_b * 100).ToString(CultureInfo.InvariantCulture);
        _rBox.Text = Color.R8.ToString();
        _gBox.Text = Color.G8.ToString();
        _blBox.Text = Color.B8.ToString();
        _hexBox.Text = Color.Hex;
        _updating = false;
    }

    private WriteableBitmap SquareBitmap(double hue)
    {
        _squareBitmap ??= new WriteableBitmap(Side, Side, 96, 96, PixelFormats.Bgr32, null);
        var pixels = new int[Side * Side];
        for (int y = 0; y < Side; y++)
        {
            double b = 1 - y / (double)(Side - 1);
            for (int x = 0; x < Side; x++)
            {
                var c = PaletteColor.FromHsb(hue, x / (double)(Side - 1), b);
                pixels[y * Side + x] = (c.R8 << 16) | (c.G8 << 8) | c.B8;
            }
        }
        _squareBitmap.WritePixels(new Int32Rect(0, 0, Side, Side), pixels, Side * 4, 0);
        return _squareBitmap;
    }

    private static WriteableBitmap HueStrip()
    {
        var bitmap = new WriteableBitmap(1, Side, 96, 96, PixelFormats.Bgr32, null);
        var pixels = new int[Side];
        for (int y = 0; y < Side; y++)
        {
            var c = PaletteColor.FromHsb(y / (double)Side * 360, 1, 1);
            pixels[y] = (c.R8 << 16) | (c.G8 << 8) | c.B8;
        }
        bitmap.WritePixels(new Int32Rect(0, 0, 1, Side), pixels, 4, 0);
        return bitmap;
    }

    protected override bool Validate()
    {
        if (Keyboard.FocusedElement is TextBox box)
        {
            if (box == _hexBox) { if (PaletteColor.FromHex(_hexBox.Text) is { } c) SetColor(c); }
            else if (box == _rBox || box == _gBox || box == _blBox) FromRgbFields();
            else FromHsbFields();
        }
        return true;
    }
}
