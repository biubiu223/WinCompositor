using Compositor.App.Dialogs;
using Compositor.App.Services;
using Compositor.Core.Filters;
using Compositor.Core.Segmentation;

namespace Compositor.App;

/// <summary>Select ▸ Subject, the Magic Wand's Object mode and Filter ▸ Remove Background: the model they share.</summary>
public partial class MainWindow
{
    private bool _offeringModel;

    /// <summary>
    /// The installed model; without one, null — and (once the current click is over) the offer to get one. Sessions
    /// ask this whenever a command needs the model.
    /// </summary>
    private ISubjectModel? SubjectModelOrOffer()
    {
        if (SubjectModels.Current() is { } model) return model;
        if (!_offeringModel)
        {
            _offeringModel = true;
            Dispatcher.BeginInvoke(() =>
            {
                try { OfferSubjectModel(); }
                finally { _offeringModel = false; }
            });
        }
        return null;
    }

    /// <summary>Explains what's needed and fetches it if the user agrees; true once a model is installed.</summary>
    private bool OfferSubjectModel() => new SubjectModelDialog { Owner = this }.ShowDialog() == true && SubjectModels.Find() is not null;

    private async void SelectSubject()
    {
        if (SubjectModels.Current() is null && !OfferSubjectModel()) return;
        await S.SelectSubjectAsync();
    }

    /// <summary>Edit ▸ Content-Aware Fill: its workspace over the selection on the active layer.</summary>
    private void ContentAwareFill()
    {
        if (S.BeginContentFill() is not { } job) return;
        new ContentFillWindow(this, S, job).ShowDialog();
    }

    /// <summary>Edit ▸ Generative Fill (创造式生成): the same workspace, opening on Texture; Stable Diffusion is one pick away.</summary>
    private void GenerativeFill()
    {
        if (S.BeginContentFill() is not { } job) return;
        new ContentFillWindow(this, S, job, generate: true).ShowDialog();
    }

    private void RemoveBackground()
    {
        if (SubjectModels.Current() is null && !OfferSubjectModel()) return;
        S.BeginFilter(FilterKind.RemoveBackground);
    }
}
