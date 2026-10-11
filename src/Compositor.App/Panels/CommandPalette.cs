using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Compositor.Core.Editing;
using Compositor.Core.Localization;

namespace Compositor.App.Panels;

/// <summary>One thing the command palette can run: a menu command or a tool, by its full path ("滤镜 › 高斯模糊…").</summary>
public sealed record PaletteEntry(string Title, string? Shortcut, bool IsEnabled, bool IsOn, Action Perform);

/// <summary>
/// The command palette (Ctrl+F): type a few letters of any menu command or tool and press Enter. Disabled commands are
/// listed greyed and never run. Mirrors CommandPaletteView.swift.
/// </summary>
public sealed class CommandPalette : Window
{
    private readonly IReadOnlyList<PaletteEntry> _entries;
    private readonly TextBox _search;
    private readonly TextBlock _placeholder;
    private readonly ListBox _list;
    private bool _closing;

    public CommandPalette(Window owner, IReadOnlyList<PaletteEntry> entries)
    {
        _entries = entries;
        Owner = owner;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Width = 600;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Title = L.T("命令", "Commands");

        _search = new TextBox { FontSize = 15, Padding = new Thickness(20, 14, 20, 14), BorderThickness = new Thickness(0), Background = Brushes.Transparent };
        _search.SetResourceReference(ForegroundProperty, "TextBrush");
        _search.SetResourceReference(TextBox.CaretBrushProperty, "TextBrush");
        _placeholder = new TextBlock { Text = L.T("搜索命令和工具…", "Search commands and tools…"), FontSize = 15, Margin = new Thickness(13, 8, 0, 0), IsHitTestVisible = false };
        _placeholder.SetResourceReference(TextBlock.ForegroundProperty, "TextDimBrush");
        var field = new Grid();
        field.Children.Add(_placeholder);
        field.Children.Add(_search);

        _list = new ListBox
        {
            MaxHeight = 420, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Focusable = false,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, System.Windows.Controls.ScrollBarVisibility.Disabled);
        _list.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (ItemAt(e.OriginalSource as DependencyObject) is { } item) { _list.SelectedItem = item; Run(); }
        };

        var separator = new Border { Height = 1, Margin = new Thickness(0, 2, 0, 2) };
        separator.SetResourceReference(Border.BackgroundProperty, "BorderBrush");
        var stack = new StackPanel();
        stack.Children.Add(field);
        stack.Children.Add(separator);
        stack.Children.Add(_list);
        var frame = new Border
        {
            CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(6), Child = stack,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = 0.5 },
            Margin = new Thickness(12),
        };
        frame.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        frame.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        Content = frame;

        _search.TextChanged += (_, _) => Filter();
        Loaded += (_, _) =>
        {
            Left = owner.Left + (owner.ActualWidth - ActualWidth) / 2;
            Top = owner.Top + 70;
            if (owner.WindowState == WindowState.Maximized)
            {
                var area = SystemParameters.WorkArea;
                Left = area.Left + (area.Width - ActualWidth) / 2;
                Top = area.Top + 70;
            }
            _search.Focus();
        };
        Deactivated += (_, _) => { if (!_closing) { _closing = true; Close(); } };
        Filter();
    }

    private ListBoxItem? ItemAt(DependencyObject? source)
    {
        while (source is not null and not ListBoxItem) source = VisualTreeHelper.GetParent(source);
        return source as ListBoxItem;
    }

    private void Filter()
    {
        _placeholder.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var ranked = CommandSearch.Rank(_entries, _search.Text, e => e.Title, e => e.IsEnabled);
        _list.Items.Clear();
        foreach (var entry in ranked.Take(200)) _list.Items.Add(Row(entry));
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
    }

    private static ListBoxItem Row(PaletteEntry entry)
    {
        var row = new DockPanel { Margin = new Thickness(4, 3, 4, 3) };
        if (entry.Shortcut is { } shortcut)
        {
            var key = new TextBlock { Text = shortcut, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            key.SetResourceReference(TextBlock.ForegroundProperty, "TextDimBrush");
            DockPanel.SetDock(key, Dock.Right);
            row.Children.Add(key);
        }
        var check = new TextBlock { Text = entry.IsOn ? "✓" : "", Width = 16, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(check);
        var title = new TextBlock { Text = entry.Title, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        title.SetResourceReference(TextBlock.ForegroundProperty, entry.IsEnabled ? "TextBrush" : "TextDimBrush");
        if (!entry.IsEnabled) title.Opacity = 0.6;
        row.Children.Add(title);
        return new ListBoxItem { Content = row, Tag = entry, Padding = new Thickness(4, 2, 4, 2) };
    }

    private void Run()
    {
        if (_list.SelectedItem is not ListBoxItem { Tag: PaletteEntry entry }) return;
        if (!entry.IsEnabled) { System.Media.SystemSounds.Beep.Play(); return; }
        _closing = true;
        Close();
        Owner?.Activate();
        // Run once the palette is gone, so a command that opens a dialog or panel gets the focus.
        Owner?.Dispatcher.BeginInvoke(entry.Perform, System.Windows.Threading.DispatcherPriority.Input);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        switch (e.Key)
        {
            case Key.Escape:
                _closing = true;
                Close();
                Owner?.Activate();
                e.Handled = true;
                break;
            case Key.Enter:
                Run();
                e.Handled = true;
                break;
            case Key.Down or Key.Up when _list.Items.Count > 0:
            {
                int step = e.Key == Key.Down ? 1 : -1;
                _list.SelectedIndex = (_list.SelectedIndex + step + _list.Items.Count) % _list.Items.Count;
                _list.ScrollIntoView(_list.SelectedItem);
                e.Handled = true;
                break;
            }
            case Key.PageDown or Key.PageUp when _list.Items.Count > 0:
            {
                int step = e.Key == Key.PageDown ? 10 : -10;
                _list.SelectedIndex = Math.Clamp(_list.SelectedIndex + step, 0, _list.Items.Count - 1);
                _list.ScrollIntoView(_list.SelectedItem);
                e.Handled = true;
                break;
            }
        }
    }
}
