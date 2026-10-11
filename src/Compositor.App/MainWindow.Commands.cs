using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Compositor.App.Services;
using Compositor.Core.Editing;
using Compositor.Core.IO;
using Compositor.Core.Localization;
using Compositor.Core.Model;
using Compositor.Core.Painting;

namespace Compositor.App;

public partial class MainWindow
{
    /// <summary>A menu command and its shortcut; the same table drives the menus and the keyboard.</summary>
    private sealed class AppCommand
    {
        public required Func<string> Header { get; init; }
        /// <summary>Its name in the menu, in the interface language.</summary>
        public required string StaticHeader { get; init; }
        /// <summary>
        /// Its Chinese name, which its shortcut id is made of whatever the interface language, so the shortcuts people
        /// have changed (saved by id) keep working when the language changes.
        /// </summary>
        public required string IdName { get; init; }
        public Key Key { get; set; } = Key.None;
        public ModifierKeys Modifiers { get; set; }
        /// <summary>A second shortcut (Ctrl+Y for Redo).</summary>
        public (Key Key, ModifierKeys Modifiers)? Alternate { get; set; }
        /// <summary>
        /// Its menu path in Chinese ("menu:文件 › 新建…"), the stable id its shortcut is changed by; set once the menus
        /// are built. Never translated: it is saved in the settings.
        /// </summary>
        public string Id { get; set; } = "";
        /// <summary>Its menu path as shown, in the interface language ("File ▸ New…"); set with <see cref="Id"/>.</summary>
        public string MenuPath { get; set; } = "";
        public Key DefaultKey { get; set; }
        public ModifierKeys DefaultModifiers { get; set; }
        public required Action Execute { get; init; }
        public Func<bool>? CanExecute { get; init; }
        public Func<bool>? IsChecked { get; init; }
        /// <summary>Shown in the menu only, handled elsewhere (single-key tool shortcuts, Delete).</summary>
        public string? GestureText { get; init; }

        public string Gesture => Key == Key.None ? GestureText ?? "" : FormatGesture(Key, Modifiers);
        public bool Enabled => CanExecute?.Invoke() ?? true;
    }

    private readonly List<AppCommand> _commands = new();

    private static string FormatGesture(Key key, ModifierKeys modifiers)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        parts.Add(key switch
        {
            Key.OemPlus => "+",
            Key.OemMinus => "-",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.Oem1 => ";",
            Key.OemQuotes => "'",
            Key.OemQuestion => "/",
            Key.Add => "Num+",
            Key.Subtract => "Num-",
            Key.Back => "Backspace",
            Key.Delete => "Delete",
            >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
            _ => key.ToString(),
        });
        return string.Join("+", parts);
    }

    /// <summary>
    /// A command named in both languages, as <see cref="L.T"/> takes them: the menu shows the interface language's
    /// name; the Chinese is also what its shortcut id is made of, so the id stays the same in every language.
    /// </summary>
    private AppCommand Command(string chinese, string english, Action execute, Key key = Key.None, ModifierKeys modifiers = ModifierKeys.None,
                               Func<bool>? canExecute = null, Func<bool>? isChecked = null, Func<string>? dynamicHeader = null,
                               (Key, ModifierKeys)? alternate = null, string? gesture = null) =>
        NamedCommand(chinese, L.T(chinese, english), execute, key, modifiers, canExecute, isChecked, dynamicHeader, alternate, gesture);

    /// <summary>
    /// A command shown as <paramref name="header"/> (a name that comes from elsewhere, such as an adjustment's display
    /// name), its shortcut id made of <paramref name="idName"/>, its fixed Chinese name.
    /// </summary>
    private AppCommand NamedCommand(string idName, string header, Action execute, Key key = Key.None, ModifierKeys modifiers = ModifierKeys.None,
                                    Func<bool>? canExecute = null, Func<bool>? isChecked = null, Func<string>? dynamicHeader = null,
                                    (Key, ModifierKeys)? alternate = null, string? gesture = null)
    {
        var command = new AppCommand
        {
            Header = dynamicHeader ?? (() => header), StaticHeader = header, IdName = idName, Key = key, Modifiers = modifiers, Execute = execute,
            CanExecute = canExecute, IsChecked = isChecked, Alternate = alternate, GestureText = gesture,
        };
        _commands.Add(command);
        return command;
    }

    /// <summary>Joins the menus of a shortcut id's path. Part of the id: never translated.</summary>
    private const string IdSeparator = " › ";

    /// <summary>Joins the menus of a path as shown ("文件 › 新建…", "File ▸ New…").</summary>
    private static string PathSeparator => L.T(" › ", " ▸ ");

    private AppCommand? _paletteCommand;

    /// <summary>Help ▸ Search Commands (Ctrl+F): every menu command and tool, by name.</summary>
    private void ShowCommandPalette()
    {
        if (OwnedWindows.OfType<Panels.CommandPalette>().Any()) return;
        var entries = new List<Panels.PaletteEntry>();
        void Walk(ItemsControl menu, string path)
        {
            foreach (var item in menu.Items.OfType<MenuItem>())
            {
                string header = item.Tag is AppCommand c ? c.Header() : item.Header?.ToString() ?? "";
                string title = path.Length == 0 ? header : path + PathSeparator + header;
                if (item.Tag is AppCommand command)
                {
                    if (ReferenceEquals(command, _paletteCommand)) continue;
                    entries.Add(new Panels.PaletteEntry(title, command.Gesture.Length > 0 ? command.Gesture : null, command.Enabled,
                        command.IsChecked?.Invoke() ?? false, () => { if (command.Enabled) { command.Execute(); AfterCommand(); } }));
                }
                else if (item.Items.Count > 0) Walk(item, title);
            }
        }
        Walk(MainMenu, "");
        foreach (var tool in RailTools)
        {
            var t = tool;
            entries.Add(new Panels.PaletteEntry(L.T("工具", "Tool") + PathSeparator + tool.Label(), null, true, _session.Tool == tool,
                () => { _session.SelectTool(t); Canvas.Focus(); }));
        }
        new Panels.CommandPalette(this, entries).Show();
    }

    private EditorSession S => _session;
    private bool HasDocument => _session?.Document is not null;

    private void BuildMenu()
    {
        _commands.Clear();
        MainMenu.Items.Clear();

        // File
        var file = TopMenu("文件", "File");
        Add(file, Command("新建…", "New…", NewCanvas, Key.N, ModifierKeys.Control));
        Add(file, Command("打开…", "Open…", OpenDialog, Key.O, ModifierKeys.Control));
        Add(file, Command("打开项目文件夹…", "Open Project Folder…", OpenProjectDialog, Key.O, ModifierKeys.Control | ModifierKeys.Alt));
        var recent = SubMenu("最近打开", "Open Recent");
        foreach (var path in Services.Settings.Current.RecentProjects)
        {
            var item = new MenuItem { Header = Path.GetFileNameWithoutExtension(path.TrimEnd('\\', '/')), ToolTip = path };
            var target = path;
            item.Click += (_, _) => OpenPaths(new[] { target });
            recent.Items.Add(item);
        }
        if (recent.Items.Count > 0) recent.Items.Add(new Separator());
        var clear = new MenuItem { Header = L.T("清除最近项目", "Clear Recent Projects"), IsEnabled = Services.Settings.Current.RecentProjects.Count > 0 };
        clear.Click += (_, _) => { Services.Settings.Current.RecentProjects.Clear(); Services.Settings.Current.Save(); BuildMenu(); };
        recent.Items.Add(clear);
        file.Items.Add(recent);
        Add(file, Command("导入图像…", "Import Images…", ImportImagesDialog));
        file.Items.Add(new Separator());
        Add(file, Command("关闭", "Close", () => CloseSession(S), Key.W, ModifierKeys.Control));
        Add(file, Command("存储", "Save", () => SaveSession(S, false), Key.S, ModifierKeys.Control, () => HasDocument));
        Add(file, Command("存储为…", "Save As…", () => SaveSession(S, true), Key.S, ModifierKeys.Control | ModifierKeys.Shift, () => HasDocument));
        file.Items.Add(new Separator());
        Add(file, Command("导出为…", "Export As…", () => ExportAs(), Key.W, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, () => HasDocument));
        Add(file, Command("导出 PNG…", "Export PNG…", () => Export(false), Key.E, ModifierKeys.Control | ModifierKeys.Shift, () => HasDocument));
        Add(file, Command("导出 JPEG…", "Export JPEG…", () => Export(true), Key.S, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, () => HasDocument));
        file.Items.Add(new Separator());
        Add(file, Command("退出", "Exit", Close, gesture: "Alt+F4"));

        // Edit
        var edit = TopMenu("编辑", "Edit");
        Add(edit, Command("撤销", "Undo", () => S.Undo(), Key.Z, ModifierKeys.Control, () => S.CanUndo || S.TransformEdit is not null || S.GradientEdit is not null,
            dynamicHeader: () => S.GradientEdit is not null ? L.T("取消渐变", "Cancel Gradient") : S.UndoTitle));
        Add(edit, Command("重做", "Redo", () => S.Redo(), Key.Z, ModifierKeys.Control | ModifierKeys.Shift, () => S.CanRedo, dynamicHeader: () => S.RedoTitle,
            alternate: (Key.Y, ModifierKeys.Control)));
        edit.Items.Add(new Separator());
        Add(edit, Command("剪切", "Cut", CutToClipboard, Key.X, ModifierKeys.Control, () => S.CanCopyPixels));
        Add(edit, Command("拷贝", "Copy", CopyToClipboard, Key.C, ModifierKeys.Control, () => S.CanCopyPixels || S.CanCopyLayers));
        Add(edit, Command("合并拷贝", "Copy Merged", () => CopyToClipboard(merged: true), Key.C, ModifierKeys.Control | ModifierKeys.Shift, () => HasDocument && S.Selection is { IsEmpty: false }));
        Add(edit, Command("粘贴", "Paste", PasteFromClipboard, Key.V, ModifierKeys.Control));
        edit.Items.Add(new Separator());
        Add(edit, Command("填充前景色", "Fill with Foreground Color", () => S.FillSelection(EditorSession.FillSource.Foreground), Key.Back, ModifierKeys.Alt, () => S.CanEditPixels));
        Add(edit, Command("填充背景色", "Fill with Background Color", () => S.FillSelection(EditorSession.FillSource.Background), Key.Back, ModifierKeys.Control, () => S.CanEditPixels));
        Add(edit, Command("内容识别填充…", "Content-Aware Fill…", ContentAwareFill, Key.Back, ModifierKeys.Shift, () => S.CanOpenContentFill));
        Add(edit, Command("创造式生成…", "Generative Fill…", GenerativeFill, canExecute: () => S.CanOpenContentFill));
        Add(edit, Command("清除", "Clear", () => S.ClearSelectedPixels(), canExecute: () => S.CanEditPixels && S.Selection is not null, gesture: "Delete"));
        edit.Items.Add(new Separator());
        Add(edit, Command("自由变换", "Free Transform", () => { S.SelectTool(Tool.Move); S.TransformCommand(); }, Key.T, ModifierKeys.Control,
            () => S.CanTransform || S.CanTransformSelection,
            dynamicHeader: () => S.CanTransformSelection ? L.T("变换选区内容", "Transform Selection") : L.T("自由变换", "Free Transform")));
        Add(edit, Command("扭曲", "Distort", () => { S.SelectTool(Tool.Move); S.DistortCommand(); }, canExecute: () => S.CanDistort && !S.IsDistorting));
        edit.Items.Add(new Separator());
        Add(edit, Command("键盘快捷键…", "Keyboard Shortcuts…", ShowShortcutEditor));
        Add(edit, Command("首选项…", "Preferences…", ShowPreferences, Key.K, ModifierKeys.Control));

        // Image
        var image = TopMenu("图像", "Image");
        image.Items.Add(AdjustmentsMenu());
        image.Items.Add(new Separator());
        Add(image, Command("画布大小…", "Canvas Size…", CanvasSizeDialog, Key.C, ModifierKeys.Control | ModifierKeys.Alt, () => HasDocument && S.CanEditLayers));
        Add(image, Command("图像大小…", "Image Size…", ImageSizeDialog, Key.I, ModifierKeys.Control | ModifierKeys.Alt, () => HasDocument && S.CanEditLayers));
        Add(image, Command("裁切…", "Trim…", TrimDialog, canExecute: () => S.CanTrim));
        Add(image, Command("裁剪到选区", "Crop to Selection", () => { if (S.Selection is { IsEmpty: false } sel) { S.SelectTool(Tool.Crop); S.CommitCrop(); S.SelectTool(Tool.Move); } },
            canExecute: () => HasDocument && S.Selection is { IsEmpty: false }));
        var rotation = SubMenu("图像旋转", "Image Rotation");
        Add(rotation, Command("顺时针 90 度", "90° Clockwise", () => S.RotateCanvas(true), canExecute: () => S.CanEditLayers));
        Add(rotation, Command("逆时针 90 度", "90° Counterclockwise", () => S.RotateCanvas(false), canExecute: () => S.CanEditLayers));
        rotation.Items.Add(new Separator());
        Add(rotation, Command("水平翻转画布", "Flip Canvas Horizontal", () => S.FlipCanvas(true), canExecute: () => S.CanEditLayers));
        Add(rotation, Command("垂直翻转画布", "Flip Canvas Vertical", () => S.FlipCanvas(false), canExecute: () => S.CanEditLayers));
        image.Items.Add(rotation);

        // Layer
        var layer = TopMenu("图层", "Layer");
        Add(layer, Command("新建图层", "New Layer", () => S.AddBlankLayer(), Key.N, ModifierKeys.Control | ModifierKeys.Shift, () => S.CanEditLayers));
        Add(layer, Command("新建组", "New Group", () => S.AddFolder(), canExecute: () => S.CanEditLayers));
        layer.Items.Add(NewAdjustmentLayerMenu());
        Add(layer, EditAdjustmentLayerCommand());
        layer.Items.Add(LayerStyleMenu());
        layer.Items.Add(new Separator());
        Add(layer, Command("复制图层", "Duplicate Layer", () => S.LayerViaCopy(), Key.J, ModifierKeys.Control, () => S.CanEditLayers && S.ActiveLayer is not null,
            dynamicHeader: () => S.Selection is null ? L.T("复制图层", "Duplicate Layer") : L.T("通过拷贝的图层", "Layer via Copy")));
        Add(layer, Command("通过剪切的图层", "Layer via Cut", () => S.LayerViaCopy(cut: true), Key.J, ModifierKeys.Control | ModifierKeys.Shift, () => S.CanCopyPixels && !S.IsMaskSelected));
        Add(layer, Command("删除图层", "Delete Layer", () => S.DeleteLayerOrMask(), canExecute: () => S.CanEditLayers && S.ActiveLayer is not null,
            dynamicHeader: () => S.IsMaskSelected && S.ActiveLayer?.Mask is not null ? L.T("删除图层蒙版", "Delete Layer Mask")
                : S.SelectedLayerIds.Count > 1 ? L.T("删除图层", "Delete Layers") : L.T("删除图层", "Delete Layer")));
        Add(layer, Command("重命名图层…", "Rename Layer…", () => S.RenamingLayerId = S.ActiveLayerId, canExecute: () => S.CanEditLayers && S.ActiveLayer is not null));
        Add(layer, Command("隐藏图层", "Hide Layer", () => { if (S.ActiveLayerId is { } id) S.ToggleLayerVisibility(id); }, Key.Oem2, ModifierKeys.Control,
            () => S.ActiveLayer is not null, dynamicHeader: () => S.ActiveLayer?.IsVisible == false ? L.T("显示图层", "Show Layer") : L.T("隐藏图层", "Hide Layer")));
        layer.Items.Add(new Separator());
        var mask = SubMenu("图层蒙版", "Layer Mask");
        Add(mask, Command("显示全部", "Reveal All", () => S.AddMask(true), canExecute: () => S.CanEditMask && S.ActiveLayer?.Mask is null,
            dynamicHeader: () => S.Selection is null ? L.T("显示全部", "Reveal All") : L.T("显示选区", "Reveal Selection")));
        Add(mask, Command("隐藏全部", "Hide All", () => S.AddMask(false), canExecute: () => S.CanEditMask && S.ActiveLayer?.Mask is null,
            dynamicHeader: () => S.Selection is null ? L.T("隐藏全部", "Hide All") : L.T("隐藏选区", "Hide Selection")));
        mask.Items.Add(new Separator());
        Add(mask, Command("删除", "Delete", () => S.DeleteLayerMask(), canExecute: () => S.CanEditMask && S.ActiveLayer?.Mask is not null));
        Add(mask, Command("应用", "Apply", () => S.ApplyLayerMask(), canExecute: () => S.CanEditMask && S.ActiveLayer is { Mask: not null, Asset: not null }));
        Add(mask, Command("停用", "Disable", () => S.ToggleLayerMask(), canExecute: () => S.CanEditMask && S.ActiveLayer?.Mask is not null,
            dynamicHeader: () => S.ActiveLayer?.Mask?.IsEnabled == false ? L.T("启用", "Enable") : L.T("停用", "Disable")));
        Add(mask, Command("取消链接", "Unlink", () => { if (S.ActiveLayerId is { } id) S.ToggleMaskLink(id); }, canExecute: () => S.CanEditMask && S.ActiveLayer?.Mask is not null,
            dynamicHeader: () => S.ActiveLayer?.Mask?.IsLinked == false ? L.T("链接", "Link") : L.T("取消链接", "Unlink")));
        layer.Items.Add(mask);
        Add(layer, Command("创建剪贴蒙版", "Create Clipping Mask", () => { if (S.ActiveLayerId is { } id) S.ToggleClippingMask(id); }, Key.G, ModifierKeys.Control | ModifierKeys.Alt,
            () => S.ActiveLayerId is { } id && S.CanToggleClippingMask(id),
            dynamicHeader: () => S.ActiveLayer?.MaskSourceId is null ? L.T("创建剪贴蒙版", "Create Clipping Mask") : L.T("释放剪贴蒙版", "Release Clipping Mask")));
        layer.Items.Add(new Separator());
        Add(layer, Command("图层编组", "Group Layers", () => S.GroupSelectedLayers(), Key.G, ModifierKeys.Control, () => S.CanEditLayers && !S.SelectedLayerIds.IsEmpty));
        Add(layer, Command("取消图层编组", "Ungroup Layers", () => S.UngroupLayers(), Key.G, ModifierKeys.Control | ModifierKeys.Shift, () => S.CanUngroupLayers));
        Add(layer, Command("移出组", "Move Out of Group", () => S.MoveActiveLayerOutOfGroup(), canExecute: () => S.CanEditLayers && S.ActiveLayer?.ParentId is not null));
        layer.Items.Add(new Separator());
        var arrange = SubMenu("排列", "Arrange");
        Add(arrange, Command("前移一层", "Bring Forward", () => S.MoveActiveLayer(1), Key.OemCloseBrackets, ModifierKeys.Control, () => S.CanMoveActiveLayer(1)));
        Add(arrange, Command("后移一层", "Send Backward", () => S.MoveActiveLayer(-1), Key.OemOpenBrackets, ModifierKeys.Control, () => S.CanMoveActiveLayer(-1)));
        layer.Items.Add(arrange);
        Add(layer, Command("向下合并", "Merge Down", () => S.MergeLayers(), Key.E, ModifierKeys.Control, () => S.CanMergeLayers, dynamicHeader: () => S.MergeTitle));
        layer.Items.Add(new Separator());
        Add(layer, Command("水平翻转图层", "Flip Layer Horizontal", () => S.FlipLayers(true), canExecute: () => S.CanTransform));
        Add(layer, Command("垂直翻转图层", "Flip Layer Vertical", () => S.FlipLayers(false), canExecute: () => S.CanTransform));

        // Select
        var select = TopMenu("选择", "Select");
        Add(select, Command("全部", "All", () => S.SelectAll(), Key.A, ModifierKeys.Control, () => HasDocument));
        Add(select, Command("取消选择", "Deselect", () => S.Deselect(), Key.D, ModifierKeys.Control, () => S.Selection is not null && S.CanEditSelection));
        Add(select, Command("反选", "Inverse", () => S.InvertSelection(), Key.I, ModifierKeys.Control | ModifierKeys.Shift, () => S.Selection is not null && S.CanEditSelection));
        Add(select, Command("色彩范围…", "Color Range…", () => S.BeginColorRange(), canExecute: () => S.CanSelectColorRange));
        Add(select, Command("主体", "Subject", SelectSubject, Key.A, ModifierKeys.Control | ModifierKeys.Alt, () => S.CanSelectSubject));
        select.Items.Add(new Separator());
        Add(select, Command("载入图层像素", "Layer’s Pixels", () => { if (S.ActiveLayerId is { } id) S.LoadLayerSelection(id); }, canExecute: () => S.ActiveLayer?.Asset is not null && S.CanEditSelection));
        Add(select, Command("蒙版的黑色区域", "Mask’s Black Areas", () => { if (S.ActiveLayerId is { } id) S.LoadMaskSelection(id); }, canExecute: () => S.ActiveLayer?.Mask is not null && S.CanEditSelection));
        select.Items.Add(new Separator());
        Add(select, Command("扩展…", "Expand…", () => SelectionAmountDialog(L.T("扩展选区", "Expand Selection"), 1, 500, S.ExpandSelection), canExecute: () => S.CanModifySelection));
        Add(select, Command("收缩…", "Contract…", () => SelectionAmountDialog(L.T("收缩选区", "Contract Selection"), 1, 500, S.ContractSelection), canExecute: () => S.CanModifySelection));
        Add(select, Command("羽化…", "Feather…", () => SelectionAmountDialog(L.T("羽化选区", "Feather Selection"), 2, 250, S.FeatherSelection), Key.F6, ModifierKeys.Shift, () => S.CanModifySelection));

        // Filter
        BuildFilterMenu(TopMenu("滤镜", "Filter"));

        // View
        var view = TopMenu("视图", "View");
        Add(view, Command("放大", "Zoom In", () => S.ZoomKeyboard(1), Key.OemPlus, ModifierKeys.Control, () => HasDocument, alternate: (Key.Add, ModifierKeys.Control)));
        Add(view, Command("缩小", "Zoom Out", () => S.ZoomKeyboard(-1), Key.OemMinus, ModifierKeys.Control, () => HasDocument, alternate: (Key.Subtract, ModifierKeys.Control)));
        Add(view, Command("按屏幕大小缩放", "Fit on Screen", () => S.Fit(), Key.D0, ModifierKeys.Control, () => HasDocument));
        Add(view, Command("100%", "100%", () => S.ZoomTo(1), Key.D1, ModifierKeys.Control, () => HasDocument));
        view.Items.Add(new Separator());
        Add(view, Command("全屏（仅画布）", "Full Screen (Canvas Only)", ToggleCanvasOnly, canExecute: () => HasDocument, isChecked: () => S.CanvasOnly, gesture: "F"));
        Add(view, Command("导航器（300% 及以上）", "Navigator (300% and above)", ToggleNavigator, isChecked: () => Services.Settings.Current.ShowsNavigator));
        Add(view, Command("像素网格（800% 以上）", "Pixel Grid (800% and above)", () => S.ShowsPixelGrid = !S.ShowsPixelGrid, isChecked: () => S.ShowsPixelGrid));
        Add(view, Command("显示变换控件", "Show Transform Controls", () => S.ShowsTransformControls = !S.ShowsTransformControls, Key.H, ModifierKeys.Control, isChecked: () => S.ShowsTransformControls));
        view.Items.Add(new Separator());
        var show = SubMenu("显示", "Show");
        Add(show, Command("网格", "Grid", () => S.ShowsGrid = !S.ShowsGrid, Key.OemQuotes, ModifierKeys.Control, isChecked: () => S.ShowsGrid));
        Add(show, Command("参考线", "Guides", () => S.ShowsGuides = !S.ShowsGuides, Key.Oem1, ModifierKeys.Control, isChecked: () => S.ShowsGuides));
        view.Items.Add(show);
        Add(view, Command("网格设置…", "Grid Settings…", GridSettingsDialog, canExecute: () => HasDocument));
        Add(view, Command("标尺", "Rulers", () => S.ShowsRulers = !S.ShowsRulers, Key.R, ModifierKeys.Control, isChecked: () => S.ShowsRulers));
        view.Items.Add(new Separator());
        Add(view, Command("对齐", "Snap", () => S.SnapEnabled = !S.SnapEnabled, Key.Oem1, ModifierKeys.Control | ModifierKeys.Shift, isChecked: () => S.SnapEnabled));
        var snapTo = SubMenu("对齐到", "Snap To");
        Add(snapTo, Command("参考线", "Guides", () => S.SnapToGuides = !S.SnapToGuides, isChecked: () => S.SnapToGuides));
        Add(snapTo, Command("网格", "Grid", () => S.SnapToGrid = !S.SnapToGrid, isChecked: () => S.SnapToGrid));
        Add(snapTo, Command("图层", "Layers", () => S.SnapToLayers = !S.SnapToLayers, isChecked: () => S.SnapToLayers));
        Add(snapTo, Command("文档边界", "Document Bounds", () => S.SnapToDocumentBounds = !S.SnapToDocumentBounds, isChecked: () => S.SnapToDocumentBounds));
        view.Items.Add(snapTo);
        view.Items.Add(new Separator());
        Add(view, Command("锁定参考线", "Lock Guides", () => S.LocksGuides = !S.LocksGuides, Key.Oem1, ModifierKeys.Control | ModifierKeys.Alt, isChecked: () => S.LocksGuides));
        Add(view, Command("清除参考线", "Clear Guides", () => S.ClearGuides(), canExecute: () => S.Document is { Guides.IsEmpty: false }));

        // Help
        var help = TopMenu("帮助", "Help");
        Add(help, _paletteCommand = Command("搜索命令…", "Search Commands…", ShowCommandPalette, Key.F, ModifierKeys.Control));
        Add(help, Command("键盘快捷键", "Keyboard Shortcuts", ShowShortcuts));
        Add(help, Command("关于 WinCompositor", "About WinCompositor", ShowAbout));
        ApplyShortcuts();
    }

    /// <summary>
    /// Gives each menu command its stable id (its menu path, in Chinese whatever the interface language) and its path as
    /// shown, and puts the person's shortcut on it.
    /// </summary>
    private void ApplyShortcuts()
    {
        void Walk(ItemsControl menu, string idPath, string path)
        {
            foreach (var item in menu.Items.OfType<MenuItem>())
            {
                string header = item.Tag is AppCommand c ? c.StaticHeader : item.Header?.ToString() ?? "";
                // A submenu's tag is its Chinese name (see SubMenu).
                string idName = item.Tag switch { AppCommand tagged => tagged.IdName, string chinese => chinese, _ => header };
                string fullId = idPath.Length == 0 ? idName : idPath + IdSeparator + idName;
                string full = path.Length == 0 ? header : path + PathSeparator + header;
                if (item.Tag is AppCommand command)
                {
                    command.Id = "menu:" + fullId;
                    command.MenuPath = full;
                    command.DefaultKey = command.Key;
                    command.DefaultModifiers = command.Modifiers;
                    if (Shortcuts.OverrideFor(command.Id) is { } chord)
                    {
                        command.Key = chord.Key;
                        command.Modifiers = chord.Modifiers;
                        command.Alternate = null;
                    }
                    item.InputGestureText = command.Gesture;
                }
                else if (item.Items.Count > 0) Walk(item, fullId, full);
            }
        }
        Walk(MainMenu, "", "");
    }

    private List<ShortcutDefinition> MenuShortcutDefinitions() => _commands.Where(c => c.Id.Length > 0)
        .Select(c => new ShortcutDefinition(c.Id, c.MenuPath, ShortcutGroup.Menus, new Chord(c.DefaultKey, c.DefaultModifiers))).ToList();

    /// <summary>Edit ▸ Keyboard Shortcuts: change any menu command's or canvas key's shortcut.</summary>
    private void ShowShortcutEditor()
    {
        var dialog = new Dialogs.ShortcutsDialog(MenuShortcutDefinitions(), Shortcuts.CanvasKeys) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        Shortcuts.Save(dialog.Values);
        BuildMenu();
    }

    private MenuItem TopMenu(string chinese, string english)
    {
        var menu = SubMenu(chinese, english);
        menu.SubmenuOpened += (_, e) => { if (ReferenceEquals(e.OriginalSource, menu)) RefreshMenu(menu); };
        MainMenu.Items.Add(menu);
        return menu;
    }

    /// <summary>
    /// A menu of commands, named in both languages. Its tag keeps the Chinese name, which the shortcut ids of the
    /// commands in it are made of (see <see cref="ApplyShortcuts"/>).
    /// </summary>
    private static MenuItem SubMenu(string chinese, string english) => new() { Header = L.T(chinese, english), Tag = chinese };

    private void Add(MenuItem parent, AppCommand command)
    {
        var item = new MenuItem { Header = command.StaticHeader, InputGestureText = command.Gesture, Tag = command };
        item.Click += (_, _) => { if (command.Enabled) { command.Execute(); AfterCommand(); } };
        if (parent.Items.Count == 0 && !ReferenceEquals(parent.Parent, MainMenu))
            parent.SubmenuOpened += (_, e) => { if (ReferenceEquals(e.OriginalSource, parent)) RefreshMenu(parent); };
        parent.Items.Add(item);
    }

    /// <summary>Menus show what the commands can do right now, read as they open.</summary>
    private static void RefreshMenu(MenuItem menu)
    {
        foreach (var child in menu.Items.OfType<MenuItem>())
        {
            if (child.Tag is AppCommand command)
            {
                child.Header = command.Header();
                child.IsEnabled = command.Enabled;
                child.IsChecked = command.IsChecked?.Invoke() ?? false;
            }
            else if (child.Items.Count > 0)
            {
                RefreshMenu(child);
                child.IsEnabled = child.Items.OfType<MenuItem>().Any(i => i.IsEnabled);
            }
        }
    }

    private void AfterCommand()
    {
        RebuildTabs();
        UpdateStatus();
        if (Keyboard.FocusedElement is not TextBox) Canvas.Focus();
    }

    // MARK: Keyboard

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        // A dialog of this window is open: its keys are its own. The adjustment panel isn't modal: the canvas still
        // zooms and pans, and Enter and Escape answer the panel.
        if (OwnedWindows.Cast<Window>().Any(w => w.IsVisible && w is not Panels.IEditPanel)) return;
        // Typing with the Type tool: every key is the text's.
        if (Canvas.IsTextInput(Keyboard.FocusedElement)) return;
        if (EditPanel is { } panel && Keyboard.FocusedElement is not TextBox && !MainMenu.IsKeyboardFocusWithin
            && Keyboard.FocusedElement is not MenuItem)
        {
            if (key == Key.Escape) { panel.Finish(commit: false); e.Handled = true; return; }
            if (key == Key.Enter) { panel.Finish(commit: true); e.Handled = true; return; }
        }
        if (key is Key.LeftAlt or Key.RightAlt)
        {
            // Alt is a modifier for tools here, as in Photoshop: it never opens the menu bar on its own.
            e.Handled = true;
            Canvas.UpdateCursor();
            return;
        }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift) { Canvas.UpdateCursor(); return; }
        if (Keyboard.FocusedElement is TextBox textBox)
        {
            if (key == Key.Escape) { Canvas.Focus(); e.Handled = true; }
            return;
        }
        if (Keyboard.FocusedElement is ComboBox { IsDropDownOpen: true }) return;
        if (key == Key.Space)
        {
            if (!S.CanvasOnly || true) Canvas.SpaceHeld = true;
            e.Handled = true;
            return;
        }
        foreach (var command in _commands)
        {
            bool matches = (command.Key == key && command.Modifiers == modifiers)
                           || (command.Alternate is { } alt && alt.Key == key && alt.Modifiers == modifiers);
            if (!matches) continue;
            if (command.Enabled) { command.Execute(); AfterCommand(); }
            e.Handled = true;
            return;
        }
        // A changed canvas key reaches the code written for its default; a default moved elsewhere does nothing.
        if (Shortcuts.TranslateCanvas(new Chord(key, modifiers)) is not { } translated) { e.Handled = true; return; }
        if (HandleToolKey(translated.Key, translated.Modifiers, e.IsRepeat)) e.Handled = true;
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Space) { Canvas.SpaceHeld = false; e.Handled = Keyboard.FocusedElement is not TextBox; }
        if (key is Key.LeftAlt or Key.RightAlt) e.Handled = true;
        Canvas.UpdateCursor();
    }

    /// <summary>Single keys and keys the menus don't list: tools, brush size, opacity digits, arrows, Enter and Escape.</summary>
    private bool HandleToolKey(Key key, ModifierKeys modifiers, bool repeat)
    {
        bool shift = modifiers == ModifierKeys.Shift;
        bool none = modifiers == ModifierKeys.None;
        bool hasDocument = HasDocument;

        switch (key)
        {
            case Key.Escape:
                if (S.TransformEdit is not null) S.CancelTransform();
                else if (S.GradientEdit is not null) S.CancelGradient();
                else if (S.ShapeDraft is not null) S.CancelShape();
                else if (S.TextDraft is not null) S.CommitText();
                else if (S.LassoDraft is not null) S.CancelLasso();
                else if (S.Tool == Tool.Crop && S.CropRect is not null && S.Document is { } doc && S.CropRect != doc.Bounds) S.CropRect = doc.Bounds;
                else if (S.CanvasOnly) ToggleCanvasOnly();
                else if (S.ViewsMaskAlone) S.ViewsMaskAlone = false;
                return true;
            case Key.Enter:
                if (S.TransformEdit is not null) S.CommitTransform();
                else if (S.GradientEdit is not null) S.CommitGradient();
                else if (S.LassoDraft is { Kind: LassoKind.Polygonal }) S.FinishLasso();
                else if (S.Tool == Tool.Crop) S.CommitCrop();
                else if (S.Tool == Tool.Type && S.ActiveLayer?.Text is not null) S.EditActiveText();
                else if (S.TextDraft is not null) S.CommitText();
                return true;
            case Key.Delete:
            case Key.Back:
                if (!none) return false;
                if (S.LassoDraft is { Kind: LassoKind.Polygonal }) { S.RemoveLastLassoPoint(); return true; }
                if (S.TransformEdit is not null) return true;
                if (S.Selection is not null) S.ClearSelectedPixels();
                else S.DeleteLayerOrMask();
                AfterCommand();
                return true;
            case Key.Tab when none || shift:
                CycleToolMode();
                return true;
            case Key.Left or Key.Right or Key.Up or Key.Down when none || shift:
            {
                if (!hasDocument) return false;
                double step = shift ? 10 : 1;
                double dx = key == Key.Left ? -step : key == Key.Right ? step : 0;
                double dy = key == Key.Up ? -step : key == Key.Down ? step : 0;
                if (S.Tool == Tool.Move) S.NudgeLayer(dx, dy);
                else if (S.Tool.IsSelectionTool() && S.Selection is not null) S.NudgeSelection(dx, dy);
                return true;
            }
            case Key.OemPlus when shift && S.Tool == Tool.Move:
                S.CycleBlendMode(true);
                return true;
            case Key.OemMinus when shift && S.Tool == Tool.Move:
                S.CycleBlendMode(false);
                return true;
        }

        if (key == Key.OemOpenBrackets || key == Key.OemCloseBrackets)
        {
            if (!S.Tool.IsBrushTool()) return false;
            if (shift) S.ChangeBrushHardness(key == Key.OemCloseBrackets);
            else if (none) S.ChangeBrushSize(key == Key.OemCloseBrackets);
            Canvas.InvalidateVisual();
            RefreshOptions();
            return true;
        }
        if (none && key >= Key.D0 && key <= Key.D9 && !repeat)
        {
            S.TypeOpacityDigit(key - Key.D0);
            RefreshOptions();
            LayersPanel.Refresh();
            return true;
        }
        if (none && key >= Key.NumPad0 && key <= Key.NumPad9 && !repeat)
        {
            S.TypeOpacityDigit(key - Key.NumPad0);
            RefreshOptions();
            LayersPanel.Refresh();
            return true;
        }
        if (repeat) return false;
        if (none)
        {
            switch (key)
            {
                case Key.V: S.SelectTool(Tool.Move); return true;
                case Key.M: S.SelectTool(Tool.Marquee); return true;
                case Key.L: S.SelectTool(Tool.Lasso); return true;
                case Key.W: S.SelectTool(Tool.Wand); return true;
                case Key.C: S.SelectTool(Tool.Crop); return true;
                case Key.B: S.BrushMode = BrushToolMode.Paint; S.SelectTool(Tool.Brush); return true;
                case Key.E: S.BrushMode = BrushToolMode.Erase; S.SelectTool(Tool.Brush); return true;
                case Key.G: S.SelectTool(Tool.Gradient); return true;
                case Key.J: S.SelectTool(Tool.SpotHealing); return true;
                case Key.S: S.SelectTool(Tool.CloneStamp); return true;
                case Key.R: S.SelectTool(Tool.Smear); return true;
                case Key.U: S.SelectTool(Tool.Shape); return true;
                case Key.T: S.SelectTool(Tool.Type); return true;
                case Key.I: S.SelectTool(Tool.Eyedropper); return true;
                case Key.H: S.SelectTool(Tool.Hand); return true;
                case Key.Z: S.SelectTool(Tool.Zoom); return true;
                case Key.A: S.SelectTool(Tool.Idle); return true;
                case Key.X: S.SwapColors(); return true;
                case Key.D: S.ResetColors(); return true;
                case Key.F: ToggleCanvasOnly(); return true;
            }
        }
        if (shift)
        {
            switch (key)
            {
                case Key.M: S.SelectTool(Tool.Marquee); S.MarqueeKind = S.MarqueeKind == LassoKind.Rectangle ? LassoKind.Ellipse : LassoKind.Rectangle; return true;
                case Key.L: S.SelectTool(Tool.Lasso); S.LassoKind = S.LassoKind == LassoKind.Freehand ? LassoKind.Polygonal : LassoKind.Freehand; return true;
                case Key.U: S.SelectTool(Tool.Shape); S.ToggleShapeKind(); RebuildOptions(); return true;
                case Key.J: S.SelectTool(Tool.Remove); return true;
            }
        }
        return false;
    }

    /// <summary>Tab steps the current tool through its own modes.</summary>
    private void CycleToolMode()
    {
        switch (S.Tool)
        {
            case Tool.Marquee: S.MarqueeKind = S.MarqueeKind == LassoKind.Rectangle ? LassoKind.Ellipse : LassoKind.Rectangle; break;
            case Tool.Lasso: S.LassoKind = S.LassoKind == LassoKind.Freehand ? LassoKind.Polygonal : LassoKind.Freehand; break;
            case Tool.Brush: S.BrushMode = S.BrushMode == BrushToolMode.Paint ? BrushToolMode.Erase : BrushToolMode.Paint; break;
            case Tool.Wand: S.WandSettings = S.WandSettings with { Contiguous = !S.WandSettings.Contiguous }; RefreshOptions(); break;
            case Tool.Shape: S.ToggleShapeKind(); RebuildOptions(); break;
            case Tool.Smear: S.SmearMode = (Compositor.Core.Painting.SmearMode)(((int)S.SmearMode + 1) % 3); RebuildOptions(); UpdateStatus(); break;
            case Tool.SpotHealing: S.SpotHealingMode = (Compositor.Core.Pixels.SpotHealingMode)(((int)S.SpotHealingMode + 1) % 3); RebuildOptions(); break;
            case Tool.Gradient:
                S.GradientSettings = S.GradientSettings with { Shape = S.GradientSettings.Shape == GradientShape.Linear ? GradientShape.Radial : GradientShape.Linear };
                RebuildOptions();
                break;
        }
    }

    // MARK: Clipboard

    private const string TokenFormat = "Compositor.ClipboardToken";

    private void CopyToClipboard() => CopyToClipboard(false);

    private void CopyToClipboard(bool merged)
    {
        if (!merged && S.Selection is null && S.CanCopyLayers)
        {
            var layers = S.CopyLayers();
            if (layers is null) return;
            var data = new DataObject();
            data.SetData(TokenFormat, layers.Token.ToString());
            TrySetClipboard(data);
            return;
        }
        var copied = S.CopyPixels(merged);
        if (copied is null) { System.Media.SystemSounds.Beep.Play(); return; }
        PutPixelsOnClipboard(copied);
    }

    private void CutToClipboard()
    {
        var copied = S.CutPixels();
        if (copied is null) { System.Media.SystemSounds.Beep.Play(); return; }
        PutPixelsOnClipboard(copied);
    }

    private void PutPixelsOnClipboard(SharedClipboard.Pixels copied)
    {
        var data = new DataObject();
        data.SetData(TokenFormat, copied.Token.ToString());
        // PNG keeps transparency for the apps that read it; the bitmap is for those that don't.
        var png = ImageExporter.EncodePng(copied.Image.Image, S.Document?.Resolution ?? 72);
        data.SetData("PNG", new MemoryStream(png));
        data.SetImage(Bitmaps.ToBitmapSource(copied.Image.Image));
        TrySetClipboard(data);
    }

    private void TrySetClipboard(DataObject data)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try { Clipboard.SetDataObject(data, true); return; }
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(40); }
        }
    }

    private void PasteFromClipboard()
    {
        IDataObject? data = null;
        try { data = Clipboard.GetDataObject(); } catch { /* clipboard busy */ }
        // Our own copy, when it is still what the clipboard holds: exact, layers included.
        if (data?.GetData(TokenFormat) is string token && Guid.TryParse(token, out var id))
        {
            if (SharedClipboard.CopiedLayers is { } layers && layers.Token == id && HasDocument) { S.PasteLayers(layers); AfterCommand(); return; }
            if (SharedClipboard.CopiedPixels is { } pixels && pixels.Token == id) { S.PastePixels(pixels.Image, pixels.Source); AfterPaste(); return; }
        }
        if (data is null) return;
        try
        {
            if (data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream stream && ImageImporter.DecodeBytes(stream.ToArray(), L.T("粘贴", "Pasted")) is { } png)
            {
                S.PastePixels(png);
                AfterPaste();
                return;
            }
            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files)
            {
                OpenPaths(files.Where(f => ImageImporter.IsImageFile(f) || Core.IO.Psd.PsdReader.IsPsdPath(f)));
                return;
            }
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is BitmapSource source && Bitmaps.FromBitmapSource(source) is { } bitmap)
            {
                using (bitmap)
                {
                    // Some apps put an alpha of zero on every pixel of a bitmap; that means opaque.
                    var span = bitmap.GetPixelSpan();
                    bool clear = true;
                    for (int i = 3; i < span.Length; i += 4) if (span[i] != 0) { clear = false; break; }
                    if (clear) for (int i = 3; i < span.Length; i += 4) span[i] = 255;
                    S.PastePixels(RasterImage.FromBitmap(bitmap, false, L.T("粘贴", "Pasted")));
                }
                AfterPaste();
            }
        }
        catch (Exception error)
        {
            Dialogs.MessageDialog.Show(this, error.Message, L.T("无法粘贴", "Couldn’t paste"), Dialogs.MessageKind.Warning);
        }
    }

    private void AfterPaste()
    {
        RefreshAll();
        RebuildTabs();
        Canvas.Focus();
    }

    // MARK: Help

    private void TrimDialog()
    {
        var dialog = new Dialogs.TrimDialog { Owner = this };
        if (dialog.ShowDialog() == true) S.Trim(dialog.Options);
    }

    private void ToggleNavigator()
    {
        Services.Settings.Current.ShowsNavigator = !Services.Settings.Current.ShowsNavigator;
        Services.Settings.Current.Save();
        Navigator.Update();
    }

    /// <summary>Help ▸ Keyboard Shortcuts: every shortcut in effect, the canvas keys first, then the menus' by menu.</summary>
    private void ShowShortcuts()
    {
        var sections = new List<Dialogs.ShortcutSection>();
        // The canvas keys: the tools (listed first, up to No Tool — the A key, by its id, whatever its title's
        // language), then the brush and canvas keys after them.
        var canvas = Shortcuts.CanvasKeys.Select(d => new Dialogs.ShortcutEntry(d.Title, Shortcuts.Effective(d).Label)).ToList();
        int toolsEnd = Shortcuts.CanvasKeys.ToList().FindIndex(d => d.Id == NoToolKeyId) + 1;
        if (toolsEnd <= 0) toolsEnd = canvas.Count;
        sections.Add(new Dialogs.ShortcutSection(L.T("工具", "Tools"), canvas.Take(toolsEnd).ToList()));
        if (toolsEnd < canvas.Count) sections.Add(new Dialogs.ShortcutSection(L.T("画布与画笔", "Canvas & Brushes"), canvas.Skip(toolsEnd).ToList()));
        // The menu commands, grouped by their top menu (their id is "menu:" and the path), in menu order; each group
        // titled with the top menu's name as shown.
        foreach (var menu in _commands.Where(c => c.Key != Key.None).GroupBy(c => TopMenu(c.Id.StartsWith("menu:", StringComparison.Ordinal) ? c.Id[5..] : c.Id, IdSeparator)))
            sections.Add(new Dialogs.ShortcutSection(TopMenu(menu.First().MenuPath, PathSeparator) ?? L.T("其他", "Other"),
                menu.Select(c => new Dialogs.ShortcutEntry(c.Header(),
                    c.Alternate is { } alternate ? $"{c.Gesture} / {FormatGesture(alternate.Key, alternate.Modifiers)}" : c.Gesture)).ToList()));
        var notes = new[]
        {
            L.T("1–0 设置图层不透明度，快速输入两位数字可设精确值", "1–0 set the layer’s opacity; type two digits quickly for an exact value."),
            L.T("方向键微移，按住 Shift 每次 10 像素", "Arrow keys nudge; hold Shift to move 10 pixels at a time."),
            L.T("按住空格拖动平移画布", "Hold Space and drag to pan the canvas."),
        };
        new Dialogs.ShortcutsReferenceWindow(this, sections, notes, ShowShortcutEditor).ShowDialog();

        static string? TopMenu(string path, string separator)
        {
            int end = path.IndexOf(separator, StringComparison.Ordinal);
            return end > 0 ? path[..end] : null;
        }
    }

    /// <summary>The id of the No Tool canvas key (A), the last of the tool keys. Ids are fixed: they are saved in the settings.</summary>
    private const string NoToolKeyId = "canvas:A:0";

    private void ShowAbout() => new Dialogs.AboutWindow(this).ShowDialog();
}
