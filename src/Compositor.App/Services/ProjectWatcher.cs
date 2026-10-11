using System.IO;
using System.Windows.Threading;

namespace Compositor.App.Services;

/// <summary>
/// Tells its owner when a project package changes on disk, whoever changed it: another app, an agent, a sync client,
/// a git checkout. A package is replaced by renaming a finished copy over it, so the folder holding it is watched (for
/// anything under the package's path). Events are coalesced, and the handler runs on the interface thread. Mirrors
/// ProjectWatcher.swift.
/// </summary>
public sealed class ProjectWatcher : IDisposable
{
    /// <summary>How long to wait after the last event before reporting, so a save that touches several files reports once.</summary>
    private static readonly TimeSpan Coalescing = TimeSpan.FromMilliseconds(300);

    public string Path { get; }
    private readonly FileSystemWatcher? _watcher;
    private readonly DispatcherTimer _timer;
    private readonly Dispatcher _dispatcher;

    public ProjectWatcher(string path, Action onChange, Dispatcher dispatcher)
    {
        Path = System.IO.Path.GetFullPath(path).TrimEnd('\\', '/');
        _dispatcher = dispatcher;
        _timer = new DispatcherTimer(Coalescing, DispatcherPriority.Background, (_, _) => { _timer!.Stop(); onChange(); }, dispatcher);
        _timer.Stop();
        var parent = System.IO.Path.GetDirectoryName(Path);
        if (parent is null || !Directory.Exists(parent)) return;
        try
        {
            _watcher = new FileSystemWatcher(parent)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            _watcher.Changed += OnEvent;
            _watcher.Created += OnEvent;
            _watcher.Deleted += OnEvent;
            _watcher.Renamed += (s, e) => { if (Concerns(e.OldFullPath) || Concerns(e.FullPath)) Note(); };
            // Too many events at once: something changed; check.
            _watcher.Error += (_, _) => Note();
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception) { _watcher = null; }
    }

    private bool Concerns(string path) =>
        path.Equals(Path, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(Path + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private void OnEvent(object sender, FileSystemEventArgs e)
    {
        if (Concerns(e.FullPath)) Note();
    }

    private void Note() => _dispatcher.BeginInvoke(() => { _timer.Stop(); _timer.Start(); });

    public void Dispose()
    {
        _timer.Stop();
        if (_watcher is null) return;
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
    }
}
