using Compositor.Core.Geometry;

namespace Compositor.Core.Rendering;

/// <summary>
/// Document: pixels with a top-left origin. View: device-independent units (WPF's 1/96 inch). Zoom 1 means one
/// document pixel per physical display pixel. Mirrors CanvasViewport.swift.
/// </summary>
public sealed class CanvasViewport
{
    public SizeD ViewSize { get; private set; } = SizeD.Zero;
    public double BackingScale { get; private set; } = 1;
    public double Zoom { get; private set; } = 1;
    public SizeD Pan { get; private set; } = SizeD.Zero;
    public bool FollowsFit { get; private set; } = true;

    public const double MinZoom = 0.001, MaxZoom = 32;
    public static readonly double[] KeyboardZoomLevels =
    {
        0.125, 1.0 / 6.0, 0.25, 1.0 / 3.0, 0.5, 2.0 / 3.0, 1, 1.25, 1.5, 2, 3, 4, 5, 6, 8, 12, 16, 24, 32,
    };

    public event EventHandler? Changed;

    /// <summary>View units per document pixel.</summary>
    public double PointsPerPixel => Zoom / BackingScale;
    public PointD Center => new(ViewSize.Width / 2, ViewSize.Height / 2);

    public CanvasViewport Clone() => new()
    {
        ViewSize = ViewSize, BackingScale = BackingScale, Zoom = Zoom, Pan = Pan, FollowsFit = FollowsFit,
    };

    public void CopyFrom(CanvasViewport other)
    {
        ViewSize = other.ViewSize; BackingScale = other.BackingScale; Zoom = other.Zoom; Pan = other.Pan; FollowsFit = other.FollowsFit;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public RectD DocumentRect(SizeD size)
    {
        double w = size.Width * PointsPerPixel, h = size.Height * PointsPerPixel;
        return new RectD(Center.X - w / 2 + Pan.Width, Center.Y - h / 2 + Pan.Height, w, h);
    }

    public PointD DocumentPoint(PointD viewPoint, SizeD documentSize)
    {
        var origin = DocumentRect(documentSize).Origin;
        return new PointD((viewPoint.X - origin.X) / PointsPerPixel, (viewPoint.Y - origin.Y) / PointsPerPixel);
    }

    public PointD ViewPoint(PointD documentPoint, SizeD documentSize)
    {
        var origin = DocumentRect(documentSize).Origin;
        return new PointD(origin.X + documentPoint.X * PointsPerPixel, origin.Y + documentPoint.Y * PointsPerPixel);
    }

    /// <summary>Document pixels → view units.</summary>
    public Affine DocumentToView(SizeD documentSize)
    {
        var origin = DocumentRect(documentSize).Origin;
        return Affine.Scale(PointsPerPixel, PointsPerPixel).Concat(Affine.Translation(origin.X, origin.Y));
    }

    public void Fit(SizeD documentSize)
    {
        if (ViewSize.Width <= 0 || ViewSize.Height <= 0 || documentSize.IsEmpty)
        {
            FollowsFit = true;
            return;
        }
        Zoom = Clamp(Math.Min(Math.Max(1, ViewSize.Width - 96) / documentSize.Width,
                              Math.Max(1, ViewSize.Height - 96) / documentSize.Height) * BackingScale);
        Pan = SizeD.Zero;
        FollowsFit = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Resize(SizeD size, double backingScale, SizeD? documentSize)
    {
        double oldScale = PointsPerPixel;
        ViewSize = size;
        BackingScale = Math.Max(1, backingScale);
        if (FollowsFit && documentSize is { } doc)
        {
            Fit(doc);
        }
        else
        {
            double ratio = PointsPerPixel / oldScale;
            Pan = new SizeD(Pan.Width * ratio, Pan.Height * ratio);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SetZoom(double value, PointD anchor, SizeD documentSize)
    {
        if (!double.IsFinite(value)) return;
        var pixel = DocumentPoint(anchor, documentSize);
        Zoom = Clamp(value);
        var moved = ViewPoint(pixel, documentSize);
        Pan = new SizeD(Pan.Width + anchor.X - moved.X, Pan.Height + anchor.Y - moved.Y);
        FollowsFit = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public double KeyboardZoomTarget(int step)
    {
        if (step == 0) return Zoom;
        double tolerance = Math.Max(1e-9, Math.Abs(Zoom) * 1e-9);
        if (step > 0)
        {
            foreach (var level in KeyboardZoomLevels) if (level > Zoom + tolerance) return level;
            return Zoom;
        }
        for (int i = KeyboardZoomLevels.Length - 1; i >= 0; i--) if (KeyboardZoomLevels[i] < Zoom - tolerance) return KeyboardZoomLevels[i];
        return Zoom;
    }

    /// <summary>The part of the document the canvas shows, in document pixels (reaching past its edges when the view shows more).</summary>
    public RectD VisibleDocumentRect(SizeD documentSize)
    {
        var a = DocumentPoint(new PointD(0, 0), documentSize);
        var b = DocumentPoint(new PointD(ViewSize.Width, ViewSize.Height), documentSize);
        return new RectD(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
    }

    /// <summary>Scrolls so <paramref name="point"/> (document pixels) is in the middle of the canvas, at the same zoom.</summary>
    public void CenterView(PointD point, SizeD documentSize)
    {
        double scale = PointsPerPixel;
        double tx = documentSize.Width * scale / 2 - point.X * scale, ty = documentSize.Height * scale / 2 - point.Y * scale;
        Translate(tx - Pan.Width, ty - Pan.Height);
    }

    public void Translate(double dx, double dy)
    {
        Pan = new SizeD(Pan.Width + dx, Pan.Height + dy);
        FollowsFit = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static double Clamp(double value) => Math.Min(MaxZoom, Math.Max(MinZoom, value));
}
