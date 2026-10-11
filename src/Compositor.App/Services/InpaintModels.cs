using System.IO;
using Compositor.Core.Inpainting;
using Compositor.Core.Localization;
using Windows.AI.MachineLearning;

namespace Compositor.App.Services;

/// <summary>
/// MI-GAN (Picsart AI Research, MIT) as an ONNX pipeline on Windows' own WinML: any size in, the hole painted in out
/// (it crops around the hole and works at 512 pixels inside). Runs on the processor, which is quicker than the graphics
/// card for a model this small.
/// </summary>
public sealed class WinMLInpaintModel : IInpaintModel
{
    private readonly string _path;
    private readonly object _lock = new();
    private LearningModel? _model;
    private LearningModelSession? _session;

    private readonly ModelIdle _idle;

    public WinMLInpaintModel(string path)
    {
        _path = path;
        _idle = new ModelIdle(Unload, TimeSpan.FromMinutes(2));
    }

    public void Unload()
    {
        lock (_lock)
        {
            _session?.Dispose();
            _session = null;
            _model?.Dispose();
            _model = null;
        }
    }

    public byte[] Inpaint(ReadOnlySpan<byte> rgba, byte[] hole, int width, int height)
    {
        int plane = width * height;
        // Straight RGB in channel planes; the mask is 255 where the picture is kept, 0 where it's painted.
        var image = new byte[3 * plane];
        var mask = new byte[plane];
        for (int i = 0; i < plane; i++)
        {
            int a = rgba[i * 4 + 3];
            for (int k = 0; k < 3; k++) image[k * plane + i] = a == 0 ? (byte)0 : (byte)Math.Min(255, rgba[i * 4 + k] * 255 / a);
            mask[i] = hole[i] != 0 ? (byte)0 : (byte)255;
        }
        lock (_lock)
        {
            _model ??= LearningModel.LoadFromFilePath(_path);
            _session ??= new LearningModelSession(_model, new LearningModelDevice(LearningModelDeviceKind.Cpu));
            var inputs = _model.InputFeatures.ToList();
            using var imageTensor = TensorUInt8Bit.CreateFromArray(new long[] { 1, 3, height, width }, image);
            using var maskTensor = TensorUInt8Bit.CreateFromArray(new long[] { 1, 1, height, width }, mask);
            var binding = new LearningModelBinding(_session);
            binding.Bind(inputs[0].Name, imageTensor);
            binding.Bind(inputs[1].Name, maskTensor);
            var result = _session.Evaluate(binding, "inpaint");
            if (!result.Succeeded)
                throw new InvalidOperationException(L.T($"修补模型运行失败（0x{result.ErrorStatus:X8}）。", $"The inpainting model failed to run (0x{result.ErrorStatus:X8})."));
            if (result.Outputs[_model.OutputFeatures[0].Name] is not TensorUInt8Bit output)
                throw new InvalidOperationException(L.T("修补模型没有给出图像。", "The inpainting model returned no image."));
            var values = output.GetAsVectorView();
            var rgb = new byte[3 * plane];
            for (int i = 0; i < plane; i++)
                for (int k = 0; k < 3; k++) rgb[i * 3 + k] = values[k * plane + i];
            _idle.Touch();
            return rgb;
        }
    }
}

/// <summary>
/// LaMa (Samsung AI, Apache-2.0) on WinML: large-mask inpainting with Fourier convolutions, which carries lines,
/// edges and repeating structure across a hole far better than smaller models. It works on a 512-pixel square: the
/// picture (with its context) is scaled to that and the answer scaled back. On the processor, because the graphics
/// card's DirectML can't run its Fourier units.
/// </summary>
public sealed class WinMLLamaModel : IInpaintModel
{
    private const int Side = 512;
    private readonly string _path;
    private readonly object _lock = new();
    private readonly ModelIdle _idle;
    private LearningModel? _model;
    private LearningModelSession? _session;

    public WinMLLamaModel(string path)
    {
        _path = path;
        _idle = new ModelIdle(Unload, TimeSpan.FromMinutes(2));
    }

    public void Unload()
    {
        lock (_lock)
        {
            _session?.Dispose();
            _session = null;
            _model?.Dispose();
            _model = null;
        }
    }

    public byte[] Inpaint(ReadOnlySpan<byte> rgba, byte[] hole, int width, int height)
    {
        // The picture (straight RGB) and its hole, scaled to the model's square.
        var source = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(width, height, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul));
        var pixels = source.GetPixelSpan();
        for (int i = 0; i < width * height; i++)
        {
            int a = rgba[i * 4 + 3];
            for (int k = 0; k < 3; k++) pixels[i * 4 + k] = a == 0 ? (byte)0 : (byte)Math.Min(255, rgba[i * 4 + k] * 255 / a);
            pixels[i * 4 + 3] = 255;
        }
        using var square = source.Resize(new SkiaSharp.SKImageInfo(Side, Side, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul),
                                         new SkiaSharp.SKSamplingOptions(SkiaSharp.SKCubicResampler.Mitchell));
        source.Dispose();
        int plane = Side * Side;
        var image = new float[3 * plane];
        var mask = new float[plane];
        var squarePixels = square.GetPixelSpan();
        for (int y = 0; y < Side; y++)
            for (int x = 0; x < Side; x++)
            {
                int i = y * Side + x;
                for (int k = 0; k < 3; k++) image[k * plane + i] = squarePixels[i * 4 + k] / 255f;
                // A square pixel is to be filled if any of the picture under it is.
                int x0 = x * width / Side, x1 = Math.Max(x0 + 1, (x + 1) * width / Side), y0 = y * height / Side, y1 = Math.Max(y0 + 1, (y + 1) * height / Side);
                bool any = false;
                for (int sy = y0; sy < y1 && !any; sy++)
                    for (int sx = x0; sx < x1 && !any; sx++) any = hole[Math.Min(height - 1, sy) * width + Math.Min(width - 1, sx)] != 0;
                mask[i] = any ? 1 : 0;
            }
        float[] answer;
        lock (_lock)
        {
            _model ??= LearningModel.LoadFromFilePath(_path);
            _session ??= new LearningModelSession(_model, new LearningModelDevice(LearningModelDeviceKind.Cpu));
            using var imageTensor = TensorFloat.CreateFromArray(new long[] { 1, 3, Side, Side }, image);
            using var maskTensor = TensorFloat.CreateFromArray(new long[] { 1, 1, Side, Side }, mask);
            var binding = new LearningModelBinding(_session);
            binding.Bind("image", imageTensor);
            binding.Bind("mask", maskTensor);
            var result = _session.Evaluate(binding, "lama");
            if (!result.Succeeded) throw new InvalidOperationException(L.T($"LaMa 运行失败（0x{result.ErrorStatus:X8}）。", $"LaMa failed to run (0x{result.ErrorStatus:X8})."));
            var values = ((TensorFloat)result.Outputs["output"]).GetAsVectorView();
            answer = new float[values.Count];
            for (int i = 0; i < answer.Length; i++) answer[i] = values[i];
        }
        _idle.Touch();
        // Its answer (0–255) scaled back to the picture's size.
        using var painted = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(Side, Side, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul));
        var paintedPixels = painted.GetPixelSpan();
        for (int i = 0; i < plane; i++)
        {
            for (int k = 0; k < 3; k++) paintedPixels[i * 4 + k] = (byte)Math.Clamp(answer[k * plane + i], 0, 255);
            paintedPixels[i * 4 + 3] = 255;
        }
        using var back = painted.Resize(new SkiaSharp.SKImageInfo(width, height, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul),
                                        new SkiaSharp.SKSamplingOptions(SkiaSharp.SKCubicResampler.Mitchell));
        var backPixels = back.GetPixelSpan();
        var rgb = new byte[width * height * 3];
        for (int i = 0; i < width * height; i++)
            for (int k = 0; k < 3; k++) rgb[i * 3 + k] = backPixels[i * 4 + k];
        return rgb;
    }
}

/// <summary>
/// Stable Diffusion inpainting as Generative Fill's model: the crop scaled so its long side is 512 (both sides
/// multiples of 64), painted, and scaled back.
/// </summary>
public sealed class WinMLGenerativeModel : IGenerativeModel
{
    private readonly StableDiffusionInpainter _inpainter;
    private readonly ModelIdle _idle;

    public WinMLGenerativeModel(string folder)
    {
        _inpainter = new StableDiffusionInpainter(folder);
        // About two gigabytes of graphics memory: let go sooner.
        _idle = new ModelIdle(_inpainter.Unload, TimeSpan.FromSeconds(90));
    }

    public byte[] Generate(ReadOnlySpan<byte> rgba, byte[] hole, int width, int height, int seed,
                           IProgress<double>? progress, CancellationToken cancel)
    {
        // The UNet halves the latents three times and joins them back up, so the picture's sides must be multiples
        // of 64 (8 for the VAE, 8 more for the UNet's levels).
        double scale = 512.0 / Math.Max(width, height);
        int w = Math.Max(256, (int)Math.Round(width * scale / 64) * 64), h = Math.Max(256, (int)Math.Round(height * scale / 64) * 64);
        var source = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(width, height, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul));
        var pixels = source.GetPixelSpan();
        for (int i = 0; i < width * height; i++)
        {
            int a = rgba[i * 4 + 3];
            for (int k = 0; k < 3; k++) pixels[i * 4 + k] = a == 0 ? (byte)0 : (byte)Math.Min(255, rgba[i * 4 + k] * 255 / a);
            pixels[i * 4 + 3] = 255;
        }
        using var sized = source.Resize(new SkiaSharp.SKImageInfo(w, h, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul),
                                        new SkiaSharp.SKSamplingOptions(SkiaSharp.SKCubicResampler.Mitchell));
        source.Dispose();
        int plane = w * h;
        var rgb = new float[3 * plane];
        var mask = new float[plane];
        var sizedPixels = sized.GetPixelSpan();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                for (int k = 0; k < 3; k++) rgb[k * plane + i] = sizedPixels[i * 4 + k];
                int sx = Math.Min(width - 1, (int)((x + 0.5) * width / w)), sy = Math.Min(height - 1, (int)((y + 0.5) * height / h));
                mask[i] = hole[sy * width + sx] != 0 ? 1 : 0;
            }
        float[] painted;
        try { painted = _inpainter.Generate(rgb, mask, w, h, seed, steps: 25, progress: progress, cancel: cancel); }
        finally { _idle.Touch(); }
        using var output = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(w, h, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul));
        var outputPixels = output.GetPixelSpan();
        for (int i = 0; i < plane; i++)
        {
            for (int k = 0; k < 3; k++) outputPixels[i * 4 + k] = (byte)painted[k * plane + i];
            outputPixels[i * 4 + 3] = 255;
        }
        using var back = output.Resize(new SkiaSharp.SKImageInfo(width, height, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul),
                                       new SkiaSharp.SKSamplingOptions(SkiaSharp.SKCubicResampler.Mitchell));
        var backPixels = back.GetPixelSpan();
        var result = new byte[width * height * 3];
        for (int i = 0; i < width * height; i++)
            for (int k = 0; k < 3; k++) result[i * 3 + k] = backPixels[i * 4 + k];
        return result;
    }
}

/// <summary>Where the inpainting models live: beside the program (bundled) or in the models folder.</summary>
public static class InpaintModels
{
    public const string LamaFile = "lama_fp32.onnx", FileName = "migan_pipeline_v2.onnx", GenerativeFolder = "sd-inpainting";

    private static IEnumerable<string> Folders() => new[] { Path.Combine(AppContext.BaseDirectory, "Models"), SubjectModels.Folder };

    private static string? FindFile(string name)
    {
        foreach (var folder in Folders())
        {
            var path = Path.Combine(folder, name);
            if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) return path;
        }
        return null;
    }

    /// <summary>The model neural reconstruction uses: LaMa when it's there, MI-GAN otherwise.</summary>
    public static string? Find() => FindFile(LamaFile) ?? FindFile(FileName);

    /// <summary>The name shown for it.</summary>
    public static string Name => FindFile(LamaFile) is not null ? "LaMa" : "MI-GAN";

    private static IInpaintModel? _loaded;

    public static IInpaintModel? Current()
    {
        if (_loaded is not null) return _loaded;
        if (FindFile(LamaFile) is { } lama) return _loaded = new WinMLLamaModel(lama);
        return FindFile(FileName) is { } path ? _loaded = new WinMLInpaintModel(path) : null;
    }

    /// <summary>The Stable Diffusion folder (UNet, VAE and the precomputed empty-prompt embedding), when present.</summary>
    public static string? FindGenerative()
    {
        foreach (var folder in Folders())
        {
            var path = Path.Combine(folder, GenerativeFolder);
            if (File.Exists(Path.Combine(path, "unet", "model.onnx")) && File.Exists(Path.Combine(path, StableDiffusionInpainter.EmbeddingFile))) return path;
        }
        return null;
    }

    private static WinMLGenerativeModel? _generative;

    public static IGenerativeModel? Generative()
    {
        if (_generative is not null) return _generative;
        return FindGenerative() is { } folder ? _generative = new WinMLGenerativeModel(folder) : null;
    }
}
