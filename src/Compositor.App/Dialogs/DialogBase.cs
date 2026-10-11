using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Compositor.Core.Localization;

namespace Compositor.App.Dialogs;

/// <summary>A dark, fixed-size dialog with OK and Cancel along the bottom.</summary>
public abstract class DialogBase : Window
{
    // The labels of Row: at least this wide, and all as wide as the longest, so the controls line up down the dialog.
    private const double RowLabelWidth = 90;
    private const string RowLabelGroup = "DialogRowLabel";

    protected readonly StackPanel Body = new() { Margin = new Thickness(20, 18, 20, 10) };
    protected readonly Button OkButton;
    protected readonly Button CancelButton;

    /// <param name="ok">The default button's text; null for OK.</param>
    protected DialogBase(string title, string? ok = null)
    {
        Title = title;
        SetResourceReference(StyleProperty, "DarkWindow");
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        MinWidth = 320;
        SourceInitialized += (_, _) => Services.DarkTitleBar.Apply(this);

        OkButton = new Button { Content = ok ?? L.T("确定", "OK"), IsDefault = true, MinWidth = 84, Margin = new Thickness(8, 0, 0, 0) };
        CancelButton = new Button { Content = L.T("取消", "Cancel"), IsCancel = true, MinWidth = 84 };
        OkButton.Click += (_, _) => { if (Validate()) DialogResult = true; };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(20, 6, 20, 18) };
        buttons.Children.Add(CancelButton);
        buttons.Children.Add(OkButton);
        var root = new DockPanel();
        Grid.SetIsSharedSizeScope(root, true);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(Body);
        Content = root;
    }

    /// <summary>Checks the values before closing with OK; false keeps the dialog open.</summary>
    protected virtual bool Validate() => true;

    protected static TextBlock Label(string text, double width = 90) => new()
    {
        Text = text,
        Width = width,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = (Brush)Application.Current.FindResource("TextDimBrush"),
    };

    /// <summary>
    /// A labeled row: label, then the controls. The labels share one column, 90 wide or as wide as the longest label
    /// needs (English ones run longer), so the controls of every row start at the same place.
    /// </summary>
    protected StackPanel Row(string label, params UIElement[] controls)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        var text = Label(label);
        text.Width = double.NaN;
        text.Margin = new Thickness(0, 0, 12, 0);
        var cell = new Grid();
        cell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = RowLabelWidth, SharedSizeGroup = RowLabelGroup });
        cell.Children.Add(text);
        row.Children.Add(cell);
        foreach (var control in controls) row.Children.Add(control);
        Body.Children.Add(row);
        return row;
    }

    protected static TextBlock Note(string text) => new()
    {
        Text = text,
        Margin = new Thickness(0, 0, 0, 10),
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 360,
        Foreground = (Brush)Application.Current.FindResource("TextDimBrush"),
    };

    protected static TextBox IntBox(int value, double width = 80) => new() { Text = value.ToString(), Width = width };

    protected static bool TryInt(TextBox box, int min, int max, out int value) =>
        int.TryParse(box.Text.Trim(), out value) && value >= min && value <= max;

    protected void Warn(string message) => MessageDialog.Show(this, message, Title, MessageKind.Warning);
}
