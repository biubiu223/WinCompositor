using System.IO;
using System.Net.Http;
using Compositor.Core.Localization;
using Compositor.Core.Segmentation;
using Windows.AI.MachineLearning;

namespace Compositor.App.Services;

/// <summary>
/// Runs an open salient-object model (IS-Net or U²-Net, as ONNX) on Windows' own machine-learning runtime, on the
/// graphics card when there is one. Loaded on first use, off the interface thread.
/// </summary>
public sealed class WinMLSubjectModel : ISubjectModel
{
    private readonly string _path;
    private readonly object _lock = new();
    private LearningModel? _model;
    private LearningModelSession? _session;

    private readonly ModelIdle _idle;

    public WinMLSubjectModel(string path, SubjectModelSpec spec)
    {
        _path = path;
        Spec = spec;
        _idle = new ModelIdle(Unload, TimeSpan.FromMinutes(2));
    }

    /// <summary>Lets go of the session and its graphics memory; the next use loads it again.</summary>
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

    public SubjectModelSpec Spec { get; }

    public float[] Infer(float[] input)
    {
        lock (_lock)
        {
            var session = Session();
            int side = Spec.Side;
            using var tensor = TensorFloat.CreateFromArray(new long[] { 1, 3, side, side }, input);
            var binding = new LearningModelBinding(session);
            binding.Bind(_model!.InputFeatures[0].Name, tensor);
            var result = session.Evaluate(binding, "subject");
            if (!result.Succeeded)
                throw new InvalidOperationException(L.T($"模型运行失败（0x{result.ErrorStatus:X8}）。", $"The model failed to run (0x{result.ErrorStatus:X8})."));
            // The first output is the finished mask (IS-Net and U²-Net add their side outputs after it).
            if (result.Outputs[_model.OutputFeatures[0].Name] is not TensorFloat output)
                throw new InvalidOperationException(L.T("模型没有给出遮罩。", "The model returned no mask."));
            var values = output.GetAsVectorView();
            var array = new float[values.Count];
            for (int i = 0; i < array.Length; i++) array[i] = values[i];
            _idle.Touch();
            return array;
        }
    }

    private LearningModelSession Session()
    {
        if (_session is not null) return _session;
        _model ??= LearningModel.LoadFromFilePath(_path);
        // The graphics card when it can; the processor otherwise.
        try { _session = new LearningModelSession(_model, new LearningModelDevice(LearningModelDeviceKind.DirectXHighPerformance)); }
        catch { _session = new LearningModelSession(_model, new LearningModelDevice(LearningModelDeviceKind.Cpu)); }
        return _session;
    }
}

/// <summary>Where the subject models live, which one to use, and fetching the recommended one.</summary>
public static class SubjectModels
{
    public sealed record Known(string FileName, SubjectModelSpec Spec, string Title, long Size, string Url);

    /// <summary>In the order they're preferred: the most accurate first.</summary>
    public static readonly Known[] All =
    {
        new("isnet-general-use.onnx", SubjectModelSpec.IsNet, L.T("IS-Net 通用模型 · Apache-2.0", "IS-Net general-use · Apache-2.0"), 178_648_008,
            "https://github.com/danielgatis/rembg/releases/download/v0.0.0/isnet-general-use.onnx"),
        new("u2net.onnx", SubjectModelSpec.U2Net, "U²-Net · Apache-2.0", 175_997_641,
            "https://github.com/danielgatis/rembg/releases/download/v0.0.0/u2net.onnx"),
        new("silueta.onnx", SubjectModelSpec.U2Net, L.T("Silueta · U²-Net 精简版", "Silueta · compact U²-Net"), 44_173_029,
            "https://github.com/danielgatis/rembg/releases/download/v0.0.0/silueta.onnx"),
        new("u2netp.onnx", SubjectModelSpec.U2Net, L.T("U²-Net 轻量版", "U²-Net lite"), 4_574_861,
            "https://github.com/danielgatis/rembg/releases/download/v0.0.0/u2netp.onnx"),
    };

    public static Known Recommended => All[0];

    /// <summary>%LOCALAPPDATA%\Compositor\Models, where downloaded and chosen models go.</summary>
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Compositor", "Models");

    /// <summary>The best model on this computer: in the models folder, or a Models folder beside the program.</summary>
    public static (string Path, Known Model)? Find()
    {
        foreach (var known in All)
            foreach (var folder in new[] { Folder, Path.Combine(AppContext.BaseDirectory, "Models") })
            {
                var path = Path.Combine(folder, known.FileName);
                if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) return (path, known);
            }
        return null;
    }

    private static WinMLSubjectModel? _loaded;
    private static string? _loadedPath;

    /// <summary>The model to use, or null when none is installed.</summary>
    public static ISubjectModel? Current()
    {
        if (Find() is not { } found) return null;
        if (_loaded is null || _loadedPath != found.Path)
        {
            _loaded = new WinMLSubjectModel(found.Path, found.Model.Spec);
            _loadedPath = found.Path;
        }
        return _loaded;
    }

    /// <summary>Copies a model file the user chose into the models folder (its name says which kind it is).</summary>
    public static Known? Install(string file)
    {
        var name = Path.GetFileName(file);
        var known = All.FirstOrDefault(k => string.Equals(k.FileName, name, StringComparison.OrdinalIgnoreCase));
        if (known is null) return null;
        Directory.CreateDirectory(Folder);
        File.Copy(file, Path.Combine(Folder, known.FileName), overwrite: true);
        return known;
    }

    /// <summary>Downloads a model into the models folder, reporting progress 0–1; a partial file never takes its place.</summary>
    public static async Task Download(Known model, IProgress<double> progress, CancellationToken cancel)
    {
        Directory.CreateDirectory(Folder);
        var target = Path.Combine(Folder, model.FileName);
        var partial = target + ".part";
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WinCompositor");
        using (var response = await client.GetAsync(model.Url, HttpCompletionOption.ResponseHeadersRead, cancel))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? model.Size;
            await using var source = await response.Content.ReadAsStreamAsync(cancel);
            await using var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            var buffer = new byte[1 << 16];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancel)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), cancel);
                done += read;
                progress.Report(total > 0 ? (double)done / total : 0);
            }
            if (done != model.Size)
                throw new InvalidDataException(L.T($"下载的文件大小不对（{done:N0} 字节，应为 {model.Size:N0}）。",
                    $"The downloaded file is the wrong size ({done:N0} bytes; expected {model.Size:N0})."));
        }
        File.Move(partial, target, overwrite: true);
    }
}
