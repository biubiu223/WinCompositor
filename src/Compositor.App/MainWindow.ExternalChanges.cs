using System.IO;
using System.Windows;
using Compositor.App.Dialogs;
using Compositor.App.Services;
using Compositor.Core.Editing;
using Compositor.Core.IO;
using Compositor.Core.Localization;

namespace Compositor.App;

/// <summary>
/// Keeps an open project in step with its package on disk. When something else writes the package, the document is
/// reloaded in place: same tab, same view, same layer selection where the layers still exist. Only a real change in
/// content counts; a package merely touched, or caught half written, is left alone. Unsaved work is never replaced
/// without asking. Mirrors ProjectController+ExternalChanges.swift.
/// </summary>
public partial class MainWindow
{
    /// <summary>One tab's watch: what the package looked like when last read or written, and whether a check is running or waiting.</summary>
    private sealed class ExternalChangeState
    {
        public ProjectWatcher? Watcher;
        public string? KnownDigest;
        public bool Pending, Checking, Saving;
        public int RecheckAttempt;
    }

    private readonly Dictionary<EditorSession, ExternalChangeState> _external = new();

    private ExternalChangeState External(EditorSession session)
    {
        if (!_external.TryGetValue(session, out var state)) _external[session] = state = new ExternalChangeState();
        return state;
    }

    /// <summary>Starts (or restarts) watching the project a tab has open. Called after an open and after every save.</summary>
    private void WatchProject(EditorSession session)
    {
        if (session.ProjectPath is not { } path) { StopWatchingProject(session); return; }
        var state = External(session);
        string full = Path.GetFullPath(path).TrimEnd('\\', '/');
        if (!string.Equals(state.Watcher?.Path, full, StringComparison.OrdinalIgnoreCase))
        {
            state.Watcher?.Dispose();
            state.Watcher = new ProjectWatcher(full, () => NoteExternalChange(session), Dispatcher);
        }
        state.KnownDigest = ProjectDigest.Compute(full);
    }

    private void StopWatchingProject(EditorSession session)
    {
        if (!_external.Remove(session, out var state)) return;
        state.Watcher?.Dispose();
    }

    /// <summary>A save of our own is about to write the package: its events are ours.</summary>
    private void BeginOwnSave(EditorSession session) => External(session).Saving = true;

    private void EndOwnSave(EditorSession session)
    {
        var state = External(session);
        state.Saving = false;
        state.Pending = false;
        WatchProject(session);
    }

    /// <summary>The tab came to the front (or the window did): a change that waited on unsaved work can be asked about now.</summary>
    private void ResumeExternalChangeCheck(EditorSession session)
    {
        if (_external.TryGetValue(session, out var state) && state.Pending) NoteExternalChange(session);
    }

    private void NoteExternalChange(EditorSession session)
    {
        var state = External(session);
        state.Pending = true;
        if (!state.Checking) _ = CheckExternalChange(session, state);
    }

    private async Task CheckExternalChange(EditorSession session, ExternalChangeState state)
    {
        state.Checking = true;
        try
        {
            while (state.Pending)
            {
                state.Pending = false;
                if (!_sessions.Contains(session) || session.ProjectPath is not { } path || session.Document is null || state.Saving) return;
                // Compare content, not modification dates: sync clients touch metadata without changing anything.
                var digest = await Task.Run(() => ProjectDigest.Compute(path));
                if (digest is null || digest == state.KnownDigest) continue;
                // Wait for an edit in progress to finish rather than pulling the document out from under it.
                if (!session.CanUseHistory || session.TransformEdit is not null || session.BrushStroke is not null)
                {
                    ScheduleRecheck(session, state);
                    return;
                }
                if (session.IsModified)
                {
                    if (!ReferenceEquals(session, _session) || !IsActive) { state.Pending = true; return; }
                    // Buttons named for what they do. Keeping the open document stays the default (Enter, Esc and the
                    // close box), since reloading throws the unsaved changes away.
                    string name = Path.GetFileName(path);
                    var answer = MessageDialog.Ask(this, L.T($"“{name}”已在磁盘上被更改", $"“{name}” was changed on disk."),
                        L.T("另一个程序更改了这个项目。可以重新载入磁盘上的版本（未存储的更改会丢失），或保留当前内容，继续编辑。",
                            "Another program changed this project. You can revert to the version on disk, losing your unsaved changes, or keep what you have and continue editing."),
                        MessageKind.Warning,
                        new MessageButton(L.T("重新载入", "Revert"), MessageBoxResult.Yes),
                        new MessageButton(L.T("保留当前内容", "Keep Mine"), MessageBoxResult.No) { IsDefault = true, IsCancel = true });
                    if (answer != MessageBoxResult.Yes) { state.KnownDigest = digest; continue; }
                }
                // A package that fails to load, half written or mid-sync, leaves the open document alone.
                ProjectSnapshot snapshot;
                try { snapshot = await Task.Run(() => ProjectStore.Load(path)); }
                catch (Exception) { continue; }
                if (session.ProjectPath != path || session.Document is null) return;
                state.RecheckAttempt = 0;
                session.ReloadProject(snapshot);
                // Remember the package as loaded: it may have changed again while the question was up.
                state.KnownDigest = await Task.Run(() => ProjectDigest.Compute(path));
                if (ReferenceEquals(session, _session)) RefreshAll();
                RebuildTabs();
            }
        }
        finally { state.Checking = false; }
    }

    /// <summary>Retries while the tab is busy, backing off so a long operation is not polled.</summary>
    private void ScheduleRecheck(EditorSession session, ExternalChangeState state)
    {
        state.Pending = true;
        int attempt = state.RecheckAttempt;
        state.RecheckAttempt = Math.Min(attempt + 1, 7);
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250 * (1 << attempt)) };
        timer.Tick += (_, _) => { timer.Stop(); NoteExternalChange(session); };
        timer.Start();
    }
}
