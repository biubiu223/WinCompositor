namespace Compositor.Core.CameraRaw;

/// <summary>
/// What the Camera Raw panel has the canvas show besides the plain grade: Alt-drag's clipping view, the sharpening
/// mask, the clipping warnings, a point color's range, and sections switched off with their eye. None of it is ever
/// applied to the layer. Mirrors CameraRawPreviewMode and the panel's view state.
/// </summary>
public sealed record CameraRawView
{
    /// <summary>1 shows only what clips at the top (black elsewhere), 2 what clips at the bottom (white elsewhere).</summary>
    public int Clipping { get; init; }
    /// <summary>The sharpening mask in gray (white is sharpened), while Masking is dragged with Alt.</summary>
    public bool SharpenMask { get; init; }
    /// <summary>Clipped shadows painted blue.</summary>
    public bool ShadowWarning { get; init; }
    /// <summary>Clipped highlights painted red.</summary>
    public bool HighlightWarning { get; init; }
    /// <summary>A point color whose range shows, everything outside it dimmed; −1 for none.</summary>
    public int VisualizePoint { get; init; } = -1;
    /// <summary>Sections previewed as if left at their defaults.</summary>
    public CameraRawSection Hidden { get; init; }

    /// <summary>Paints something even when the settings change nothing.</summary>
    public bool Paints => Clipping != 0 || SharpenMask || VisualizePoint >= 0 || ShadowWarning || HighlightWarning;

    /// <summary>The preview is the grade itself (so its histogram is the grade's).</summary>
    public bool ShowsGrade => Clipping == 0 && !SharpenMask && VisualizePoint < 0;
}
