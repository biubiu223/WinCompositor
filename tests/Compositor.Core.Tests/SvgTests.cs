using Compositor.Core.Geometry;
using Compositor.Core.IO;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Core.Tests;

public class SvgTests
{
    private static (byte R, byte G, byte B, byte A) At(RasterImage image, int x, int y)
    {
        using var pixmap = image.PeekPixels();
        var span = pixmap.GetPixelSpan();
        int p = y * pixmap.RowBytes + x * 4;
        return (span[p], span[p + 1], span[p + 2], span[p + 3]);
    }

    [Fact]
    public void DrawsShapesWithFillsAndStrokes()
    {
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="50" viewBox="0 0 200 100">
              <rect x="0" y="0" width="100" height="100" fill="#ff0000"/>
              <circle cx="150" cy="50" r="40" fill="rgb(0, 0, 255)" stroke="lime" stroke-width="10"/>
              <path d="M 100 0 L 110 0 L 110 10 Z" fill="none" stroke="black"/>
            </svg>
            """;
        var root = SvgRenderer.Parse(svg);
        Assert.Equal(new SizeD(100, 50), SvgRenderer.IntrinsicSize(root));
        var image = SvgRenderer.RenderString(svg, 100, 50);
        Assert.Equal((255, 0, 0, 255), At(image, 20, 25));
        Assert.Equal((0, 0, 255, 255), At(image, 75, 25));
        var ring = At(image, 75, 6); // the stroke, 40–45 user units above the center
        Assert.True(ring.G > 200 && ring.R < 30, ring.ToString());
        Assert.Equal(0, At(image, 98, 2).A);
    }

    [Fact]
    public void GradientsStylesGroupsAndUse()
    {
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" viewBox="0 0 100 100">
              <style>.green { fill: #00ff00 } #big { opacity: 0.5 }</style>
              <defs>
                <linearGradient id="g"><stop offset="0" stop-color="black"/><stop offset="100%" stop-color="white"/></linearGradient>
                <rect id="tile" width="10" height="10"/>
              </defs>
              <rect width="100" height="20" fill="url(#g)"/>
              <g transform="translate(0 30)" class="green"><use xlink:href="#tile" x="5"/></g>
              <rect id="big" y="60" width="100" height="40" fill="blue" style="fill: red"/>
            </svg>
            """;
        var image = SvgRenderer.RenderString(svg, 100, 100);
        Assert.True(At(image, 2, 10).R < 20);
        Assert.True(At(image, 97, 10).R > 235);
        Assert.InRange(At(image, 50, 10).R, 100, 155);
        Assert.Equal((0, 255, 0, 255), At(image, 10, 35));
        Assert.Equal(0, At(image, 2, 35).A);
        var half = At(image, 50, 80);
        Assert.InRange(half.A, 120, 135); // red, at half opacity
        Assert.True(half.R > 100 && half.B == 0);
    }

    [Fact]
    public void ClipPathsAndEvenOdd()
    {
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" width="40" height="40">
              <clipPath id="c"><rect width="20" height="40"/></clipPath>
              <rect width="40" height="40" fill="black" clip-path="url(#c)"/>
            </svg>
            """;
        var image = SvgRenderer.RenderString(svg, 40, 40);
        Assert.Equal(255, At(image, 10, 20).A);
        Assert.Equal(0, At(image, 30, 20).A);

        const string ring = """
            <svg xmlns="http://www.w3.org/2000/svg" width="40" height="40">
              <path fill-rule="evenodd" d="M0 0H40V40H0Z M10 10H30V30H10Z"/>
            </svg>
            """;
        var holed = SvgRenderer.RenderString(ring, 40, 40);
        Assert.Equal(255, At(holed, 5, 5).A);
        Assert.Equal(0, At(holed, 20, 20).A);
    }

    [Fact]
    public void ImportsFittedToTheCanvas()
    {
        var path = Path.Combine(Path.GetTempPath(), $"compositor-{Guid.NewGuid():N}.svg");
        File.WriteAllText(path, """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 10 20"><rect width="10" height="20" fill="#336699"/></svg>""");
        try
        {
            var image = SvgRenderer.Render(path, new SizeD(400, 400));
            Assert.Equal((200, 400), (image.Width, image.Height));
            Assert.Equal((0x33, 0x66, 0x99, 255), At(image, 100, 200));
            Assert.Throws<ImageImportException>(() => SvgRenderer.RenderString("<html/>", 10, 10));
        }
        finally { File.Delete(path); }
    }
}
