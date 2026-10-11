using Compositor.Core.Localization;

namespace Compositor.Core.Inpainting;

/// <summary>Where Content-Aware Fill may take pixels from.</summary>
public enum FillSampling
{
    /// <summary>A band around the fill area, wide enough for its size.</summary>
    Auto,
    /// <summary>Everything on the layer outside the fill area.</summary>
    All,
    /// <summary>What the sampling brush painted.</summary>
    Custom,
}

/// <summary>How the fill area is rebuilt.</summary>
public enum FillMethod
{
    /// <summary>Smooth surroundings are stretched in; anything with texture is synthesized.</summary>
    Auto,
    /// <summary>Patch-based synthesis from the sampling area (PatchMatch, coarse to fine).</summary>
    Texture,
    /// <summary>The edge colors stretched smoothly across (skies, gradients, plain backgrounds), with matching grain.</summary>
    Smooth,
    /// <summary>Generative Fill: a diffusion model paints the area anew, to fit what surrounds it.</summary>
    Generate,
}

public static class FillNames
{
    public static string ToDisplayName(this FillMethod method) => method switch
    {
        FillMethod.Texture => L.T("纹理合成", "Texture Synthesis"),
        FillMethod.Smooth => L.T("平滑延展", "Smooth Fill"),
        FillMethod.Generate => L.T("AI 生成", "AI Generation"),
        _ => L.T("自动重建", "Auto"),
    };
}

/// <summary>Content-Aware Fill's settings.</summary>
public sealed record ContentFillSettings
{
    public FillMethod Method { get; init; } = FillMethod.Auto;
    /// <summary>The AI model rebuilds the area's structure first; synthesis then brings back the picture's own detail.</summary>
    public bool Neural { get; init; } = true;
    /// <summary>Changes the random choices, for another result.</summary>
    public int Seed { get; init; } = 1;
}

/// <summary>
/// An inpainting model: the picture (premultiplied RGBA) and its hole (255 to fill) in, the picture's colors with the
/// hole painted in out, as straight RGB, three bytes a pixel, the same size.
/// </summary>
public interface IInpaintModel
{
    byte[] Inpaint(ReadOnlySpan<byte> rgba, byte[] hole, int width, int height);
}

/// <summary>
/// A generative inpainting model (Stable Diffusion): the picture (premultiplied RGBA) and its hole (255 to paint) in,
/// the picture as the model paints it out, straight RGB, three bytes a pixel, the same size. <paramref name="seed"/>
/// picks one of the many fills that fit.
/// </summary>
public interface IGenerativeModel
{
    byte[] Generate(ReadOnlySpan<byte> rgba, byte[] hole, int width, int height, int seed,
                    IProgress<double>? progress, CancellationToken cancel);
}
