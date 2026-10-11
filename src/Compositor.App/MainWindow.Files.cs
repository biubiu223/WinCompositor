using System.IO;
using System.Windows;
using System.Windows.Input;
using Compositor.App.Dialogs;
using Compositor.Core.Editing;
using Compositor.Core.Geometry;
using Compositor.Core.IO;
using Compositor.Core.Localization;
using Compositor.Core.Model;
using Compositor.Core.Rendering;
using Microsoft.Win32;
using SkiaSharp;

namespace Compositor.App;

public partial class MainWindow
{
    private const string ImagePatterns = "*.png;*.jpg;*.jpeg;*.jpe;*.jfif;*.webp;*.gif;*.bmp;*.tif;*.tiff;*.heic;*.heif;*.avif;*.svg;*.svgz";
    private const string RawPatterns = "*.dng;*.cr2;*.cr3;*.nef;*.nrw;*.arw;*.raf;*.orf;*.rw2;*.pef;*.srw";

    private static string ImageFilter =>
        L.T("图像文件", "Image Files") + "|" + ImagePatterns + "|" + L.T("相机 RAW", "Camera Raw Files") + "|" + RawPatterns + "|"
        + L.T("Photoshop 文件", "Photoshop Files") + "|*.psd;*.psb|" + L.T("所有文件", "All Files") + "|*.*";

    /// <summary>File › New: the New Canvas dialog, into an empty tab or a new one.</summary>
    public void NewCanvas()
    {
        var dialog = new NewCanvasDialog { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var session = SessionForNewDocument();
        session.CreateDocument(dialog.CanvasWidth, dialog.CanvasHeight, dialog.Resolution, dialog.BackgroundColor);
        RefreshAll();
        RebuildTabs();
        Canvas.Focus();
    }

    /// <summary>Ctrl+O: an image (opened as a new canvas) or a project's manifest.json.</summary>
    public void OpenDialog()
    {
        var dialog = new OpenFileDialog
        {
            Title = L.T("打开", "Open"),
            Filter = L.T("可打开的文件", "All Supported Files") + "|" + ImagePatterns + ";*.psd;*.psb;" + RawPatterns + ";manifest.json|"
                     + L.T("Compositor 项目 (manifest.json)", "Compositor Project (manifest.json)") + "|manifest.json|" + ImageFilter,
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        OpenPaths(dialog.FileNames, asDocuments: true);
    }

    /// <summary>A .comp project folder (Windows shows the package as a folder).</summary>
    public void OpenProjectDialog()
    {
        var dialog = new OpenFolderDialog { Title = L.T("打开 Compositor 项目（选择 .comp 文件夹）", "Open Compositor Project (Choose the .comp Folder)") };
        if (dialog.ShowDialog(this) != true) return;
        OpenPaths(new[] { dialog.FolderName });
    }

    /// <summary>
    /// Opens what was dropped or chosen: projects in tabs of their own, images as layers of the current canvas (or as
    /// new canvases with <paramref name="asDocuments"/>, or when there is none).
    /// </summary>
    public void OpenPaths(IEnumerable<string> paths, PointD? at = null, bool asDocuments = false)
    {
        var images = new List<string>();
        foreach (var raw in paths)
        {
            var path = raw.TrimEnd('\\', '/');
            if (string.Equals(Path.GetFileName(path), ProjectManifest.ManifestName, StringComparison.OrdinalIgnoreCase))
                path = Path.GetDirectoryName(path)!;
            if (ImageImporter.IsProject(path)) OpenProject(path);
            else if (File.Exists(path) && (Core.IO.Psd.PsdReader.IsPsdPath(path) || Core.IO.Psd.PsdReader.Matches(path))) OpenPhotoshopFile(path);
            else if (File.Exists(path) && ImageImporter.RawExtensions.Contains(Path.GetExtension(path).ToLowerInvariant())) DevelopRaw(path, at, asDocuments);
            else if (File.Exists(path)) images.Add(path);
        }
        if (images.Count == 0) return;
        if (asDocuments)
        {
            foreach (var image in images)
            {
                var session = SessionForNewDocument();
                Report(session.ImportImages(new[] { image }));
                if (session.Document is not null) session.ProjectPath = null;
            }
        }
        else
        {
            if (_session.Document is null) at = null;
            Report(_session.ImportImages(images, at));
        }
        RefreshAll();
        RebuildTabs();
        Canvas.Focus();
    }

    private void Report(List<string> failures)
    {
        if (failures.Count > 0)
            MessageDialog.Show(this, string.Join("\n\n", failures), L.T("导入未完成", "Import couldn’t finish"), MessageKind.Warning);
    }

    public void OpenProject(string path)
    {
        // Already open: just show it.
        var existing = _sessions.FirstOrDefault(s => s.ProjectPath is { } p && string.Equals(Path.GetFullPath(p), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
        if (existing is not null) { SelectSession(existing); return; }
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            var snapshot = ProjectStore.Load(path);
            var session = SessionForNewDocument();
            session.InstallProject(snapshot, path);
            WatchProject(session);
            Services.Settings.Current.AddRecent(path);
            BuildMenu();
            RefreshAll();
            RebuildTabs();
        }
        catch (Exception error)
        {
            MessageDialog.Show(this, error.Message, L.T("无法打开项目", "Couldn’t open the project"), MessageKind.Warning);
        }
        finally { Mouse.OverrideCursor = null; }
        Canvas.Focus();
    }

    /// <summary>A camera RAW: developed in its sheet, then imported as a layer (or a new canvas).</summary>
    public async void DevelopRaw(string path, PointD? at, bool asDocument)
    {
        (ushort[] Pixels, int Width, int Height)? preview;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            preview = await Task.Run(() => Services.Bitmaps.DecodeWide(path, RawDevelopDialog.PreviewLimit));
        }
        finally { Mouse.OverrideCursor = null; }
        if (preview is not { } small)
        {
            MessageDialog.Show(this, ImageImportException.RawCodecMissing().Message, L.T("无法打开相机 RAW", "Couldn’t open the camera raw file"), MessageKind.Warning);
            return;
        }
        var dialog = new RawDevelopDialog(path, small) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var settings = dialog.Settings;
        RasterImage image;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            long budget = DocumentLimits.DocumentPixelBudget - (asDocument ? 0 : _session.Document?.Layers.Sum(l => l.Asset?.PixelCount ?? 0) ?? 0);
            image = await Task.Run(() =>
            {
                var full = Services.Bitmaps.DecodeWide(path) ?? throw ImageImportException.RawCodecMissing();
                if ((long)full.Width * full.Height > budget || full.Width > DocumentLimits.MaxSide || full.Height > DocumentLimits.MaxSide)
                    throw ImageImportException.TooLarge();
                var bytes = RawDevelop.Apply(full.Pixels, full.Width, full.Height, settings);
                return RawDevelop.ToImage(bytes, full.Width, full.Height, Path.GetFileNameWithoutExtension(path));
            });
        }
        catch (Exception error)
        {
            Mouse.OverrideCursor = null;
            MessageDialog.Show(this, error.Message, L.T("无法导入相机 RAW", "Couldn’t import the camera raw file"), MessageKind.Warning);
            return;
        }
        finally { Mouse.OverrideCursor = null; }
        var session = asDocument || _session.Document is null ? SessionForNewDocument() : _session;
        session.ImportDecoded(image, session.Document is null ? null : at);
        RefreshAll();
        RebuildTabs();
        Canvas.Focus();
    }

    /// <summary>A Photoshop file opens as a new, untitled document, after listing what it converts.</summary>
    public async void OpenPhotoshopFile(string path)
    {
        Core.IO.Psd.PsdImport import;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            long budget = DocumentLimits.DocumentPixelBudget;
            import = await Task.Run(() => Core.IO.Psd.PsdImporter.Read(path, budget));
        }
        catch (Exception error)
        {
            Mouse.OverrideCursor = null;
            MessageDialog.Show(this, error.Message, L.T("无法打开 Photoshop 文件", "Couldn’t open the Photoshop file"), MessageKind.Warning);
            return;
        }
        finally { Mouse.OverrideCursor = null; }
        string name = Path.GetFileName(path);
        if (import.Conversions.Count > 0)
        {
            var dialog = new PsdConversionDialog(name, import.Conversions) { Owner = this };
            if (dialog.ShowDialog() != true) return;
        }
        var session = SessionForNewDocument();
        session.InstallDocument(import.Document, Path.GetFileNameWithoutExtension(path));
        RefreshAll();
        RebuildTabs();
        Canvas.Focus();
    }

    public void ImportImagesDialog()
    {
        var dialog = new OpenFileDialog { Title = L.T("导入图像", "Import Images"), Filter = ImageFilter, Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        OpenPaths(dialog.FileNames);
    }

    /// <summary>Saves a project (asking where for a new one, or with <paramref name="saveAs"/>). False when it didn't.</summary>
    public bool SaveSession(EditorSession session, bool saveAs)
    {
        session.CommitTransform();
        if (session.ProjectSnapshot() is not { } snapshot) return false;
        string? path = saveAs ? null : session.ProjectPath;
        if (path is null)
        {
            var dialog = new SaveFileDialog
            {
                Title = L.T("存储为", "Save As"),
                Filter = L.T("Compositor 项目 (*.comp)", "Compositor Project (*.comp)") + "|*.comp",
                // An untitled document is offered under its untitled name: the session's Title, in either language.
                FileName = session.Title + ".comp",
                AddExtension = true,
                DefaultExt = ".comp",
                OverwritePrompt = true,
            };
            if (dialog.ShowDialog(this) != true) return false;
            path = dialog.FileName;
            if (!path.EndsWith(ProjectStore.Extension, StringComparison.OrdinalIgnoreCase)) path += ProjectStore.Extension;
        }
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            var revision = session.History.CurrentRevision;
            using var preview = DocumentRenderer.RenderToImage(snapshot.Document, scale: Math.Min(1, 1024.0 / Math.Max(snapshot.Document.Width, snapshot.Document.Height)), background: SKColors.White);
            BeginOwnSave(session);
            try { ProjectStore.Save(snapshot, path, preview); }
            finally
            {
                if (File.Exists(Path.Combine(path, ProjectManifest.ManifestName))) session.ProjectPath = path;
                EndOwnSave(session);
            }
            session.ProjectPath = path;
            session.History.MarkSaved(revision);
            Services.Settings.Current.AddRecent(path);
            BuildMenu();
            RebuildTabs();
            return true;
        }
        catch (Exception error)
        {
            MessageDialog.Show(this, error.Message, L.T("无法存储项目", "Couldn’t save the project"), MessageKind.Warning);
            return false;
        }
        finally { Mouse.OverrideCursor = null; }
    }

    private ExportFormat _lastExportFormat = ExportFormat.Png;

    /// <summary>File › Export As…: PNG, JPEG or a one-page PDF, sized and previewed first.</summary>
    public void ExportAs()
    {
        _session.CommitTransform();
        if (_session.Document is not { } document) return;
        SkiaSharp.SKImage image;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            image = DocumentRenderer.RenderToImage(document with { Selection = null });
        }
        catch (Exception error)
        {
            MessageDialog.Show(this, error.Message, L.T("无法导出", "Couldn’t export"), MessageKind.Warning);
            return;
        }
        finally { Mouse.OverrideCursor = null; }
        using (image)
        {
            var sheet = new ExportAsDialog(image, document.Resolution, _lastExportFormat) { Owner = this };
            if (sheet.ShowDialog() != true || sheet.Data is not { } data) return;
            _lastExportFormat = sheet.Format;
            var (filter, extension) = sheet.Format switch
            {
                ExportFormat.Jpeg => (JpegFilter, ".jpg"),
                ExportFormat.Pdf => (L.T("PDF 文档 (*.pdf)", "PDF Document (*.pdf)") + "|*.pdf", ".pdf"),
                _ => (PngFilter, ".png"),
            };
            var dialog = new SaveFileDialog
            {
                Title = L.T("导出为", "Export As"),
                Filter = filter,
                FileName = _session.Title + extension,
                AddExtension = true,
                OverwritePrompt = true,
            };
            if (dialog.ShowDialog(this) != true) return;
            try { File.WriteAllBytes(dialog.FileName, data); }
            catch (Exception error) { MessageDialog.Show(this, error.Message, L.T("无法导出", "Couldn’t export"), MessageKind.Warning); }
        }
    }

    private static string JpegFilter => L.T("JPEG 图像 (*.jpg)", "JPEG Image (*.jpg)") + "|*.jpg";
    private static string PngFilter => L.T("PNG 图像 (*.png)", "PNG Image (*.png)") + "|*.png";

    /// <summary>File › Export: the flattened canvas as PNG (lossless, with transparency) or JPEG.</summary>
    public void Export(bool jpeg)
    {
        _session.CommitTransform();
        if (_session.Document is not { } document) return;
        int quality = Services.Settings.Current.JpegQuality;
        if (jpeg)
        {
            var options = new JpegDialog(quality) { Owner = this };
            if (options.ShowDialog() != true) return;
            quality = options.Quality;
            Services.Settings.Current.JpegQuality = quality;
            Services.Settings.Current.Save();
        }
        var dialog = new SaveFileDialog
        {
            Title = jpeg ? L.T("导出 JPEG", "Export JPEG") : L.T("导出 PNG", "Export PNG"),
            Filter = jpeg ? JpegFilter : PngFilter,
            FileName = _session.Title + (jpeg ? ".jpg" : ".png"),
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            using var image = DocumentRenderer.RenderToImage(document with { Selection = null });
            var bytes = jpeg ? ImageExporter.EncodeJpeg(image, quality, document.Resolution) : ImageExporter.EncodePng(image, document.Resolution);
            File.WriteAllBytes(dialog.FileName, bytes);
        }
        catch (Exception error)
        {
            MessageDialog.Show(this, error.Message, L.T("无法导出", "Couldn’t export"), MessageKind.Warning);
        }
        finally { Mouse.OverrideCursor = null; }
    }

    // MARK: Image dialogs

    public void CanvasSizeDialog()
    {
        if (_session.Document is not { } document) return;
        var dialog = new CanvasSizeDialog(document, _session.ForegroundColor, _session.BackgroundColor) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _session.ResizeCanvas(dialog.CanvasWidth, dialog.CanvasHeight, dialog.Anchor, dialog.Fill);
    }

    public void ImageSizeDialog()
    {
        if (_session.Document is not { } document) return;
        var dialog = new ImageSizeDialog(document) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        Mouse.OverrideCursor = Cursors.Wait;
        try { _session.ResizeImage(dialog.ImageWidth, dialog.ImageHeight, dialog.Resolution, dialog.Sampling); }
        finally { Mouse.OverrideCursor = null; }
    }

    /// <summary>Select ▸ Expand, Contract or Feather: the amount, in a dialog titled <paramref name="title"/>.</summary>
    public void SelectionAmountDialog(string title, int initial, int maximum, Action<int> apply)
    {
        // The field is named for what is applied (not read from the title, which is in either language).
        string label = apply.Method.Name switch
        {
            nameof(EditorSession.FeatherSelection) => L.T("羽化半径", "Feather Radius"),
            nameof(EditorSession.ContractSelection) => L.T("收缩量", "Contract By"),
            nameof(EditorSession.ExpandSelection) => L.T("扩展量", "Expand By"),
            _ => L.T("数量", "Amount"),
        };
        var dialog = new AmountDialog(title, L.T("像素", "pixels"), initial, 1, maximum, label) { Owner = this };
        if (dialog.ShowDialog() == true) apply(dialog.Value);
    }

    public void GridSettingsDialog()
    {
        var dialog = new GridDialog(_session.GridSpacing, _session.GridSubdivisions) { Owner = this };
        bool shown = _session.ShowsGrid;
        _session.ShowsGrid = true;
        dialog.Preview = (spacing, subdivisions) => { _session.GridSpacing = spacing; _session.GridSubdivisions = subdivisions; };
        int oldSpacing = _session.GridSpacing, oldSubdivisions = _session.GridSubdivisions;
        if (dialog.ShowDialog() != true)
        {
            _session.GridSpacing = oldSpacing;
            _session.GridSubdivisions = oldSubdivisions;
            _session.ShowsGrid = shown;
        }
    }

    public void PickColor(bool foreground)
    {
        var dialog = new ColorPickerDialog(foreground ? _session.ForegroundColor : _session.BackgroundColor, foreground ? L.T("拾色器（前景色）", "Color Picker (Foreground Color)") : L.T("拾色器（背景色）", "Color Picker (Background Color)")) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (foreground) _session.ForegroundColor = dialog.Color; else _session.BackgroundColor = dialog.Color;
        Canvas.Focus();
    }
}
