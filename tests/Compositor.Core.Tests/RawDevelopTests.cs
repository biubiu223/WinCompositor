using Compositor.Core.IO;

namespace Compositor.Core.Tests;

public class RawDevelopTests
{
    private static ushort[] Gray(int count, ushort value)
    {
        var pixels = new ushort[count * 4];
        for (int i = 0; i < count; i++) { pixels[i * 4] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = value; pixels[i * 4 + 3] = 65535; }
        return pixels;
    }

    [Fact]
    public void ExposureWhiteBalanceAndBoost()
    {
        var mid = Gray(64, 32768);
        var asShot = RawDevelop.Apply(mid, 8, 8, new RawDevelopSettings());
        Assert.InRange(asShot[0], 126, 129);
        var brighter = RawDevelop.Apply(mid, 8, 8, new RawDevelopSettings(Exposure: 1));
        Assert.InRange(brighter[0], 165, 185);
        var warm = RawDevelop.Apply(mid, 8, 8, new RawDevelopSettings(Temperature: 60));
        Assert.True(warm[0] > warm[2] + 15, $"{warm[0]} {warm[2]}");
        var dark = RawDevelop.Apply(Gray(64, 0), 8, 8, new RawDevelopSettings(Boost: 0));
        Assert.True(dark[0] > 15, dark[0].ToString()); // a flat rendering lifts black
        Assert.All(Enumerable.Range(0, 64), i => Assert.Equal(255, asShot[i * 4 + 3]));
    }
}
