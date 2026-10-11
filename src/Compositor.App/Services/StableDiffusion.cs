using System.IO;
using Compositor.Core.Localization;
using Windows.AI.MachineLearning;

namespace Compositor.App.Services;

/// <summary>
/// Stable Diffusion 1.5 Inpainting (ONNX, half precision) on Windows' WinML and the graphics card: the picture with its
/// hole through the VAE, then the 9-channel UNet stepped by DDIM, and the latents decoded back. Mirrors diffusers'
/// OnnxStableDiffusionInpaintPipeline with an empty prompt: the model paints what fits the surroundings. The text
/// encoder's answer for the empty prompt never changes, so it ships precomputed (<see cref="EmbeddingFile"/>, 77 × 768
/// float32) instead of the 235 MB text encoder; with nothing to steer toward, guidance has no effect and one UNet pass a
/// step does.
/// </summary>
public sealed class StableDiffusionInpainter : IDisposable
{
    public const string EmbeddingFile = "empty_prompt.bin";
    private const int Tokens = 77, Width = 768;
    private const float LatentScale = 0.18215f;
    private readonly string _folder;
    private readonly object _lock = new();
    private float[]? _context;
    private LearningModelSession? _encoder, _decoder, _unet;
    private LearningModelDevice? _device;
    private readonly float[] _alphasCumprod = new float[1000];

    public StableDiffusionInpainter(string folder)
    {
        _folder = folder;
        // Scaled-linear betas from 0.00085 to 0.012, as SD 1.5 was trained.
        double product = 1, from = Math.Sqrt(0.00085), to = Math.Sqrt(0.012);
        for (int i = 0; i < 1000; i++)
        {
            double beta = Math.Pow(from + (to - from) * i / 999, 2);
            product *= 1 - beta;
            _alphasCumprod[i] = (float)product;
        }
    }

    public bool Loaded => _unet is not null;

    private void Load()
    {
        if (_unet is not null) return;
        if (_context is null)
        {
            var bytes = File.ReadAllBytes(Path.Combine(_folder, EmbeddingFile));
            if (bytes.Length != Tokens * Width * 4) throw new InvalidDataException(L.T($"{EmbeddingFile} 的大小不对。", $"{EmbeddingFile} is the wrong size."));
            _context = new float[Tokens * Width];
            Buffer.BlockCopy(bytes, 0, _context, 0, bytes.Length);
        }
        _device = new LearningModelDevice(LearningModelDeviceKind.DirectXHighPerformance);
        LearningModelSession Session(string part) =>
            new(LearningModel.LoadFromFilePath(Path.Combine(_folder, part, "model.onnx")), _device);
        _encoder = Session("vae_encoder");
        _decoder = Session("vae_decoder");
        _unet = Session("unet");
    }

    /// <summary>Lets go of the models (and the graphics memory they hold); the next use loads them again.</summary>
    public void Unload()
    {
        lock (_lock)
        {
            _unet?.Dispose(); _decoder?.Dispose(); _encoder?.Dispose();
            _unet = _decoder = _encoder = null;
            _device = null;
        }
    }

    public void Dispose() => Unload();

    /// <summary>
    /// Paints the hole of a straight-RGB picture (planes of <paramref name="width"/> × <paramref name="height"/>, both
    /// multiples of 64, 0–255) to fit its surroundings; returns the picture as decoded (RGB planes, 0–255).
    /// <paramref name="hole"/> is 1 where to paint.
    /// </summary>
    public float[] Generate(float[] rgb, float[] hole, int width, int height, int seed, int steps = 25,
                            IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        lock (_lock)
        {
            Load();
            int plane = width * height, lw = width / 8, lh = height / 8, lplane = lw * lh;

            // The picture with its hole made neutral, into latents; the hole shrunk to latent size.
            var masked = new float[3 * plane];
            for (int i = 0; i < plane; i++)
                for (int k = 0; k < 3; k++) masked[k * plane + i] = hole[i] >= 0.5f ? 0 : rgb[k * plane + i] / 127.5f - 1;
            var maskedLatents = Run(_encoder!, "sample", masked, new long[] { 1, 3, height, width }, "latent_sample");
            for (int i = 0; i < maskedLatents.Length; i++) maskedLatents[i] *= LatentScale;
            var latentMask = new float[lplane];
            for (int y = 0; y < lh; y++)
                for (int x = 0; x < lw; x++)
                {
                    // Any of the 8×8 block in the hole counts, so the edge is painted too.
                    bool any = false;
                    for (int dy = 0; dy < 8 && !any; dy++)
                        for (int dx = 0; dx < 8 && !any; dx++) any = hole[(y * 8 + dy) * width + x * 8 + dx] >= 0.5f;
                    latentMask[y * lw + x] = any ? 1 : 0;
                }

            var rng = new Random(seed);
            var latents = new float[4 * lplane];
            for (int i = 0; i < latents.Length; i++) latents[i] = Gaussian(rng);
            int ratio = 1000 / steps;
            var timesteps = Enumerable.Range(0, steps).Select(i => i * ratio + 1).Reverse().ToArray();
            var input = new float[9 * lplane];
            for (int s = 0; s < timesteps.Length; s++)
            {
                cancel.ThrowIfCancellationRequested();
                int t = timesteps[s];
                // Noisy latents, the mask, the masked picture's latents.
                Array.Copy(latents, 0, input, 0, 4 * lplane);
                Array.Copy(latentMask, 0, input, 4 * lplane, lplane);
                Array.Copy(maskedLatents, 0, input, 5 * lplane, 4 * lplane);
                var eps = RunUnet(input, lw, lh, t);
                // DDIM, deterministic.
                float at = _alphasCumprod[t], prev = t - ratio >= 0 ? _alphasCumprod[t - ratio] : _alphasCumprod[0];
                float sa = MathF.Sqrt(at), sb = MathF.Sqrt(1 - at), spa = MathF.Sqrt(prev), spb = MathF.Sqrt(1 - prev);
                for (int i = 0; i < latents.Length; i++)
                {
                    float x0 = (latents[i] - sb * eps[i]) / sa;
                    latents[i] = spa * x0 + spb * eps[i];
                }
                progress?.Report((s + 1.0) / timesteps.Length);
            }
            for (int i = 0; i < latents.Length; i++) latents[i] /= LatentScale;
            var decoded = Run(_decoder!, "latent_sample", latents, new long[] { 1, 4, lh, lw }, "sample");
            var result = new float[3 * plane];
            for (int i = 0; i < result.Length; i++) result[i] = Math.Clamp((decoded[i] + 1) * 127.5f, 0, 255);
            return result;
        }
    }

    private float[] RunUnet(float[] input, int lw, int lh, int timestep)
    {
        var binding = new LearningModelBinding(_unet!);
        using var sample = TensorFloat16Bit.CreateFromArray(new long[] { 1, 9, lh, lw }, input);
        using var time = TensorFloat16Bit.CreateFromArray(new long[] { 1 }, new[] { (float)timestep });
        using var states = TensorFloat16Bit.CreateFromArray(new long[] { 1, Tokens, Width }, _context!);
        binding.Bind("sample", sample);
        binding.Bind("timestep", time);
        binding.Bind("encoder_hidden_states", states);
        return Read(_unet!.Evaluate(binding, "unet"), "out_sample");
    }

    private static float[] Run(LearningModelSession session, string inputName, float[] data, long[] shape, string outputName)
    {
        var binding = new LearningModelBinding(session);
        using var tensor = TensorFloat16Bit.CreateFromArray(shape, data);
        binding.Bind(inputName, tensor);
        return Read(session.Evaluate(binding, inputName), outputName);
    }

    private static float[] Read(LearningModelEvaluationResult result, string name)
    {
        if (!result.Succeeded) throw new InvalidOperationException(L.T($"AI 生成失败（0x{result.ErrorStatus:X8}）。", $"AI generation failed (0x{result.ErrorStatus:X8})."));
        var values = result.Outputs[name] switch
        {
            TensorFloat16Bit half => half.GetAsVectorView(),
            TensorFloat full => full.GetAsVectorView(),
            _ => throw new InvalidOperationException(L.T("模型的输出类型不对。", "The model returned an unexpected output type.")),
        };
        var array = new float[values.Count];
        for (int i = 0; i < array.Length; i++) array[i] = values[i];
        return array;
    }

    private static float Gaussian(Random rng)
    {
        double u = 1 - rng.NextDouble(), v = rng.NextDouble();
        return (float)(Math.Sqrt(-2 * Math.Log(u)) * Math.Cos(2 * Math.PI * v));
    }
}
