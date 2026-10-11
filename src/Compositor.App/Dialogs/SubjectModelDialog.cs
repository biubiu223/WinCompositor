using System.IO;
using System.Windows;
using System.Windows.Controls;
using Compositor.App.Services;
using Compositor.Core.Localization;
using Microsoft.Win32;

namespace Compositor.App.Dialogs;

/// <summary>
/// Shown the first time Select Subject, Object Selection or Remove Background is used without a model: what it needs,
/// where it comes from and how big it is, with Download (with progress) and Choose File. Nothing is fetched until the
/// user says so.
/// </summary>
public sealed class SubjectModelDialog : DialogBase
{
    private readonly ProgressBar _progress = new() { Height = 6, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 4, 0, 6), Visibility = Visibility.Collapsed };
    private readonly TextBlock _status = Note("");
    private readonly Button _choose = new() { Content = L.T("选择本地文件…", "Choose File…"), MinWidth = 120, HorizontalAlignment = HorizontalAlignment.Left };
    private CancellationTokenSource? _download;

    public SubjectModelDialog() : base(L.T("需要 AI 模型", "AI Model Required"), L.T("下载", "Download"))
    {
        var model = SubjectModels.Recommended;
        Body.Children.Add(new TextBlock
        {
            Text = L.T("选择主体、对象选择和移除背景要用一个开源的抠图模型。",
                "Select Subject, Object Selection and Remove Background use an open-source segmentation model."),
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap, MaxWidth = 380,
        });
        Body.Children.Add(Note(L.T($"推荐：{model.FileName}（{model.Title}），{model.Size / 1_048_576.0:0} MB，来自 rembg 项目在 GitHub 上的发布页：\n{model.Url}",
            $"Recommended: {model.FileName} ({model.Title}), {model.Size / 1_048_576.0:0} MB, from the rembg project’s releases on GitHub:\n{model.Url}")));
        Body.Children.Add(Note(L.T($"下载后存放在 {SubjectModels.Folder}，之后在这台电脑上离线使用。也可以选择已经下载好的 isnet-general-use.onnx、u2net.onnx、silueta.onnx 或 u2netp.onnx。",
            $"It’s saved in {SubjectModels.Folder} and then runs offline on this computer. You can also choose an isnet-general-use.onnx, u2net.onnx, silueta.onnx or u2netp.onnx you’ve already downloaded.")));
        Body.Children.Add(_progress);
        Body.Children.Add(_status);
        _choose.Click += (_, _) => Choose();
        Body.Children.Add(_choose);
        OkButton.MinWidth = 110;
        Closing += (_, _) => _download?.Cancel();
    }

    protected override bool Validate()
    {
        if (_download is not null) return false;
        _ = Download();
        return false;
    }

    private async Task Download()
    {
        var model = SubjectModels.Recommended;
        _download = new CancellationTokenSource();
        OkButton.IsEnabled = false;
        _choose.IsEnabled = false;
        _progress.Visibility = Visibility.Visible;
        var progress = new Progress<double>(p =>
        {
            _progress.Value = p;
            _status.Text = L.T($"正在下载… {p * model.Size / 1_048_576:0} / {model.Size / 1_048_576.0:0} MB",
                $"Downloading… {p * model.Size / 1_048_576:0} / {model.Size / 1_048_576.0:0} MB");
        });
        try
        {
            await SubjectModels.Download(model, progress, _download.Token);
            DialogResult = true;
        }
        catch (OperationCanceledException) { DeletePartial(); }
        catch (Exception error)
        {
            DeletePartial();
            _status.Text = L.T("下载失败：", "Download failed: ") + error.Message;
            _progress.Visibility = Visibility.Collapsed;
            OkButton.IsEnabled = true;
            _choose.IsEnabled = true;
        }
        finally { _download = null; }
    }

    private static void DeletePartial()
    {
        try { File.Delete(Path.Combine(SubjectModels.Folder, SubjectModels.Recommended.FileName + ".part")); } catch { }
    }

    private void Choose()
    {
        var dialog = new OpenFileDialog
        {
            Title = L.T("选择抠图模型", "Choose a Segmentation Model"),
            Filter = L.T("ONNX 模型 (*.onnx)|*.onnx", "ONNX Models (*.onnx)|*.onnx"),
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (SubjectModels.Install(dialog.FileName) is null)
            {
                var names = string.Join(L.T("、", ", "), SubjectModels.All.Select(k => k.FileName));
                _status.Text = L.T($"不认识这个文件名。请选择 {names} 之一。", $"That file name isn’t one WinCompositor knows. Choose one of {names}.");
                return;
            }
            DialogResult = true;
        }
        catch (Exception error) { _status.Text = L.T("无法使用这个文件：", "Couldn’t use this file: ") + error.Message; }
    }
}
