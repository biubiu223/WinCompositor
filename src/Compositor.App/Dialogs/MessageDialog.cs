using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Compositor.App.Services;
using Compositor.Core.Localization;

namespace Compositor.App.Dialogs;

/// <summary>What a message is about: picks its icon, and the sound Windows plays for that kind of message.</summary>
public enum MessageKind { None, Information, Warning, Error, Question }

/// <summary>One button of a <see cref="MessageDialog"/>, and the answer it gives.</summary>
public sealed record MessageButton(string Text, MessageBoxResult Result)
{
    /// <summary>The blue button: Enter presses it, and it has the focus when the dialog opens. One per dialog.</summary>
    public bool IsDefault { get; init; }

    /// <summary>The answer Esc and the title bar's close box give.</summary>
    public bool IsCancel { get; init; }

    /// <summary>Set apart at the left edge, away from the others, as macOS does with Don't Save: a choice that throws work away.</summary>
    public bool Leading { get; init; }
}

/// <summary>
/// The editor's own message box, in place of the system's white one: an icon, a bold heading saying what happened, the
/// details under it, and buttons named for what they do. As in a Windows task dialog the caption is just the app's
/// name, since the heading already says the rest. The details can be selected and copied (Ctrl+C with nothing
/// selected copies the whole message), so an error can be pasted into a report; long ones scroll.
/// </summary>
public sealed class MessageDialog : Window
{
    private const string AppName = "WinCompositor";

    // The two colors the palette doesn't have; muted so they sit on the dark window without glaring.
    private static readonly Color Amber = Color.FromRgb(0xE8, 0xA3, 0x3D);
    private static readonly Color SoftRed = Color.FromRgb(0xE5, 0x67, 0x5F);

    private MessageBoxResult _result;
    private readonly string _copyText;

    /// <summary>A notice with an OK button: <paramref name="title"/> is the bold heading, <paramref name="message"/> the text under it.</summary>
    public static void Show(Window? owner, string? message, string title, MessageKind kind = MessageKind.Information) =>
        Ask(owner, title, message, kind, new MessageButton(L.T("确定", "OK"), MessageBoxResult.OK) { IsDefault = true, IsCancel = true });

    /// <summary>
    /// A question. Buttons are laid out in the order given: <see cref="MessageButton.Leading"/> ones at the left edge,
    /// the rest at the right, so the default button goes last. Returns the answer of the button pressed; Esc and the
    /// close box give the <see cref="MessageButton.IsCancel"/> button's answer, or None when there is none.
    /// </summary>
    /// <param name="owner">Centered on this window. Null (or a window not shown yet) centers it on the screen.</param>
    public static MessageBoxResult Ask(Window? owner, string heading, string? detail, MessageKind kind, params MessageButton[] buttons)
    {
        // A window belongs to the interface thread; a message raised from elsewhere is shown there.
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            return dispatcher.Invoke(() => Ask(owner, heading, detail, kind, buttons));
        if (buttons.Length == 0) buttons = new[] { new MessageButton(L.T("确定", "OK"), MessageBoxResult.OK) { IsDefault = true, IsCancel = true } };

        var dialog = new MessageDialog(heading, detail, kind, buttons);
        if (UsableOwner(owner) is { } host) dialog.Owner = host;
        else
        {
            // Nothing to sit on (an error before the main window is up): on its own, so it needs a taskbar button
            // to be found again behind other programs.
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.ShowInTaskbar = true;
        }
        try
        {
            dialog.ShowDialog();
            return dialog._result;
        }
        catch (InvalidOperationException)
        {
            // WPF can't run a modal window while the dispatcher is suspended (a message raised mid-layout) or the app is
            // shutting down; the system box still can, and a message such as an unhandled error must not be lost.
            try { dialog.Close(); } catch { }
            return SystemFallback(heading, detail, kind, buttons);
        }
    }

    private MessageDialog(string heading, string? detail, MessageKind kind, IReadOnlyList<MessageButton> buttons)
    {
        Title = AppName;
        SetResourceReference(StyleProperty, "DarkWindow");
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        // Wide enough that a short message doesn't look cramped, narrow enough that a long one wraps into lines that
        // are comfortable to read.
        MinWidth = 360;
        MaxWidth = 560;
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);

        bool hasHeading = !string.IsNullOrWhiteSpace(heading);
        bool hasDetail = !string.IsNullOrWhiteSpace(detail);
        _copyText = hasHeading && hasDetail ? heading + Environment.NewLine + Environment.NewLine + detail : hasHeading ? heading : detail ?? "";
        var cancel = buttons.FirstOrDefault(b => b.IsCancel);
        _result = cancel?.Result ?? MessageBoxResult.None;

        // The message: the icon, then the heading and the details beside it.
        var text = new StackPanel();
        if (hasHeading) text.Children.Add(new TextBlock { Text = heading, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (hasDetail) text.Children.Add(Details(detail!, secondary: hasHeading));
        var icon = Badge(kind);
        var message = new Grid { Margin = new Thickness(22, 22, 26, 22) };
        message.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        message.ColumnDefinitions.Add(new ColumnDefinition());
        if (icon is not null)
        {
            icon.Margin = new Thickness(0, 0, 16, 0);
            message.Children.Add(icon);
            // Centers the heading's first line on the icon.
            text.Margin = new Thickness(0, hasHeading ? 5 : 7, 0, 0);
        }
        Grid.SetColumn(text, 1);
        message.Children.Add(text);

        // The buttons, on a band of the panel color like the command area of a Windows dialog.
        var leading = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 24, 0) };
        var trailing = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var focusRing = FocusRing();
        var primary = buttons.FirstOrDefault(b => b.IsDefault) ?? (buttons.Count == 1 ? buttons[0] : null);
        Button? first = null, defaultButton = null;
        foreach (var spec in buttons)
        {
            var group = spec.Leading ? leading : trailing;
            var button = new Button
            {
                Content = spec.Text, IsDefault = ReferenceEquals(spec, primary), MinWidth = 84,
                Margin = new Thickness(group.Children.Count > 0 ? 8 : 0, 0, 0, 0),
                // The theme hides focus rectangles (the canvas and tool buttons don't want them), but here Tab moves
                // between choices and has to show which one Enter would press.
                FocusVisualStyle = focusRing,
            };
            var answer = spec.Result;
            button.Click += (_, _) => { _result = answer; Close(); };
            group.Children.Add(button);
            first ??= button;
            if (button.IsDefault) defaultButton = button;
        }
        var footer = new DockPanel();
        if (leading.Children.Count > 0)
        {
            DockPanel.SetDock(leading, Dock.Left);
            footer.Children.Add(leading);
        }
        footer.Children.Add(trailing);
        var band = new Border { Child = footer, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(16, 12, 16, 12) };
        band.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        band.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");

        var root = new DockPanel();
        DockPanel.SetDock(band, Dock.Bottom);
        root.Children.Add(band);
        root.Children.Add(message);
        Content = root;

        Loaded += (_, _) =>
        {
            (defaultButton ?? first)?.Focus();
            PlaySound(kind);
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                // _result already holds the cancel answer (or None); a pressed button is the only thing that changes it.
                Close();
                e.Handled = true;
            }
            else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && Keyboard.FocusedElement is not TextBox { SelectionLength: > 0 })
            {
                // With nothing selected, Ctrl+C copies the whole message, as the system message box does.
                try { Clipboard.SetText(_copyText); } catch { /* The clipboard is held by another program: nothing to do. */ }
                e.Handled = true;
            }
        };
    }

    // MARK: Building

    /// <summary>
    /// The details: a read-only text box with the field look stripped, so it reads as plain text but can be selected,
    /// in a scroller of its own so a long error doesn't push the buttons off the screen.
    /// </summary>
    private static FrameworkElement Details(string detail, bool secondary)
    {
        var body = new TextBox
        {
            Text = detail,
            IsReadOnly = true,
            IsTabStop = false,
            TextWrapping = TextWrapping.Wrap,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            MinHeight = 0,
            VerticalContentAlignment = VerticalAlignment.Top,
            // The text box insets its text by a couple of pixels; this lines it up with the heading.
            Margin = new Thickness(-2, 0, 0, 0),
        };
        // Under a heading the details are the secondary line; on their own they are the message.
        body.SetResourceReference(ForegroundProperty, secondary ? "TextDimBrush" : "TextBrush");
        var scroller = new ScrollViewer
        {
            Content = body,
            Focusable = false,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = Math.Clamp(SystemParameters.WorkArea.Height * 0.5, 160, 380),
            Margin = new Thickness(0, secondary ? 6 : 0, 0, 0),
        };
        // Keeps the text off the scroll bar when there is one.
        scroller.ScrollChanged += (_, _) =>
            body.Margin = new Thickness(-2, 0, scroller.ComputedVerticalScrollBarVisibility == Visibility.Visible ? 10 : 0, 0);
        // The text box would take the wheel for itself even with nothing to scroll; scroll the outer viewer instead.
        scroller.PreviewMouseWheel += (_, e) =>
        {
            if (scroller.ScrollableHeight <= 0) return;
            scroller.ScrollToVerticalOffset(scroller.VerticalOffset - e.Delta * 0.4);
            e.Handled = true;
        };
        return scroller;
    }

    /// <summary>
    /// The kind's icon: a round badge tinted with its color and a symbol drawn in it, as vectors so it stays sharp at any
    /// scale. Blue (the accent) for information and questions, amber for warnings, a soft red for errors.
    /// </summary>
    private static FrameworkElement? Badge(MessageKind kind)
    {
        // On a 32 × 32 badge: the stroked part of the symbol, and the dot of an i, ! or ? (y of its center; 0 for none).
        (string Stroke, double Dot) symbol = kind switch
        {
            MessageKind.Information => ("M 16,14.5 L 16,22", 10.4),
            MessageKind.Warning => ("M 16,9.5 L 16,17.5", 21.9),
            MessageKind.Error => ("M 11.8,11.8 L 20.2,20.2 M 20.2,11.8 L 11.8,20.2", 0),
            MessageKind.Question => ("M 12.9,13 A 3.1,3.1 0 1 1 17.5,15.7 Q 16,16.5 16,18.3", 22.1),
            _ => ("", -1),
        };
        if (symbol.Dot < 0) return null;
        var color = kind switch
        {
            MessageKind.Warning => Amber,
            MessageKind.Error => SoftRed,
            // The lighter accent: the plain one is too dark to read as a thin stroke on the tinted badge.
            _ => Application.Current?.TryFindResource("AccentHoverColor") is Color accent ? accent : Color.FromRgb(0x4A, 0x8D, 0xF8),
        };
        var ink = new SolidColorBrush(color);
        ink.Freeze();
        var tint = new SolidColorBrush(Color.FromArgb(0x30, color.R, color.G, color.B));
        tint.Freeze();

        var badge = new Canvas { Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Top };
        badge.Children.Add(new Ellipse { Width = 32, Height = 32, Fill = tint });
        badge.Children.Add(new Path
        {
            Data = Geometry.Parse(symbol.Stroke), Stroke = ink, StrokeThickness = 2.2,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
        });
        if (symbol.Dot > 0) badge.Children.Add(new Path { Data = new EllipseGeometry(new Point(16, symbol.Dot), 1.5, 1.5), Fill = ink });
        return badge;
    }

    /// <summary>A rounded ring just outside the button, shown only when the focus got there from the keyboard.</summary>
    private static Style FocusRing()
    {
        var ring = new FrameworkElementFactory(typeof(Border));
        ring.SetValue(MarginProperty, new Thickness(-3));
        ring.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        ring.SetValue(Border.BorderThicknessProperty, new Thickness(1.5));
        ring.SetResourceReference(Border.BorderBrushProperty, "TextBrush");
        var style = new Style();
        style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate { VisualTree = ring }));
        return style;
    }

    // MARK: Helpers

    /// <summary>
    /// The owner, or else the app's active window, whichever can own a dialog: WPF throws for a window not shown yet
    /// (an error while the main window is still being built) or already closed.
    /// </summary>
    private static Window? UsableOwner(Window? owner)
    {
        if (CanOwn(owner)) return owner;
        var active = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        return CanOwn(active) ? active : null;

        static bool CanOwn(Window? window)
        {
            if (window is null) return false;
            try { return window.IsVisible && new WindowInteropHelper(window).Handle != IntPtr.Zero; }
            catch (InvalidOperationException) { return false; }
        }
    }

    /// <summary>The sound the system message box makes for the same kind of message.</summary>
    private static void PlaySound(MessageKind kind)
    {
        var sound = kind switch
        {
            MessageKind.Information => SystemSounds.Asterisk,
            MessageKind.Warning => SystemSounds.Exclamation,
            MessageKind.Error => SystemSounds.Hand,
            MessageKind.Question => SystemSounds.Question,
            _ => null,
        };
        try { sound?.Play(); } catch { /* No sound device: the message still shows. */ }
    }

    /// <summary>The system message box, for when a WPF window can't be shown; buttons map to its nearest set.</summary>
    private static MessageBoxResult SystemFallback(string heading, string? detail, MessageKind kind, IReadOnlyList<MessageButton> buttons)
    {
        var results = buttons.Select(b => b.Result).ToHashSet();
        var set = results.SetEquals(new[] { MessageBoxResult.Yes, MessageBoxResult.No, MessageBoxResult.Cancel }) ? MessageBoxButton.YesNoCancel
            : results.SetEquals(new[] { MessageBoxResult.Yes, MessageBoxResult.No }) ? MessageBoxButton.YesNo
            : results.SetEquals(new[] { MessageBoxResult.OK, MessageBoxResult.Cancel }) ? MessageBoxButton.OKCancel
            : MessageBoxButton.OK;
        var image = kind switch
        {
            MessageKind.Information => MessageBoxImage.Information,
            MessageKind.Warning => MessageBoxImage.Warning,
            MessageKind.Error => MessageBoxImage.Error,
            MessageKind.Question => MessageBoxImage.Question,
            _ => MessageBoxImage.None,
        };
        string text = string.IsNullOrWhiteSpace(detail) ? heading : string.IsNullOrWhiteSpace(heading) ? detail : heading + "\n\n" + detail;
        var answer = MessageBox.Show(text, AppName, set, image);
        // Buttons the system box can't offer: only the safe answer can be assumed.
        if (set == MessageBoxButton.OK && !results.Contains(MessageBoxResult.OK))
            return buttons.FirstOrDefault(b => b.IsCancel)?.Result ?? MessageBoxResult.None;
        return answer;
    }
}
