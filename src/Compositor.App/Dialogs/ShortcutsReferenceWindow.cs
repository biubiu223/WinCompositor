using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Compositor.App.Services;
using Compositor.Core.Localization;
using Shapes = System.Windows.Shapes;

namespace Compositor.App.Dialogs;

/// <summary>One line of the shortcut reference: what it does, and its keys as the menus write them ("Ctrl+Shift+S").</summary>
public sealed record ShortcutEntry(string Action, string Keys);

/// <summary>A titled group of shortcuts: one menu, or the tools and canvas keys.</summary>
public sealed record ShortcutSection(string Title, IReadOnlyList<ShortcutEntry> Entries);

/// <summary>
/// Help ▸ Keyboard Shortcuts: a read-only, searchable cheat sheet of every shortcut, grouped as the menus are and drawn
/// as key caps. Changing them is Edit ▸ Keyboard Shortcuts (<see cref="ShortcutsDialog"/>), one button away.
/// </summary>
public sealed class ShortcutsReferenceWindow : Window
{
    private sealed record RowView(FrameworkElement Element, string Action, string Keys);
    private sealed record GroupView(FrameworkElement Element, List<RowView> Rows);

    private readonly List<GroupView> _groups = new();
    private readonly int _total;
    private readonly TextBox _search;
    private readonly TextBlock _placeholder;
    private readonly Button _clear;
    private readonly TextBlock _summary;
    private readonly ScrollViewer _scroller;
    private readonly FrameworkElement _empty;
    private readonly TextBlock _emptyTitle;
    private readonly TextBlock _emptyHint;

    /// <param name="editShortcuts">Opens Edit ▸ Keyboard Shortcuts; called once this window has closed. Null hides the button.</param>
    public ShortcutsReferenceWindow(Window owner, IReadOnlyList<ShortcutSection> sections, IReadOnlyList<string> notes, Action? editShortcuts)
    {
        Owner = owner;
        Title = L.T("键盘快捷键", "Keyboard Shortcuts");
        SetResourceReference(StyleProperty, "DarkWindow");
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        // Sized to the screen and never past it, however many shortcuts there are: the list scrolls inside instead.
        var area = SystemParameters.WorkArea;
        // Wider in English, where the columns need more room (see ColumnsPanel).
        Width = Math.Min(L.IsEnglish ? 1100 : 880, area.Width * 0.9);
        Height = Math.Min(660, area.Height * 0.9);
        MinWidth = Math.Min(640, Width);
        MinHeight = Math.Min(420, Height);
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);

        // Header: the title with how many shortcuts are showing, and the search field.
        var title = new TextBlock { Text = L.T("键盘快捷键", "Keyboard Shortcuts"), FontSize = 18, FontWeight = FontWeights.SemiBold };
        _summary = Dim("");
        _summary.Margin = new Thickness(0, 3, 0, 0);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(title);
        titles.Children.Add(_summary);

        // 26 puts typing 29 px in (with the text box's own inset), clear of the magnifier, and keeps it short of the
        // clear button on the right.
        _search = new TextBox { Height = 30, Padding = new Thickness(26, 0, 26, 0) };
        AutomationProperties.SetName(_search, L.T("搜索快捷键", "Search shortcuts"));
        var lens = new GeometryGroup();
        lens.Children.Add(new EllipseGeometry(new Point(6, 6), 4.5, 4.5));
        lens.Children.Add(new LineGeometry(new Point(9.4, 9.4), new Point(13, 13)));
        var icon = new Shapes.Path
        {
            Data = lens, StrokeThickness = 1.4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Width = 14, Height = 14, Margin = new Thickness(9, 0, 0, 0), IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
        };
        icon.SetResourceReference(Shapes.Shape.StrokeProperty, "TextDimBrush");
        _placeholder = Dim(L.T("搜索动作或按键", "Search actions or keys"));
        _placeholder.Margin = new Thickness(29, 0, 0, 0);
        _placeholder.IsHitTestVisible = false;
        // The hint sits exactly where typing starts, measured once the box is laid out rather than assumed from the
        // template, so it stays lined up if the text box style changes.
        _search.SizeChanged += (_, _) =>
        {
            var origin = _search.GetRectFromCharacterIndex(0);
            if (!origin.IsEmpty) _placeholder.Margin = new Thickness(Math.Round(origin.X), 0, 0, 0);
        };
        var cross = new Shapes.Path
        {
            Data = Geometry.Parse("M 0,0 L 7,7 M 7,0 L 0,7"), StrokeThickness = 1.4,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        };
        cross.SetResourceReference(Shapes.Shape.StrokeProperty, "TextDimBrush");
        _clear = new Button
        {
            Content = cross, Width = 22, Height = 22, Padding = new Thickness(0), Margin = new Thickness(0, 0, 4, 0),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            ToolTip = L.T("清除搜索 (Esc)", "Clear Search (Esc)"), Visibility = Visibility.Collapsed,
        };
        _clear.SetResourceReference(StyleProperty, "FlatButton");
        _clear.Click += (_, _) => { _search.Clear(); Keyboard.Focus(_search); };
        var searchBox = new Grid { Width = 280, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
        searchBox.Children.Add(_search);
        searchBox.Children.Add(icon);
        searchBox.Children.Add(_placeholder);
        searchBox.Children.Add(_clear);

        var header = new Grid { Margin = new Thickness(20, 14, 20, 14) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(searchBox, 1);
        header.Children.Add(titles);
        header.Children.Add(searchBox);

        // Body: a card per group, packed into columns; a message in its place when nothing matches.
        var columns = new ColumnsPanel { Margin = new Thickness(20, 16, 10, 20) };
        foreach (var section in sections)
        {
            var group = BuildGroup(section);
            _groups.Add(group);
            columns.Children.Add(group.Element);
            _total += group.Rows.Count;
        }
        _scroller = new ScrollViewer
        {
            Content = columns, Focusable = false,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        _emptyTitle = new TextBlock { FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        _emptyHint = Dim(L.T("换个关键字试试，或按 Esc 清空搜索", "Try a different word, or press Esc to clear the search."));
        _emptyHint.HorizontalAlignment = HorizontalAlignment.Center;
        _emptyHint.Margin = new Thickness(0, 6, 0, 0);
        var empty = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(40), IsHitTestVisible = false, Visibility = Visibility.Collapsed,
        };
        empty.Children.Add(_emptyTitle);
        empty.Children.Add(_emptyHint);
        _empty = empty;
        var body = new Grid();
        body.Children.Add(_scroller);
        body.Children.Add(empty);

        // Footer: the notes in secondary text on the left, the buttons at the bottom right.
        var footer = new Grid { Margin = new Thickness(20, 12, 20, 16) };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var notesList = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        foreach (var note in notes)
            if (!string.IsNullOrWhiteSpace(note)) notesList.Children.Add(NoteLine(note));
        footer.Children.Add(notesList);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(24, 0, 0, 0) };
        if (editShortcuts is not null)
        {
            var edit = new Button { Content = L.T("修改快捷键…", "Edit Shortcuts…"), MinWidth = 96, Margin = new Thickness(0, 0, 8, 0) };
            edit.Click += (_, _) =>
            {
                Close();
                // Once this window is gone (and a ShowDialog that opened it has returned), so the editor opens over
                // the main window rather than on top of a reference that is about to go out of date.
                Dispatcher.BeginInvoke(editShortcuts);
            };
            buttons.Children.Add(edit);
        }
        var close = new Button { Content = L.T("关闭", "Close"), IsDefault = true, MinWidth = 84 };
        close.Click += (_, _) => Close();
        buttons.Children.Add(close);
        Grid.SetColumn(buttons, 1);
        footer.Children.Add(buttons);

        var root = new DockPanel();
        var topRule = Rule();
        var bottomRule = Rule();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(topRule, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        DockPanel.SetDock(bottomRule, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(topRule);
        root.Children.Add(footer);
        root.Children.Add(bottomRule);
        root.Children.Add(body);
        Content = root;

        _search.TextChanged += (_, _) => Filter();
        _search.PreviewKeyDown += OnSearchKey;
        FocusManager.SetFocusedElement(this, _search);
        Loaded += (_, _) => Keyboard.Focus(_search);
        Filter();
    }

    // MARK: Building

    private GroupView BuildGroup(ShortcutSection section)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = section.Title, FontSize = 13, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(6, 0, 6, 7), TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var rule = Rule();
        rule.Margin = new Thickness(6, 0, 6, 4);
        stack.Children.Add(rule);
        var rows = new List<RowView>();
        foreach (var entry in section.Entries)
        {
            var row = BuildRow(entry);
            stack.Children.Add(row);
            rows.Add(new RowView(row, entry.Action ?? "", Compact(entry.Keys ?? "")));
        }
        var card = new Border
        {
            Child = stack, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Padding = new Thickness(6, 10, 6, 6),
            Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed,
        };
        card.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        return new GroupView(card, rows);
    }

    /// <summary>The action on the left (cut short with an ellipsis, in full in its tooltip), its key caps on the right.</summary>
    private static FrameworkElement BuildRow(ShortcutEntry entry)
    {
        var name = new TextBlock
        {
            Text = entry.Action, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0), ToolTip = entry.Action,
        };
        // The tooltip only says something when the name doesn't fit.
        name.ToolTipOpening += (_, e) => { if (!IsTrimmed(name)) e.Handled = true; };
        var keys = BuildKeys(entry.Keys ?? "");
        Grid.SetColumn(keys, 1);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(name);
        grid.Children.Add(keys);
        // A faint band under the pointer carries the eye from a name across to its keys in a wide column.
        var row = new Border { Child = grid, Padding = new Thickness(6, 3, 6, 3), MinHeight = 26, CornerRadius = new CornerRadius(4), Background = Brushes.Transparent };
        row.MouseEnter += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "PanelAltBrush");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        return row;
    }

    private static FrameworkElement BuildKeys(string keys)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        var alternatives = SplitAlternatives(keys);
        // A cleared shortcut reads as Chord.None.Label ("无" / "None"): plain secondary text, not a key cap.
        if (alternatives.Count == 0 || (alternatives.Count == 1 && alternatives[0] == Chord.None.Label))
        {
            panel.Children.Add(Dim(Chord.None.Label));
            return panel;
        }
        for (int a = 0; a < alternatives.Count; a++)
        {
            if (a > 0) panel.Children.Add(Joiner("/", 6));
            var caps = SplitKeys(alternatives[a]);
            for (int i = 0; i < caps.Count; i++)
            {
                if (i > 0) panel.Children.Add(Joiner("+", 3));
                panel.Children.Add(KeyCap(caps[i]));
            }
        }
        return panel;
    }

    private static Border KeyCap(string key)
    {
        var cap = new Border
        {
            Child = new TextBlock { Text = key, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            MinWidth = 22, Padding = new Thickness(6, 1, 6, 1), CornerRadius = new CornerRadius(4),
            // A slightly heavier lower edge reads as a key rather than a tag.
            BorderThickness = new Thickness(1, 1, 1, 2), VerticalAlignment = VerticalAlignment.Center,
        };
        cap.SetResourceReference(Border.BackgroundProperty, "ControlBrush");
        cap.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        return cap;
    }

    /// <summary>The faint "+" between the keys of one shortcut (or "/" between alternatives).</summary>
    private static TextBlock Joiner(string text, double space)
    {
        var block = Dim(text);
        block.FontSize = 10;
        block.Margin = new Thickness(space, 0, space, 0);
        return block;
    }

    private static FrameworkElement NoteLine(string note)
    {
        var bullet = Dim("•");
        bullet.VerticalAlignment = VerticalAlignment.Top;
        bullet.Margin = new Thickness(0, 0, 7, 0);
        var text = Dim(note);
        text.VerticalAlignment = VerticalAlignment.Top;
        text.TextWrapping = TextWrapping.Wrap;
        Grid.SetColumn(text, 1);
        var line = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition());
        line.Children.Add(bullet);
        line.Children.Add(text);
        return line;
    }

    private static TextBlock Dim(string text)
    {
        var block = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextDimBrush");
        return block;
    }

    private static Border Rule()
    {
        var rule = new Border { Height = 1 };
        rule.SetResourceReference(Border.BackgroundProperty, "BorderBrush");
        return rule;
    }

    private static bool IsTrimmed(TextBlock block)
    {
        if (string.IsNullOrEmpty(block.Text) || block.ActualWidth <= 0) return false;
        var typeface = new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch);
        var text = new FormattedText(block.Text, CultureInfo.CurrentUICulture, block.FlowDirection, typeface, block.FontSize, Brushes.White,
            null, TextOptions.GetTextFormattingMode(block), VisualTreeHelper.GetDpi(block).PixelsPerDip);
        return text.WidthIncludingTrailingWhitespace > block.ActualWidth + 0.5;
    }

    // MARK: Search

    /// <summary>
    /// Shows the rows whose action or keys contain the search text, ignoring case, and the groups that keep any.
    /// Only visibility changes, so typing never rebuilds the list.
    /// </summary>
    private void Filter()
    {
        string query = _search.Text.Trim();
        // Keys are matched without spaces, so "ctrl + s" finds Ctrl+S.
        string keysQuery = Compact(query);
        int shown = 0;
        foreach (var group in _groups)
        {
            int visible = 0;
            foreach (var row in group.Rows)
            {
                bool match = query.Length == 0
                             || row.Action.Contains(query, StringComparison.OrdinalIgnoreCase)
                             || (keysQuery.Length > 0 && row.Keys.Contains(keysQuery, StringComparison.OrdinalIgnoreCase));
                row.Element.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                if (match) visible++;
            }
            group.Element.Visibility = visible > 0 ? Visibility.Visible : Visibility.Collapsed;
            shown += visible;
        }
        _placeholder.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _clear.Visibility = _search.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _summary.Text = query.Length == 0
            ? L.T($"共 {_total} 个快捷键", _total == 1 ? "1 shortcut" : $"{_total} shortcuts")
            : L.T($"找到 {shown} 个，共 {_total} 个", $"{shown} of {_total} shown");
        _emptyTitle.Text = query.Length == 0 ? L.T("没有快捷键", "No shortcuts") : L.T($"没有与“{query}”匹配的快捷键", $"No shortcuts match “{query}”");
        _emptyHint.Visibility = query.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _empty.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
        // What matches may all be above the old scroll position.
        _scroller.ScrollToTop();
    }

    private static string Compact(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));

    // MARK: Keys

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Esc empties the search first, so a second Esc closes; Ctrl+F returns to the search from anywhere.
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (_search.Text.Length > 0) { _search.Clear(); Keyboard.Focus(_search); }
            else Close();
            return;
        }
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            Keyboard.Focus(_search);
            _search.SelectAll();
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    /// <summary>Typing stays in the search field while the arrow and page keys scroll the list.</summary>
    private void OnSearchKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            // Enter in a search field shouldn't close the window through the default button.
            case Key.Enter: e.Handled = true; break;
            case Key.Down: _scroller.ScrollToVerticalOffset(_scroller.VerticalOffset + 48); e.Handled = true; break;
            case Key.Up: _scroller.ScrollToVerticalOffset(_scroller.VerticalOffset - 48); e.Handled = true; break;
            case Key.PageDown: _scroller.PageDown(); e.Handled = true; break;
            case Key.PageUp: _scroller.PageUp(); e.Handled = true; break;
        }
    }

    // MARK: Key text

    /// <summary>Alternative shortcuts for one action, written "Ctrl+Shift+Z / Ctrl+Y". A bare "/" is the slash key ("Ctrl+/").</summary>
    internal static IReadOnlyList<string> SplitAlternatives(string? keys)
    {
        if (string.IsNullOrWhiteSpace(keys)) return Array.Empty<string>();
        return keys.Split(" / ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static readonly string[] Modifiers = { "Ctrl", "Alt", "Shift", "Win" };

    /// <summary>
    /// The key caps of a shortcut as the menus write it: "Ctrl+Shift+S" → Ctrl, Shift, S. A "+" where a key is expected
    /// (at the start, or right after a separator) is the plus key itself: "Ctrl++" → Ctrl, +. A "+" ending the text
    /// belongs to the key name before it, "Num+", unless that's a modifier ("Ctrl+" → Ctrl, +). Every other "+"
    /// separates keys. Spaces around keys are dropped.
    /// </summary>
    internal static IReadOnlyList<string> SplitKeys(string? keys)
    {
        var caps = new List<string>();
        if (string.IsNullOrWhiteSpace(keys)) return caps;
        string text = keys.Trim();
        int n = text.Length, i = 0;
        while (i < n)
        {
            string cap;
            if (text[i] == '+')
            {
                cap = "+";
                i++;
            }
            else
            {
                int start = i;
                while (i < n && text[i] != '+') i++;
                cap = text[start..i].Trim();
                if (i == n - 1 && !Modifiers.Contains(cap, StringComparer.OrdinalIgnoreCase))
                {
                    i++;
                    cap = text[start..i].Trim();
                }
            }
            if (cap.Length > 0) caps.Add(cap);
            // The separator; a "+" left last is a key of its own, taken on the next pass.
            if (i < n - 1 && text[i] == '+') i++;
        }
        return caps;
    }

    // MARK: Layout

    /// <summary>
    /// Lays the group cards out in up to three equal columns, each card going into the column that is shortest so far,
    /// so groups of 3 and of 40 shortcuts pack without the holes a WrapPanel's rows would leave. Columns stop growing
    /// at a readable width; the block is centered when the window is wider than that.
    /// </summary>
    private sealed class ColumnsPanel : Panel
    {
        // The narrowest a column gets while the longest shortcut (four key caps, or two shortcuts) still leaves room for
        // its name: 260 for Chinese names, 330 for English ones, which run longer ("Switch Foreground and Background
        // Colors"). Either gives three columns at the window's default size for that language and two at the minimum.
        private const double Gap = 14, MaxColumn = 440;
        private static double MinColumn => L.IsEnglish ? 330 : 260;
        private const int MaxColumns = 3;

        private static (int Count, double Width) Columns(double available)
        {
            int count = Math.Clamp((int)Math.Floor((available + Gap) / (MinColumn + Gap)), 1, MaxColumns);
            double width = Math.Floor(Math.Min(MaxColumn, (available - Gap * (count - 1)) / count));
            return (count, Math.Max(0, width));
        }

        private static int Shortest(double[] heights)
        {
            int best = 0;
            for (int c = 1; c < heights.Length; c++)
                if (heights[c] < heights[best] - 0.5) best = c;
            return best;
        }

        protected override Size MeasureOverride(Size available)
        {
            double total = double.IsInfinity(available.Width) ? MaxColumns * (MaxColumn + Gap) - Gap : available.Width;
            var (count, width) = Columns(total);
            var heights = new double[count];
            foreach (UIElement child in InternalChildren)
            {
                child.Measure(new Size(width, double.PositiveInfinity));
                if (child.Visibility == Visibility.Collapsed) continue;
                int c = Shortest(heights);
                heights[c] += (heights[c] > 0 ? Gap : 0) + child.DesiredSize.Height;
            }
            return new Size(Math.Min(total, count * width + Gap * (count - 1)), heights.Max());
        }

        protected override Size ArrangeOverride(Size final)
        {
            var (count, width) = Columns(final.Width);
            double left = Math.Floor(Math.Max(0, (final.Width - (count * width + Gap * (count - 1))) / 2));
            var heights = new double[count];
            foreach (UIElement child in InternalChildren)
            {
                if (child.Visibility == Visibility.Collapsed) { child.Arrange(new Rect()); continue; }
                int c = Shortest(heights);
                double top = heights[c] > 0 ? heights[c] + Gap : 0;
                child.Arrange(new Rect(left + c * (width + Gap), top, width, child.DesiredSize.Height));
                heights[c] = top + child.DesiredSize.Height;
            }
            return final;
        }
    }
}
