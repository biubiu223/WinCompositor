using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Compositor.Core.Editing;
using Compositor.Core.Model;

namespace Compositor.App.Drawing;

/// <summary>
/// Where the Type tool's typing goes. A tiny text box sits at the caret: it receives keys and input-method composition
/// (Pinyin and the like show there while being composed, with the candidate window beside the caret), and whatever it
/// receives is moved into the text at once, so the canvas — which draws the text as it will be — stays the only place
/// the text shows. Editing keys, the clipboard and undo within the text are handled here.
/// </summary>
internal sealed class TextInputSink
{
    private readonly CanvasView _view;
    private bool _composing;
    private bool _clearing;
    private int _anchor, _caret;
    private readonly Stack<(LayerText Style, int Anchor, int Caret)> _undo = new(), _redo = new();

    public Canvas Host { get; }
    public TextBox Box { get; }

    public TextInputSink(Canvas host, CanvasView view)
    {
        Host = host;
        _view = view;
        Box = new TextBox
        {
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            Padding = new Thickness(4, 0, 4, 0),
            MinWidth = 2,
            Height = double.NaN,
            AcceptsReturn = false,
            AcceptsTab = false,
            IsUndoEnabled = false,
            FontSize = 16,
            Visibility = Visibility.Collapsed,
            // The canvas draws the caret; the box shows one only while something is being composed in it.
            CaretBrush = Brushes.Transparent,
        };
        InputMethod.SetIsInputMethodEnabled(Box, true);
        host.Children.Add(Box);
        Box.PreviewKeyDown += OnKey;
        Box.TextChanged += OnTextChanged;
        Box.LostKeyboardFocus += (_, _) => _view.InvalidateVisual();
        Box.GotKeyboardFocus += (_, _) => _view.InvalidateVisual();
        TextCompositionManager.AddPreviewTextInputStartHandler(Box, (_, e) => _composing = e.TextComposition.CompositionText.Length > 0 || _composing);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(Box, (_, e) => _composing = e.TextComposition.CompositionText.Length > 0);
        // A finished composition: the text services have already put the result in the box; take it once they're done.
        TextCompositionManager.AddPreviewTextInputHandler(Box, (_, _) =>
        {
            _composing = false;
            Box.Dispatcher.BeginInvoke(Flush, System.Windows.Threading.DispatcherPriority.Input);
        });
    }

    private EditorSession? Session => _view.Session;
    private TextDraft? Draft => Session?.TextDraft;

    // MARK: Showing and hiding

    public void Activate()
    {
        if (Draft is not { } draft) return;
        if (Box.Visibility != Visibility.Visible)
        {
            _undo.Clear();
            _redo.Clear();
            _anchor = draft.SelectionStart;
            _caret = draft.SelectionStart + draft.SelectionLength;
            Box.Visibility = Visibility.Visible;
        }
        Place();
        if (!Box.IsKeyboardFocused) Box.Dispatcher.BeginInvoke(() => { if (Draft is not null) Keyboard.Focus(Box); });
    }

    public void Deactivate()
    {
        if (Box.Visibility == Visibility.Collapsed) return;
        bool focused = Box.IsKeyboardFocusWithin;
        // Focus leaves first, so the text services end any composition before the box goes.
        if (focused) _view.Focus();
        _composing = false;
        _taken = "";
        Clear();
        Box.Visibility = Visibility.Collapsed;
        _undo.Clear();
        _redo.Clear();
        if (focused) _view.Focus();
    }

    /// <summary>
    /// Puts the box at the caret, so composition shows where the letters will go. Nothing about the box changes while
    /// an input method is composing in it: the text services keep their own picture of it, and changing it under them
    /// can crash them.
    /// </summary>
    public void Place()
    {
        if (_composing || Box.Text.Length > 0) return;
        if (_view.CaretOnScreen() is not { } caret || Draft is null) return;
        double size = Math.Clamp(caret.Height * 0.8, 12, 48);
        if (Math.Abs(Box.FontSize - size) > 0.5) Box.FontSize = size;
        Canvas.SetLeft(Box, caret.Position.X);
        Canvas.SetTop(Box, caret.Position.Y + Math.Max(0, caret.Height - size * 1.35));
    }

    private void Clear()
    {
        _taken = "";
        if (Box.Text.Length == 0) return;
        _clearing = true;
        Box.Text = "";
        _clearing = false;
    }

    /// <summary>Keys that type something (handled by the box itself), as opposed to editing keys handled here.</summary>
    private static bool IsLetterKey(KeyEventArgs e) =>
        !Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
        && e.Key is not (Key.Back or Key.Delete or Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End
            or Key.Enter or Key.Escape or Key.Tab or Key.PageUp or Key.PageDown);

    /// <summary>Empties the box once the input has finished with it (never from inside its own change notification).</summary>
    private void ClearSoon() => Box.Dispatcher.BeginInvoke(() =>
    {
        if (_composing) return;
        Clear();
        Box.Background = Brushes.Transparent;
        Box.CaretBrush = Brushes.Transparent;
        Place();
    }, System.Windows.Threading.DispatcherPriority.Background);

    // MARK: Editing

    private (int Start, int Length) Range => (Math.Min(_anchor, _caret), Math.Abs(_caret - _anchor));

    /// <summary>Selects from <paramref name="anchor"/> to <paramref name="caret"/> (equal for a caret).</summary>
    public void Select(int anchor, int caret)
    {
        if (Draft is not { } draft) return;
        int length = draft.Style.Content.Length;
        _anchor = Math.Clamp(anchor, 0, length);
        _caret = Math.Clamp(caret, 0, length);
        var (start, count) = Range;
        Session!.SetTextSelection(start, count);
        Place();
    }

    private void Remember()
    {
        if (Draft is not { } draft) return;
        _undo.Push((draft.Style, _anchor, _caret));
        _redo.Clear();
    }

    private void Replace(string text)
    {
        if (Draft is null) return;
        var (start, length) = Range;
        if (length == 0 && text.Length == 0) return;
        Remember();
        Session!.ReplaceText(start, length, text);
        Select(start + text.Length, start + text.Length);
    }

    private string _taken = "";

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_clearing) return;
        if (Box.Text.Length == 0)
        {
            // Emptied: a composition was cancelled (Escape in the input method), or the box was cleared.
            _composing = false;
            _taken = "";
            Box.Background = Brushes.Transparent;
            Box.CaretBrush = Brushes.Transparent;
            return;
        }
        // Show what is being composed against a dark backing so it reads over any picture.
        Box.Background = new SolidColorBrush(Color.FromArgb(200, 32, 32, 32));
        Box.CaretBrush = Brushes.White;
        if (!_composing) Flush();
    }

    /// <summary>Letters typed or a finished composition go into the text; the box is emptied once input is done with it.</summary>
    private void Flush()
    {
        if (_composing || Box.Text.Length == 0) return;
        var text = Box.Text;
        string fresh = text.StartsWith(_taken, StringComparison.Ordinal) ? text[_taken.Length..] : text;
        _taken = text;
        if (fresh.Length > 0) Replace(fresh.Replace("\r\n", "\n").Replace('\r', '\n'));
        ClearSoon();
    }

    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';

    private int Step(string content, int index, int direction, bool word)
    {
        if (direction < 0)
        {
            if (index <= 0) return 0;
            int i = index - 1;
            if (i > 0 && char.IsLowSurrogate(content[i]) && char.IsHighSurrogate(content[i - 1])) i--;
            if (!word) return i;
            while (i > 0 && !IsWord(content[i])) i--;
            while (i > 0 && IsWord(content[i - 1])) i--;
            return i;
        }
        if (index >= content.Length) return content.Length;
        int j = index + 1;
        if (j < content.Length && char.IsLowSurrogate(content[j]) && char.IsHighSurrogate(content[j - 1])) j++;
        if (!word) return j;
        while (j < content.Length && IsWord(content[j])) j++;
        while (j < content.Length && !IsWord(content[j]) && content[j] != '\n') j++;
        return j;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (_composing || Draft is not { } draft || Session is not { } session) return;
        // Letters already taken into the text may still sit in the box: empty it before a key acts on the text.
        if (Box.Text.Length > 0 && !IsLetterKey(e)) { Clear(); Box.Background = Brushes.Transparent; }
        var content = draft.Style.Content;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control), shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var (start, length) = Range;
        void Move(int to) { if (shift) Select(_anchor, to); else Select(to, to); }
        switch (key)
        {
            case Key.Escape:
                // Escape applies the text, as Photoshop's "Esc commits" does.
                session.CommitText();
                break;
            case Key.Enter when ctrl:
                session.CommitText();
                break;
            case Key.Enter:
                Replace("\n");
                break;
            case Key.Tab:
                Replace("\t");
                break;
            case Key.Back:
                if (length > 0) Replace("");
                else if (start > 0) { _anchor = Step(content, start, -1, ctrl); Replace(""); }
                break;
            case Key.Delete:
                if (length > 0) Replace("");
                else if (start < content.Length) { _anchor = start; _caret = Step(content, start, 1, ctrl); Replace(""); }
                break;
            case Key.Left:
                Move(length > 0 && !shift ? start : Step(content, _caret, -1, ctrl));
                break;
            case Key.Right:
                Move(length > 0 && !shift ? start + length : Step(content, _caret, 1, ctrl));
                break;
            case Key.Up:
                Move(draft.Layout.Vertical(_caret, -1));
                break;
            case Key.Down:
                Move(draft.Layout.Vertical(_caret, 1));
                break;
            case Key.Home:
                Move(ctrl ? 0 : draft.Layout.Lines.FirstOrDefault(l => _caret >= l.Start && _caret <= l.End)?.Start ?? 0);
                break;
            case Key.End:
                Move(ctrl ? content.Length : draft.Layout.Lines.FirstOrDefault(l => _caret >= l.Start && _caret <= l.End)?.End ?? content.Length);
                break;
            case Key.A when ctrl:
                Select(0, content.Length);
                break;
            case Key.C when ctrl:
            case Key.X when ctrl:
                if (length > 0)
                {
                    try { Clipboard.SetText(content.Substring(start, length)); } catch { /* clipboard busy */ }
                    if (key == Key.X) Replace("");
                }
                break;
            case Key.V when ctrl:
                try
                {
                    if (Clipboard.ContainsText()) Replace(Clipboard.GetText().Replace("\r\n", "\n").Replace('\r', '\n'));
                }
                catch { /* clipboard busy */ }
                break;
            case Key.Z when ctrl && !shift:
                if (_undo.Count > 0)
                {
                    var (style, anchor, caret) = _undo.Pop();
                    _redo.Push((draft.Style, _anchor, _caret));
                    session.ChangeTextStyle((_, _, _) => style);
                    Select(anchor, caret);
                }
                break;
            case Key.Z when ctrl && shift:
            case Key.Y when ctrl:
                if (_redo.Count > 0)
                {
                    var (style, anchor, caret) = _redo.Pop();
                    _undo.Push((draft.Style, _anchor, _caret));
                    session.ChangeTextStyle((_, _, _) => style);
                    Select(anchor, caret);
                }
                break;
            default:
                return; // a letter: it arrives through the box
        }
        e.Handled = true;
    }
}
