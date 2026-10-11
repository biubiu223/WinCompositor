using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Compositor.App.Dialogs;
using Compositor.App.Drawing;
using Compositor.App.Panels;
using Compositor.App.Services;
using Compositor.Core.Editing;
using Compositor.Core.Geometry;
using Compositor.Core.IO;
using Compositor.Core.Localization;
using Compositor.Core.Model;
using Compositor.Core.Painting;

namespace Compositor.App;

public partial class MainWindow : Window
{
    private readonly List<EditorSession> _sessions = new();
    private EditorSession _session = null!;
    private readonly Dictionary<Tool, RadioButton> _toolButtons = new();
    private Ruler? _horizontalRuler, _verticalRuler;
    private ColorSwatches? _swatches;
    private WindowState _stateBeforeCanvasOnly;
    private WindowStyle _styleBeforeCanvasOnly;

    public EditorSession Session => _session;

    public MainWindow()
    {
        InitializeComponent();
        // Single-key tool shortcuts must reach the canvas, not an input method's composition (Pinyin and the like).
        InputMethod.SetIsInputMethodEnabled(this, false);
        InputMethod.SetIsInputMethodEnabled(Canvas, false);
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        var settings = Services.Settings.Current;
        Width = settings.WindowWidth;
        Height = settings.WindowHeight;
        if (settings.WindowMaximized) WindowState = WindowState.Maximized;
        PanelColumn.Width = new GridLength(Math.Clamp(settings.LayersPanelWidth, 200, 520));

        NewButton.Content = Icons.Make("plus", 16);
        NewButton.ToolTip = L.T("新建画布 (Ctrl+N)", "New Canvas (Ctrl+N)");
        NewButton.Click += (_, _) => NewCanvas();
        BuildZoomButtons();
        BuildToolRail();
        BuildMenu();
        _horizontalRuler = new Ruler(GuideAxis.Horizontal, Canvas);
        _verticalRuler = new Ruler(GuideAxis.Vertical, Canvas);
        HorizontalRulerHost.Content = _horizontalRuler;
        VerticalRulerHost.Content = _verticalRuler;
        Canvas.ViewChanged += (_, _) => UpdateStatus();
        Canvas.TextInputHost = TextInputHost;

        PreviewKeyDown += OnPreviewKeyDown;
        Activated += (_, _) => { if (_session is not null) ResumeExternalChangeCheck(_session); };
        PreviewKeyUp += OnPreviewKeyUp;
        Deactivated += (_, _) => Canvas.SpaceHeld = false;
        DragEnter += OnDragOver;
        DragOver += OnDragOver;
        DragLeave += (_, _) => DropHighlight.Visibility = Visibility.Collapsed;
        Drop += OnDrop;
        Closing += OnClosing;
        // Every AI model lets go of its memory and graphics memory as the window goes.
        Closed += (_, _) => Services.ModelIdle.ReleaseAll();
        Loaded += (_, _) => Canvas.Focus();

        AddSession(NewSession(), select: true);
    }

    // MARK: Sessions and tabs

    private EditorSession NewSession()
    {
        var s = Services.Settings.Current;
        var session = new EditorSession
        {
            ShowsRulers = s.ShowsRulers,
            ShowsGrid = s.ShowsGrid,
            ShowsGuides = s.ShowsGuides,
            ShowsPixelGrid = s.ShowsPixelGrid,
            SnapEnabled = s.SnapEnabled,
            SnapToGuides = s.SnapToGuides,
            SnapToGrid = s.SnapToGrid,
            SnapToLayers = s.SnapToLayers,
            SnapToDocumentBounds = s.SnapToDocumentBounds,
            GridSpacing = s.GridSpacing,
            GridSubdivisions = s.GridSubdivisions,
            AutoSelect = s.AutoSelect,
            ShowsTransformControls = s.ShowsTransformControls,
            SubjectModelSource = SubjectModelOrOffer,
            InstalledSubjectModel = Services.SubjectModels.Current,
            InpaintModelSource = Services.InpaintModels.Current,
        };
        if (_session is { } current)
        {
            // Tool and colors carry over to a new tab, as they do in one window.
            session.Tool = current.Tool;
            session.ForegroundColor = current.ForegroundColor;
            session.BackgroundColor = current.BackgroundColor;
            session.BrushSettings = current.BrushSettings;
            session.BrushMode = current.BrushMode;
        }
        return session;
    }

    private void AddSession(EditorSession session, bool select)
    {
        _sessions.Add(session);
        if (select) SelectSession(session);
        RebuildTabs();
    }

    private void SelectSession(EditorSession session)
    {
        if (ReferenceEquals(_session, session)) return;
        if (_session is { } old)
        {
            // An adjustment panel belongs to its tab's document: switching tabs cancels it.
            old.CancelAdjustmentEdit();
            old.CancelEffectEdit();
            old.CancelColorRange();
            old.CommitTransform();
            old.PropertyChanged -= OnSessionProperty;
            old.DocumentChanged -= OnDocumentChanged;
            old.Message -= OnSessionMessage;
            old.History.Changed -= OnHistoryChanged;
        }
        _session = session;
        session.PropertyChanged += OnSessionProperty;
        session.DocumentChanged += OnDocumentChanged;
        session.Message += OnSessionMessage;
        session.History.Changed += OnHistoryChanged;
        Canvas.Session = session;
        Navigator.Session = session;
        LayersPanel.Session = session;
        _swatches?.Attach(session);
        RefreshAll();
        RebuildTabs();
        ResumeExternalChangeCheck(session);
    }

    /// <summary>The session to put a newly opened file in: the current one when it's still empty, else a new tab.</summary>
    private EditorSession SessionForNewDocument()
    {
        if (_session.Document is null) return _session;
        var session = NewSession();
        AddSession(session, select: true);
        return session;
    }

    private void RebuildTabs()
    {
        TabStrip.Children.Clear();
        foreach (var session in _sessions)
        {
            bool active = ReferenceEquals(session, _session);
            var title = new TextBlock
            {
                Text = (session.Document is null ? L.T("新标签页", "New Tab") : session.Title) + (session.IsModified ? " •" : ""),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = active ? (Brush)FindResource("TextBrush") : (Brush)FindResource("TextDimBrush"),
                MaxWidth = 180,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var close = new Button { Style = (Style)FindResource("FlatButton"), Content = Icons.Make("close", 10), Padding = new Thickness(3), Margin = new Thickness(6, 0, 0, 0), ToolTip = L.T("关闭 (Ctrl+W)", "Close (Ctrl+W)") };
            var target = session;
            close.Click += (_, _) => CloseSession(target);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(title);
            row.Children.Add(close);
            var tab = new Border
            {
                Child = row,
                Padding = new Thickness(12, 4, 6, 4),
                Margin = new Thickness(0, 0, 4, 0),
                CornerRadius = new CornerRadius(6),
                Background = active ? (Brush)FindResource("ControlHoverBrush") : Brushes.Transparent,
                BorderBrush = active ? (Brush)FindResource("BorderBrush") : Brushes.Transparent,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Arrow,
                ToolTip = session.ProjectPath,
            };
            tab.MouseLeftButtonDown += (_, _) => SelectSession(target);
            tab.MouseUp += (_, e) => { if (e.ChangedButton == MouseButton.Middle) CloseSession(target); };
            TabStrip.Children.Add(tab);
        }
        Title = _session.Document is null ? "WinCompositor" : $"{_session.Title}{(_session.IsModified ? " •" : "")} — WinCompositor";
    }

    private void CloseSession(EditorSession session)
    {
        session.CancelAdjustmentEdit();
        session.CancelEffectEdit();
        session.CancelColorRange();
        if (!ConfirmDiscard(session)) return;
        StopWatchingProject(session);
        int index = _sessions.IndexOf(session);
        _sessions.Remove(session);
        if (_sessions.Count == 0) _sessions.Add(NewSession());
        if (ReferenceEquals(session, _session))
        {
            var next = _sessions[Math.Clamp(index, 0, _sessions.Count - 1)];
            _session = null!;
            SelectSession(next);
        }
        session.ClearProject();
        RebuildTabs();
    }

    private void OnSessionMessage(object? sender, SessionMessage message)
    {
        MessageDialog.Show(this, message.Text, message.Title, message.IsError ? MessageKind.Warning : MessageKind.Information);
    }

    private void OnHistoryChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(RebuildTabs);

    private void OnDocumentChanged(object? sender, EventArgs e)
    {
        if (_session.Document is null != WelcomeHost.Content is WelcomeView) UpdateWelcome();
        UpdateStatus();
        if (_session.Tool is Tool.Move or Tool.Crop) RefreshOptions();
    }

    private void OnSessionProperty(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(EditorSession.SubjectBusy):
            case nameof(EditorSession.RemoveBusy):
                // A model is looking at the picture: a wait cursor until it's done.
                Mouse.OverrideCursor = _session.SubjectBusy || _session.RemoveBusy ? Cursors.AppStarting : null;
                // A finished removal leaves several copies of the layer behind: give the memory back once idle.
                if (!_session.RemoveBusy && !_session.SubjectBusy)
                    Dispatcher.BeginInvoke(new Action(Services.MemoryRelease.Compact), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                break;
            case nameof(EditorSession.WandMode):
                RebuildOptions();
                UpdateStatus();
                break;
            case nameof(EditorSession.Tool):
                UpdateToolRail();
                RebuildOptions();
                UpdateStatus();
                break;
            case nameof(EditorSession.BrushMode):
            case nameof(EditorSession.MarqueeKind):
            case nameof(EditorSession.LassoKind):
            case nameof(EditorSession.ShapeKind):
                UpdateToolRail();
                RebuildOptions();
                UpdateStatus();
                break;
            case nameof(EditorSession.TransformEdit):
            case nameof(EditorSession.CropRect):
            case nameof(EditorSession.ActiveLayerId):
                if (_session.Tool is Tool.Move or Tool.Crop) RefreshOptions();
                if (_session.IsDistorting != _hintDistorting) { _hintDistorting = _session.IsDistorting; StatusHint.Text = ToolHint(); }
                break;
            case nameof(EditorSession.ShowsRulers):
            case nameof(EditorSession.CanvasOnly):
                UpdateChrome();
                break;
            case nameof(EditorSession.IsMaskSelected):
            case nameof(EditorSession.ViewsMaskAlone):
                UpdateMaskBadge();
                break;
            case nameof(EditorSession.ProjectPath):
                RebuildTabs();
                break;
            case nameof(EditorSession.AdjustmentEdit):
                // Layer edits are off while a panel is open, on again once it closes.
                if (_session.AdjustmentEdit is not null) ShowAdjustmentPanelSoon();
                RefreshOptions();
                LayersPanel.Refresh();
                break;
            case nameof(EditorSession.ColorRange):
                if (_session.ColorRange is not null) ShowColorRangePanelSoon();
                RefreshOptions();
                LayersPanel.Refresh();
                break;
            case nameof(EditorSession.EffectEdit):
                if (_session.EffectEdit is not null) ShowEffectPanelSoon();
                RefreshOptions();
                LayersPanel.Refresh();
                break;
            case nameof(EditorSession.CollapsedEffectIds):
                LayersPanel.Refresh();
                break;
            case nameof(EditorSession.CloneSource):
            case nameof(EditorSession.SmearMode):
                if (_session.Tool is Tool.CloneStamp or Tool.Smear) { RebuildOptions(); UpdateStatus(); }
                break;
            case nameof(EditorSession.GradientEdit):
            case nameof(EditorSession.GradientSettings):
            case nameof(EditorSession.TextDraft):
            case nameof(EditorSession.CurrentTextStyle):
            case nameof(EditorSession.ForegroundColor):
            case nameof(EditorSession.BackgroundColor):
                if (_session.Tool is Tool.Gradient or Tool.Type or Tool.Shape) RefreshOptions();
                break;
        }
    }

    private void RefreshAll()
    {
        UpdateToolRail();
        RebuildOptions();
        UpdateChrome();
        UpdateWelcome();
        UpdateStatus();
        UpdateMaskBadge();
        Canvas.UpdateCursor();
    }

    // MARK: Chrome

    private void UpdateChrome()
    {
        bool only = _session.CanvasOnly;
        var visibility = only ? Visibility.Collapsed : Visibility.Visible;
        MainMenu.Visibility = visibility;
        TopBar.Visibility = visibility;
        OptionsBar.Visibility = visibility;
        StatusBar.Visibility = visibility;
        ToolColumn.Width = new GridLength(only ? 0 : 56);
        ToolRail.Visibility = visibility;
        PanelSplitter.Visibility = visibility;
        LayersPanel.Visibility = visibility;
        SplitterColumn.Width = new GridLength(only ? 0 : 5);
        if (only) PanelColumn.MinWidth = 0;
        PanelColumn.Width = only ? new GridLength(0) : new GridLength(Math.Clamp(Services.Settings.Current.LayersPanelWidth, 200, 520));
        if (!only) PanelColumn.MinWidth = 200;
        bool rulers = _session.ShowsRulers && _session.Document is not null && !only;
        RulerRow.Height = new GridLength(rulers ? Ruler.Thickness : 0);
        RulerColumn.Width = new GridLength(rulers ? Ruler.Thickness : 0);
        _horizontalRuler?.InvalidateVisual();
        _verticalRuler?.InvalidateVisual();
    }

    /// <summary>F: the canvas alone on black over the whole screen; F or Esc brings everything back.</summary>
    private void ToggleCanvasOnly()
    {
        if (_session.Document is null && !_session.CanvasOnly) return;
        if (!_session.CanvasOnly)
        {
            Services.Settings.Current.LayersPanelWidth = PanelColumn.ActualWidth;
            _stateBeforeCanvasOnly = WindowState;
            _styleBeforeCanvasOnly = WindowStyle;
            WindowStyle = WindowStyle.None;
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
            _session.CanvasOnly = true;
        }
        else
        {
            _session.CanvasOnly = false;
            WindowStyle = _styleBeforeCanvasOnly;
            WindowState = _stateBeforeCanvasOnly;
        }
        Canvas.Focus();
    }

    private void UpdateWelcome()
    {
        if (_session.Document is null)
        {
            if (WelcomeHost.Content is not WelcomeView)
                WelcomeHost.Content = new WelcomeView(
                    create: (w, h, res, bg) => { _session.CreateDocument(w, h, res, bg); RebuildTabs(); Canvas.Focus(); },
                    open: OpenProjectDialog,
                    openRecent: path => OpenPaths(new[] { path }));
            WelcomeHost.Visibility = Visibility.Visible;
        }
        else
        {
            WelcomeHost.Content = null;
            WelcomeHost.Visibility = Visibility.Collapsed;
        }
        UpdateChrome();
    }

    private void UpdateMaskBadge()
    {
        if (_session.MaskAloneLayer is { } layer)
        {
            MaskBadgeText.Text = L.T($"正在单独显示“{layer.Name}”的蒙版 · 按住 Alt 单击蒙版缩略图返回",
                $"Viewing the mask of “{layer.Name}” alone · Alt-click the mask thumbnail to go back");
            MaskBadge.Visibility = Visibility.Visible;
        }
        else MaskBadge.Visibility = Visibility.Collapsed;
        Canvas.InvalidateVisual();
    }

    private void UpdateStatus()
    {
        if (_session.Document is { } document)
        {
            StatusZoom.Text = $"{_session.Viewport.Zoom * 100:0.#}%";
            StatusSize.Text = L.T($"{document.Width} × {document.Height} 像素 · {document.Resolution:0.##} ppi",
                $"{document.Width} × {document.Height} pixels · {document.Resolution:0.##} ppi");
            StatusColor.Text = L.T("sRGB · 8 位", "sRGB · 8-bit");
        }
        else
        {
            StatusZoom.Text = "";
            StatusSize.Text = L.T("随时可以开始", "Ready when you are");
            StatusColor.Text = "";
        }
        StatusHint.Text = ToolHint();
        _horizontalRuler?.InvalidateVisual();
        _verticalRuler?.InvalidateVisual();
    }

    private bool _hintDistorting;

    private string ToolHint() => _session.Tool switch
    {
        Tool.Move when _session.IsDistorting => L.T("拖动角点或边扭曲 · 在框内拖动移动 · Shift 约束方向 · 回车应用 · Esc 取消",
            "Drag a corner or edge to distort · Drag inside to move · Shift constrains the direction · Enter applies · Esc cancels."),
        Tool.Move => L.T("拖动以移动 · 手柄缩放 · 圆点旋转 · Ctrl 拖动手柄扭曲 · Ctrl 单击选择图层 · Alt 拖动复制 · 1–0 不透明度 · 空格平移",
            "Drag to move · Handles scale · The dot rotates · Ctrl-drag a handle to distort · Ctrl-click selects a layer · Alt-drag duplicates · 1–0 opacity · Space pans."),
        Tool.Marquee => (_session.MarqueeKind == LassoKind.Ellipse ? L.T("拖出椭圆", "Drag out an ellipse") : L.T("拖出矩形", "Drag out a rectangle"))
                        + L.T(" · Shift 添加 · Alt 减去 · 拖动中按 Shift 约束比例 · 在选区内拖动可移动选区 · Ctrl 拖动移动像素 · Delete 清除 · Ctrl+D 取消选择",
                              " · Shift adds · Alt subtracts · Shift while dragging constrains proportions · Drag inside the selection to move it · Ctrl-drag moves pixels · Delete clears · Ctrl+D deselects."),
        Tool.Lasso => _session.LassoKind == LassoKind.Freehand
            ? L.T("拖动绘制选区 · Shift 添加 · Alt 减去 · Alt+Backspace 填充 · Ctrl+D 取消选择",
                "Drag to draw a selection · Shift adds · Alt subtracts · Alt+Backspace fills · Ctrl+D deselects.")
            : L.T("单击放置角点 · 单击起点、双击或回车闭合 · Backspace 删除角点 · Esc 取消",
                "Click to place points · Click the start point, double-click or press Enter to close · Backspace removes a point · Esc cancels."),
        Tool.Wand when _session.WandMode == WandMode.Object => L.T("单击对象即可选中它（AI 识别）· Shift 添加 · Alt 减去 · 在选区内拖动可移动",
            "Click an object to select it (AI) · Shift adds · Alt subtracts · Drag inside the selection to move it."),
        Tool.Wand => L.T("单击选择相近颜色 · Shift 添加 · Alt 减去 · 在选区内拖动可移动 · Ctrl 拖动移动像素",
            "Click to select similar colors · Shift adds · Alt subtracts · Drag inside the selection to move it · Ctrl-drag moves pixels."),
        Tool.Crop => L.T("拖动裁剪 · 回车应用 · Esc 取消 · 空格平移", "Drag to crop · Enter applies · Esc cancels · Space pans."),
        Tool.Brush => (_session.BrushMode == BrushToolMode.Erase ? L.T("拖动擦除", "Drag to erase") : L.T("拖动绘画", "Drag to paint"))
                      + L.T(" · [ ] 大小 · Shift+[ ] 硬度 · 1–0 不透明度 · Shift 单击画直线 · Alt 吸取颜色 · 空格平移",
                            " · [ ] size · Shift+[ ] hardness · 1–0 opacity · Shift-click draws a straight line · Alt picks a color · Space pans."),
        Tool.SpotHealing => L.T("涂抹要去除的瑕疵，松开后自动用周围的内容修复 · [ ] 大小",
            "Paint over a blemish; when you release, it’s repaired from its surroundings · [ ] size."),
        Tool.Remove => L.T("涂抹或圈住要去掉的东西，松开鼠标即自动抹除（纹理合成 + AI 神经重建）· [ ] 大小 · 空格平移",
            "Paint over or circle what to remove; it’s removed when you release (Texture Synthesis + Neural Reconstruction) · [ ] size · Space pans."),
        Tool.CloneStamp => _session.CloneSource is null
            ? L.T("先按住 Alt 单击要仿制的地方，然后在别处涂抹", "Alt-click where to clone from first, then paint elsewhere.")
            : L.T("涂抹以仿制 · Alt 单击更换仿制源 · 十字线显示正在仿制的位置", "Paint to clone · Alt-click to change the source · The crosshair shows where it’s copying from."),
        Tool.Smear => _session.SmearMode switch
        {
            Compositor.Core.Painting.SmearMode.Liquify => L.T("拖动推移像素 · [ ] 大小 · 强度控制推移量", "Drag to push pixels · [ ] size · Strength sets how far they move."),
            Compositor.Core.Painting.SmearMode.Blur => L.T("涂抹以模糊 · 重复涂抹会更模糊", "Paint to blur · Paint again to blur more."),
            _ => L.T("拖动涂抹颜色 · 强度控制拖带多少", "Drag to smudge colors · Strength sets how much color is carried."),
        },
        Tool.Gradient => _session.GradientEdit is null
            ? L.T("拖动画出渐变 · Shift 约束到 45° · 渐变在应用前可拖动两端调整",
                "Drag to draw a gradient · Shift constrains to 45° · Drag its ends to adjust it before it’s applied.")
            : L.T("拖动端点调整 · 回车应用 · Esc 取消 · 切换工具或图层时自动应用",
                "Drag the ends to adjust · Enter applies · Esc cancels · Switching tools or layers applies it."),
        Tool.Shape => L.T("拖动绘制形状（前景色）· Shift 正方形/圆形/45° 直线 · Alt 从中心 · Shift+U 切换形状",
            "Drag to draw a shape (foreground color) · Shift for a square, circle or 45° line · Alt draws from the center · Shift+U switches shapes."),
        Tool.Type => _session.TextDraft is null
            ? L.T("单击输入点文字 · 拖动创建段落文字框 · 单击文字图层可再次编辑",
                "Click to type point text · Drag to create a paragraph text box · Click a type layer to edit it again.")
            : L.T("输入文字 · Esc 或 Ctrl+回车应用 · 在框外单击也会应用 · 选中部分文字后可单独改颜色和字体",
                "Type your text · Esc or Ctrl+Enter applies · Clicking outside the box also applies · Select part of the text to change its color and font."),
        Tool.Hand => L.T("拖动平移 · Ctrl+滚轮缩放", "Drag to pan · Ctrl+wheel zooms."),
        Tool.Zoom => L.T("单击放大 · Alt 单击缩小 · 左右拖动连续缩放", "Click to zoom in · Alt-click to zoom out · Drag left or right to zoom continuously."),
        _ => L.T("未选择工具 · 按工具快捷键选择一个", "No tool selected · Press a tool’s shortcut to choose one."),
    };

    // MARK: Top bar and tool rail

    private void BuildZoomButtons()
    {
        Button Text(string text, string tip, Action action)
        {
            var b = new Button { Style = (Style)FindResource("FlatButton"), Content = new TextBlock { Text = text }, ToolTip = tip, Margin = new Thickness(2, 0, 2, 0), Padding = new Thickness(8, 4, 8, 4) };
            b.Click += (_, _) => { action(); Canvas.Focus(); };
            return b;
        }
        Button Icon(string icon, string tip, Action action)
        {
            var b = new Button { Style = (Style)FindResource("FlatButton"), Content = Icons.Make(icon, 16), ToolTip = tip, Margin = new Thickness(2, 0, 2, 0), Padding = new Thickness(5) };
            b.Click += (_, _) => { action(); Canvas.Focus(); };
            return b;
        }
        ZoomButtons.Children.Add(Text(L.T("适合", "Fit"), L.T("按屏幕大小缩放 (Ctrl+0)", "Fit on Screen (Ctrl+0)"), () => _session.Fit()));
        ZoomButtons.Children.Add(Text("100%", L.T("实际像素 (Ctrl+1)", "Actual Pixels (Ctrl+1)"), () => _session.ZoomTo(1)));
        ZoomButtons.Children.Add(Icon("zoomIn", L.T("放大 (Ctrl++)", "Zoom In (Ctrl++)"), () => _session.ZoomKeyboard(1)));
        ZoomButtons.Children.Add(Icon("zoomOut", L.T("缩小 (Ctrl+-)", "Zoom Out (Ctrl+-)"), () => _session.ZoomKeyboard(-1)));
    }

    private static readonly Tool[] RailTools =
    {
        Tool.Move, Tool.Marquee, Tool.Lasso, Tool.Wand, Tool.Crop, Tool.Brush, Tool.SpotHealing, Tool.Remove, Tool.CloneStamp, Tool.Smear,
        Tool.Gradient, Tool.Shape, Tool.Type, Tool.Eyedropper, Tool.Hand, Tool.Zoom,
    };

    private void BuildToolRail()
    {
        foreach (var tool in RailTools)
        {
            var button = new RadioButton { Style = (Style)FindResource("ToolButton"), ToolTip = tool.Label(), Margin = new Thickness(0, 0, 0, 6) };
            var t = tool;
            button.Click += (_, _) => { _session.SelectTool(t); Canvas.Focus(); };
            _toolButtons[tool] = button;
            ToolStack.Children.Add(button);
        }
        _swatches = new ColorSwatches(this) { Margin = new Thickness(0, 14, 0, 0) };
        ToolStack.Children.Add(_swatches);
    }

    private void UpdateToolRail()
    {
        foreach (var (tool, button) in _toolButtons)
        {
            string icon = tool switch
            {
                Tool.Move => "move",
                Tool.Marquee => _session.MarqueeKind == LassoKind.Ellipse ? "marqueeEllipse" : "marquee",
                Tool.Lasso => _session.LassoKind == LassoKind.Polygonal ? "polygonalLasso" : "lasso",
                Tool.Wand => "wand",
                Tool.Crop => "crop",
                Tool.Brush => _session.BrushMode == BrushToolMode.Erase ? "eraser" : "brush",
                Tool.SpotHealing => "heal",
                Tool.Remove => "remove",
                Tool.CloneStamp => "stamp",
                Tool.Smear => "smear",
                Tool.Gradient => "gradient",
                Tool.Shape => _session.ShapeKind switch { ShapeKind.Ellipse => "shapeEllipse", ShapeKind.Line => "shapeLine", _ => "shapeRect" },
                Tool.Type => "type",
                Tool.Eyedropper => "eyedropper",
                Tool.Hand => "hand",
                _ => "zoom",
            };
            button.Content = Icons.Make(icon, 19, dashed: tool == Tool.Marquee);
            button.IsChecked = _session.Tool == tool;
        }
    }

    private void RebuildOptions() => OptionsHost.Content = OptionsBarBuilder.Build(this, _session);
    internal void RebuildOptionsNow() => RebuildOptions();
    private void RefreshOptions() { if (OptionsHost.Content is IRefreshable r) r.Refresh(); else RebuildOptions(); }

    // MARK: Drag and drop

    private void OnDragOver(object sender, DragEventArgs e)
    {
        bool files = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = files ? DragDropEffects.Copy : DragDropEffects.None;
        DropHighlight.Visibility = files ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        DropHighlight.Visibility = Visibility.Collapsed;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        var position = e.GetPosition(Canvas);
        PointD? at = null;
        if (_session.Document is not null && position.X >= 0 && position.Y >= 0 && position.X <= Canvas.ActualWidth && position.Y <= Canvas.ActualHeight)
            at = _session.ToDocument(new PointD(position.X, position.Y));
        OpenPaths(paths, at);
    }

    // MARK: Closing

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        foreach (var session in _sessions.ToList())
        {
            if (!session.IsModified || session.Document is null) continue;
            SelectSession(session);
            if (!ConfirmDiscard(session)) { e.Cancel = true; return; }
        }
        var settings = Services.Settings.Current;
        settings.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal) { settings.WindowWidth = Width; settings.WindowHeight = Height; }
        if (!_session.CanvasOnly && PanelColumn.ActualWidth > 0) settings.LayersPanelWidth = PanelColumn.ActualWidth;
        settings.ShowsRulers = _session.ShowsRulers;
        settings.ShowsGrid = _session.ShowsGrid;
        settings.ShowsGuides = _session.ShowsGuides;
        settings.ShowsPixelGrid = _session.ShowsPixelGrid;
        settings.SnapEnabled = _session.SnapEnabled;
        settings.SnapToGuides = _session.SnapToGuides;
        settings.SnapToGrid = _session.SnapToGrid;
        settings.SnapToLayers = _session.SnapToLayers;
        settings.SnapToDocumentBounds = _session.SnapToDocumentBounds;
        settings.GridSpacing = _session.GridSpacing;
        settings.GridSubdivisions = _session.GridSubdivisions;
        settings.AutoSelect = _session.AutoSelect;
        settings.ShowsTransformControls = _session.ShowsTransformControls;
        settings.Save();
    }

    /// <summary>Asks whether to save a modified project before it goes. False when the person cancels.</summary>
    private bool ConfirmDiscard(EditorSession session)
    {
        if (!session.IsModified || session.Document is null) return true;
        // Photoshop's and the Mac's layout: Don't Save apart at the left so it isn't hit by mistake, Save the default.
        var answer = MessageDialog.Ask(this, L.T($"要存储对“{session.Title}”所做的更改吗？", $"Save changes to “{session.Title}”?"),
            L.T("不存储的话，更改将会丢失。", "Your changes will be lost if you don’t save them."), MessageKind.Question,
            new MessageButton(L.T("不存储", "Don’t Save"), MessageBoxResult.No) { Leading = true },
            new MessageButton(L.T("取消", "Cancel"), MessageBoxResult.Cancel) { IsCancel = true },
            new MessageButton(L.T("存储", "Save"), MessageBoxResult.Yes) { IsDefault = true });
        if (answer == MessageBoxResult.Yes) return SaveSession(session, saveAs: false);
        // Only an explicit Don't Save lets the work go; anything else keeps it open.
        return answer == MessageBoxResult.No;
    }
}

/// <summary>An options bar that can refresh its values without being rebuilt.</summary>
public interface IRefreshable
{
    void Refresh();
}
