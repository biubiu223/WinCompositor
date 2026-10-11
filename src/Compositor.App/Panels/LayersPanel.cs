using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Compositor.App.Controls;
using Compositor.App.Services;
using Compositor.Core.Adjustments;
using Compositor.Core.Editing;
using Compositor.Core.Localization;
using Compositor.Core.Model;
using SelectionMode = Compositor.Core.Editing.SelectionMode;

namespace Compositor.App.Panels;

/// <summary>
/// The Layers panel: blend mode and opacity above, the layers top first, buttons below. Mirrors LayersPanel.swift and
/// NativeLayerList.swift.
/// </summary>
public sealed class LayersPanel : DockPanel
{
    private EditorSession? _session;
    private readonly StackPanel _rows = new();
    private readonly ScrollViewer _scroll;
    private readonly ComboBox _blend = new() { Focusable = false, MinWidth = 120 };
    private readonly NumberField _opacity = new(L.T("不透明度", "Opacity"), 58) { Minimum = 0, Maximum = 100, Suffix = "%" };
    private readonly Slider _opacitySlider = new() { Minimum = 0, Maximum = 100, Width = 80 };
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private readonly Border _dropLine = new() { Height = 2, Background = (Brush)Application.Current.FindResource("AccentBrush"), Visibility = Visibility.Collapsed };
    private readonly Border _dropBox = new() { BorderBrush = (Brush)Application.Current.FindResource("AccentBrush"), BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(4), Visibility = Visibility.Collapsed };
    private bool _refreshQueued;
    private bool _updatingHeader;

    // Pointer state
    private LayerRow? _pressedRow;
    private Point _pressPoint;
    private bool _dragging;
    private (Guid? Parent, Guid? Above, bool AtBottom, bool Into, Guid Target)? _dropTarget;
    private bool? _swipeVisible;

    public LayersPanel()
    {
        Background = (Brush)Application.Current.FindResource("PanelBrush");
        LastChildFill = true;

        // Header: title, blend mode, opacity.
        var header = new StackPanel { Margin = new Thickness(12, 10, 12, 8) };
        header.Children.Add(new TextBlock { Text = L.T("图层", "Layers"), FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 0, 0, 8) });
        foreach (var group in BlendModes.Groups)
        {
            if (_blend.Items.Count > 0) _blend.Items.Add(new Separator { IsEnabled = false, Margin = new Thickness(4, 2, 4, 2) });
            foreach (var mode in group) _blend.Items.Add(new ComboBoxItem { Content = mode.ToDisplayName(), Tag = mode });
        }
        _blend.SelectionChanged += (_, _) =>
        {
            if (_updatingHeader || _session is null) return;
            if (_blend.SelectedItem is ComboBoxItem { Tag: BlendMode mode }) _session.SetLayerBlendMode(mode);
        };
        _blend.DropDownClosed += (_, _) => _session?.PreviewBlendMode(null);
        header.Children.Add(_blend);
        var opacityRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        opacityRow.Children.Add(_opacity);
        _opacitySlider.Margin = new Thickness(8, 0, 0, 0);
        opacityRow.Children.Add(_opacitySlider);
        header.Children.Add(opacityRow);
        _opacity.ValueChanged += v => _session?.SetLayerOpacity(v / 100);
        _opacity.ScrubStarted += () => _session?.BeginOpacityEdit();
        _opacity.ScrubEnded += () => _session?.FinishOpacityEdit();
        _opacitySlider.PreviewMouseLeftButtonDown += (_, _) => _session?.BeginOpacityEdit();
        _opacitySlider.PreviewMouseLeftButtonUp += (_, _) => _session?.FinishOpacityEdit();
        _opacitySlider.ValueChanged += (_, _) =>
        {
            if (_updatingHeader || _session is null) return;
            _session.SetLayerOpacity(_opacitySlider.Value / 100);
            _updatingHeader = true;
            _opacity.Value = Math.Round(_opacitySlider.Value);
            _updatingHeader = false;
        };
        var headerBorder = new Border { Child = header, BorderBrush = (Brush)Application.Current.FindResource("BorderBrush"), BorderThickness = new Thickness(0, 0, 0, 1) };
        SetDock(headerBorder, Dock.Top);
        Children.Add(headerBorder);

        // Footer buttons.
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8, 4, 8, 6) };
        var fx = new Button
        {
            Style = (Style)Application.Current.FindResource("FlatButton"),
            Content = new TextBlock { Text = "fx", FontStyle = FontStyles.Italic, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(2, -2, 2, 0) },
            ToolTip = L.T("添加图层样式", "Add a layer style"),
            Padding = new Thickness(5, 3, 5, 3),
            Margin = new Thickness(2, 0, 0, 0),
        };
        fx.Click += (_, _) =>
        {
            if (_session is not { } session) return;
            var menu = new ContextMenu { PlacementTarget = fx, Placement = PlacementMode.Top };
            foreach (var kind in LayerEffectKinds.All)
            {
                var k = kind;
                var item = new MenuItem { Header = kind.ToDisplayName() + "…", IsEnabled = session.CanEditEffects };
                item.Click += (_, _) => session.OpenEffect(k);
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        };
        footer.Children.Add(fx);
        footer.Children.Add(FooterButton("mask", L.T("添加蒙版（Alt 单击添加隐藏蒙版）", "Add a layer mask (Alt-click for a mask that hides)"), () => _session?.AddMask(!Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))));
        footer.Children.Add(FooterButton("newFolder", L.T("新建组", "Create a new group"), () => _session?.AddFolder()));
        footer.Children.Add(FooterButton("newLayer", L.T("新建图层 (Ctrl+Shift+N)", "Create a new layer (Ctrl+Shift+N)"), () => _session?.AddBlankLayer()));
        footer.Children.Add(FooterButton("trash", L.T("删除图层或蒙版", "Delete layer or mask"), () => _session?.DeleteLayerOrMask()));
        var footerBorder = new Border { Child = footer, BorderBrush = (Brush)Application.Current.FindResource("BorderBrush"), BorderThickness = new Thickness(0, 1, 0, 0) };
        SetDock(footerBorder, Dock.Bottom);
        Children.Add(footerBorder);

        // Rows, with the drop indicators drawn over them.
        var listGrid = new Grid();
        _scroll = new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false, Padding = new Thickness(0, 4, 0, 4) };
        listGrid.Children.Add(_scroll);
        _overlay.Children.Add(_dropLine);
        _overlay.Children.Add(_dropBox);
        listGrid.Children.Add(_overlay);
        listGrid.Background = Brushes.Transparent;
        Children.Add(listGrid);

        listGrid.PreviewMouseMove += OnListMouseMove;
        listGrid.PreviewMouseLeftButtonUp += OnListMouseUp;
        listGrid.LostMouseCapture += (_, _) => EndPointer();
        listGrid.MouseLeftButtonDown += (_, e) =>
        {
            // A click below the rows clears nothing; it just gives the canvas its keys back.
            if (e.OriginalSource == listGrid || e.OriginalSource == _rows) Window.GetWindow(this)?.Focus();
        };
    }

    private Button FooterButton(string icon, string tip, Action action)
    {
        var button = new Button { Style = (Style)Application.Current.FindResource("FlatButton"), Content = Icons.Make(icon, 17), ToolTip = tip, Padding = new Thickness(5), Margin = new Thickness(2, 0, 0, 0) };
        button.Click += (_, _) => action();
        return button;
    }

    public EditorSession? Session
    {
        get => _session;
        set
        {
            if (_session is not null)
            {
                _session.PropertyChanged -= OnSessionChanged;
                _session.DocumentChanged -= OnDocumentChanged;
            }
            _session = value;
            if (_session is not null)
            {
                _session.PropertyChanged += OnSessionChanged;
                _session.DocumentChanged += OnDocumentChanged;
            }
            Refresh();
        }
    }

    private void OnDocumentChanged(object? sender, EventArgs e) => QueueRefresh();

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EditorSession.ActiveLayerId) or nameof(EditorSession.SelectedLayerIds) or nameof(EditorSession.IsMaskSelected)
            or nameof(EditorSession.CollapsedGroupIds) or nameof(EditorSession.RenamingLayerId) or nameof(EditorSession.ViewsMaskAlone)
            or nameof(EditorSession.Document) or nameof(EditorSession.BrushStroke) or nameof(EditorSession.TransformEdit))
            QueueRefresh();
    }

    private void QueueRefresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.BeginInvoke(() => { _refreshQueued = false; Refresh(); }, System.Windows.Threading.DispatcherPriority.Background);
    }

    // MARK: Building

    public void Refresh()
    {
        if (_dragging || _swipeVisible is not null) return;
        _rows.Children.Clear();
        var session = _session;
        UpdateHeader();
        if (session?.Document is not { } document) return;
        var entries = document.Entries(topFirst: true, session.CollapsedGroupIds);
        foreach (var entry in entries)
        {
            var row = new LayerRow(this, session, document, entry);
            _rows.Children.Add(row);
            // The layer's effects beneath it, as Photoshop lists them, unless folded away.
            if (entry.Layer.Effects is { IsEmpty: false } effects && !session.CollapsedEffectIds.Contains(entry.Layer.Id))
            {
                _rows.Children.Add(new EffectRow(session, entry, null, effects));
                foreach (var effect in effects.All) _rows.Children.Add(new EffectRow(session, entry, effect, effects));
            }
        }
    }

    private void UpdateHeader()
    {
        _updatingHeader = true;
        var layer = _session?.ActiveLayer;
        bool editable = _session?.CanEditOpacity == true;
        _opacity.IsEnabled = editable;
        _opacitySlider.IsEnabled = editable;
        _blend.IsEnabled = _session?.CanEditAppearance == true;
        if (layer is not null)
        {
            _opacity.Value = Math.Round(layer.Opacity * 100);
            _opacitySlider.Value = layer.Opacity * 100;
            if (layer.IsGroup)
            {
                _blend.SelectedItem = null;
                _blend.Text = L.T("穿透", "Pass Through");
            }
            else _blend.SelectedItem = _blend.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (BlendMode)i.Tag == layer.BlendMode);
        }
        _updatingHeader = false;
    }

    // MARK: Pointer handling shared by the rows

    internal void BeginRowPress(LayerRow row, MouseButtonEventArgs e)
    {
        _pressedRow = row;
        _pressPoint = e.GetPosition(this);
        _dragging = false;
        ((UIElement)_scroll.Parent).CaptureMouse();
    }

    internal void BeginSwipe(Guid id)
    {
        if (_session is null) return;
        _swipeVisible = _session.BeginVisibilitySwipe(id);
        if (_swipeVisible is null) return;
        ((UIElement)_scroll.Parent).CaptureMouse();
    }

    private LayerRow? RowAt(Point point)
    {
        foreach (var row in _rows.Children.OfType<LayerRow>())
        {
            var top = row.TranslatePoint(new Point(0, 0), (UIElement)_scroll.Parent);
            if (point.Y >= top.Y && point.Y < top.Y + row.ActualHeight) return row;
        }
        return null;
    }

    private void OnListMouseMove(object sender, MouseEventArgs e)
    {
        if (_session is null) return;
        var list = (UIElement)_scroll.Parent;
        var point = e.GetPosition(list);
        if (_swipeVisible is { } visible)
        {
            if (RowAt(point) is { } row) _session.SetVisibilityInSwipe(row.Entry.Layer.Id, visible);
            return;
        }
        if (_pressedRow is null || e.LeftButton != MouseButtonState.Pressed) return;
        if (!_dragging && (e.GetPosition(this) - _pressPoint).Length < 5) return;
        _dragging = true;
        UpdateDropTarget(point);
        // Scroll when dragging near the edges.
        if (point.Y < 16) _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset - 8);
        else if (point.Y > list.RenderSize.Height - 16) _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset + 8);
    }

    private void UpdateDropTarget(Point point)
    {
        _dropTarget = null;
        _dropLine.Visibility = Visibility.Collapsed;
        _dropBox.Visibility = Visibility.Collapsed;
        if (_pressedRow is not { } pressed || _session?.Document is not { } document) return;
        var row = RowAt(point);
        var list = (UIElement)_scroll.Parent;
        if (row is null)
        {
            // Past the last row: the very bottom of the stack.
            var last = _rows.Children.OfType<LayerRow>().LastOrDefault();
            if (last is null) return;
            var bottom = last.TranslatePoint(new Point(0, last.ActualHeight), list);
            if (point.Y < bottom.Y) return;
            _dropTarget = (null, null, true, false, last.Entry.Layer.Id);
            ShowLine(bottom.Y, 0);
            return;
        }
        var layer = row.Entry.Layer;
        if (layer.Id == pressed.Entry.Layer.Id) return;
        var top = row.TranslatePoint(new Point(0, 0), list);
        double y = point.Y - top.Y, h = row.ActualHeight;
        bool expandedFolder = layer.IsGroup && !_session.CollapsedGroupIds.Contains(layer.Id);
        double indent = 26 + row.Entry.Depth * 14;
        if (layer.IsGroup && y > h * 0.3 && y < h * 0.7)
        {
            if (!_session.CanPlaceLayer(pressed.Entry.Layer.Id, layer.Id)) return;
            _dropTarget = (layer.Id, null, false, true, layer.Id);
            Canvas.SetLeft(_dropBox, 2);
            Canvas.SetTop(_dropBox, top.Y);
            _dropBox.Width = Math.Max(0, row.ActualWidth - 4);
            _dropBox.Height = h;
            _dropBox.Visibility = Visibility.Visible;
            return;
        }
        if (y <= h / 2)
        {
            // Above this row: on top of it among its siblings.
            if (!_session.CanPlaceLayer(pressed.Entry.Layer.Id, layer.ParentId)) return;
            _dropTarget = (layer.ParentId, layer.Id, false, false, layer.Id);
            ShowLine(top.Y, indent);
        }
        else if (expandedFolder)
        {
            if (!_session.CanPlaceLayer(pressed.Entry.Layer.Id, layer.Id)) return;
            _dropTarget = (layer.Id, null, false, true, layer.Id);
            ShowLine(top.Y + h, indent + 14);
        }
        else
        {
            // Below this row: just under it among its siblings.
            if (!_session.CanPlaceLayer(pressed.Entry.Layer.Id, layer.ParentId)) return;
            var siblings = document.Layers.Where(l => l.ParentId == layer.ParentId && l.Id != pressed.Entry.Layer.Id).ToList();
            int index = siblings.FindIndex(l => l.Id == layer.Id);
            if (index > 0) _dropTarget = (layer.ParentId, siblings[index - 1].Id, false, false, layer.Id);
            else _dropTarget = (layer.ParentId, null, true, false, layer.Id);
            ShowLine(top.Y + h, indent);
        }
    }

    private void ShowLine(double y, double indent)
    {
        Canvas.SetLeft(_dropLine, indent);
        Canvas.SetTop(_dropLine, y - 1);
        _dropLine.Width = Math.Max(0, ActualWidth - indent - 8);
        _dropLine.Visibility = Visibility.Visible;
    }

    private void OnListMouseUp(object sender, MouseButtonEventArgs e)
    {
        var session = _session;
        if (_swipeVisible is not null)
        {
            _swipeVisible = null;
            session?.EndVisibilitySwipe();
            ((UIElement)_scroll.Parent).ReleaseMouseCapture();
            Refresh();
            return;
        }
        if (_pressedRow is not { } pressed) return;
        bool dragged = _dragging;
        var target = _dropTarget;
        EndPointer();
        if (session is null) return;
        if (dragged)
        {
            if (target is { } t)
            {
                if (t.Into) session.PlaceLayer(pressed.Entry.Layer.Id, t.Parent);
                else session.PlaceLayer(pressed.Entry.Layer.Id, t.Parent, above: t.Above, atBottom: t.AtBottom);
            }
            Refresh();
            return;
        }
        pressed.Click(e);
    }

    private void EndPointer()
    {
        _pressedRow = null;
        _dragging = false;
        _dropTarget = null;
        _dropLine.Visibility = Visibility.Collapsed;
        _dropBox.Visibility = Visibility.Collapsed;
        var list = (UIElement)_scroll.Parent;
        if (list.IsMouseCaptured) list.ReleaseMouseCapture();
    }

    internal ContextMenu BuildContextMenu(Layer layer)
    {
        var session = _session!;
        var menu = new ContextMenu();
        void Item(string header, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        if (layer.Adjustment is { } adjustment && adjustment.Kind.IsEditable())
        {
            var adjustmentName = adjustment.Kind.ToDisplayName();
            Item(L.T($"编辑{adjustmentName}…", $"Edit {adjustmentName}…"), () => session.EditAdjustmentLayer(layer.Id), session.CanEditLayers);
            menu.Items.Add(new Separator());
        }
        Item(L.T("重命名图层…", "Rename Layer…"), () => session.RenamingLayerId = layer.Id);
        Item(L.T("复制图层", "Duplicate Layer"), () => session.DuplicateLayers());
        Item(L.T("删除图层", "Delete Layer"), () => session.DeleteSelectedLayers());
        menu.Items.Add(new Separator());
        Item(L.T("图层编组", "Group Layers"), () => session.GroupSelectedLayers());
        Item(L.T("取消图层编组", "Ungroup Layers"), () => session.UngroupLayers(), layer.IsGroup);
        Item(L.T("移出组", "Move Out of Group"), () => session.MoveActiveLayerOutOfGroup(), layer.ParentId is not null);
        menu.Items.Add(new Separator());
        Item(layer.MaskSourceId is null ? L.T("创建剪贴蒙版", "Create Clipping Mask") : L.T("释放剪贴蒙版", "Release Clipping Mask"), () => session.ToggleClippingMask(layer.Id), session.CanToggleClippingMask(layer.Id));
        if (layer.Mask is null)
        {
            Item(session.Selection is null ? L.T("添加图层蒙版", "Add Layer Mask") : L.T("添加图层蒙版（显示选区）", "Add Layer Mask (Reveal Selection)"), () => session.AddMask(true));
        }
        else
        {
            Item(layer.Mask.IsEnabled ? L.T("停用图层蒙版", "Disable Layer Mask") : L.T("启用图层蒙版", "Enable Layer Mask"), () => session.ToggleLayerMask());
            Item(L.T("应用图层蒙版", "Apply Layer Mask"), () => session.ApplyLayerMask(), layer.Asset is not null);
            Item(L.T("删除图层蒙版", "Delete Layer Mask"), () => session.DeleteLayerMask());
            Item(layer.Mask.IsLinked ? L.T("取消链接蒙版", "Unlink Mask") : L.T("链接蒙版", "Link Mask"), () => session.ToggleMaskLink(layer.Id));
        }
        menu.Items.Add(new Separator());
        var styles = new MenuItem { Header = L.T("图层样式", "Layer Style"), IsEnabled = EditorSession.CanHaveEffects(layer) && session.CanEditLayers };
        foreach (var kind in LayerEffectKinds.All)
        {
            var k = kind;
            var item = new MenuItem { Header = kind.ToDisplayName() + "…" };
            item.Click += (_, _) => session.OpenEffect(k, layer.Id);
            styles.Items.Add(item);
        }
        menu.Items.Add(styles);
        Item(L.T("拷贝图层样式", "Copy Layer Style"), () => session.CopyEffects(), layer.Effects is { IsEmpty: false });
        Item(L.T("粘贴图层样式", "Paste Layer Style"), () => session.PasteEffects(), session.CanPasteEffects);
        Item(L.T("清除图层样式", "Clear Layer Style"), () => session.ClearEffects(), layer.Effects is { IsEmpty: false } && session.CanEditLayers);
        menu.Items.Add(new Separator());
        Item(L.T("选择像素", "Select Pixels"), () => session.LoadLayerSelection(layer.Id), layer.Asset is not null);
        Item(session.MergeTitle, () => session.MergeLayers(), session.CanMergeLayers);
        return menu;
    }
}

/// <summary>
/// A line under a layer listing its effects: "效果" with an eye for all of them, then one per effect with its own eye.
/// Double-click an effect to edit it; right-click for more.
/// </summary>
internal sealed class EffectRow : Border
{
    public EffectRow(EditorSession session, HierarchyEntry entry, LayerEffect? effect, LayerEffects effects)
    {
        var layer = entry.Layer;
        Height = 22;
        Margin = new Thickness(4, 0, 4, 1);
        Background = Brushes.Transparent;
        bool shown = effect is null ? effects.All.Any(e => e.IsEnabled) : effect.IsEnabled;
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var eye = new Border { Width = 26, Height = 22, Background = Brushes.Transparent, ToolTip = effect is null ? L.T("显示/隐藏全部效果", "Show/hide all effects") : L.T("显示/隐藏", "Show/hide ") + effect.Kind.ToDisplayName() };
        if (shown) eye.Child = Icons.Make("eye", 13);
        eye.MouseLeftButtonDown += (_, e) =>
        {
            if (effect is null) session.ToggleAllEffects(layer.Id); else session.ToggleEffect(layer.Id, effect.Kind);
            e.Handled = true;
        };
        row.Children.Add(eye);
        row.Children.Add(new Border { Width = entry.Depth * 14 + (effect is null ? 36 : 52) });
        var text = new TextBlock
        {
            Text = effect is null ? L.T("效果", "Effects") : effect.Kind.ToDisplayName(),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11.5,
            Foreground = (Brush)Application.Current.FindResource(entry.Visible && shown ? "TextBrush" : "TextDimBrush"),
        };
        row.Children.Add(text);
        Child = row;
        ToolTip = effect is null ? null : L.T("双击编辑", "Double-click to edit ") + effect.Kind.ToDisplayName();

        MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (effect is not null && e.ClickCount == 2) { session.OpenEffect(effect.Kind, layer.Id); return; }
            if (session.ActiveLayerId != layer.Id) session.SelectLayer(layer.Id);
        };
        MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            var menu = new ContextMenu { PlacementTarget = this };
            void Item(string header, Action action, bool enabled = true)
            {
                var item = new MenuItem { Header = header, IsEnabled = enabled && session.CanEditLayers };
                item.Click += (_, _) => action();
                menu.Items.Add(item);
            }
            if (effect is not null)
            {
                Item(L.T("编辑", "Edit ") + effect.Kind.ToDisplayName() + "…", () => session.OpenEffect(effect.Kind, layer.Id));
                Item(effect.IsEnabled ? L.T("隐藏", "Hide") : L.T("显示", "Show"), () => session.ToggleEffect(layer.Id, effect.Kind));
                Item(L.T("删除", "Delete ") + effect.Kind.ToDisplayName(), () => session.RemoveEffect(layer.Id, effect.Kind));
            }
            else
            {
                Item(shown ? L.T("隐藏全部效果", "Hide All Effects") : L.T("显示全部效果", "Show All Effects"), () => session.ToggleAllEffects(layer.Id));
                Item(L.T("清除图层样式", "Clear Layer Style"), () => { session.SelectLayer(layer.Id); session.ClearEffects(); });
            }
            menu.IsOpen = true;
        };
    }
}

/// <summary>One row of the Layers panel.</summary>
internal sealed class LayerRow : Border
{
    private static Brush? _checker;
    private readonly LayersPanel _panel;
    private readonly EditorSession _session;
    public HierarchyEntry Entry { get; }
    private readonly FrameworkElement? _thumb;
    private readonly FrameworkElement? _maskThumb;

    public LayerRow(LayersPanel panel, EditorSession session, CanvasDocument document, HierarchyEntry entry)
    {
        _panel = panel;
        _session = session;
        Entry = entry;
        var layer = entry.Layer;
        bool selected = session.SelectedLayerIds.Contains(layer.Id);
        bool active = session.ActiveLayerId == layer.Id;
        Height = 44;
        Margin = new Thickness(4, 0, 4, 1);
        CornerRadius = new CornerRadius(5);
        Background = selected ? (Brush)Application.Current.FindResource("SelectionBrush") : Brushes.Transparent;
        Cursor = Cursors.Arrow;

        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        // Eye.
        var eye = new Border { Width = 26, Height = 44, Background = Brushes.Transparent, ToolTip = L.T("显示/隐藏（拖过多个眼睛一起切换）", "Show/hide (drag across several eyes to switch them together)") };
        if (layer.IsVisible) eye.Child = Icons.Make("eye", 15);
        eye.MouseLeftButtonDown += (_, e) => { _panel.BeginSwipe(layer.Id); e.Handled = true; };
        row.Children.Add(eye);
        row.Children.Add(new Border { Width = entry.Depth * 14 });
        // Folder disclosure, or the clipping arrow.
        if (layer.IsGroup)
        {
            bool collapsed = session.CollapsedGroupIds.Contains(layer.Id);
            var chevron = new Border { Width = 16, Height = 30, Background = Brushes.Transparent, Child = Icons.Make(collapsed ? "chevronRight" : "chevronDown", 11) };
            chevron.MouseLeftButtonDown += (_, e) => { session.ToggleGroupExpansion(layer.Id); e.Handled = true; };
            row.Children.Add(chevron);
        }
        else if (layer.MaskSourceId is not null)
        {
            row.Children.Add(new Border { Width = 16, Child = Icons.Make("clip", 12), ToolTip = L.T("剪贴蒙版", "Clipping mask") });
        }
        else row.Children.Add(new Border { Width = 16 });

        // Thumbnail.
        if (layer.IsGroup)
        {
            _thumb = new Border { Width = 34, Height = 34, Child = Icons.Make("folder", 22), Margin = new Thickness(0, 0, 4, 0) };
        }
        else if (layer.Adjustment is { } adjustment)
        {
            _thumb = AdjustmentThumbnail(adjustment.Kind);
            if (active && !session.IsMaskSelected) Outline(_thumb);
        }
        else
        {
            // The badge is 34 pixels wide in a 9-point font: a short word in English.
            _thumb = Thumbnail(layer.Asset, isMask: false, layer.IsAdjustment ? L.T("调整", "Adj.") : null);
            ((FrameworkElement)_thumb).Margin = new Thickness(0, 0, 4, 0);
            if (active && !session.IsMaskSelected) Outline(_thumb);
        }
        _thumb.ToolTip = layer.IsGroup ? null
            : layer.Adjustment is { } kind ? L.T($"{kind.Kind.ToDisplayName()}调整图层", $"{kind.Kind.ToDisplayName()} adjustment layer") + (kind.Kind.IsEditable() ? L.T(" · 双击编辑设置", " · Double-click to edit its settings") : "")
            : L.T("Ctrl 单击：载入像素为选区", "Ctrl-click: load the pixels as a selection");
        row.Children.Add(_thumb);

        // Mask thumbnail.
        if (layer.Mask is { } mask)
        {
            if (!mask.IsLinked) row.Children.Add(new Border { Width = 4 });
            else row.Children.Add(new Border { Width = 10, Child = Icons.Make("link", 10), ToolTip = L.T("蒙版已链接", "Layer and mask are linked") });
            var thumb = Thumbnail(mask.Asset, isMask: true, null);
            if (!mask.IsEnabled)
            {
                var cross = new Grid();
                cross.Children.Add(thumb);
                cross.Children.Add(new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse("M 3,3 L 31,31 M 31,3 L 3,31"), Stroke = Brushes.Red, StrokeThickness = 2, IsHitTestVisible = false,
                });
                _maskThumb = cross;
            }
            else _maskThumb = thumb;
            _maskThumb.ToolTip = L.T("单击：编辑蒙版 · Alt 单击：单独查看蒙版 · Shift 单击：停用/启用", "Click: edit the mask · Alt-click: view the mask alone · Shift-click: disable/enable");
            if (active && session.IsMaskSelected) Outline(_maskThumb);
            row.Children.Add(_maskThumb);
        }

        // Name.
        if (session.RenamingLayerId == layer.Id)
        {
            var box = new TextBox { Text = layer.Name, Width = 130, Margin = new Thickness(8, 0, 0, 0) };
            bool done = false;
            void Commit(bool keep)
            {
                if (done) return;
                done = true;
                if (keep) session.RenameLayer(layer.Id, box.Text);
                else session.RenamingLayerId = null;
                Window.GetWindow(panel)?.Focus();
            }
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { Commit(true); e.Handled = true; }
                else if (e.Key == Key.Escape) { Commit(false); e.Handled = true; }
            };
            box.LostKeyboardFocus += (_, _) => Commit(true);
            box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
            row.Children.Add(box);
        }
        else
        {
            var name = new TextBlock
            {
                Text = layer.Name,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = entry.Visible ? (Brush)Application.Current.FindResource("TextBrush") : (Brush)Application.Current.FindResource("TextDimBrush"),
                FontStyle = layer.MaskSourceId is not null ? FontStyles.Normal : FontStyles.Normal,
            };
            if (layer.Text is not null) name.Text = "T  " + layer.Name;
            row.Children.Add(name);
        }
        if (layer.Effects is { IsEmpty: false })
        {
            // fx: folds the effects list beneath the row away, or brings it back.
            bool folded = session.CollapsedEffectIds.Contains(layer.Id);
            var badge = new TextBlock
            {
                Text = folded ? "fx ▸" : "fx ▾",
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,
                ToolTip = folded ? L.T("显示效果", "Show effects") : L.T("折叠效果", "Collapse effects"),
                Foreground = (Brush)Application.Current.FindResource("TextDimBrush"),
            };
            badge.MouseLeftButtonDown += (_, e) => { session.ToggleEffectsExpansion(layer.Id); e.Handled = true; };
            row.Children.Add(badge);
        }
        Child = row;
        ContextMenu = null;
        MouseRightButtonUp += (_, e) =>
        {
            if (!session.SelectedLayerIds.Contains(layer.Id)) session.SelectLayer(layer.Id);
            var menu = panel.BuildContextMenu(layer);
            menu.PlacementTarget = this;
            menu.IsOpen = true;
            e.Handled = true;
        };
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2 && layer.IsAdjustment && Inside(_thumb, e))
            {
                // Double-clicking an adjustment layer's icon opens its settings, as in Photoshop.
                session.SelectLayer(layer.Id);
                session.EditAdjustmentLayer(layer.Id);
                e.Handled = true;
                return;
            }
            if (e.ClickCount == 2 && e.OriginalSource is TextBlock && session.RenamingLayerId != layer.Id)
            {
                session.SelectLayer(layer.Id);
                session.RenamingLayerId = layer.Id;
                e.Handled = true;
                return;
            }
            if (e.OriginalSource is DependencyObject d && FindAncestor<TextBox>(d) is not null) return;
            panel.BeginRowPress(this, e);
            e.Handled = true;
        };
    }

    private static T? FindAncestor<T>(DependencyObject node) where T : DependencyObject
    {
        for (var current = node; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is T match) return match;
        return null;
    }

    private bool Inside(FrameworkElement? element, MouseButtonEventArgs e)
    {
        if (element is null) return false;
        var p = e.GetPosition(element);
        return p.X >= 0 && p.Y >= 0 && p.X < element.ActualWidth && p.Y < element.ActualHeight;
    }

    /// <summary>A plain click (not a drag) on the row, handled once the button comes up.</summary>
    public void Click(MouseButtonEventArgs e)
    {
        var layer = Entry.Layer;
        var session = _session;
        var modifiers = Keyboard.Modifiers;
        bool ctrl = modifiers.HasFlag(ModifierKeys.Control), shift = modifiers.HasFlag(ModifierKeys.Shift), alt = modifiers.HasFlag(ModifierKeys.Alt);
        if (Inside(_maskThumb, e) && layer.Mask is not null)
        {
            if (alt) session.ToggleMaskAlone(layer.Id);
            else if (shift) session.ToggleLayerMask(layer.Id);
            else if (ctrl) session.LoadMaskSelection(layer.Id, white: true);
            else session.SelectLayerTarget(layer.Id, mask: true);
            return;
        }
        if (Inside(_thumb, e) && ctrl && layer.Asset is not null)
        {
            session.LoadLayerSelection(layer.Id, shift ? SelectionMode.Add : alt ? SelectionMode.Subtract : SelectionMode.Replace);
            return;
        }
        if (alt && !ctrl && !shift)
        {
            // Alt-click a layer: clip it to the one below, or release it, as Alt-clicking the line between them does in Photoshop.
            if (session.CanToggleClippingMask(layer.Id)) session.ToggleClippingMask(layer.Id);
            return;
        }
        if (ctrl)
        {
            var ids = session.SelectedLayerIds.Contains(layer.Id) ? session.SelectedLayerIds.Remove(layer.Id) : session.SelectedLayerIds.Add(layer.Id);
            if (ids.IsEmpty) return;
            session.SelectLayers(ids, ids.Contains(layer.Id) ? layer.Id : ids.First());
            return;
        }
        if (shift && session.ActiveLayerId is { } anchor && session.Document is { } document)
        {
            // A range, in the order the panel lists them.
            var order = document.Entries(topFirst: true, session.CollapsedGroupIds).Select(r => r.Layer.Id).ToList();
            int a = order.IndexOf(anchor), b = order.IndexOf(layer.Id);
            if (a >= 0 && b >= 0)
            {
                var range = order.Skip(Math.Min(a, b)).Take(Math.Abs(a - b) + 1);
                session.SelectLayers(range, layer.Id);
                return;
            }
        }
        session.SelectLayerTarget(layer.Id, mask: false);
    }

    private static void Outline(FrameworkElement element)
    {
        if (element is Border border)
        {
            border.BorderBrush = Brushes.White;
            border.BorderThickness = new Thickness(1.5);
        }
        else if (element is Grid grid && grid.Children.Count > 0 && grid.Children[0] is Border inner)
        {
            inner.BorderBrush = Brushes.White;
            inner.BorderThickness = new Thickness(1.5);
        }
    }

    private static Brush Checker()
    {
        if (_checker is not null) return _checker;
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(200, 200, 200)), null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(255, 255, 255)), null, new RectangleGeometry(new Rect(0, 0, 4, 4))));
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(255, 255, 255)), null, new RectangleGeometry(new Rect(4, 4, 4, 4))));
        var brush = new DrawingBrush(group) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 8, 8), ViewportUnits = BrushMappingMode.Absolute };
        brush.Freeze();
        return _checker = brush;
    }

    /// <summary>Photoshop's adjustment-layer badge: a circle half dark, half light, with the kind beneath it.</summary>
    private static Border AdjustmentThumbnail(Compositor.Core.Adjustments.AdjustmentKind kind)
    {
        var grid = new Grid();
        grid.Children.Add(new System.Windows.Shapes.Ellipse { Width = 18, Height = 18, Fill = Brushes.White, Stroke = new SolidColorBrush(Color.FromRgb(30, 30, 30)), StrokeThickness = 1 });
        grid.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 9,0 A 9,9 0 0 0 9,18 Z"),
            Fill = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
            Width = 18, Height = 18,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        });
        return new Border
        {
            Width = 34,
            Height = 34,
            Margin = new Thickness(0, 0, 4, 0),
            Background = new SolidColorBrush(Color.FromRgb(70, 70, 70)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Child = grid,
        };
    }

    private static Border Thumbnail(RasterImage? image, bool isMask, string? label)
    {
        var border = new Border
        {
            Width = 34,
            Height = 34,
            Background = isMask ? Brushes.Black : Checker(),
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            SnapsToDevicePixels = true,
        };
        if (image is not null)
        {
            border.Child = new Image
            {
                Source = Bitmaps.Thumbnail(image),
                Stretch = image.IsUniform && isMask ? Stretch.Fill : Stretch.Uniform,
                Margin = new Thickness(1),
            };
            RenderOptions.SetBitmapScalingMode(border.Child, BitmapScalingMode.HighQuality);
        }
        else if (label is not null)
        {
            border.Background = new SolidColorBrush(Color.FromRgb(60, 60, 60));
            border.Child = new TextBlock { Text = label, FontSize = 9, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        }
        return border;
    }
}
