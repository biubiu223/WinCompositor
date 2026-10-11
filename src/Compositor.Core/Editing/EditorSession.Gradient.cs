using Compositor.Core.Geometry;
using Compositor.Core.Localization;
using Compositor.Core.Model;
using Compositor.Core.Painting;
using SkiaSharp;

namespace Compositor.Core.Editing;

/// <summary>Linear runs from start to end; radial is centered on the start with the end on its rim.</summary>
public enum GradientShape { Linear, Radial }

public enum GradientStyle { ForegroundToBackground, ForegroundToTransparent }

public sealed record GradientSettings(GradientShape Shape = GradientShape.Linear,
                                      GradientStyle Style = GradientStyle.ForegroundToTransparent,
                                      bool Reversed = false, double Opacity = 1);

/// <summary>
/// An uncommitted gradient on one layer or mask: its line in document pixels, previewed in a raster edit that never
/// touches the document until it is applied. Mirrors GradientEdit in Gradient.swift.
/// </summary>
public sealed class GradientEdit
{
    internal GradientEdit(RasterEdit raster, PointD start)
    {
        Raster = raster;
        Start = start;
        End = start;
    }

    public RasterEdit Raster { get; }
    public PointD Start { get; internal set; }
    public PointD End { get; internal set; }
    public bool HasLine => Start.DistanceTo(End) >= 0.5;
    /// <summary>Changes whenever the preview does.</summary>
    public int Revision { get; internal set; }
}

public sealed partial class EditorSession
{
    private GradientSettings _gradientSettings = new();
    public GradientSettings GradientSettings
    {
        get => _gradientSettings;
        set { if (SetField(ref _gradientSettings, value)) RefreshGradient(); }
    }

    private GradientEdit? _gradientEdit;
    /// <summary>The gradient dragged out but not yet applied (Enter, or switching tools or layers, applies it).</summary>
    public GradientEdit? GradientEdit { get => _gradientEdit; private set { _gradientEdit = value; OnPropertyChanged(); Invalidate(); } }

    /// <summary>The gradient's two ends, in straight sRGB with alpha (gray on a mask), in drawing order.</summary>
    public (SKColor From, SKColor To) GradientColors(bool mask)
    {
        SKColor Color(PaletteColor value, byte alpha)
        {
            if (mask) { byte l = (byte)Math.Round(value.Luminance * 255); return new SKColor(l, l, l, alpha); }
            return value.ToSK(alpha);
        }
        var from = Color(ForegroundColor, 255);
        var to = GradientSettings.Style == GradientStyle.ForegroundToBackground ? Color(BackgroundColor, 255) : Color(ForegroundColor, 0);
        return GradientSettings.Reversed ? (to, from) : (from, to);
    }

    /// <summary>
    /// A press with the Gradient tool: a new line from <paramref name="point"/>. Dragging again on the same layer
    /// replaces the pending line rather than stacking a second gradient.
    /// </summary>
    public void BeginGradient(PointD point)
    {
        if (Tool != Tool.Gradient) return;
        if (GradientEdit is { } pending)
        {
            if (pending.Raster.Layer.Id == ActiveLayerId && pending.Raster.IsMask == IsMaskSelected)
            {
                pending.Start = point;
                pending.End = point;
                RefreshGradient();
                return;
            }
            CommitGradient();
        }
        if (!CanPaint || ActiveLayer is not { } layer || Document is not { } document)
        {
            if (PaintRefusal is { } refusal) ShowError(L.T("无法使用渐变", "Couldn’t use the gradient"), refusal);
            return;
        }
        FinishOpacityEdit();
        try
        {
            var raster = MakeRasterEdit(layer, () => new RasterEdit(layer, IsMaskSelected, document.Width, document.Height, grows: true));
            GradientEdit = new GradientEdit(raster, point);
        }
        catch (Exception error) { ShowError(L.T("无法使用渐变", "Couldn’t use the gradient"), error.Message); }
    }

    /// <summary>Moves the line's end (while dragging), or either end by its handle.</summary>
    public void MoveGradient(PointD? start = null, PointD? end = null)
    {
        if (GradientEdit is not { } edit) return;
        if (start is { } s) edit.Start = s;
        if (end is { } e) edit.End = e;
        RefreshGradient();
    }

    /// <summary>Draws the pending gradient again from its line, the settings and the colors.</summary>
    public void RefreshGradient()
    {
        if (GradientEdit is not { } edit || Document is not { } document) return;
        if (edit.HasLine)
        {
            try
            {
                var (from, to) = GradientColors(edit.Raster.IsMask);
                var start = edit.Start.ToSK();
                var end = edit.End.ToSK();
                var shape = GradientSettings.Shape;
                double opacity = Math.Clamp(GradientSettings.Opacity, 0.01, 1);
                var canvasRect = new SKRect(0, 0, document.Width, document.Height);
                edit.Raster.PaintOver(document.Bounds, canvas =>
                {
                    using var shader = shape == GradientShape.Linear
                        ? SKShader.CreateLinearGradient(start, end, new[] { from, to }, null, SKShaderTileMode.Clamp)
                        : SKShader.CreateRadialGradient(start, Math.Max(0.5f, SKPoint.Distance(start, end)), new[] { from, to }, null, SKShaderTileMode.Clamp);
                    using var paint = new SKPaint { Shader = shader, IsDither = true, Color = SKColors.Black.WithAlpha((byte)Math.Round(opacity * 255)) };
                    canvas.DrawRect(canvasRect, paint);
                });
            }
            catch (Exception error)
            {
                CancelGradient();
                ShowError(L.T("无法使用渐变", "Couldn’t use the gradient"), error.Message);
                return;
            }
        }
        edit.Revision++;
        Invalidate();
    }

    /// <summary>The drag ended: a click without a line leaves nothing pending.</summary>
    public void EndGradientDrag()
    {
        if (GradientEdit is { HasLine: false }) CancelGradient();
    }

    public void CancelGradient()
    {
        if (GradientEdit is not { } edit) return;
        edit.Raster.Release();
        GradientEdit = null;
    }

    /// <summary>Applies the pending gradient as one undo step.</summary>
    public void CommitGradient()
    {
        if (GradientEdit is not { } edit) return;
        if (!edit.HasLine) { CancelGradient(); return; }
        try { CommitRasterEdit(edit.Raster, edit.Raster.IsMask ? "Gradient Mask" : "Gradient"); }
        catch (Exception error) { ShowError(L.T("无法使用渐变", "Couldn’t use the gradient"), error.Message); }
        CancelGradient();
    }
}
