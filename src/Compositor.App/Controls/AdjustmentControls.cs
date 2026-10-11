using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Compositor.Core.Adjustments;
using Compositor.Core.Localization;

namespace Compositor.App.Controls;

internal static class Theme
{
    public static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
    public static Color Color(string key) => (Color)Application.Current.FindResource(key);
    public static Pen Pen(Color color, double thickness = 1) { var p = new Pen(new SolidColorBrush(color), thickness); p.Freeze(); return p; }

    public static Color FromHsb(double hue, double saturation = 1, double brightness = 1)
    {
        hue = ((hue % 360) + 360) % 360 / 60;
        double c = brightness * saturation, x = c * (1 - Math.Abs(hue % 2 - 1)), m = brightness - c;
        var (r, g, b) = (int)hue switch
        {
            0 => (c, x, 0.0), 1 => (x, c, 0.0), 2 => (0.0, c, x), 3 => (0.0, x, c), 4 => (x, 0.0, c), _ => (c, 0.0, x),
        };
        return System.Windows.Media.Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    public static LinearGradientBrush Gradient(params Color[] colors)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (int i = 0; i < colors.Length; i++) brush.GradientStops.Add(new GradientStop(colors[i], colors.Length == 1 ? 0 : i / (double)(colors.Length - 1)));
        brush.Freeze();
        return brush;
    }

    /// <summary>The hue circle, red to red, centered on <paramref name="center"/> degrees.</summary>
    public static LinearGradientBrush Spectrum(double center = 180)
    {
        var colors = Enumerable.Range(0, 13).Select(i => FromHsb(center - 180 + i * 30)).ToArray();
        return Gradient(colors);
    }
}

/// <summary>
/// A slider with an optional colored track and a round knob, as Photoshop's adjustment sliders. Double-click resets.
/// Drags report every step so the canvas preview can follow.
/// </summary>
public sealed class TrackSlider : FrameworkElement
{
    private const double Knob = 6.5;
    private double _value;
    private bool _dragging;

    public double Minimum { get; init; }
    public double Maximum { get; init; } = 100;
    public double Default { get; init; }
    public int Decimals { get; init; }
    /// <summary>Equal steps along the track multiply the value (radius, distance).</summary>
    public bool Logarithmic { get; init; }
    /// <summary>A colored track; null draws a plain one filled from the origin to the knob.</summary>
    public Brush? Track { get; set; }

    public event Action<double>? ValueChanged;

    public TrackSlider()
    {
        Height = 18;
        MinWidth = 120;
        Cursor = Cursors.Hand;
        Focusable = false;
    }

    public double Value
    {
        get => _value;
        set { _value = Math.Clamp(value, Minimum, Maximum); InvalidateVisual(); }
    }

    private double Fraction(double v)
    {
        if (Logarithmic && Minimum > 0) return (Math.Log(v) - Math.Log(Minimum)) / (Math.Log(Maximum) - Math.Log(Minimum));
        return (v - Minimum) / (Maximum - Minimum);
    }

    private double FromFraction(double t)
    {
        t = Math.Clamp(t, 0, 1);
        double v = Logarithmic && Minimum > 0 ? Math.Exp(Math.Log(Minimum) + t * (Math.Log(Maximum) - Math.Log(Minimum))) : Minimum + t * (Maximum - Minimum);
        return Math.Clamp(Math.Round(v, Decimals), Minimum, Maximum);
    }

    private double X(double v) => Knob + Fraction(v) * Math.Max(1, ActualWidth - 2 * Knob);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, cy = ActualHeight / 2;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, ActualHeight));
        if (Track is { } track)
        {
            var rect = new Rect(Knob, cy - 3, Math.Max(1, w - 2 * Knob), 6);
            dc.DrawRoundedRectangle(track, Theme.Pen(System.Windows.Media.Color.FromArgb(90, 0, 0, 0)), rect, 3, 3);
        }
        else
        {
            var rect = new Rect(Knob, cy - 2, Math.Max(1, w - 2 * Knob), 4);
            dc.DrawRoundedRectangle(Theme.Brush("ControlHoverBrush"), null, rect, 2, 2);
            double origin = X(Math.Clamp(0, Minimum, Maximum)), x = X(_value);
            if (Math.Abs(x - origin) > 0.5)
                dc.DrawRectangle(Theme.Brush("AccentBrush"), null, new Rect(Math.Min(x, origin), cy - 2, Math.Abs(x - origin), 4));
        }
        var knob = new Point(X(_value), cy);
        dc.DrawEllipse(new SolidColorBrush(System.Windows.Media.Color.FromArgb(70, 0, 0, 0)), null, new Point(knob.X, knob.Y + 0.8), Knob, Knob);
        dc.DrawEllipse(_dragging ? Brushes.White : new SolidColorBrush(Theme.Color("TextColor")), Theme.Pen(System.Windows.Media.Color.FromArgb(120, 0, 0, 0), 0.75), knob, Knob - 0.5, Knob - 0.5);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        e.Handled = true;
        if (e.ClickCount == 2) { Set(Default); return; }
        _dragging = true;
        CaptureMouse();
        Set(FromFraction((e.GetPosition(this).X - Knob) / Math.Max(1, ActualWidth - 2 * Knob)));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed) return;
        Set(FromFraction((e.GetPosition(this).X - Knob) / Math.Max(1, ActualWidth - 2 * Knob)));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _dragging = false;
        ReleaseMouseCapture();
        InvalidateVisual();
    }

    private void Set(double value)
    {
        value = Math.Clamp(value, Minimum, Maximum);
        if (value == _value) { InvalidateVisual(); return; }
        _value = value;
        InvalidateVisual();
        ValueChanged?.Invoke(value);
    }
}

/// <summary>Label, slider and exact field on one row. Double-click the label or the knob to reset.</summary>
public sealed class SliderRow : Grid
{
    private readonly TrackSlider _slider;
    private readonly TextBox _box;
    private readonly int _decimals;
    private readonly double _min, _max, _default;
    private double _value;

    public event Action<double>? ValueChanged;

    /// <param name="inputMaximum">A typed value may go this far, past the slider's own end (which then stays pinned).</param>
    public SliderRow(string label, double min, double max, double value, double @default = 0, int decimals = 0, string unit = "",
                     bool logarithmic = false, Brush? track = null, double labelWidth = 92, double? inputMaximum = null)
    {
        _decimals = decimals;
        _min = min; _max = Math.Max(max, inputMaximum ?? max); _default = @default;
        Margin = new Thickness(0, 0, 0, 8);
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 150 });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(unit.Length > 1 ? 34 : 20) });
        // Cut short with an ellipsis rather than clipped mid-letter if a (longer, English) label outgrows its column.
        var title = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent, TextTrimming = TextTrimming.CharacterEllipsis };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextDimBrush");
        title.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) Commit(_default); };
        _slider = new TrackSlider { Minimum = min, Maximum = max, Default = @default, Decimals = decimals, Logarithmic = logarithmic, Track = track, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        _slider.ValueChanged += v => Commit(v, fromSlider: true);
        _slider.ToolTip = L.T("双击复位", "Double-click to reset");
        _box = new TextBox { TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        _box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitText(); e.Handled = false; }
            else if (e.Key == Key.Escape) Show();
        };
        _box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Up or Key.Down)) return;
            CommitText();
            double step = Math.Pow(10, -_decimals) * (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1);
            Commit(_value + (e.Key == Key.Up ? step : -step));
            _box.SelectAll();
            e.Handled = true;
        };
        _box.LostKeyboardFocus += (_, _) => CommitText();
        _box.GotKeyboardFocus += (_, _) => _box.SelectAll();
        var unitText = new TextBlock { Text = unit, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
        unitText.SetResourceReference(TextBlock.ForegroundProperty, "TextDimBrush");
        SetColumn(_slider, 1); SetColumn(_box, 2); SetColumn(unitText, 3);
        Children.Add(title); Children.Add(_slider); Children.Add(_box); Children.Add(unitText);
        Value = value;
    }

    public double Value
    {
        get => _value;
        set { _value = Math.Clamp(value, _min, _max); _slider.Value = _value; Show(); }
    }

    public Brush? Track { get => _slider.Track; set { _slider.Track = value; _slider.InvalidateVisual(); } }

    private void Show() => _box.Text = _value.ToString("F" + _decimals, CultureInfo.InvariantCulture);

    private void CommitText()
    {
        var text = _box.Text.Trim().TrimEnd('%', '°', ' ');
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) Commit(v);
        else Show();
    }

    private void Commit(double value, bool fromSlider = false)
    {
        value = Math.Clamp(Math.Round(value, _decimals), _min, _max);
        bool changed = value != _value;
        _value = value;
        if (!fromSlider) _slider.Value = value;
        Show();
        if (changed) ValueChanged?.Invoke(value);
    }
}

/// <summary>
/// Levels' histogram with the input black, gamma and white handles beneath it (or, with <see cref="Output"/>, the
/// output gradient with its two handles). Mirrors LevelsSheet's histogram and handles.
/// </summary>
public sealed class LevelsGraph : FrameworkElement
{
    private const double HandleBand = 16, Inset = 7;
    private int? _dragging;

    public bool Output { get; init; }
    public double[]? Bins { get; set; }
    public Color BinColor { get; set; } = Colors.Gray;
    public LevelRange Range { get; set; } = new();
    public event Action<LevelRange>? RangeChanged;

    public LevelsGraph() { Height = 150; Focusable = false; }

    private double Width0 => Math.Max(1, ActualWidth - 2 * Inset);
    private double X(double level) => Inset + level / 255 * Width0;
    private double Top => ActualHeight - HandleBand;

    private double[] Handles()
    {
        var r = Range.Normalized;
        if (Output) return new[] { r.OutputBlack, r.OutputWhite };
        return new[] { r.Black, r.Black + (r.White - r.Black) * Math.Pow(0.5, r.Gamma), r.White };
    }

    protected override void OnRender(DrawingContext dc)
    {
        double top = Top;
        var area = new Rect(Inset, 0, Width0, top);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (Output)
        {
            dc.DrawRectangle(Theme.Gradient(Colors.Black, Colors.White), Theme.Pen(Theme.Color("BorderColor")), area);
        }
        else
        {
            dc.DrawRectangle(new SolidColorBrush(System.Windows.Media.Color.FromArgb(70, 0, 0, 0)), Theme.Pen(Theme.Color("BorderColor")), area);
            if (Bins is { } bins && LevelsAutomatic.DisplayScale(bins) is var peak and > 0)
            {
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    g.BeginFigure(new Point(area.Left, area.Bottom), true, true);
                    for (int i = 0; i < 256; i++)
                    {
                        double h = area.Height * Math.Min(1, bins[i] / peak);
                        double x0 = area.Left + i * area.Width / 256, x1 = area.Left + (i + 1) * area.Width / 256;
                        g.LineTo(new Point(x0, area.Bottom - h), false, false);
                        g.LineTo(new Point(x1, area.Bottom - h), false, false);
                    }
                    g.LineTo(new Point(area.Right, area.Bottom), false, false);
                }
                geometry.Freeze();
                dc.DrawGeometry(new SolidColorBrush(BinColor), null, geometry);
            }
        }
        var handles = Handles();
        for (int i = 0; i < handles.Length; i++)
        {
            double x = X(handles[i]);
            Color fill = i == 0 ? Colors.Black : i == handles.Length - 1 ? Colors.White : System.Windows.Media.Color.FromRgb(128, 128, 128);
            var triangle = new StreamGeometry();
            using (var g = triangle.Open())
            {
                g.BeginFigure(new Point(x, top + 2), true, true);
                g.LineTo(new Point(x + 6, top + 13), true, false);
                g.LineTo(new Point(x - 6, top + 13), true, false);
            }
            triangle.Freeze();
            dc.DrawGeometry(new SolidColorBrush(fill), Theme.Pen(_dragging == i ? Theme.Color("AccentColor") : System.Windows.Media.Color.FromRgb(150, 150, 150), _dragging == i ? 1.5 : 1), triangle);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var p = e.GetPosition(this);
        if (p.Y < Top - 4) return;
        var handles = Handles();
        int best = 0;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < handles.Length; i++)
        {
            double d = Math.Abs(X(handles[i]) - p.X);
            // Ties (handles on top of each other) go to the one that can move toward the pointer.
            if (d < bestDistance - 0.01 || (Math.Abs(d - bestDistance) < 0.01 && p.X > X(handles[i]))) { best = i; bestDistance = d; }
        }
        if (bestDistance > 12) return;
        _dragging = best;
        CaptureMouse();
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging is not { } index || e.LeftButton != MouseButtonState.Pressed) return;
        double level = Math.Clamp((e.GetPosition(this).X - Inset) / Width0 * 255, 0, 255);
        var r = Range.Normalized;
        if (Output)
            r = index == 0 ? r with { OutputBlack = Math.Round(level) } : r with { OutputWhite = Math.Round(level) };
        else if (index == 0) r = r with { Black = Math.Min(r.White - 1, Math.Round(level)) };
        else if (index == 2) r = r with { White = Math.Max(r.Black + 1, Math.Round(level)) };
        else
        {
            double fraction = Math.Clamp((level - r.Black) / (r.White - r.Black), 0.001, 0.999);
            r = r with { Gamma = Math.Round(Math.Clamp(Math.Log(fraction) / Math.Log(0.5), 0.1, 9.99), 2) };
        }
        Range = r;
        InvalidateVisual();
        RangeChanged?.Invoke(r);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _dragging = null;
        ReleaseMouseCapture();
        InvalidateVisual();
    }
}

/// <summary>
/// The Curves graph: click to add a point, drag to move one, drag an inner point off the graph (or press Delete) to
/// remove it. Mirrors CurvesControls.swift.
/// </summary>
public sealed class CurvesGraph : FrameworkElement
{
    private int? _dragging;
    private bool _removing;

    public List<CurvePoint> Points { get; set; } = CurvesSettings.Identity();
    public double[]? Bins { get; set; }
    public Color LineColor { get; set; } = Colors.White;
    public int? Selected { get; private set; }
    public event Action<List<CurvePoint>>? PointsChanged;
    public event Action? SelectionChanged;

    public CurvesGraph() { Width = 300; Height = 300; Focusable = true; FocusVisualStyle = null; }

    private Point Position(CurvePoint p) => new(p.X / 255 * ActualWidth, (1 - p.Y / 255) * ActualHeight);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(new SolidColorBrush(System.Windows.Media.Color.FromArgb(90, 0, 0, 0)), Theme.Pen(Theme.Color("BorderColor")), new Rect(0, 0, w, h));
        if (Bins is { } bins && LevelsAutomatic.DisplayScale(bins) is var peak and > 0)
        {
            var fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(55, 255, 255, 255));
            for (int i = 0; i < 256; i++)
            {
                double bar = h * Math.Min(1, bins[i] / peak);
                if (bar > 0) dc.DrawRectangle(fill, null, new Rect(i * w / 256, h - bar, w / 256 + 0.3, bar));
            }
        }
        var grid = Theme.Pen(System.Windows.Media.Color.FromArgb(35, 255, 255, 255));
        for (int i = 1; i < 4; i++)
        {
            dc.DrawLine(grid, new Point(i * w / 4, 0), new Point(i * w / 4, h));
            dc.DrawLine(grid, new Point(0, i * h / 4), new Point(w, i * h / 4));
        }
        dc.DrawLine(Theme.Pen(System.Windows.Media.Color.FromArgb(50, 255, 255, 255)), new Point(0, h), new Point(w, 0));
        var settings = new CurvesSettings();
        settings.Channels[0] = Points;
        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            for (int x = 0; x <= 255; x++)
            {
                var p = Position(new CurvePoint(x, settings.Value(x, 0)));
                if (x == 0) g.BeginFigure(p, false, false); else g.LineTo(p, true, false);
            }
        }
        line.Freeze();
        dc.DrawGeometry(null, Theme.Pen(LineColor, 1.8), line);
        for (int i = 0; i < Points.Count; i++)
        {
            if (_removing && _dragging == i) continue;
            var p = Position(Points[i]);
            dc.DrawRectangle(Selected == i ? Theme.Brush("AccentBrush") : Brushes.White, Theme.Pen(Colors.Black, 0.75), new Rect(p.X - 4, p.Y - 4, 8, 8));
        }
    }

    private (double X, double Y) Level(Point p) =>
        (Math.Clamp(p.X / ActualWidth * 255, 0, 255), Math.Clamp(255 - p.Y / ActualHeight * 255, 0, 255));

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var (x, y) = Level(e.GetPosition(this));
        var p = new List<CurvePoint>(Points);
        int nearest = -1;
        double best = double.MaxValue;
        for (int i = 0; i < p.Count; i++)
        {
            double d = Math.Sqrt(Math.Pow(p[i].X - x, 2) + Math.Pow(p[i].Y - y, 2));
            if (d < best) { best = d; nearest = i; }
        }
        if (nearest >= 0 && best < 10) _dragging = nearest;
        else if (p.Count < 32 && x > 1 && x < 254 && p.All(q => Math.Abs(q.X - x) > 1))
        {
            var point = new CurvePoint(Math.Round(x), Math.Round(y));
            p.Add(point);
            p.Sort((a, b) => a.X.CompareTo(b.X));
            _dragging = p.IndexOf(point);
            Points = p;
            PointsChanged?.Invoke(new List<CurvePoint>(p));
        }
        if (_dragging is null) return;
        Selected = _dragging;
        SelectionChanged?.Invoke();
        CaptureMouse();
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging is not { } i || e.LeftButton != MouseButtonState.Pressed || i >= Points.Count) return;
        var position = e.GetPosition(this);
        var p = new List<CurvePoint>(Points);
        // Dragged well off the graph: an inner point goes, as in Photoshop (it comes back if dragged in again).
        bool outside = position.X < -24 || position.Y < -24 || position.X > ActualWidth + 24 || position.Y > ActualHeight + 24;
        _removing = outside && i > 0 && i < p.Count - 1;
        var (x, y) = Level(position);
        double nx = i > 0 && i < p.Count - 1 ? Math.Min(p[i + 1].X - 1, Math.Max(p[i - 1].X + 1, Math.Round(x))) : p[i].X;
        p[i] = new CurvePoint(nx, Math.Round(y));
        Points = p;
        InvalidateVisual();
        PointsChanged?.Invoke(_removing ? p.Where((_, k) => k != i).ToList() : new List<CurvePoint>(p));
        SelectionChanged?.Invoke();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_removing && _dragging is { } i && i > 0 && i < Points.Count - 1)
        {
            Points = Points.Where((_, k) => k != i).ToList();
            Selected = null;
            SelectionChanged?.Invoke();
        }
        _removing = false;
        _dragging = null;
        ReleaseMouseCapture();
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Delete or Key.Back) { RemoveSelected(); e.Handled = true; }
    }

    public void RemoveSelected()
    {
        if (Selected is not { } i || i <= 0 || i >= Points.Count - 1) return;
        Points = Points.Where((_, k) => k != i).ToList();
        Selected = null;
        InvalidateVisual();
        PointsChanged?.Invoke(new List<CurvePoint>(Points));
        SelectionChanged?.Invoke();
    }

    public void ClearSelection() { Selected = null; InvalidateVisual(); }
}

/// <summary>
/// Hue/Saturation's two spectrum bars, the hues as they are and as the adjustment leaves them, with the selected
/// range's band handles between them: outer marks are the falloff, inner bars the full-strength range.
/// </summary>
public sealed class SpectrumBar : FrameworkElement
{
    private const double Bar = 14, Gap = 14;
    private int? _dragging;

    public HueSaturationSettings Settings { get; set; } = new();
    public event Action<HueBand>? BandChanged;

    public SpectrumBar() { Height = Bar * 2 + Gap; Focusable = false; }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, ActualHeight));
        dc.DrawRectangle(Theme.Spectrum(180), null, new Rect(0, 0, w, Bar));
        var response = AdjustmentPixels.HueResponse(Settings);
        const int slices = 120;
        for (int i = 0; i < slices; i++)
        {
            double hue = i * 360.0 / slices;
            double shown = hue + response[(int)Math.Round(hue)].Shift;
            dc.DrawRectangle(new SolidColorBrush(Theme.FromHsb(shown)), null, new Rect(i * w / slices, Bar + Gap, w / slices + 0.6, Bar));
        }
        var band = Settings.Band;
        var mark = new SolidColorBrush(Theme.Color("TextColor"));
        double mid = Bar + Gap / 2;
        // The covered stretch, shaded (wrapping past the ends).
        void Shade(double from, double to, byte alpha)
        {
            var brush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(alpha, 255, 255, 255));
            double span = HueBand.Forward(from, to);
            double start = from;
            while (span > 0.01)
            {
                double run = Math.Min(span, 360 - start);
                dc.DrawRectangle(brush, null, new Rect(start / 360 * w, mid - 2, run / 360 * w, 4));
                span -= run;
                start = 0;
            }
        }
        Shade(band.FalloffStart, band.FalloffEnd, 60);
        Shade(band.RangeStart, band.RangeEnd, 150);
        for (int i = 0; i < 4; i++)
        {
            double x = band.Handle(i) / 360 * w;
            if (i is 1 or 2) dc.DrawRectangle(mark, null, new Rect(x - 1.5, Bar + 1, 3, Gap - 2));
            else
            {
                var triangle = new StreamGeometry();
                using (var g = triangle.Open())
                {
                    g.BeginFigure(new Point(x, mid - 5), true, true);
                    g.LineTo(new Point(x + 5, mid + 5), true, false);
                    g.LineTo(new Point(x - 5, mid + 5), true, false);
                }
                triangle.Freeze();
                dc.DrawGeometry(mark, Theme.Pen(Colors.Black, 0.5), triangle);
            }
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var p = e.GetPosition(this);
        if (ActualWidth <= 0) return;
        double degrees = Math.Clamp(p.X / ActualWidth, 0, 1) * 360;
        int best = 0;
        double bestGap = double.MaxValue;
        for (int i = 0; i < 4; i++)
        {
            double gap = Math.Abs(Settings.Band.Handle(i) - degrees) % 360;
            gap = Math.Min(gap, 360 - gap);
            if (gap < bestGap) { bestGap = gap; best = i; }
        }
        if (bestGap / 360 * ActualWidth > 14) return;
        _dragging = best;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging is not { } index || e.LeftButton != MouseButtonState.Pressed || ActualWidth <= 0) return;
        double degrees = Math.Round(Math.Clamp(e.GetPosition(this).X / ActualWidth, 0, 1) * 360);
        var band = Settings.Band.WithHandle(index, degrees);
        if (band == Settings.Band) return;
        BandChanged?.Invoke(band);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _dragging = null;
        ReleaseMouseCapture();
    }
}

/// <summary>A color well: shows a color and opens the picker when clicked.</summary>
public sealed class ColorWell : Border
{
    private Core.Model.PaletteColor _color;
    public event Action<Core.Model.PaletteColor>? ColorChanged;
    public string PickerTitle { get; init; } = L.T("拾色器", "Color Picker");

    public ColorWell(Core.Model.PaletteColor color)
    {
        Width = 44; Height = 22;
        CornerRadius = new CornerRadius(3);
        BorderThickness = new Thickness(1);
        SetResourceReference(BorderBrushProperty, "BorderBrush");
        Cursor = Cursors.Hand;
        Color = color;
        MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            var dialog = new Dialogs.ColorPickerDialog(_color, PickerTitle) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true) return;
            Color = dialog.Color;
            ColorChanged?.Invoke(_color);
        };
    }

    public Core.Model.PaletteColor Color
    {
        get => _color;
        set
        {
            _color = value;
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(
                (byte)Math.Round(value.Red * 255), (byte)Math.Round(value.Green * 255), (byte)Math.Round(value.Blue * 255)));
        }
    }
}
