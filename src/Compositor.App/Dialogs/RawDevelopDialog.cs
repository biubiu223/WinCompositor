using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Compositor.App.Controls;
using Compositor.Core.IO;
using Compositor.Core.Localization;

namespace Compositor.App.Dialogs;

/// <summary>
/// Develop a camera RAW before it comes in: a RAW holds more range than a layer can, so what to keep is chosen here
/// rather than assumed. The preview develops a screen-sized copy as the sliders move; Import develops the full frame
/// once. Mirrors RawDevelopSheet.swift.
/// </summary>
public sealed class RawDevelopDialog : DialogBase
{
    private const int PreviewSide = 900;
    private readonly (ushort[] Pixels, int Width, int Height) _preview;
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly SliderRow _exposure, _temperature, _tint, _boost;
    private RawDevelopSettings _settings = new();
    private CancellationTokenSource? _job;

    public RawDevelopSettings Settings => _settings;

    public RawDevelopDialog(string path, (ushort[] Pixels, int Width, int Height) preview) : base(L.T($"冲印“{Path.GetFileName(path)}”", $"Develop “{Path.GetFileName(path)}”"), L.T("导入", "Import"))
    {
        _preview = preview;
        System.Windows.Media.RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        Body.Children.Add(new Border
        {
            Width = 560, Height = 340, Child = _image, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 0, 14),
            Background = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)),
        });
        _exposure = Slider(L.T("曝光", "Exposure"), -3, 3, 0, 2, " EV", v => _settings = _settings with { Exposure = v }, Theme.Gradient(Colors.Black, Colors.White));
        _temperature = Slider(L.T("色温", "Temperature"), -100, 100, 0, 0, "", v => _settings = _settings with { Temperature = v },
            Theme.Gradient(Color.FromRgb(70, 120, 230), Color.FromRgb(128, 128, 128), Color.FromRgb(235, 200, 60)));
        _tint = Slider(L.T("色调", "Tint"), -100, 100, 0, 0, "", v => _settings = _settings with { Tint = v },
            Theme.Gradient(Color.FromRgb(70, 190, 80), Color.FromRgb(128, 128, 128), Color.FromRgb(210, 70, 200)));
        _boost = Slider(L.T("增强", "Boost"), 0, 1, 1, 2, "", v => _settings = _settings with { Boost = v });
        Body.Children.Add(Note(L.T("色温和色调相对相机自己的读数；增强为 1 时保留解码器的全部对比度，为 0 时得到平淡、中性的影调。",
            "Temperature and Tint are relative to the camera’s own reading. Boost at 1 keeps all of the decoder’s contrast; at 0 the tones come out flat and neutral.")));
        var reset = new Button { Content = L.T("复位", "Reset"), MinWidth = 70, HorizontalAlignment = HorizontalAlignment.Left };
        reset.Click += (_, _) =>
        {
            _settings = new RawDevelopSettings();
            _exposure.Value = 0; _temperature.Value = 0; _tint.Value = 0; _boost.Value = 1;
            Develop();
        };
        Body.Children.Add(reset);
        Loaded += (_, _) => Develop();
        Closed += (_, _) => _job?.Cancel();
    }

    private SliderRow Slider(string label, double min, double max, double value, int decimals, string unit, Action<double> set, Brush? track = null)
    {
        // English labels ("Temperature") need more room than the Chinese two characters.
        var row = new SliderRow(label, min, max, value, value, decimals, unit, track: track, labelWidth: L.IsEnglish ? 90 : 70) { Width = 520 };
        row.ValueChanged += v => { set(v); Develop(); };
        Body.Children.Add(row);
        return row;
    }

    /// <summary>Develops the screen-sized copy (off the interface thread; a newer change supersedes it).</summary>
    private void Develop()
    {
        _job?.Cancel();
        var job = _job = new CancellationTokenSource();
        var settings = _settings;
        var (pixels, width, height) = _preview;
        _ = Run();
        async Task Run()
        {
            try
            {
                await Task.Delay(40, job.Token);
                var bytes = await Task.Run(() => RawDevelop.Apply(pixels, width, height, settings), job.Token);
                if (job.IsCancellationRequested) return;
                // Premultiplied RGBA → premultiplied BGRA for WPF.
                for (int i = 0; i < bytes.Length; i += 4) (bytes[i], bytes[i + 2]) = (bytes[i + 2], bytes[i]);
                var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, bytes, width * 4);
                bitmap.Freeze();
                _image.Source = bitmap;
            }
            catch (OperationCanceledException) { }
        }
    }

    public static int PreviewLimit => PreviewSide;
}
