using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using Compositor.App.Services;
using Compositor.Core.Localization;

namespace Compositor.App.Dialogs;

/// <summary>
/// Edit ▸ Preferences: Compositor's own settings, in titled groups (for now General, with the interface language).
/// Each group is a card of rows, the setting's name on the left and its control, with a line of explanation, on the
/// right; the names share one column across every group. A new setting is one more <see cref="AddSetting"/> call, a
/// new group one more <see cref="AddGroup"/>. Nothing is applied here: OK hands the choices back through the
/// properties (<see cref="InterfaceLanguage"/>) and the caller keeps them.
/// </summary>
public sealed class PreferencesWindow : Window
{
    private const double Gutter = 24;
    private const string NameColumn = "PreferenceName";

    private readonly StackPanel _groups = new() { Margin = new Thickness(Gutter, 18, Gutter, 22) };
    private readonly List<RadioButton> _languageChoices = new();
    private readonly Style _focusRing = FocusRing();

    /// <summary>
    /// The interface language chosen, as <see cref="Settings.Language"/> stores it: <see cref="Settings.SystemLanguage"/>,
    /// <see cref="Settings.ChineseLanguage"/> or <see cref="Settings.EnglishLanguage"/>.
    /// </summary>
    public string InterfaceLanguage { get; private set; }

    /// <param name="owner">Centered on this window; null centers it on the screen.</param>
    /// <param name="language">The <see cref="Settings.Language"/> in effect; anything unknown counts as following Windows.</param>
    public PreferencesWindow(Window? owner, string? language)
    {
        Owner = owner;
        InterfaceLanguage = Settings.NormalizeLanguage(language);
        Title = L.T("首选项", "Preferences");
        SetResourceReference(StyleProperty, "DarkWindow");
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        MinWidth = 440;
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);

        Grid.SetIsSharedSizeScope(_groups, true);
        var general = AddGroup(L.T("常规", "General"));
        AddSetting(general, L.T("界面语言", "Language"), LanguageChoices(),
            L.T("重启 WinCompositor 后生效。", "Takes effect after restarting WinCompositor."));

        var footer = Footer();
        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(_groups);
        Content = root;

        // The language in effect has the focus, so the arrow keys move between the choices straight away; Enter is
        // OK and Esc Cancel from anywhere in the window.
        Loaded += (_, _) => (_languageChoices.FirstOrDefault(c => c.IsChecked == true) ?? _languageChoices.FirstOrDefault())?.Focus();
    }

    // MARK: Groups and settings

    /// <summary>A titled group: its name, then a card for its settings, which is returned.</summary>
    private StackPanel AddGroup(string title)
    {
        var heading = new TextBlock
        {
            Text = title, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(1, _groups.Children.Count > 0 ? 22 : 0, 0, 8),
        };
        var rows = new StackPanel();
        var card = new Border { Child = rows, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6) };
        card.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        _groups.Children.Add(heading);
        _groups.Children.Add(card);
        return rows;
    }

    /// <summary>One setting in a group: its name, then the control with an optional line of explanation under it.</summary>
    private static void AddSetting(StackPanel group, string name, FrameworkElement control, string? note = null)
    {
        if (group.Children.Count > 0)
        {
            var rule = new Border { Height = 1 };
            rule.SetResourceReference(Border.BackgroundProperty, "BorderBrush");
            group.Children.Add(rule);
        }
        var label = new TextBlock { Text = name, Margin = new Thickness(0, 0, 28, 0), VerticalAlignment = VerticalAlignment.Top };
        var right = new StackPanel();
        right.Children.Add(control);
        if (!string.IsNullOrEmpty(note))
        {
            var hint = new TextBlock { Text = note, Margin = new Thickness(0, 12, 0, 0) };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "TextDimBrush");
            right.Children.Add(hint);
        }
        AutomationProperties.SetName(control, name);
        Grid.SetColumn(right, 1);
        var row = new Grid { Margin = new Thickness(16, 14, 18, 14) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 80, SharedSizeGroup = NameColumn });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(label);
        row.Children.Add(right);
        group.Children.Add(row);
    }

    // MARK: Language

    /// <summary>
    /// Following Windows, with the language that gives here in brackets, then the two languages. Each language is
    /// written in its own words, untranslated, so whoever can't read the interface as it is can still find theirs.
    /// </summary>
    private StackPanel LanguageChoices()
    {
        var panel = new StackPanel();
        string windows = Settings.ResolveLanguage(Settings.SystemLanguage) == AppLanguage.English ? "English" : "简体中文";
        Add(Settings.SystemLanguage, L.T("跟随系统", "Use Windows Language"), L.T($"（{windows}）", $" ({windows})"));
        Add(Settings.ChineseLanguage, "简体中文");
        Add(Settings.EnglishLanguage, "English");
        return panel;

        void Add(string value, string name, string? detail = null)
        {
            var text = new TextBlock();
            text.Inlines.Add(new Run(name));
            if (detail is not null)
            {
                var dim = new Run(detail);
                dim.SetResourceReference(TextElement.ForegroundProperty, "TextDimBrush");
                text.Inlines.Add(dim);
            }
            var choice = new RadioButton
            {
                Content = text, GroupName = "InterfaceLanguage", IsChecked = value == InterfaceLanguage,
                HorizontalAlignment = HorizontalAlignment.Left, FocusVisualStyle = _focusRing,
                Margin = new Thickness(0, panel.Children.Count > 0 ? 9 : 0, 0, 0),
            };
            AutomationProperties.SetName(choice, name + (detail ?? ""));
            choice.Checked += (_, _) => InterfaceLanguage = value;
            _languageChoices.Add(choice);
            panel.Children.Add(choice);
        }
    }

    // MARK: Building

    /// <summary>Cancel and OK on a band of the panel color, as the command area of a Windows dialog.</summary>
    private Border Footer()
    {
        var cancel = new Button { Content = L.T("取消", "Cancel"), IsCancel = true, MinWidth = 84, FocusVisualStyle = _focusRing };
        var ok = new Button { Content = L.T("确定", "OK"), IsDefault = true, MinWidth = 84, Margin = new Thickness(8, 0, 0, 0), FocusVisualStyle = _focusRing };
        ok.Click += (_, _) => DialogResult = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        var band = new Border { Child = buttons, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(16, 12, 16, 12) };
        band.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        band.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        return band;
    }

    /// <summary>
    /// A rounded ring just outside a control, shown only when the focus got there from the keyboard: the theme hides
    /// focus rectangles, but here Tab and the arrow keys move between choices and have to show where they are.
    /// </summary>
    private static Style FocusRing()
    {
        var ring = new FrameworkElementFactory(typeof(Border));
        ring.SetValue(MarginProperty, new Thickness(-3));
        ring.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        ring.SetValue(Border.BorderThicknessProperty, new Thickness(1.5));
        ring.SetResourceReference(Border.BorderBrushProperty, "TextBrush");
        var style = new Style();
        style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate { VisualTree = ring }));
        return style;
    }
}
