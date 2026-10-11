using Compositor.Core.Inpainting;
using Compositor.Core.Localization;
using Compositor.Core.Model;
using Compositor.Core.Painting;
using Compositor.Core.Segmentation;

namespace Compositor.Core.Editing;

/// <summary>The Remove tool's options.</summary>
/// <param name="Neural">The inpainting model rebuilds the structure under the stroke before synthesis adds the grain.</param>
/// <param name="SnapToObjects">An object the stroke mostly covers is found by the subject model and removed whole.</param>
public sealed record RemoveSettings(bool Neural = true, bool SnapToObjects = true);

public sealed partial class EditorSession
{
    private RemoveSettings _removeSettings = new();
    public RemoveSettings RemoveSettings { get => _removeSettings; set => SetField(ref _removeSettings, value); }

    /// <summary>The inpainting model for the Remove tool's neural reconstruction; null without one.</summary>
    public Func<IInpaintModel?>? InpaintModelSource { get; set; }


    /// <summary>The subject model when it is installed, without offering to fetch it (the Remove tool works without it).</summary>
    public Func<ISubjectModel?>? InstalledSubjectModel { get; set; }

    private bool _removeBusy;
    /// <summary>A Remove stroke is being filled away (its wash stays on the canvas until it's done).</summary>
    public bool RemoveBusy { get => _removeBusy; private set => SetField(ref _removeBusy, value); }

    /// <summary>
    /// The Remove tool's stroke has ended: what it covered (and anything it encircles, or an object it mostly covers)
    /// is filled from its surroundings the way Content-Aware Fill's Texture method does it (the inpainting model
    /// rebuilds the structure, texture synthesis lays the picture's own grain over it), off the interface thread, then
    /// put on the layer as one undo step.
    /// </summary>
    private async Task RemoveAsync(BrushStroke stroke)
    {
        RemoveBusy = true;
        try
        {
            if (Document is not { } document || stroke.Layer.Asset is not { } asset) return;
            int width = asset.Width, height = asset.Height;
            var coverage = stroke.PaintedCoverage(width, height);
            var painted = new byte[coverage.Length];
            bool any = false;
            for (int i = 0; i < coverage.Length; i++) if (coverage[i] >= 24) { painted[i] = 255; any = true; }
            if (!any) return;
            var rgba = asset.CopyPixels();
            // The selection still limits where the fill may go, after the area grows.
            byte[]? allowed = null;
            if (stroke.Selection is { } clip) allowed = GridCoverage(clip, stroke.Layer.Transform.PixelToDocument(width, height), width, height);
            var settings = RemoveSettings;
            var subject = settings.SnapToObjects ? InstalledSubjectModel?.Invoke() : null;
            var inpaint = settings.Neural ? InpaintModelSource?.Invoke() : null;

            int seed = Random.Shared.Next(1, int.MaxValue);
            var (filled, hole) = await Task.Run(() =>
            {
                var area = RemoveMask.CloseLoops(painted, width, height);
                if (subject is not null)
                {
                    try { area = RemoveMask.SnapToObjects(subject, rgba, area, width, height); }
                    catch { /* without the model's help the stroke is the area */ }
                }
                // A little past the stroke's edge, so no rim of what was removed is left behind.
                area = FillMasks.Grow(area, width, height, 3);
                if (allowed is not null)
                    for (int i = 0; i < area.Length; i++) if (allowed[i] < 128) area[i] = 0;
                var sample = FillMasks.Auto(rgba, area, width, height);
                // Content-Aware Fill's Texture method: the model continues the structure (kerbs, lawns, edges), then
                // synthesis stitches the most similar patches around it over that, for the picture's own grain.
                var outcome = ContentFill.Run(rgba, width, height, area, sample,
                    new ContentFillSettings { Method = FillMethod.Texture, Neural = inpaint is not null, Seed = seed }, inpaint);
                return (outcome.Rgba, area);
            });
            // The layer must still be as the stroke found it, and the stroke still the one shown.
            if (!ReferenceEquals(BrushStroke, stroke) || Document is not { } now || now.Id != document.Id
                || now.Find(stroke.Layer.Id) is not { } layer || !ReferenceEquals(layer.Asset, asset) || layer.Transform != stroke.Layer.Transform) return;
            var image = RasterImage.FromRgba(filled, width, height, asset.Name);
            BeginEdit("Remove Tool");
            Document = now.UpdateLayer(layer.Id, l => l with { Asset = image, Text = null, Shape = null });
            EndEdit();
        }
        catch (Exception error) { ShowError(L.T("无法抹除", "Couldn’t remove"), error.Message); }
        finally
        {
            if (ReferenceEquals(BrushStroke, stroke)) CancelBrush();
            else stroke.Release();
            RemoveBusy = false;
        }
    }
}
