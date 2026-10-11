using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Compositor.App.Controls;
using Compositor.Core.IO;
using Compositor.Core.Localization;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.App.Dialogs;

public enum ExportFormat { Png, Jpeg, Pdf }

/// <summary>
/// File ▸ Export As…: the flattened canvas as a PNG, a JPEG or a one-page PDF, at its own size or scaled, previewed as
/// it will be written, with its file size. A light take on Photoshop's Export As. Mirrors ExportAsSheet.swift.
/// </summary>
public sealed class ExportAsDialog : DialogBase
{
    private const double PreviewWidth = 560, PreviewHeight = 330;

    private readonly SKImage _source;
    private readonly double _resolution;
    private ExportFormat _format;
    private int _width, _height;
    private int _quality;
    private PaletteColor _background = PaletteColor.White;

    private readonly RadioButton[] _formats;
    private readonly TextBox _widthBox, _heightBox;
    private readonly TextBlock _percent;
    private readonly StackPanel _qualityRow, _pageRow, _backgroundRow;
    private readonly SliderRow _qualitySlider;
    private readonly TextBlock _page;
    private readonly TextBlock _status, _info;
    private readonly ScrollViewer _scroller;
    private readonly Image _preview = new() { SnapsToDevicePixels = true };
    private bool _actualSize;
    private CancellationTokenSource? _job;
    private (ExportFormat Format, int Width, int Height, int Quality, PaletteColor Background)? _ready;

    /// <summary>What to write once Export… is pressed.</summary>
    public byte[]? Data { get; private set; }
    public ExportFormat Format => _format;

    public ExportAsDialog(SKImage source, double resolution, ExportFormat format) : base(L.T("导出为", "Export As"), L.T("导出…", "Export…"))
    {
        _source = source;
        _resolution = resolution > 0 ? resolution : 72;
        _format = format;
        _width = source.Width;
        _height = source.Height;
        _quality = Math.Clamp(Services.Settings.Current.JpegQuality, 0, 100);

        // The preview: fitted, or at 100% (double-click switches), over the canvas's checkerboard.
        var fit = new Button
        {
            Content = L.T("适合", "Fit"), Style = (Style)FindResource("FlatButton"), Padding = new Thickness(8, 2, 8, 2),
            ToolTip = L.T("显示整个图像", "Show the whole image"),
        };
        var actual = new Button
        {
            Content = "100%", Style = (Style)FindResource("FlatButton"), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(4, 0, 0, 0),
            ToolTip = L.T("每个导出像素对应一个屏幕像素", "Each pixel of the export is one pixel of the screen"),
        };
        fit.Click += (_, _) => SetActualSize(false);
        actual.Click += (_, _) => SetActualSize(true);
        var zoomRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, 6) };
        zoomRow.Children.Add(fit);
        zoomRow.Children.Add(actual);
        Body.Children.Add(zoomRow);
        RenderOptions.SetBitmapScalingMode(_preview, BitmapScalingMode.HighQuality);
        var holder = new Grid { Background = Checkerboard() };
        holder.Children.Add(_preview);
        _scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = holder, Background = new SolidColorBrush(Color.FromRgb(31, 31, 31)),
        };
        _scroller.MouseDoubleClick += (_, _) => SetActualSize(!_actualSize);
        AddPanning(_scroller);
        Body.Children.Add(new Border { Width = PreviewWidth, Height = PreviewHeight, Child = _scroller, Margin = new Thickness(0, 0, 0, 14),
            ToolTip = L.T("拖动移动 · 双击在“适合”与 100% 之间切换", "Drag to move around · Double-click switches between Fit and 100%") });

        // Format.
        var formats = new StackPanel { Orientation = Orientation.Horizontal };
        _formats = new[] { ("PNG", ExportFormat.Png), ("JPEG", ExportFormat.Jpeg), ("PDF", ExportFormat.Pdf) }
            .Select(f =>
            {
                var radio = new RadioButton { Content = f.Item1, GroupName = "format" + GetHashCode(), IsChecked = f.Item2 == format, Margin = new Thickness(0, 0, 18, 0) };
                radio.Checked += (_, _) => { _format = f.Item2; Changed(); };
                formats.Children.Add(radio);
                return radio;
            }).ToArray();
        Row(L.T("格式", "Format"), formats);

        // Size, keeping the canvas's proportions.
        _widthBox = new TextBox { Width = 70, TextAlignment = TextAlignment.Right };
        _heightBox = new TextBox { Width = 70, TextAlignment = TextAlignment.Right };
        _percent = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0), MinWidth = 44 };
        var scale = new ComboBox { Width = 90, ToolTip = L.T("缩放导出的图像：文档本身不变", "Scale the export: the document stays as it is") };
        scale.Items.Add(L.T("缩放…", "Scale…"));
        foreach (var p in new[] { 25, 50, 75, 100, 200 }) scale.Items.Add($"{p}%");
        scale.SelectedIndex = 0;
        scale.SelectionChanged += (_, _) =>
        {
            if (scale.SelectedIndex <= 0) return;
            SetScale(new[] { 0.25, 0.5, 0.75, 1, 2 }[scale.SelectedIndex - 1]);
            scale.SelectedIndex = 0;
        };
        void Commit(TextBox box, bool isWidth)
        {
            if (int.TryParse(box.Text.Trim(), out var v) && v > 0) SetScale(isWidth ? v / (double)_source.Width : v / (double)_source.Height);
            else ShowSize();
        }
        _widthBox.LostKeyboardFocus += (_, _) => Commit(_widthBox, true);
        _heightBox.LostKeyboardFocus += (_, _) => Commit(_heightBox, false);
        _widthBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(_widthBox, true); e.Handled = true; } };
        _heightBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(_heightBox, false); e.Handled = true; } };
        Row(L.T("尺寸", "Size"), _widthBox, Dim(" × "), _heightBox, Dim(L.T(" 像素", " px")), _percent, scale);

        _qualitySlider = new SliderRow("", 0, 100, _quality, 90, unit: "%", labelWidth: 0) { Width = 380 };
        _qualitySlider.ValueChanged += v => { _quality = (int)v; Changed(); };
        _qualityRow = Row(L.T("品质", "Quality"), _qualitySlider);
        _page = Dim("");
        _pageRow = Row(L.T("页面", "Page"), _page);
        var well = new ColorWell(_background) { PickerTitle = L.T("导出背景", "Export Background"), ToolTip = L.T("填充透明区域的颜色", "Color that fills transparent areas") };
        well.ColorChanged += c => { _background = c; Changed(); };
        _backgroundRow = Row(L.T("背景", "Background"), well);

        var footer = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        _info = Dim("");
        _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(_status, Dock.Right);
        footer.Children.Add(_status);
        footer.Children.Add(_info);
        Body.Children.Add(footer);

        ShowSize();
        Changed();
        Closed += (_, _) => _job?.Cancel();
    }

    private static TextBlock Dim(string text) => new()
    {
        Text = text, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)Application.Current.FindResource("TextDimBrush"),
    };

    private (ExportFormat, int, int, int, PaletteColor) Requested => (_format, _width, _height, _quality, _background);

    /// <summary>Both sides follow one, keeping the canvas's proportions, within the export limits.</summary>
    private void SetScale(double scale)
    {
        double largest = Math.Min(DocumentLimits.MaxSide / (double)Math.Max(_source.Width, _source.Height),
                                  Math.Sqrt(DocumentLimits.MaxSurfacePixels / ((double)_source.Width * _source.Height)));
        double clamped = Math.Min(largest, Math.Max(0.01, double.IsFinite(scale) ? scale : 1));
        _width = Math.Max(1, (int)Math.Round(_source.Width * clamped));
        _height = Math.Max(1, (int)Math.Round(_source.Height * clamped));
        ShowSize();
        Changed();
    }

    private void ShowSize()
    {
        _widthBox.Text = _width.ToString();
        _heightBox.Text = _height.ToString();
        _percent.Text = $"{Math.Round(_width / (double)_source.Width * 100)}%";
        string Inches(int pixels) => (pixels / _resolution).ToString("0.##");
        _page.Text = L.T($"{Inches(_width)} × {Inches(_height)} 英寸，{Math.Round(_resolution)} DPI",
            $"{Inches(_width)} × {Inches(_height)} in at {Math.Round(_resolution)} DPI");
    }

    private void SetActualSize(bool actual)
    {
        _actualSize = actual;
        LayoutPreview();
    }

    private void LayoutPreview()
    {
        if (_preview.Source is not BitmapSource bitmap) return;
        if (_actualSize)
        {
            // One export pixel per screen pixel.
            var dpi = VisualTreeHelper.GetDpi(this);
            _preview.Stretch = Stretch.Fill;
            _preview.Width = _width / dpi.DpiScaleX;
            _preview.Height = _height / dpi.DpiScaleY;
            RenderOptions.SetBitmapScalingMode(_preview, BitmapScalingMode.NearestNeighbor);
            _scroller.HorizontalScrollBarVisibility = _scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            if (_scroller.Content is Grid grid) { grid.HorizontalAlignment = HorizontalAlignment.Left; grid.VerticalAlignment = VerticalAlignment.Top; }
            _scroller.ScrollToHorizontalOffset(Math.Max(0, (_preview.Width - PreviewWidth) / 2));
            _scroller.ScrollToVerticalOffset(Math.Max(0, (_preview.Height - PreviewHeight) / 2));
        }
        else
        {
            _preview.Stretch = Stretch.Fill;
            double fitScale = Math.Min(PreviewWidth / _width, PreviewHeight / _height);
            _preview.Width = _width * fitScale;
            _preview.Height = _height * fitScale;
            RenderOptions.SetBitmapScalingMode(_preview, BitmapScalingMode.HighQuality);
            _scroller.HorizontalScrollBarVisibility = _scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            if (_scroller.Content is Grid grid) { grid.HorizontalAlignment = HorizontalAlignment.Center; grid.VerticalAlignment = VerticalAlignment.Center; }
        }
    }

    private void AddPanning(ScrollViewer scroller)
    {
        Point? start = null;
        Point origin = default;
        scroller.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (!_actualSize || e.ClickCount > 1) return;
            start = e.GetPosition(scroller);
            origin = new Point(scroller.HorizontalOffset, scroller.VerticalOffset);
            scroller.CaptureMouse();
            scroller.Cursor = Cursors.ScrollAll;
        };
        scroller.PreviewMouseMove += (_, e) =>
        {
            if (start is not { } s) return;
            var p = e.GetPosition(scroller);
            scroller.ScrollToHorizontalOffset(origin.X - (p.X - s.X));
            scroller.ScrollToVerticalOffset(origin.Y - (p.Y - s.Y));
        };
        scroller.PreviewMouseLeftButtonUp += (_, _) =>
        {
            if (start is null) return;
            start = null;
            scroller.ReleaseMouseCapture();
            scroller.Cursor = null;
        };
    }

    /// <summary>A setting changed: the export is made again (after a short pause, so dragging a slider stays smooth).</summary>
    private void Changed()
    {
        _qualityRow.Visibility = _format == ExportFormat.Jpeg ? Visibility.Visible : Visibility.Collapsed;
        _pageRow.Visibility = _format == ExportFormat.Pdf ? Visibility.Visible : Visibility.Collapsed;
        _backgroundRow.Visibility = _format == ExportFormat.Png ? Visibility.Collapsed : Visibility.Visible;
        _info.Text = _format == ExportFormat.Png ? L.T("保留透明度 · sRGB", "Transparency kept · sRGB") : "sRGB";
        _status.Text = L.T("正在更新…", "Updating…");
        _status.ClearValue(TextBlock.ForegroundProperty);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextDimBrush");
        OkButton.IsEnabled = false;
        _job?.Cancel();
        var job = _job = new CancellationTokenSource();
        var requested = Requested;
        var source = _source;
        double resolution = _resolution;
        _ = Run();

        async Task Run()
        {
            try
            {
                await Task.Delay(200, job.Token);
                var (format, width, height, quality, background) = requested;
                var color = new SKColor((byte)Math.Round(background.Red * 255), (byte)Math.Round(background.Green * 255), (byte)Math.Round(background.Blue * 255));
                var (data, preview) = await Task.Run(() =>
                {
                    using var sized = ImageExporter.Resized(source, width, height);
                    job.Token.ThrowIfCancellationRequested();
                    switch (format)
                    {
                        case ExportFormat.Jpeg:
                        {
                            var bytes = ImageExporter.EncodeJpeg(sized, quality, resolution, color);
                            // The preview is the JPEG as written, artifacts and all.
                            using var decoded = SKBitmap.Decode(bytes);
                            return (bytes, ToBitmap(decoded));
                        }
                        case ExportFormat.Pdf:
                        {
                            var bytes = ImageExporter.EncodePdf(sized, resolution, color);
                            using var flat = ImageExporter.Flattened(sized, color);
                            using var bitmap = SKBitmap.FromImage(flat);
                            return (bytes, ToBitmap(bitmap));
                        }
                        default:
                        {
                            var bytes = ImageExporter.EncodePng(sized, resolution);
                            using var bitmap = SKBitmap.FromImage(sized);
                            return (bytes, ToBitmap(bitmap));
                        }
                    }
                }, job.Token);
                if (job.IsCancellationRequested) return;
                Data = data;
                _ready = requested;
                _preview.Source = preview;
                LayoutPreview();
                _status.Text = FileSize(data.Length);
                _status.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                OkButton.IsEnabled = true;
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (job.IsCancellationRequested) return;
                _status.Text = error.Message;
                _status.Foreground = Brushes.IndianRed;
            }
        }
    }

    private static BitmapSource ToBitmap(SKBitmap bitmap)
    {
        var info = new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var bytes = new byte[info.BytesSize];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(bytes, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { using var pixmap = bitmap.PeekPixels(); pixmap.ReadPixels(info, handle.AddrOfPinnedObject(), info.RowBytes, 0, 0); }
        finally { handle.Free(); }
        var source = BitmapSource.Create(info.Width, info.Height, 96, 96, PixelFormats.Pbgra32, null, bytes, info.RowBytes);
        source.Freeze();
        return source;
    }

    private static string FileSize(long bytes) => bytes switch
    {
        < 1024 => L.T($"{bytes} 字节", $"{bytes} bytes"),
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.##} MB",
    };

    /// <summary>The canvas's checkerboard: 10-point squares of two grays, so a transparent PNG shows what's see-through.</summary>
    private static Brush Checkerboard()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(77, 77, 77)), null, new RectangleGeometry(new Rect(0, 0, 20, 20))));
        var light = new SolidColorBrush(Color.FromRgb(89, 89, 89));
        group.Children.Add(new GeometryDrawing(light, null, new RectangleGeometry(new Rect(0, 0, 10, 10))));
        group.Children.Add(new GeometryDrawing(light, null, new RectangleGeometry(new Rect(10, 10, 10, 10))));
        var brush = new DrawingBrush(group) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 20, 20), ViewportUnits = BrushMappingMode.Absolute };
        brush.Freeze();
        return brush;
    }

    protected override bool Validate()
    {
        if (Data is null || _ready != Requested) return false;
        if (_format == ExportFormat.Jpeg)
        {
            Services.Settings.Current.JpegQuality = _quality;
            Services.Settings.Current.Save();
        }
        return true;
    }
}
