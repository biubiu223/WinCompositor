using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Compositor.App.Services;
using Compositor.Core.Localization;

namespace Compositor.App.Dialogs;

/// <summary>
/// Help ▸ About Compositor, in the editor's own dark look rather than the white system message box: the icon, name and
/// version on a header band; what the app is, where it comes from and what it's built with; then the AI models it ships
/// with, what each one is for and its license, and a way to the license files beside the program.
/// </summary>
public sealed class AboutWindow : Window
{
    // Wide enough that the longest model row (Stable Diffusion's name beside its license) stays on one line and the
    // paragraphs wrap at a comfortable length; the height follows the content.
    private const double ContentWidth = 480;
    private const double Gutter = 24;
    private const double IconSize = 64;

    private static readonly (string Name, string Use, string License)[] BuiltInModels =
    {
        ("IS-Net general-use", L.T("选择主体、对象选择、移除背景", "Select Subject, Object Selection, Remove Background"), "Apache-2.0"),
        ("LaMa", L.T("神经重建、抹除工具", "Neural Reconstruction, Remove Tool"), "Apache-2.0"),
        ("MI-GAN", L.T("修补模型后备", "Fallback inpainting model"), "MIT"),
        ("Stable Diffusion 1.5 Inpainting", L.T("AI 生成", "AI Generation"), "CreativeML Open RAIL-M"),
    };

    // Where the build and the published program put the models and their license files.
    private static readonly string ModelsFolder = Path.Combine(AppContext.BaseDirectory, "Models");

    private readonly Image _icon = new() { Width = IconSize, Height = IconSize, VerticalAlignment = VerticalAlignment.Center };
    private readonly BitmapFrame[] _iconFrames = LoadIconFrames();
    private readonly Button _openFolder = new() { Content = L.T("打开许可证文件夹", "Open License Folder"), Padding = new Thickness(14, 3, 14, 3), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _folderNote = Dim("");

    public AboutWindow(Window owner)
    {
        Owner = owner;
        Title = L.T("关于 WinCompositor", "About WinCompositor");
        SetResourceReference(StyleProperty, "DarkWindow");
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        // On a short screen at a high scale the window stops at the work area and the middle scrolls, rather than
        // running off the bottom with the Close button.
        MaxHeight = SystemParameters.WorkArea.Height;

        var close = new Button { Content = L.T("关闭", "Close"), IsDefault = true, MinWidth = 84 };
        close.Click += (_, _) => Close();

        var middle = new ScrollViewer
        {
            Content = Body(),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        };
        var header = Header();
        var footer = Footer(close);
        var root = new DockPanel { Width = ContentWidth };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(footer);
        root.Children.Add(middle);
        Content = root;

        ShowIcon(VisualTreeHelper.GetDpi(this).DpiScaleX);
        SourceInitialized += (_, _) =>
        {
            DarkTitleBar.Apply(this);
            // Now the monitor is known: the icon frame for its scale, before the first frame is drawn.
            ShowIcon(VisualTreeHelper.GetDpi(this).DpiScaleX);
        };
        DpiChanged += (_, e) => ShowIcon(e.NewDpi.DpiScaleX);
        // Close is both the default and the focused button, so Enter closes; Escape closes too, shown modally or not.
        Loaded += (_, _) => close.Focus();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        };
    }

    // MARK: Building

    /// <summary>The icon beside the name, subtitle and version, on a band a shade lighter than the window.</summary>
    private Border Header()
    {
        RenderOptions.SetBitmapScalingMode(_icon, BitmapScalingMode.HighQuality);
        AutomationProperties.SetName(_icon, "WinCompositor");

        var name = new TextBlock { Text = "WinCompositor", FontSize = 22, FontWeight = FontWeights.SemiBold };
        // The window draws text in Display mode, tuned for small sizes; a title this large keeps its shapes better in Ideal.
        TextOptions.SetTextFormattingMode(name, TextFormattingMode.Ideal);
        // The name says where it comes from: Compositor, for Windows.
        var subtitle = Dim(L.T("Compositor 的 Windows 版", "Compositor for Windows"), 13);
        subtitle.Margin = new Thickness(1, 1, 0, 0);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 0, 0) };
        text.Children.Add(name);
        text.Children.Add(subtitle);
        if (ShortVersion() is { Length: > 0 } version)
        {
            var badge = new Border
            {
                Child = Dim(L.T("版本 ", "Version ") + version, 11),
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(7, 1, 7, 2),
                Margin = new Thickness(1, 9, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            badge.SetResourceReference(Border.BackgroundProperty, "ControlBrush");
            badge.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            text.Children.Add(badge);
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_icon);
        row.Children.Add(text);

        // A barely-there lift from the panel shades the editor already uses: it sets the identity apart from the
        // details without bringing in a color of its own (the accent stays for the Close button).
        var shade = new LinearGradientBrush((Color)FindResource("PanelAltColor"), (Color)FindResource("PanelColor"), new Point(0, 0), new Point(0, 1));
        shade.Freeze();
        var band = new Border { Child = row, Background = shade, Padding = new Thickness(Gutter, 22, Gutter, 22), BorderThickness = new Thickness(0, 0, 0, 1) };
        band.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        return band;
    }

    /// <summary>What it is, where it comes from, what it's built with; the models and their licenses.</summary>
    private StackPanel Body()
    {
        var body = new StackPanel { Margin = new Thickness(Gutter, 18, Gutter, 20) };
        body.Children.Add(new TextBlock
        {
            Text = L.T("面向合成与后期处理的类 Photoshop 图像编辑器。", "A Photoshop-style image editor for compositing and retouching."),
            FontSize = 13, TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(Facts(
            (L.T("移植自", "Ported from"), L.T("Robbie Tilton 的 Compositor（macOS，MIT 许可证）", "Compositor by Robbie Tilton (macOS, MIT License)")),
            (L.T("项目文件", "Project files"), L.T(".comp 格式，与 Mac 版通用", ".comp format, shared with the Mac version")),
            (L.T("技术", "Built with"), ".NET 8 · WPF · SkiaSharp · Windows ML")));
        body.Children.Add(SectionTitle(L.T("内置 AI 模型", "Built-in AI Models"), L.T("均在本机离线运行", "All run offline on this computer")));
        body.Children.Add(ModelTable());
        body.Children.Add(FolderRow());
        return body;
    }

    /// <summary>Label and value pairs, the labels in one column so the values line up.</summary>
    private static Grid Facts(params (string Label, string Value)[] facts)
    {
        var grid = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < facts.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            double top = i == 0 ? 0 : 6;
            var label = Dim(facts[i].Label);
            label.Margin = new Thickness(0, top, 18, 0);
            var value = new TextBlock { Text = facts[i].Value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0) };
            Grid.SetRow(label, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            grid.Children.Add(label);
            grid.Children.Add(value);
        }
        return grid;
    }

    private static DockPanel SectionTitle(string title, string note)
    {
        var panel = new DockPanel { Margin = new Thickness(0, 22, 0, 8) };
        var hint = Dim(note);
        hint.VerticalAlignment = VerticalAlignment.Bottom;
        DockPanel.SetDock(hint, Dock.Right);
        panel.Children.Add(hint);
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Bottom });
        return panel;
    }

    /// <summary>
    /// One row per model: the name over what it's for, the license on the right edge. Two lines a row keep the long
    /// names and licenses whole at this width, where three columns would have to break them.
    /// </summary>
    private static Border ModelTable()
    {
        var rows = new StackPanel();
        for (int i = 0; i < BuiltInModels.Length; i++)
        {
            var (name, use, license) = BuiltInModels[i];
            if (i > 0)
            {
                var rule = new Border { Height = 1 };
                rule.SetResourceReference(Border.BackgroundProperty, "BorderBrush");
                rows.Children.Add(rule);
            }
            var licenseText = Dim(license);
            licenseText.Margin = new Thickness(16, 0, 0, 0);
            licenseText.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(licenseText, Dock.Right);
            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock { Text = name, TextWrapping = TextWrapping.Wrap });
            var useText = Dim(use);
            useText.Margin = new Thickness(0, 2, 0, 0);
            info.Children.Add(useText);
            var row = new DockPanel { Margin = new Thickness(14, 8, 14, 9) };
            row.Children.Add(licenseText);
            row.Children.Add(info);
            rows.Children.Add(row);
        }
        var table = new Border { Child = rows, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6) };
        table.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        table.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        return table;
    }

    /// <summary>Where the model and license files are, and a button to open the folder.</summary>
    private DockPanel FolderRow()
    {
        // A disabled button shows no tooltip unless asked to; this one explains why it's disabled.
        ToolTipService.SetShowOnDisabled(_openFolder, true);
        _openFolder.Click += (_, _) => OpenModelsFolder();
        _folderNote.Margin = new Thickness(0, 0, 16, 0);
        ShowFolderState();
        var row = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(_openFolder, Dock.Right);
        row.Children.Add(_openFolder);
        row.Children.Add(_folderNote);
        return row;
    }

    private static DockPanel Footer(Button close)
    {
        var credit = Dim(L.T("MIT 许可证 · 原版 © 2026 Wonder Assembly LLC", "MIT License · Original © 2026 Wonder Assembly LLC"), 11);
        credit.Margin = new Thickness(0, 0, 16, 0);
        DockPanel.SetDock(close, Dock.Right);
        var line = new DockPanel { Margin = new Thickness(Gutter, 14, Gutter, 16) };
        line.Children.Add(close);
        line.Children.Add(credit);
        var divider = new Border { Height = 1 };
        divider.SetResourceReference(Border.BackgroundProperty, "BorderBrush");
        DockPanel.SetDock(divider, Dock.Top);
        var footer = new DockPanel();
        footer.Children.Add(divider);
        footer.Children.Add(line);
        return footer;
    }

    private static TextBlock Dim(string text, double size = 12)
    {
        var block = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextDimBrush");
        return block;
    }

    // MARK: Models folder

    private void ShowFolderState()
    {
        bool exists = Directory.Exists(ModelsFolder);
        _openFolder.IsEnabled = exists;
        _openFolder.ToolTip = exists ? ModelsFolder : L.T("找不到 Models 文件夹，它应在：\n", "The Models folder can’t be found. It should be at:\n") + ModelsFolder;
        _folderNote.Text = exists
            ? L.T("模型与许可证文件在程序旁的 Models 文件夹。", "The models and their licenses are in the Models folder beside the program.")
            : L.T("程序旁没有 Models 文件夹。", "There’s no Models folder beside the program.");
    }

    private void OpenModelsFolder()
    {
        // It may have gone since the window opened.
        if (!Directory.Exists(ModelsFolder))
        {
            ShowFolderState();
            return;
        }
        try
        {
            // Quoted, so a program folder with spaces in its path (Program Files) opens as one path.
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + ModelsFolder + "\"") { UseShellExecute = true });
        }
        catch (Exception error)
        {
            _folderNote.Text = L.T("无法打开文件夹：", "Couldn’t open the folder: ") + error.Message;
        }
    }

    // MARK: Icon and version

    /// <summary>The icon's frames, smallest first. The assembly-qualified address finds it whichever assembly started the process.</summary>
    private static BitmapFrame[] LoadIconFrames()
    {
        try
        {
            var assembly = typeof(AboutWindow).Assembly.GetName().Name;
            var resource = Application.GetResourceStream(new Uri($"pack://application:,,,/{assembly};component/Assets/Compositor.ico"));
            if (resource is null) return Array.Empty<BitmapFrame>();
            var memory = new MemoryStream();
            using (resource.Stream) resource.Stream.CopyTo(memory);
            memory.Position = 0;
            var decoder = BitmapDecoder.Create(memory, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            return decoder.Frames.Where(f => f.PixelWidth > 0).OrderBy(f => f.PixelWidth).ToArray();
        }
        catch
        {
            // No icon is no reason to fail the About box.
            return Array.Empty<BitmapFrame>();
        }
    }

    /// <summary>
    /// The smallest frame that covers the icon's size in device pixels, so it's drawn 1:1 or scaled down, never up:
    /// 64 px at 100%, 128 px at 200%.
    /// </summary>
    private void ShowIcon(double scale)
    {
        if (_iconFrames.Length == 0) return;
        double needed = IconSize * Math.Max(1, scale);
        _icon.Source = _iconFrames.FirstOrDefault(f => f.PixelWidth >= needed - 0.5) ?? _iconFrames[^1];
    }

    /// <summary>The program's version as people say it: 0.1, or 0.1.2 once there's a third part.</summary>
    /// <summary>
    /// The version as people read it: "0.1 Beta 1" for 0.1.0-beta.1 (from the informational version, which keeps the
    /// pre-release part the numeric assembly version drops), "0.1.2" for 0.1.2.
    /// </summary>
    private static string ShortVersion()
    {
        var assembly = typeof(AboutWindow).Assembly;
        var full = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                   ?? assembly.GetName().Version?.ToString() ?? "";
        full = full.Split('+')[0];
        var parts = full.Split('-', 2);
        if (!Version.TryParse(parts[0], out var version)) return full;
        var number = version.Build > 0 ? $"{version.Major}.{version.Minor}.{version.Build}" : $"{version.Major}.{version.Minor}";
        if (parts.Length < 2) return number;
        // "beta.1" → "Beta 1", "rc.2" → "RC 2".
        var label = parts[1].Split('.', 2);
        var stage = label[0].Length <= 2 ? label[0].ToUpperInvariant() : char.ToUpperInvariant(label[0][0]) + label[0][1..];
        return label.Length > 1 ? $"{number} {stage} {label[1]}" : $"{number} {stage}";
    }
}
