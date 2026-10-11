using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Compositor.Core.Editing;
using Compositor.Core.Geometry;
using Compositor.Core.Localization;
using Compositor.Core.Rendering;
using SkiaSharp;

namespace Compositor.App.Drawing;

/// <summary>
/// The Navigator: a minimap in the canvas's top-right corner, shown from 300% zoom, when the view is a small part of
/// the document. The whole document in small, with a box around what the canvas shows; clicking or dragging in it
/// moves the view there. Its picture is redrawn a moment after edits settle, and only while it shows. Mirrors
/// NavigatorMinimap.swift and NavigatorGeometry.swift.
/// </summary>
public sealed class NavigatorMinimap : FrameworkElement
{
    public const double ZoomShown = 3;
    /// <summary>The most room the document takes: it's fitted inside, keeping its proportions.</summary>
    private static readonly Size Largest = new(150, 108);
    private const int PicturePixels = 360;

    private EditorSession? _session;
    private BitmapSource? _picture;
    private bool _stale = true;
    private readonly DispatcherTimer _redraw;
    private static readonly Brush Back = Frozen(new SolidColorBrush(Color.FromRgb(28, 28, 28)));
    private static readonly Pen Edge = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(64, 255, 255, 255)), 1));

    public NavigatorMinimap()
    {
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(0, 12, 12, 0);
        Cursor = Cursors.Arrow;
        Visibility = Visibility.Collapsed;
        Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Opacity = 0.45 };
        ToolTip = L.T("导航器：单击或拖动移动视图", "Navigator: click or drag to move the view");
        // Edits settle first, so a stroke or a drag redraws the picture once, afterwards.
        _redraw = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => { _redraw!.Stop(); Redraw(); }, Dispatcher);
        _redraw.Stop();
    }

    private static T Frozen<T>(T freezable) where T : Freezable { freezable.Freeze(); return freezable; }

    public EditorSession? Session
    {
        get => _session;
        set
        {
            if (ReferenceEquals(_session, value)) return;
            if (_session is not null)
            {
                _session.DocumentChanged -= OnDocumentChanged;
                _session.Viewport.Changed -= OnViewportChanged;
            }
            _session = value;
            _picture = null;
            _stale = true;
            if (_session is not null)
            {
                _session.DocumentChanged += OnDocumentChanged;
                _session.Viewport.Changed += OnViewportChanged;
            }
            Update();
        }
    }

    private void OnDocumentChanged(object? sender, EventArgs e)
    {
        _stale = true;
        if (Visibility == Visibility.Visible) { _redraw.Stop(); _redraw.Start(); }
        Update();
    }

    private void OnViewportChanged(object? sender, EventArgs e) => Update();

    /// <summary>Shown from 300% zoom; its box follows the view.</summary>
    public void Update()
    {
        bool shown = Services.Settings.Current.ShowsNavigator && _session is { Document: { } document } && document.Width > 0 && _session.Viewport.Zoom >= ZoomShown;
        Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (!shown) return;
        var size = BoxSize(_session!.Document!.Size);
        if (Width != size.Width || Height != size.Height) { Width = size.Width; Height = size.Height; }
        if (_stale && _picture is null) Redraw();
        else if (_stale && !_redraw.IsEnabled) _redraw.Start();
        InvalidateVisual();
    }

    private static Size BoxSize(SizeD document)
    {
        double scale = Math.Min(Largest.Width / document.Width, Largest.Height / document.Height);
        return new Size(Math.Round(document.Width * scale), Math.Round(document.Height * scale));
    }

    private void Redraw()
    {
        if (_session?.Document is not { } document) return;
        _stale = false;
        try
        {
            double scale = Math.Min(1, PicturePixels / (double)Math.Max(document.Width, document.Height));
            using var image = DocumentRenderer.RenderToImage(document with { Selection = null }, scale: scale);
            var info = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            var bytes = new byte[info.BytesSize];
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(bytes, System.Runtime.InteropServices.GCHandleType.Pinned);
            try { image.ReadPixels(info, handle.AddrOfPinnedObject(), info.RowBytes, 0, 0); }
            finally { handle.Free(); }
            var picture = BitmapSource.Create(info.Width, info.Height, 96, 96, PixelFormats.Pbgra32, null, bytes, info.RowBytes);
            picture.Freeze();
            _picture = picture;
        }
        catch { _picture = null; }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_session?.Document is not { } document) return;
        var box = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.PushClip(new RectangleGeometry(box, 4, 4));
        dc.DrawRectangle(Back, null, box);
        if (_picture is not null)
        {
            System.Windows.Media.RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
            // The checkerboard's darker gray behind transparent parts is the backdrop itself.
            dc.DrawImage(_picture, box);
        }
        // What the canvas shows, held to the picture.
        var shown = _session.Viewport.VisibleDocumentRect(document.Size);
        double sx = box.Width / document.Width, sy = box.Height / document.Height;
        var rect = new Rect(shown.X * sx, shown.Y * sy, shown.Width * sx, shown.Height * sy);
        rect.Intersect(box);
        if (!rect.IsEmpty)
        {
            if (rect.Width < 3) rect = new Rect(rect.X - (3 - rect.Width) / 2, rect.Y, 3, rect.Height);
            if (rect.Height < 3) rect = new Rect(rect.X, rect.Y - (3 - rect.Height) / 2, rect.Width, 3);
            var accent = (Brush)FindResource("AccentBrush");
            dc.DrawRectangle(null, new Pen(accent, 2), new Rect(rect.X + 1, rect.Y + 1, Math.Max(1, rect.Width - 2), Math.Max(1, rect.Height - 2)));
        }
        dc.Pop();
        dc.DrawRoundedRectangle(null, Edge, new Rect(0.5, 0.5, box.Width - 1, box.Height - 1), 4, 4);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        CaptureMouse();
        MoveView(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsMouseCaptured) MoveView(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        ReleaseMouseCapture();
        e.Handled = true;
    }

    private void MoveView(Point point)
    {
        if (_session?.Document is not { } document || ActualWidth <= 0) return;
        double x = Math.Clamp(point.X / ActualWidth * document.Width, 0, document.Width);
        double y = Math.Clamp(point.Y / ActualHeight * document.Height, 0, document.Height);
        _session.Viewport.CenterView(new PointD(x, y), document.Size);
    }
}
