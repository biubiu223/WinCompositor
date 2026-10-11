using System.Collections.Immutable;
using Compositor.Core.Model;

namespace Compositor.Core.Editing;

/// <summary>An effect's panel being open: the layer it belongs to, which effect, and the layer's effects before.</summary>
public sealed record EffectEdit(Guid LayerId, LayerEffectKind Kind, LayerEffects? Original);

public sealed partial class EditorSession
{
    private EffectEdit? _effectEdit;
    /// <summary>The effect whose panel is open. Its changes show live; OK is one undo step, Cancel puts it back.</summary>
    public EffectEdit? EffectEdit
    {
        get => _effectEdit;
        private set
        {
            if (_effectEdit == value) return;
            _effectEdit = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
        }
    }

    /// <summary>Layer style copied with Copy Layer Style, shared by every open document as Photoshop's is.</summary>
    public static LayerEffects? CopiedEffects { get; private set; }

    private ImmutableHashSet<Guid> _collapsedEffectIds = ImmutableHashSet<Guid>.Empty;
    /// <summary>Layers whose effects list is folded away in the Layers panel.</summary>
    public ImmutableHashSet<Guid> CollapsedEffectIds { get => _collapsedEffectIds; set => SetField(ref _collapsedEffectIds, value); }

    /// <summary>Effects go on layers with pixels: not folders, adjustment layers or empty layers.</summary>
    public static bool CanHaveEffects(Layer? layer) => layer is { IsGroup: false, IsAdjustment: false, Asset: not null };

    public bool CanEditEffects => CanEditLayers && SelectedLayerIds.Count == 1 && CanHaveEffects(ActiveLayer);

    public LayerEffect? EditingEffect => EffectEdit is { } edit ? Document?.Find(edit.LayerId)?.Effects?[edit.Kind] : null;

    private static LayerEffects? Normalized(LayerEffects? effects) => effects is { IsEmpty: false } ? effects : null;

    /// <summary>
    /// Layer ▸ Layer Style ▸ (an effect), or a double-click on one in the Layers panel: adds the effect if the layer
    /// doesn't have it yet (a stroke or overlay in the background color, as on the Mac) and opens its panel.
    /// </summary>
    public bool OpenEffect(LayerEffectKind kind, Guid? layerId = null)
    {
        CommitTransform();
        var id = layerId ?? ActiveLayerId;
        if (!CanEditLayers || Document is not { } document || document.Find(id) is not { } layer || !CanHaveEffects(layer)) return false;
        var original = layer.Effects;
        bool adding = original?.Contains(kind) != true;
        if (id != ActiveLayerId) SelectLayer(id);
        BeginEdit((adding ? "Add " : "Edit ") + kind.ToName());
        if (adding)
        {
            var effect = LayerEffect.Default(kind, BackgroundColor);
            Document = document.UpdateLayer(layer.Id, l => l with { Effects = (l.Effects ?? LayerEffects.Empty).With(effect) });
        }
        else if (original![kind] is { IsEnabled: false } hidden)
        {
            // Opening a hidden effect shows it, as Photoshop's checkbox does.
            Document = document.UpdateLayer(layer.Id, l => l with { Effects = l.Effects!.With(hidden with { Enabled = true }) });
        }
        CollapsedEffectIds = CollapsedEffectIds.Remove(layer.Id);
        EffectEdit = new EffectEdit(layer.Id, kind, original);
        return true;
    }

    /// <summary>The panel's settings changed: shown on the layer straight away.</summary>
    public void UpdateEffect(LayerEffect effect)
    {
        if (EffectEdit is not { } edit || effect.Kind != edit.Kind || !effect.IsValid || Document is not { } document) return;
        if (document.Find(edit.LayerId) is null) return;
        Document = document.UpdateLayer(edit.LayerId, l => l with { Effects = (l.Effects ?? LayerEffects.Empty).With(effect) });
    }

    public void CommitEffectEdit()
    {
        if (EffectEdit is null) return;
        EffectEdit = null;
        EndEdit();
    }

    public void CancelEffectEdit()
    {
        if (EffectEdit is not { } edit) return;
        if (Document is { } document && document.Find(edit.LayerId) is not null)
            Document = document.UpdateLayer(edit.LayerId, l => l with { Effects = edit.Original });
        EffectEdit = null;
        EndEdit();
    }

    private void ChangeEffects(Guid id, string name, Func<LayerEffects, LayerEffects?> change)
    {
        if (!CanEditLayers || Document?.Find(id) is not { Effects: { } effects }) return;
        ChangeLayer(id, name, l => l with { Effects = Normalized(change(effects)) });
    }

    /// <summary>The eye beside an effect in the Layers panel.</summary>
    public void ToggleEffect(Guid id, LayerEffectKind kind)
    {
        if (Document?.Find(id)?.Effects?[kind] is not { } effect) return;
        ChangeEffects(id, (effect.IsEnabled ? "Hide " : "Show ") + kind.ToName(), e => e.With(effect with { Enabled = !effect.IsEnabled }));
    }

    /// <summary>The eye beside "效果": every effect off, or every one back on.</summary>
    public void ToggleAllEffects(Guid id)
    {
        if (Document?.Find(id)?.Effects is not { IsEmpty: false } effects) return;
        bool show = effects.All.All(e => !e.IsEnabled);
        ChangeEffects(id, show ? "Show Layer Effects" : "Hide Layer Effects",
            e => new LayerEffects(e.All.Select(x => x with { Enabled = show })));
    }

    public void RemoveEffect(Guid id, LayerEffectKind kind) => ChangeEffects(id, "Remove " + kind.ToName(), e => e.Without(kind));

    public bool CanClearEffects => CanEditLayers && ActiveLayer?.Effects is { IsEmpty: false };

    /// <summary>Layer ▸ Layer Style ▸ Clear Layer Style.</summary>
    public void ClearEffects()
    {
        if (!CanEditLayers || Document is not { } document) return;
        var ids = SelectedLayerIds.Where(id => document.Find(id)?.Effects is { IsEmpty: false }).ToList();
        if (ids.Count == 0) return;
        BeginEdit("Clear Layer Style");
        foreach (var id in ids) Document = Document!.UpdateLayer(id, l => l with { Effects = null });
        EndEdit();
    }

    public void CopyEffects()
    {
        if (ActiveLayer?.Effects is { IsEmpty: false } effects) CopiedEffects = effects;
    }

    public bool CanPasteEffects => CanEditLayers && CopiedEffects is not null && SelectedLayerIds.Any(id => CanHaveEffects(Document?.Find(id)));

    /// <summary>Layer ▸ Layer Style ▸ Paste Layer Style: onto every selected layer that can have effects.</summary>
    public void PasteEffects()
    {
        if (!CanPasteEffects || Document is not { } document || CopiedEffects is not { } effects) return;
        BeginEdit("Paste Layer Style");
        foreach (var id in SelectedLayerIds.Where(id => CanHaveEffects(document.Find(id))))
            Document = Document!.UpdateLayer(id, l => l with { Effects = effects });
        EndEdit();
    }

    public void ToggleEffectsExpansion(Guid id) =>
        CollapsedEffectIds = CollapsedEffectIds.Contains(id) ? CollapsedEffectIds.Remove(id) : CollapsedEffectIds.Add(id);
}
