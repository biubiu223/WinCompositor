using System.Windows.Input;
using Compositor.Core.Localization;

namespace Compositor.App.Services;

/// <summary>A key and its modifiers. <see cref="Key.None"/> means no shortcut.</summary>
public readonly record struct Chord(Key Key, ModifierKeys Modifiers)
{
    public static readonly Chord None = new(Key.None, ModifierKeys.None);
    public bool IsNone => Key == Key.None;

    public string Label
    {
        get
        {
            if (IsNone) return L.T("无", "None");
            var parts = new List<string>();
            if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            parts.Add(KeyName(Key));
            return string.Join("+", parts);
        }
    }

    public static string KeyName(Key key) => key switch
    {
        Key.OemPlus => "=",
        Key.OemMinus => "-",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.Oem1 => ";",
        Key.OemQuotes => "'",
        Key.OemQuestion => "/",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.Oem5 => "\\",
        Key.Oem3 => "`",
        Key.Add => "Num+",
        Key.Subtract => "Num-",
        Key.Back => "Backspace",
        Key.Delete => "Delete",
        Key.Return => "Enter",
        Key.Escape => "Esc",
        Key.Space => L.T("空格", "Space"),
        Key.Tab => "Tab",
        Key.Left => "←", Key.Right => "→", Key.Up => "↑", Key.Down => "↓",
        >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
        _ => key.ToString(),
    };

    public string Serialize() => IsNone ? "" : $"{(int)Key}|{(int)Modifiers}";

    public static Chord? Parse(string? text)
    {
        if (text is null) return null;
        if (text.Length == 0) return None;
        var parts = text.Split('|');
        return parts.Length == 2 && int.TryParse(parts[0], out var k) && int.TryParse(parts[1], out var m)
            ? new Chord((Key)k, (ModifierKeys)m & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift)) : null;
    }
}

public enum ShortcutGroup { Menus, Canvas }

/// <summary>One shortcut that can be changed: a menu command or a key the canvas answers. Mirrors ShortcutDefinition.</summary>
public sealed record ShortcutDefinition(string Id, string Title, ShortcutGroup Group, Chord Original);

/// <summary>
/// Keyboard Shortcuts: the defaults, the person's changes, and the translation that lets a changed canvas key reach
/// the code written for its default. Mirrors ShortcutSettings in KeyboardShortcuts.swift.
/// </summary>
public static class Shortcuts
{
    /// <summary>Keys the canvas answers (tools, brush keys and the like), with their defaults.</summary>
    public static readonly IReadOnlyList<ShortcutDefinition> CanvasKeys = BuildCanvasKeys();

    private static List<ShortcutDefinition> BuildCanvasKeys()
    {
        var result = new List<ShortcutDefinition>();
        // The id is the default key, never the title, so a changed shortcut is found again in either language.
        void Add(string title, Key key, ModifierKeys modifiers = ModifierKeys.None) =>
            result.Add(new ShortcutDefinition($"canvas:{key}:{(int)modifiers}", title, ShortcutGroup.Canvas, new Chord(key, modifiers)));
        Add(L.T("移动工具", "Move Tool"), Key.V);
        Add(L.T("选框工具", "Marquee Tool"), Key.M);
        Add(L.T("切换选框形状", "Cycle Marquee Shape"), Key.M, ModifierKeys.Shift);
        Add(L.T("套索工具", "Lasso Tool"), Key.L);
        Add(L.T("切换套索方式", "Cycle Lasso Mode"), Key.L, ModifierKeys.Shift);
        Add(L.T("魔棒工具", "Magic Wand Tool"), Key.W);
        Add(L.T("裁剪工具", "Crop Tool"), Key.C);
        Add(L.T("画笔工具", "Brush Tool"), Key.B);
        Add(L.T("橡皮擦", "Eraser Tool"), Key.E);
        Add(L.T("污点修复画笔", "Spot Healing Brush Tool"), Key.J);
        Add(L.T("抹除工具", "Remove Tool"), Key.J, ModifierKeys.Shift);
        Add(L.T("仿制图章", "Clone Stamp Tool"), Key.S);
        Add(L.T("模糊 / 涂抹 / 液化", "Blur / Smudge / Liquify"), Key.R);
        Add(L.T("渐变工具", "Gradient Tool"), Key.G);
        Add(L.T("形状工具", "Shape Tool"), Key.U);
        Add(L.T("切换形状种类", "Cycle Shape Kind"), Key.U, ModifierKeys.Shift);
        Add(L.T("文字工具", "Type Tool"), Key.T);
        Add(L.T("吸管工具", "Eyedropper Tool"), Key.I);
        Add(L.T("抓手工具", "Hand Tool"), Key.H);
        Add(L.T("缩放工具", "Zoom Tool"), Key.Z);
        Add(L.T("无工具", "No Tool"), Key.A);
        Add(L.T("交换前景色和背景色", "Switch Foreground and Background Colors"), Key.X);
        Add(L.T("默认前景色和背景色", "Default Foreground and Background Colors"), Key.D);
        Add(L.T("全屏（仅画布）", "Full Screen (Canvas Only)"), Key.F);
        Add(L.T("切换工具模式", "Cycle Tool Mode"), Key.Tab);
        Add(L.T("减小画笔", "Decrease Brush Size"), Key.OemOpenBrackets);
        Add(L.T("增大画笔", "Increase Brush Size"), Key.OemCloseBrackets);
        Add(L.T("降低画笔硬度", "Decrease Brush Hardness"), Key.OemOpenBrackets, ModifierKeys.Shift);
        Add(L.T("提高画笔硬度", "Increase Brush Hardness"), Key.OemCloseBrackets, ModifierKeys.Shift);
        Add(L.T("上一个混合模式", "Previous Blend Mode"), Key.OemMinus, ModifierKeys.Shift);
        Add(L.T("下一个混合模式", "Next Blend Mode"), Key.OemPlus, ModifierKeys.Shift);
        Add(L.T("删除选区像素 / 图层 / 套索点", "Delete Selected Pixels / Layer / Lasso Point"), Key.Delete);
        Add(L.T("应用当前操作", "Apply Current Operation"), Key.Return);
        Add(L.T("取消当前操作", "Cancel Current Operation"), Key.Escape);
        return result;
    }

    private static Dictionary<string, Chord> _overrides = Load();

    private static Dictionary<string, Chord> Load()
    {
        var result = new Dictionary<string, Chord>();
        foreach (var (id, text) in Settings.Current.Shortcuts)
            if (Chord.Parse(text) is { } chord) result[id] = chord;
        return result;
    }

    public static IReadOnlyDictionary<string, Chord> Overrides => _overrides;

    public static Chord Effective(ShortcutDefinition definition) =>
        _overrides.TryGetValue(definition.Id, out var chord) ? chord : definition.Original;

    public static Chord? OverrideFor(string id) => _overrides.TryGetValue(id, out var chord) ? chord : null;

    /// <summary>Why a set of changes can't be saved, or null when it can.</summary>
    public static string? Problem(IReadOnlyList<ShortcutDefinition> definitions, IReadOnlyDictionary<string, Chord> values)
    {
        var assigned = new Dictionary<Chord, string>();
        foreach (var definition in definitions)
        {
            var chord = values.TryGetValue(definition.Id, out var c) ? c : definition.Original;
            if (chord.IsNone) continue;
            if (chord == new Chord(Key.F4, ModifierKeys.Alt) || chord.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.System)
                return L.T($"{chord.Label} 由 Windows 保留，不能使用。", $"{chord.Label} is reserved by Windows.");
            if (chord.Key == Key.Space)
                return L.T("空格键用于临时抓手工具，不能分配。", "Space is reserved for the temporary Hand Tool and can’t be assigned.");
            if (assigned.TryGetValue(chord, out var other))
                return L.T($"{chord.Label} 同时分配给了“{other}”和“{definition.Title}”。", $"{chord.Label} is assigned to both “{other}” and “{definition.Title}”.");
            assigned[chord] = definition.Title;
        }
        return null;
    }

    public static void Save(Dictionary<string, Chord> values)
    {
        _overrides = new Dictionary<string, Chord>(values);
        Settings.Current.Shortcuts = values.ToDictionary(p => p.Key, p => p.Value.Serialize());
        Settings.Current.Save();
    }

    /// <summary>
    /// A key pressed on the canvas, as the canvas code expects it: a changed key becomes its default, and a default
    /// that was moved elsewhere does nothing. Null means swallow the key.
    /// </summary>
    public static Chord? TranslateCanvas(Chord input)
    {
        if (_overrides.Count == 0) return input;
        foreach (var definition in CanvasKeys)
            if (Effective(definition) == input) return definition.Original;
        foreach (var definition in CanvasKeys)
            if (definition.Original == input && Effective(definition) != input) return null;
        return input;
    }
}
