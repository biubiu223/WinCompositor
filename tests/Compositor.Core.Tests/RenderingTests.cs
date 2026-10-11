using Compositor.Core.Geometry;
using Compositor.Core.Model;
using Compositor.Core.Rendering;
using static Compositor.Core.Tests.TestImages;

namespace Compositor.Core.Tests;

public class RenderingTests
{
    [Theory]
    [InlineData(BlendMode.LinearDodge, 100, 80, 180)]
    [InlineData(BlendMode.LinearBurn, 200, 150, 95)]
    [InlineData(BlendMode.Subtract, 100, 150, 50)]
    [InlineData(BlendMode.Multiply, 128, 128, 64)]
    [InlineData(BlendMode.Screen, 128, 128, 192)]
    [InlineData(BlendMode.Difference, 200, 50, 150)]
    [InlineData(BlendMode.HardMix, 100, 200, 255)]
    [InlineData(BlendMode.PinLight, 50, 200, 100)]
    public void OpaqueBlendModesMatchPhotoshopFormulas(BlendMode mode, byte top, byte bottom, byte expected)
    {
        var document = Document(4, 4,
            Layer(Solid(4, 4, bottom, bottom, bottom)),
            Layer(Solid(4, 4, top, top, top), mode: mode));
        using var image = DocumentRenderer.RenderToImage(document);
        var pixel = Pixel(image, 1, 1);
        Assert.InRange(pixel.R, expected - 2, expected + 2);
        Assert.Equal(255, pixel.A);
    }

    [Fact]
    public void HalfOpacityNormalLayerMixesEvenly()
    {
        var document = Document(2, 2,
            Layer(Solid(2, 2, 0, 0, 0)),
            Layer(Solid(2, 2, 255, 255, 255), opacity: 0.5));
        using var image = DocumentRenderer.RenderToImage(document);
        Assert.InRange(Pixel(image, 0, 0).R, 126, 129);
    }

    [Fact]
    public void FolderOpacityMultipliesIntoChildren()
    {
        var folder = Model.Layer.Folder("Folder", new SizeD(2, 2)) with { Opacity = 0.5 };
        var child = Layer(Solid(2, 2, 255, 0, 0)) with { ParentId = folder.Id, Opacity = 0.5 };
        var document = Document(2, 2, folder, child);
        using var image = DocumentRenderer.RenderToImage(document);
        Assert.InRange(Pixel(image, 0, 0).A, 62, 66);
    }

    [Fact]
    public void HiddenFolderHidesItsContents()
    {
        var folder = Model.Layer.Folder("Folder", new SizeD(2, 2)) with { IsVisible = false };
        var child = Layer(Solid(2, 2, 255, 0, 0)) with { ParentId = folder.Id };
        using var image = DocumentRenderer.RenderToImage(Document(2, 2, folder, child));
        Assert.Equal(0, Pixel(image, 0, 0).A);
    }

    [Fact]
    public void MaskHidesWhereBlack()
    {
        var coverage = new byte[] { 255, 0, 255, 0 };
        var mask = RasterImage.FromMask(coverage, 2, 2);
        var layer = Layer(Solid(2, 2, 0, 255, 0)) with { Mask = new LayerMask(mask) };
        using var image = DocumentRenderer.RenderToImage(Document(2, 2, layer));
        Assert.Equal(255, Pixel(image, 0, 0).A);
        Assert.Equal(0, Pixel(image, 1, 0).A);
    }

    [Fact]
    public void ClippedLayerShowsOnlyWithinBase()
    {
        var baseImage = RasterImage.FromRgba(new byte[]
        {
            255, 255, 255, 255,   0, 0, 0, 0,
            255, 255, 255, 255,   0, 0, 0, 0,
        }, 2, 2);
        var baseLayer = Layer(baseImage);
        var clipped = Layer(Solid(2, 2, 0, 0, 255)) with { MaskSourceId = baseLayer.Id };
        using var image = DocumentRenderer.RenderToImage(Document(2, 2, baseLayer, clipped));
        var inside = Pixel(image, 0, 0);
        var outside = Pixel(image, 1, 0);
        Assert.Equal(255, inside.B);
        Assert.Equal(0, inside.R);
        Assert.Equal(0, outside.A);
    }

    [Fact]
    public void TransformPlacesAndFlipsLayer()
    {
        var image = RasterImage.FromRgba(new byte[]
        {
            255, 0, 0, 255,   0, 255, 0, 255,
        }, 2, 1);
        var layer = Model.Layer.FromImage(image, new PointD(1, 0)) with
        {
            Transform = new LayerTransform(new PointD(1, 0), new SizeD(2, 1), flipX: true, sampling: LayerSampling.Nearest),
        };
        using var rendered = DocumentRenderer.RenderToImage(Document(4, 1, layer));
        Assert.Equal(0, Pixel(rendered, 0, 0).A);
        Assert.Equal(255, Pixel(rendered, 1, 0).G);
        Assert.Equal(255, Pixel(rendered, 2, 0).R);
    }
}
