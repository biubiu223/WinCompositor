using Compositor.Core.Geometry;
using Compositor.Core.Model;
using Compositor.Core.Pixels;
using SkiaSharp;

namespace Compositor.Core.Editing;

/// <summary>What a click on the canvas does while Color Range is open: start over from that color, add it, or take it away.</summary>
public enum ColorRangeSampleMode { Replace, Add, Remove }

/// <summary>
/// Select ▸ Color Range: every pixel near the colors clicked on the canvas, anywhere in the image. The panel shows
/// the selection live; OK keeps it as one undo step, Cancel puts back the one there was. Mirrors ColorRangeEdit in
/// ColorRangeSelection.swift.
/// </summary>
public sealed class ColorRangeEdit
{
    public const double MaxFuzziness = 200;
    /// <summary>The panel's preview fits in this many pixels.</summary>
    public const int PreviewWidth = 292, PreviewHeight = 200;

    internal ColorRangeEdit(byte[] image, int width, int height, DocumentSelection? original)
    {
        Image = image; Width = width; Height = height; Original = original;
    }

    public double Fuzziness { get; internal set; } = 40;
    public bool Invert { get; internal set; }
    public ColorRangeSampleMode SampleMode { get; set; } = ColorRangeSampleMode.Replace;
    /// <summary>Picked colors (straight RGB, three bytes each) to select, and to leave out.</summary>
    public List<byte> Include { get; } = new();
    public List<byte> Exclude { get; } = new();
    public bool HasColors => Include.Count > 0;

    /// <summary>The selection in black and white, shrunk for the panel (one byte a pixel), with its size. Null until a color is picked.</summary>
    public byte[]? Preview { get; internal set; }
    public int PreviewPixelWidth { get; internal set; }
    public int PreviewPixelHeight { get; internal set; }
    /// <summary>The selection as it stands, shown on the canvas without an undo step until OK.</summary>
    public DocumentSelection? Result { get; internal set; }
    public string? Error { get; internal set; }

    /// <summary>The image as shown, at document size (premultiplied RGBA): what the colors are matched against.</summary>
    internal byte[] Image { get; }
    internal int Width { get; }
    internal int Height { get; }
    internal DocumentSelection? Original { get; }
    internal int Generation;
}

public sealed partial class EditorSession
{
    private ColorRangeEdit? _colorRange;
    /// <summary>Select ▸ Color Range, while its panel is open.</summary>
    public ColorRangeEdit? ColorRange
    {
        get => _colorRange;
        private set
        {
            if (ReferenceEquals(_colorRange, value)) return;
            _colorRange = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
            Invalidate();
        }
    }

    /// <summary>Raised when the Color Range panel's preview changes.</summary>
    public event EventHandler? ColorRangeChanged;

    public bool CanSelectColorRange => Document is not null && ColorRange is null && CanEditSelection && LassoDraft is null && AdjustmentEdit is null
                                       && EffectEdit is null && TextDraft is null;

    public void BeginColorRange()
    {
        if (!CanSelectColorRange || Document is not { } document || SelectionSample(sampleAllLayers: true) is not { } image) return;
        ColorRange = new ColorRangeEdit(image, document.Width, document.Height, document.Selection) { Result = document.Selection };
    }

    /// <summary>
    /// A click on the canvas while the panel is open. Shift adds the color and Alt takes it away, whichever eyedropper
    /// is chosen.
    /// </summary>
    public void SampleColorRange(PointD point, bool shift = false, bool alt = false)
    {
        if (ColorRange is not { } edit || ColorAt(edit, point) is not { } color) return;
        switch (alt ? ColorRangeSampleMode.Remove : shift ? ColorRangeSampleMode.Add : edit.SampleMode)
        {
            case ColorRangeSampleMode.Replace:
                edit.Include.Clear(); edit.Include.AddRange(color); edit.Exclude.Clear();
                break;
            case ColorRangeSampleMode.Add: edit.Include.AddRange(color); break;
            case ColorRangeSampleMode.Remove: edit.Exclude.AddRange(color); break;
        }
        UpdateColorRange();
    }

    /// <summary>Fuzziness (0–200) or Invert changed.</summary>
    public void SetColorRangeOptions(double fuzziness, bool invert)
    {
        if (ColorRange is not { } edit) return;
        fuzziness = double.IsFinite(fuzziness) ? Math.Clamp(Math.Round(fuzziness), 0, ColorRangeEdit.MaxFuzziness) : 40;
        if (edit.Fuzziness == fuzziness && edit.Invert == invert) return;
        edit.Fuzziness = fuzziness;
        edit.Invert = invert;
        UpdateColorRange();
    }

    /// <summary>
    /// Matches the image against the picked colors (off the interface thread when there is one) and shows the result
    /// as the selection, without an undo step. A newer change supersedes one still being worked out.
    /// </summary>
    private void UpdateColorRange()
    {
        if (ColorRange is not { } edit) return;
        int generation = ++edit.Generation;
        if (!edit.HasColors)
        {
            edit.Result = edit.Original;
            edit.Preview = null;
            ColorRangeUpdated();
            return;
        }
        var include = edit.Include.ToArray();
        var exclude = edit.Exclude.ToArray();
        int fuzziness = (int)edit.Fuzziness;
        bool invert = edit.Invert;
        (SKPath? Path, byte[] Preview, int W, int H, string? Error) Work()
        {
            try
            {
                var mask = ColorRangeMask(edit.Image, edit.Width, edit.Height, include, exclude, fuzziness, invert);
                var (preview, w, h) = ColorRangePreview(mask, edit.Width, edit.Height);
                var path = mask.AsSpan().IndexOfAnyExcept((byte)0) >= 0 ? MagicWand.Outline(mask, edit.Width, edit.Height) : null;
                return (path, preview, w, h, null);
            }
            catch (Exception error) { return (null, Array.Empty<byte>(), 0, 0, error.Message); }
        }
        void Show((SKPath? Path, byte[] Preview, int W, int H, string? Error) result)
        {
            if (!ReferenceEquals(ColorRange, edit) || edit.Generation != generation) return;
            edit.Error = result.Error;
            if (result.Error is not null) { ColorRangeUpdated(); return; }
            edit.Preview = result.Preview;
            edit.PreviewPixelWidth = result.W;
            edit.PreviewPixelHeight = result.H;
            edit.Result = result.Path is { } path ? new DocumentSelection(path, SelectionAntialiased) : null;
            ColorRangeUpdated();
        }
        var context = PreviewsInBackground ? SynchronizationContext.Current : null;
        if (context is null) { Show(Work()); return; }
        Task.Run(Work).ContinueWith(task => context.Post(_ => Show(task.Result), null), TaskScheduler.Default);
    }

    private void ColorRangeUpdated()
    {
        ColorRangeChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    public void CommitColorRange()
    {
        if (ColorRange is not { } edit) return;
        var result = edit.Result;
        ColorRange = null;
        if (!edit.HasColors || edit.Error is not null) return;
        if (result is not null) SetSelection(result, "Color Range");
        else Deselect();
    }

    public void CancelColorRange() => ColorRange = null;

    /// <summary>The straight color under <paramref name="point"/> (document pixels), averaged over the 3 × 3 pixels around it.</summary>
    private static byte[]? ColorAt(ColorRangeEdit edit, PointD point)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) return null;
        int x = (int)Math.Floor(point.X), y = (int)Math.Floor(point.Y);
        if (x < 0 || y < 0 || x >= edit.Width || y >= edit.Height) return null;
        long r = 0, g = 0, b = 0, a = 0;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int px = x + dx, py = y + dy;
                if (px < 0 || py < 0 || px >= edit.Width || py >= edit.Height) continue;
                int p = (py * edit.Width + px) * 4;
                r += edit.Image[p]; g += edit.Image[p + 1]; b += edit.Image[p + 2]; a += edit.Image[p + 3];
            }
        if (a == 0) return null;
        return new[] { (byte)Math.Min(255, (r * 255 + a / 2) / a), (byte)Math.Min(255, (g * 255 + a / 2) / a), (byte)Math.Min(255, (b * 255 + a / 2) / a) };
    }

    /// <summary>255 where a pixel is within <paramref name="fuzziness"/> of an included color (per channel) and of no excluded one. Mirrors color_range_mask.</summary>
    internal static byte[] ColorRangeMask(byte[] rgba, int width, int height, byte[] include, byte[] exclude, int fuzziness, bool invert)
    {
        var mask = new byte[width * height];
        static bool Near(int r, int g, int b, byte[] colors, int fuzziness)
        {
            for (int i = 0; i + 2 < colors.Length; i += 3)
                if (Math.Abs(r - colors[i]) <= fuzziness && Math.Abs(g - colors[i + 1]) <= fuzziness && Math.Abs(b - colors[i + 2]) <= fuzziness) return true;
            return false;
        }
        Parallel.For(0, height, y =>
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int p = (row + x) * 4;
                bool matches = false;
                int a = rgba[p + 3];
                if (a != 0)
                {
                    int r = (rgba[p] * 255 + a / 2) / a, g = (rgba[p + 1] * 255 + a / 2) / a, b = (rgba[p + 2] * 255 + a / 2) / a;
                    matches = Near(r, g, b, include, fuzziness) && !Near(r, g, b, exclude, fuzziness);
                }
                if (invert) matches = !matches;
                mask[row + x] = matches ? (byte)255 : (byte)0;
            }
        });
        return mask;
    }

    /// <summary>The mask shrunk to fit the panel's preview (at twice its size, for high-DPI screens).</summary>
    private static (byte[] Pixels, int Width, int Height) ColorRangePreview(byte[] mask, int width, int height)
    {
        double scale = Math.Min((double)ColorRangeEdit.PreviewWidth / width, (double)ColorRangeEdit.PreviewHeight / height) * 2;
        int w = Math.Max(1, (int)(width * scale)), h = Math.Max(1, (int)(height * scale));
        var info = new SKImageInfo(width, height, SKColorType.Gray8, SKAlphaType.Opaque);
        using var full = new SKBitmap(info);
        System.Runtime.InteropServices.Marshal.Copy(mask, 0, full.GetPixels(), mask.Length);
        using var small = new SKBitmap(new SKImageInfo(w, h, SKColorType.Gray8, SKAlphaType.Opaque));
        full.ScalePixels(small, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        var bytes = new byte[w * h];
        var span = small.GetPixelSpan();
        for (int y = 0; y < h; y++) span.Slice(y * small.RowBytes, w).CopyTo(bytes.AsSpan(y * w));
        return (bytes, w, h);
    }
}
