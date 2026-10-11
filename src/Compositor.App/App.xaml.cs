using System.Windows;
using System.Windows.Threading;
using Compositor.App.Dialogs;
using Compositor.App.Services;
using Compositor.Core.IO;
using Compositor.Core.Localization;
using SkiaSharp;

namespace Compositor.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // The interface language, settled before any window, dialog or static list of names is built: L.T picks its
        // text as each one is made. Edit ▸ Preferences changes it for the next start.
        L.Current = Settings.ResolveLanguage(Settings.Current.Language);
        // Mipmaps of large layers are kept between frames; the default 32 MB cache would rebuild them constantly.
        SKGraphics.SetResourceCacheTotalByteLimit(768L * 1024 * 1024);
        ImageImporter.FallbackDecoder = Bitmaps.DecodeWithWindows;
        DispatcherUnhandledException += OnUnhandled;
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        if (e.Args.Length > 0) window.OpenPaths(e.Args);
    }

    public static string LogPath => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Compositor-errors.log");

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try { System.IO.File.AppendAllText(LogPath, $"[{DateTime.Now:O}] {e.Exception}\n\n"); } catch { }
        // Keep the work: report the problem and carry on rather than closing with unsaved projects.
        // The caption already reads Compositor, so what happened is the heading and the exception's text the details.
        MessageDialog.Show(MainWindow, e.Exception.Message,
            L.T("出现了意外错误，操作没有完成", "An unexpected error occurred, and the operation wasn’t completed"), MessageKind.Error);
        e.Handled = true;
    }
}
