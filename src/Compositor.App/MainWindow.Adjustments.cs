using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Compositor.App.Panels;
using Compositor.Core.Adjustments;
using Compositor.Core.Filters;
using Compositor.Core.Localization;
using Compositor.Core.Model;

namespace Compositor.App;

public partial class MainWindow
{
    private AdjustmentPanel? _adjustmentPanel;
    private EffectPanel? _effectPanel;
    private ColorRangePanel? _colorRangePanel;

    /// <summary>The open adjustment, effect or Color Range panel, if any.</summary>
    private IEditPanel? EditPanel => (IEditPanel?)_adjustmentPanel ?? (IEditPanel?)_effectPanel ?? _colorRangePanel;

    private void ShowColorRangePanelSoon()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_session.ColorRange is null || _colorRangePanel is not null) return;
            var panel = new ColorRangePanel(this, _session);
            _colorRangePanel = panel;
            panel.Closed += (_, _) =>
            {
                if (ReferenceEquals(_colorRangePanel, panel)) _colorRangePanel = null;
                RebuildTabs();
                UpdateStatus();
                Canvas.InvalidateVisual();
                Canvas.Focus();
            };
            panel.Show();
        });
    }

    private void ShowEffectPanelSoon()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_session.EffectEdit is null || _effectPanel is not null) return;
            var panel = new EffectPanel(this, _session);
            _effectPanel = panel;
            panel.Closed += (_, _) =>
            {
                if (ReferenceEquals(_effectPanel, panel)) _effectPanel = null;
                RebuildTabs();
                LayersPanel.Refresh();
                Canvas.Focus();
            };
            panel.Show();
        });
    }

    /// <summary>Layer ▸ Layer Style.</summary>
    private MenuItem LayerStyleMenu()
    {
        var style = SubMenu("图层样式", "Layer Style");
        foreach (var kind in Core.Model.LayerEffectKinds.All)
        {
            var k = kind;
            Add(style, NamedCommand(IdName(kind) + "…", kind.ToDisplayName() + "…", () => S.OpenEffect(k), canExecute: () => S.CanEditEffects));
        }
        style.Items.Add(new Separator());
        Add(style, Command("拷贝图层样式", "Copy Layer Style", () => S.CopyEffects(), canExecute: () => S.ActiveLayer?.Effects is { IsEmpty: false }));
        Add(style, Command("粘贴图层样式", "Paste Layer Style", () => S.PasteEffects(), canExecute: () => S.CanPasteEffects));
        Add(style, Command("清除图层样式", "Clear Layer Style", () => S.ClearEffects(), canExecute: () => S.CanClearEffects));
        return style;
    }

    // The Chinese names the shortcut ids of the adjustment, filter and layer-style commands are made of (their display
    // names as they were in Chinese). They are ids, not interface text: fixed whatever the interface language — and
    // whatever the display names become — so the shortcuts people have saved keep working.

    private static string IdName(AdjustmentKind kind) => kind switch
    {
        AdjustmentKind.HueSaturation => "色相/饱和度",
        AdjustmentKind.Levels => "色阶",
        AdjustmentKind.Curves => "曲线",
        AdjustmentKind.Exposure => "曝光度",
        AdjustmentKind.GradientMap => "渐变映射",
        AdjustmentKind.Grain => "颗粒",
        AdjustmentKind.AddNoise => "添加杂色",
        AdjustmentKind.GaussianBlur => "高斯模糊",
        AdjustmentKind.MotionBlur => "动感模糊",
        AdjustmentKind.Invert => "反相",
        AdjustmentKind.BlackWhite => "黑白",
        _ => "色彩平衡",
    };

    private static string IdName(FilterKind kind) => kind switch
    {
        FilterKind.Vignette => "暗角",
        FilterKind.BloomGlow => "泛光",
        FilterKind.TonalContrast => "色调对比",
        FilterKind.LensCorrection => "镜头校正",
        FilterKind.Dither => "仿色",
        FilterKind.Scanlines => "扫描线",
        FilterKind.CameraRaw => "Camera Raw 滤镜",
        FilterKind.RemoveBackground => "移除背景",
        _ => "内容识别填充",
    };

    private static string IdName(LayerEffectKind kind) => kind switch
    {
        LayerEffectKind.Stroke => "描边",
        LayerEffectKind.Shadow => "投影",
        LayerEffectKind.ColorOverlay => "颜色叠加",
        LayerEffectKind.InnerShadow => "内阴影",
        LayerEffectKind.OuterGlow => "外发光",
        _ => "内发光",
    };

    /// <summary>Opens the panel for the session's adjustment edit, once the command that started it has finished.</summary>
    private void ShowAdjustmentPanelSoon()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_session.AdjustmentEdit is null || _adjustmentPanel is not null) return;
            var panel = new AdjustmentPanel(this, _session);
            _adjustmentPanel = panel;
            panel.Closed += (_, _) =>
            {
                if (ReferenceEquals(_adjustmentPanel, panel)) _adjustmentPanel = null;
                RebuildTabs();
                UpdateStatus();
                Canvas.InvalidateVisual();
                Canvas.Focus();
            };
            panel.Show();
        });
    }

    /// <summary>Image ▸ Adjustments and the Filter menu: on the active layer's pixels, with the panel; named by its kind.</summary>
    private AppCommand ImageAdjustment(AdjustmentKind kind, Key key = Key.None, ModifierKeys modifiers = ModifierKeys.None) =>
        NamedCommand(IdName(kind) + "…", kind.ToDisplayName() + "…", () => S.BeginImageAdjustment(kind), key, modifiers, () => S.CanAdjustPixels);

    /// <summary>Layer ▸ New Adjustment Layer.</summary>
    private AppCommand AdjustmentLayer(AdjustmentKind kind)
    {
        string ellipsis = kind.IsEditable() ? "…" : "";
        return NamedCommand(IdName(kind) + ellipsis, kind.ToDisplayName() + ellipsis, () => S.AddAdjustmentLayer(kind), canExecute: () => S.CanEditLayers);
    }

    /// <summary>Image ▸ Adjustments.</summary>
    private MenuItem AdjustmentsMenu()
    {
        var adjustments = SubMenu("调整", "Adjustments");
        Add(adjustments, ImageAdjustment(AdjustmentKind.Levels, Key.L, ModifierKeys.Control));
        Add(adjustments, ImageAdjustment(AdjustmentKind.Curves, Key.M, ModifierKeys.Control));
        Add(adjustments, ImageAdjustment(AdjustmentKind.Exposure));
        adjustments.Items.Add(new Separator());
        Add(adjustments, ImageAdjustment(AdjustmentKind.HueSaturation, Key.U, ModifierKeys.Control));
        Add(adjustments, ImageAdjustment(AdjustmentKind.ColorBalance, Key.B, ModifierKeys.Control));
        Add(adjustments, ImageAdjustment(AdjustmentKind.BlackWhite, Key.B, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift));
        adjustments.Items.Add(new Separator());
        Add(adjustments, Command("反相", "Invert", () => S.InvertPixels(), Key.I, ModifierKeys.Control, () => S.CanInvert,
            dynamicHeader: () => S.IsMaskSelected ? L.T("反相蒙版", "Invert Mask") : L.T("反相", "Invert")));
        Add(adjustments, ImageAdjustment(AdjustmentKind.GradientMap));
        Add(adjustments, ImageAdjustment(AdjustmentKind.Grain));
        return adjustments;
    }

    /// <summary>Layer ▸ New Adjustment Layer.</summary>
    private MenuItem NewAdjustmentLayerMenu()
    {
        var newAdjustment = SubMenu("新建调整图层", "New Adjustment Layer");
        foreach (var group in new[]
                 {
                     new[] { AdjustmentKind.Levels, AdjustmentKind.Curves, AdjustmentKind.Exposure },
                     new[] { AdjustmentKind.HueSaturation, AdjustmentKind.ColorBalance, AdjustmentKind.BlackWhite },
                     new[] { AdjustmentKind.Invert, AdjustmentKind.GradientMap, AdjustmentKind.Grain },
                     new[] { AdjustmentKind.GaussianBlur, AdjustmentKind.MotionBlur, AdjustmentKind.AddNoise },
                 })
        {
            if (newAdjustment.Items.Count > 0) newAdjustment.Items.Add(new Separator());
            foreach (var kind in group) Add(newAdjustment, AdjustmentLayer(kind));
        }
        return newAdjustment;
    }

    private AppCommand EditAdjustmentLayerCommand() =>
        Command("图层内容选项…", "Layer Content Options…", () => { if (S.ActiveLayerId is { } id) S.EditAdjustmentLayer(id); },
            canExecute: () => S.CanEditLayers && S.ActiveLayer?.Adjustment is { } a && a.Kind.IsEditable());

    /// <summary>The Filter menu.</summary>
    private void BuildFilterMenu(MenuItem filter)
    {
        Add(filter, Command("上次滤镜", "Last Filter", () => S.RepeatLastFilter(), Key.F, ModifierKeys.Control | ModifierKeys.Alt, () => S.CanRepeatLastFilter,
            dynamicHeader: () => S.LastFilterTitle is { } last ? L.T($"上次滤镜（{last}）", $"Last Filter: {last}") : L.T("上次滤镜", "Last Filter")));
        filter.Items.Add(new Separator());
        var blur = SubMenu("模糊", "Blur");
        Add(blur, ImageAdjustment(AdjustmentKind.GaussianBlur));
        Add(blur, ImageAdjustment(AdjustmentKind.MotionBlur));
        filter.Items.Add(blur);
        var noise = SubMenu("杂色", "Noise");
        Add(noise, ImageAdjustment(AdjustmentKind.AddNoise));
        filter.Items.Add(noise);
        filter.Items.Add(new Separator());
        foreach (var kind in new[] { FilterKind.Vignette, FilterKind.BloomGlow, FilterKind.TonalContrast, FilterKind.LensCorrection })
            Add(filter, Filter(kind));
        filter.Items.Add(new Separator());
        Add(filter, Filter(FilterKind.Dither));
        Add(filter, Filter(FilterKind.Scanlines));
        filter.Items.Add(new Separator());
        Add(filter, NamedCommand(IdName(FilterKind.CameraRaw) + "…", FilterKind.CameraRaw.ToDisplayName() + "…", () => S.BeginFilter(FilterKind.CameraRaw),
            Key.A, ModifierKeys.Control | ModifierKeys.Shift, () => S.CanAdjustPixels));
        Add(filter, NamedCommand(IdName(FilterKind.RemoveBackground) + "…", FilterKind.RemoveBackground.ToDisplayName() + "…", RemoveBackground,
            canExecute: () => S.CanAdjustPixels));
    }

    /// <summary>A Filter-menu effect, with its panel.</summary>
    private AppCommand Filter(FilterKind kind) =>
        NamedCommand(IdName(kind) + "…", kind.ToDisplayName() + "…", () => S.BeginFilter(kind), canExecute: () => kind switch
        {
            FilterKind.Vignette => S.CanVignette,
            FilterKind.ContentAwareFill => S.CanContentAwareFill,
            _ => S.CanAdjustPixels,
        });
}
