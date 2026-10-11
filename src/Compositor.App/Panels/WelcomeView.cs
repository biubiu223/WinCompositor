using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Compositor.App.Dialogs;
using Compositor.App.Services;
using Compositor.Core.Localization;
using Compositor.Core.Model;

namespace Compositor.App.Panels;

/// <summary>What an empty tab shows: New Canvas, Open, and recent projects. Mirrors the welcome NewCanvasSheet.</summary>
public sealed class WelcomeView : Grid
{
    public WelcomeView(Action<int, int, double, PaletteColor?> create, Action open, Action<string> openRecent)
    {
        Background = Brushes.Transparent;
        var form = new NewCanvasForm();
        var card = new StackPanel { Width = 380 };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 18) };
        var icon = new Image { Width = 40, Height = 40, Margin = new Thickness(0, 0, 12, 0) };
        try { icon.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/Compositor.ico")); } catch { }
        header.Children.Add(icon);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = L.T("新建画布", "New Canvas"), FontSize = 20, FontWeight = FontWeights.SemiBold });
        titles.Children.Add(new TextBlock { Text = L.T("也可以把图像或 .comp 项目拖到这里", "Or drop an image or a .comp project here"), Foreground = (Brush)FindResource("TextDimBrush") });
        header.Children.Add(titles);
        card.Children.Add(header);
        card.Children.Add(form);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var openButton = new Button { Content = L.T("打开项目…", "Open Project…"), MinWidth = 96 };
        openButton.Click += (_, _) => open();
        var createButton = new Button { Content = L.T("创建", "Create"), IsDefault = true, MinWidth = 96, Margin = new Thickness(8, 0, 0, 0) };
        createButton.Click += (_, _) =>
        {
            if (form.Read(out var error) is { } v) create(v.Width, v.Height, v.Resolution, v.Background);
            else MessageDialog.Show(Window.GetWindow(this), error, L.T("新建画布", "New Canvas"), MessageKind.Warning);
        };
        buttons.Children.Add(openButton);
        buttons.Children.Add(createButton);
        card.Children.Add(buttons);

        var recent = Settings.Current.RecentProjects.Where(Directory.Exists).Take(6).ToList();
        if (recent.Count > 0)
        {
            card.Children.Add(new TextBlock { Text = L.T("最近的项目", "Recent Projects"), Margin = new Thickness(0, 24, 0, 6), Foreground = (Brush)FindResource("TextDimBrush") });
            foreach (var path in recent)
            {
                var name = new TextBlock { Text = Path.GetFileNameWithoutExtension(path.TrimEnd('\\', '/')), TextTrimming = TextTrimming.CharacterEllipsis };
                var where = new TextBlock { Text = Path.GetDirectoryName(path), FontSize = 10, Foreground = (Brush)FindResource("TextDimBrush"), TextTrimming = TextTrimming.CharacterEllipsis };
                var stack = new StackPanel();
                stack.Children.Add(name);
                stack.Children.Add(where);
                var item = new Button { Style = (Style)FindResource("FlatButton"), Content = stack, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 4, 8, 4), Focusable = false };
                item.HorizontalAlignment = HorizontalAlignment.Stretch;
                var target = path;
                item.Click += (_, _) => openRecent(target);
                card.Children.Add(item);
            }
        }

        var border = new Border
        {
            Child = card,
            Background = (Brush)FindResource("PanelBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(28, 24, 28, 24),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Children.Add(border);
    }
}

/// <summary>Foreground and background colors at the foot of the tool rail. Mirrors ColorPaletteControls.swift.</summary>
public sealed class ColorSwatches : Grid
{
    private readonly MainWindow _window;
    private readonly Border _foreground, _background;
    private Core.Editing.EditorSession? _session;

    public ColorSwatches(MainWindow window)
    {
        _window = window;
        Width = 44;
        Height = 50;
        _background = Swatch(18, 18);
        _foreground = Swatch(0, 0);
        Children.Add(_background);
        Children.Add(_foreground);
        _foreground.MouseLeftButtonUp += (_, _) => _window.PickColor(true);
        _background.MouseLeftButtonUp += (_, _) => _window.PickColor(false);
        _foreground.ToolTip = L.T("前景色（单击选取）", "Foreground Color (click to choose)");
        _background.ToolTip = L.T("背景色（单击选取）", "Background Color (click to choose)");

        var swap = new Button { Style = (Style)FindResource("FlatButton"), Content = Icons.Make("swap", 12), Padding = new Thickness(1), ToolTip = L.T("交换颜色 (X)", "Switch Foreground and Background Colors (X)"),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -2, -4, 0) };
        swap.Click += (_, _) => _session?.SwapColors();
        var reset = new Button { Style = (Style)FindResource("FlatButton"), Padding = new Thickness(1), ToolTip = L.T("默认颜色 (D)", "Default Foreground and Background Colors (D)"),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(-2, 0, 0, -4) };
        var mini = new Grid { Width = 12, Height = 12 };
        mini.Children.Add(new Border { Width = 8, Height = 8, Background = Brushes.White, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(0.5), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom });
        mini.Children.Add(new Border { Width = 8, Height = 8, Background = Brushes.Black, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(0.5), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top });
        reset.Content = mini;
        reset.Click += (_, _) => _session?.ResetColors();
        Children.Add(swap);
        Children.Add(reset);
    }

    private static Border Swatch(double left, double top) => new()
    {
        Width = 26,
        Height = 26,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(left + 2, top + 2, 0, 0),
        BorderBrush = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
        BorderThickness = new Thickness(1.5),
        CornerRadius = new CornerRadius(4),
        Cursor = Cursors.Hand,
    };

    public void Attach(Core.Editing.EditorSession session)
    {
        if (_session is not null) _session.PropertyChanged -= OnChanged;
        _session = session;
        session.PropertyChanged += OnChanged;
        Update();
    }

    private void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Core.Editing.EditorSession.ForegroundColor) or nameof(Core.Editing.EditorSession.BackgroundColor) or nameof(Core.Editing.EditorSession.IsMaskSelected))
            Update();
    }

    private void Update()
    {
        if (_session is null) return;
        var fg = _session.ForegroundColor;
        var bg = _session.BackgroundColor;
        if (_session.IsMaskSelected)
        {
            // On a mask, colors paint as their gray, so that's what the swatches show.
            fg = new PaletteColor(fg.Luminance, fg.Luminance, fg.Luminance);
            bg = new PaletteColor(bg.Luminance, bg.Luminance, bg.Luminance);
        }
        _foreground.Background = Bitmaps.Brush(fg);
        _background.Background = Bitmaps.Brush(bg);
    }
}
