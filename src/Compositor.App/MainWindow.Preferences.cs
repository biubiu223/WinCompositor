using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using Compositor.App.Dialogs;
using Compositor.Core.Localization;

namespace Compositor.App;

/// <summary>Edit ▸ Preferences: the program's own settings (the interface language first of all).</summary>
public partial class MainWindow
{
    /// <summary>
    /// Opens the Preferences window and keeps what was chosen. A new interface language is stored at once but only shows
    /// from the next start, as in Photoshop, so Compositor offers to restart — when the language would really change:
    /// switching to Use Windows Language on an English Windows leaves an English interface English.
    /// </summary>
    private void ShowPreferences()
    {
        var settings = Services.Settings.Current;
        var dialog = new PreferencesWindow(this, settings.Language);
        if (dialog.ShowDialog() != true) return;
        string chosen = dialog.InterfaceLanguage;
        if (chosen == Services.Settings.NormalizeLanguage(settings.Language)) return;
        settings.Language = chosen;
        settings.Save();
        if (Services.Settings.ResolveLanguage(chosen) == L.Current) return;

        // Asked in the language in use now; the save prompts a restart brings are mentioned only when there will be some.
        bool unsaved = _sessions.Any(s => s.IsModified && s.Document is not null);
        var answer = MessageDialog.Ask(this,
            L.T("界面语言将在重启 WinCompositor 后切换", "The new language takes effect after WinCompositor restarts"),
            unsaved ? L.T("重启前会先询问是否存储未存储的更改。", "You’ll be asked whether to save your changes before it restarts.") : null,
            MessageKind.Information,
            new MessageButton(L.T("稍后", "Later"), MessageBoxResult.Cancel) { IsCancel = true },
            new MessageButton(L.T("立即重启", "Restart Now"), MessageBoxResult.OK) { IsDefault = true });
        if (answer == MessageBoxResult.OK) Restart();
    }

    /// <summary>
    /// Closes the window the usual way — every unsaved project asks first — and starts Compositor again only if it
    /// really closed. Cancel at any of those prompts (or a save that doesn't go through) and the window stays open with
    /// nothing left behind: the handler is gone again, so a later ordinary close doesn't restart.
    /// </summary>
    private void Restart()
    {
        bool closed = false;
        EventHandler onClosed = (_, _) => closed = true;
        Closed += onClosed;
        // Close asks about unsaved work (OnClosing) and, unless that is cancelled, destroys the window and raises
        // Closed before it returns; the application's own shutdown follows only after this command has finished.
        try { Close(); }
        catch (InvalidOperationException) { /* Already closing: that close goes on as it was, without a restart. */ }
        finally { Closed -= onClosed; }
        if (closed) StartAgain();
    }

    /// <summary>Starts a new Compositor, without arguments: it opens as at any start, in the language now chosen.</summary>
    private static void StartAgain()
    {
        try
        {
            var program = Environment.ProcessPath;
            if (string.IsNullOrEmpty(program)) throw new InvalidOperationException("The program's path is unknown.");
            var start = new ProcessStartInfo(program) { UseShellExecute = true };
            // Run as "dotnet WinCompositor.dll" rather than as WinCompositor.exe, the host needs to be told the program again.
            if (Path.GetFileNameWithoutExtension(program).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                && Assembly.GetEntryAssembly()?.Location is { Length: > 0 } entry)
                start.Arguments = "\"" + entry + "\"";
            Process.Start(start);
        }
        catch (Exception error)
        {
            try { File.AppendAllText(App.LogPath, $"[{DateTime.Now:O}] Restart failed: {error}\n\n"); } catch { }
            // The window is gone and the application is on its way out, which would take a window of its own with it:
            // the system box is the one that stays until it has been read.
            MessageBox.Show(
                L.T("WinCompositor 没能重新启动，请手动再打开它。", "WinCompositor couldn’t restart. Please open it again.") + "\n\n" + error.Message,
                "WinCompositor", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
