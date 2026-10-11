using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Compositor.App.Services;
using Compositor.Core.Localization;

namespace Compositor.App.Dialogs;

/// <summary>
/// Edit ▸ Keyboard Shortcuts: every menu command and canvas key with its shortcut. Click one, then press the new key
/// combination (Esc while recording leaves it as it was). Changes apply on Save. Mirrors KeyboardShortcutsSheet.
/// </summary>
public sealed class ShortcutsDialog : DialogBase
{
    private readonly List<ShortcutDefinition> _definitions;
    private readonly Dictionary<string, Chord> _draft;
    private readonly List<(ShortcutDefinition Definition, FrameworkElement Row, Button Recorder)> _rows = new();
    private readonly TextBox _search = new() { Width = 300 };
    private readonly TextBlock _problem;
    private string? _recording;

    /// <summary>The changes to save: definition id → shortcut, for those that differ from the default.</summary>
    public Dictionary<string, Chord> Values => _draft.Where(p => _definitions.First(d => d.Id == p.Key).Original != p.Value)
        .ToDictionary(p => p.Key, p => p.Value);

    public ShortcutsDialog(IReadOnlyList<ShortcutDefinition> menus, IReadOnlyList<ShortcutDefinition> canvas) : base(L.T("键盘快捷键", "Keyboard Shortcuts"), L.T("存储", "Save"))
    {
        _definitions = menus.Concat(canvas).ToList();
        _draft = new Dictionary<string, Chord>();
        foreach (var definition in _definitions)
            if (Shortcuts.OverrideFor(definition.Id) is { } chord) _draft[definition.Id] = chord;

        Body.Children.Add(Note(L.T("单击一个快捷键，然后按下新的组合键（录制时按 Esc 取消）。右键单击可清除或恢复默认。存储后生效。",
            "Click a shortcut, then press its new key combination (Esc while recording cancels). Right-click to clear it or restore its default. Changes apply when you save.")));
        Row(L.T("搜索", "Search"), _search);
        _search.TextChanged += (_, _) => Filter();

        var list = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        AddGroup(list, L.T("菜单", "Menus"), menus);
        AddGroup(list, L.T("画布与工具", "Canvas & Tools"), canvas);
        var note = new TextBlock
        {
            Text = L.T("文本框保留 Windows 的标准编辑键。按住 Alt 在绘画工具中临时吸色，Shift 约束形状和移动、添加到选区，Alt 从选区减去或从中心绘制；空格临时切换抓手。这些修饰键与鼠标手势是固定的。",
                "Text fields keep the standard Windows editing keys. Alt temporarily selects the eyedropper in painting tools. Shift constrains shapes and movement or adds to a selection; Alt subtracts from a selection or draws from the center. Space temporarily switches to the Hand Tool. These modifier keys and mouse gestures are fixed."),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0),
        };
        note.SetResourceReference(TextBlock.ForegroundProperty, "TextDimBrush");
        list.Children.Add(note);
        Body.Children.Add(new ScrollViewer { Content = list, Height = 460, Width = 620, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        _problem = new TextBlock { Foreground = Brushes.Orange, TextWrapping = TextWrapping.Wrap, MaxWidth = 620, Margin = new Thickness(0, 8, 0, 0) };
        Body.Children.Add(_problem);
        var restore = new Button { Content = L.T("恢复默认", "Restore Defaults"), MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        restore.Click += (_, _) => { _recording = null; _draft.Clear(); Refresh(); };
        Body.Children.Add(restore);
        Refresh();
    }

    private void AddGroup(StackPanel list, string title, IReadOnlyList<ShortcutDefinition> definitions)
    {
        list.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 6) });
        foreach (var definition in definitions)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            var name = new TextBlock { Text = definition.Title, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            var recorder = new Button { MinHeight = 24, Focusable = true };
            // Keys, not characters: an input method must not take them first.
            InputMethod.SetIsInputMethodEnabled(recorder, false);
            Grid.SetColumn(recorder, 1);
            grid.Children.Add(name);
            grid.Children.Add(recorder);
            var d = definition;
            recorder.Click += (_, _) => { _recording = d.Id; Refresh(); recorder.Focus(); };
            var menu = new ContextMenu();
            var clear = new MenuItem { Header = L.T("清除", "Clear") };
            clear.Click += (_, _) => { _draft[d.Id] = Chord.None; _recording = null; Refresh(); };
            var reset = new MenuItem { Header = L.T("恢复默认", "Restore Default") };
            reset.Click += (_, _) => { _draft.Remove(d.Id); _recording = null; Refresh(); };
            menu.Items.Add(clear);
            menu.Items.Add(reset);
            recorder.ContextMenu = menu;
            list.Children.Add(grid);
            _rows.Add((definition, grid, recorder));
        }
    }

    private Chord Current(ShortcutDefinition definition) => _draft.TryGetValue(definition.Id, out var chord) ? chord : definition.Original;

    private void Refresh()
    {
        foreach (var (definition, _, recorder) in _rows)
        {
            bool recording = _recording == definition.Id;
            recorder.Content = recording ? L.T("请按下组合键…", "Press Keys…") : Current(definition).Label;
            recorder.FontWeight = Current(definition) != definition.Original ? FontWeights.SemiBold : FontWeights.Normal;
            if (recording) recorder.SetResourceReference(BackgroundProperty, "SelectionBrush");
            else recorder.ClearValue(BackgroundProperty);
        }
        var problem = Shortcuts.Problem(_definitions, _draft);
        _problem.Text = problem ?? "";
        _problem.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        OkButton.IsEnabled = problem is null && _recording is null;
        Filter();
    }

    private void Filter()
    {
        string query = _search.Text.Trim();
        foreach (var (definition, row, _) in _rows)
            row.Visibility = query.Length == 0 || definition.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                             || Current(definition).Label.Contains(query, StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible : Visibility.Collapsed;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_recording is null) { base.OnPreviewKeyDown(e); return; }
        // Recording: the key is the new shortcut, never the dialog's own Enter or Esc.
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin
            or Key.ImeProcessed or Key.None) return;
        var modifiers = Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift);
        if (key == Key.Escape && modifiers == ModifierKeys.None) { _recording = null; Refresh(); return; }
        _draft[_recording] = new Chord(key, modifiers);
        _recording = null;
        Refresh();
    }

    protected override bool Validate() => _recording is null && Shortcuts.Problem(_definitions, _draft) is null;
}
