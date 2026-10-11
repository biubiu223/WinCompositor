using System.IO.Compression;

namespace Compositor.Core.CameraRaw;

/// <summary>One step of the grade: up to eight measured tables (−1 for no change) blended by weight.</summary>
public readonly struct CameraRawStage
{
    public readonly int[] Tables;
    public readonly float[] Weights;
    public CameraRawStage(int[] tables, float[] weights) { Tables = tables; Weights = weights; }
}

/// <summary>
/// Camera Raw's Light and Color sliders as Photoshop's own Camera Raw Filter draws them: color tables measured from it,
/// each the colors of a 17³ grid after one slider at one value. A value in between blends the two nearest tables.
/// Temperature and Tint come from a grid of both and blend four. Contrast, Highlights and Shadows adapt to the picture,
/// measured on backgrounds of seven brightnesses, and blend across those too. Mirrors CameraRawTables.swift.
/// </summary>
public static class CameraRawTables
{
    public const int Size = 17;
    /// <summary>The grid the stages are composed onto, finer than the tables so composing adds little blur.</summary>
    public const int Grid = 33;
    public const int Entries = Size * Size * Size * 3;
    private const int ExposureStart = 81, WhitesStart = 102, BlacksStart = 111, SaturationStart = 120, VibranceStart = 129;
    private const int ContrastStart = 138, HighlightsStart = 187, ShadowsStart = 236;
    private const int MixerStart = 285, CalibrationStart = 405, ParametricStart = 435;
    private const int GradingStart = 455, GradingLuminanceStart = 743, Count = 763;
    private static readonly double[] Brightnesses = { 53, 80, 104, 130, 160, 190, 224 };
    private static readonly double[] AdaptiveValues = { -100, -50, -25, 0, 25, 50, 100 };

    private static readonly Lazy<byte[]?> LazyData = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Every table, red slowest then green then blue, 3 bytes a color; null when the resource is missing.</summary>
    public static byte[]? Data => LazyData.Value;
    public static bool Available => Data is not null;

    /// <summary>
    /// The resource holds, for each table and channel in turn, each entry's change from no change in steps of
    /// <c>step</c> levels, as a running difference from the entry before, deflated.
    /// </summary>
    private static byte[]? Load()
    {
        try
        {
            using var stream = typeof(CameraRawTables).Assembly.GetManifestResourceStream("Compositor.Core.Resources.CameraRawTables.bin");
            if (stream is null) return null;
            var header = new byte[10];
            stream.ReadExactly(header);
            if (header[0] != 'C' || header[1] != 'R' || header[2] != 'Z' || header[3] != '3') return null;
            int Field(int at) => header[at] | header[at + 1] << 8;
            int step = Field(8);
            if (Field(4) != Size || Field(6) != Count || step <= 0) return null;
            var planes = new byte[Count * Entries];
            using (var inflate = new ZLibStream(stream, CompressionMode.Decompress)) inflate.ReadExactly(planes);
            var tables = new byte[Count * Entries];
            const int cells = Size * Size * Size;
            var level = Enumerable.Range(0, Size).Select(i => (int)Math.Round(i * 255.0 / (Size - 1))).ToArray();
            Parallel.For(0, Count, table =>
            {
                for (int channel = 0; channel < 3; channel++)
                {
                    int baseIndex = (table * 3 + channel) * cells;
                    byte running = 0;
                    for (int cell = 0; cell < cells; cell++)
                    {
                        running = unchecked((byte)(running + planes[baseIndex + cell]));
                        int axis = channel == 0 ? cell / (Size * Size) : channel == 1 ? cell / Size % Size : cell % Size;
                        int value = level[axis] + (sbyte)running * step;
                        tables[table * Entries + cell * 3 + channel] = (byte)Math.Clamp(value, 0, 255);
                    }
                }
            });
            return tables;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Exposure's 21 tables, −5…5 by 0.5: the lens vignette correction is Exposure by position.</summary>
    public const int ExposureTables = ExposureStart;

    private static CameraRawStage Blend(params (int Table, double Weight)[] parts)
    {
        var tables = new int[8];
        var weights = new float[8];
        Array.Fill(tables, -1);
        for (int i = 0; i < Math.Min(8, parts.Length); i++) { tables[i] = parts[i].Table; weights[i] = (float)parts[i].Weight; }
        return new CameraRawStage(tables, weights);
    }

    private static int Offset(int index) => index * Entries;

    private static (int Index, double Fraction) Between(double value, double[] points)
    {
        value = Math.Min(points[^1], Math.Max(points[0], value));
        int index = 0;
        while (index < points.Length - 2 && value > points[index + 1]) index++;
        return (index, (value - points[index]) / (points[index + 1] - points[index]));
    }

    private static CameraRawStage Adaptive(int start, double value, double brightness)
    {
        var (row, down) = Between(brightness, Brightnesses);
        var (column, across) = Between(value, AdaptiveValues);
        int width = AdaptiveValues.Length, b = start + row * width + column;
        return Blend((Offset(b), (1 - down) * (1 - across)), (Offset(b + 1), (1 - down) * across),
                     (Offset(b + width), down * (1 - across)), (Offset(b + width + 1), down * across));
    }

    private static (int Index, double Fraction) Between(double value, double low, double step, int tables)
    {
        double position = Math.Min(tables - 1, Math.Max(0, (value - low) / step));
        int index = Math.Min(tables - 2, (int)position);
        return (index, position - index);
    }

    private static CameraRawStage Stage(int start, double value, double low, double step, int tables)
    {
        var (index, fraction) = Between(value, low, step, tables);
        return Blend((Offset(start + index), 1 - fraction), (Offset(start + index + 1), fraction));
    }

    private static CameraRawStage Fifty(int start, double value) => Stage(start, value, -100, 50, 5);

    public static CameraRawStage WhiteBalance(double temperature, double tint)
    {
        var (row, down) = Between(temperature, -100, 25, 9);
        var (column, across) = Between(tint, -100, 25, 9);
        return Blend((Offset(row * 9 + column), (1 - down) * (1 - across)), (Offset(row * 9 + column + 1), (1 - down) * across),
                     (Offset((row + 1) * 9 + column), down * (1 - across)), (Offset((row + 1) * 9 + column + 1), down * across));
    }

    /// <summary>
    /// What the adaptive sliders read from the picture: the mean of each pixel's brightest channel (Contrast), the mean
    /// luminance (Shadows) and the log-average of the brightest channel (Highlights), in linear light, as sRGB levels.
    /// </summary>
    public readonly record struct Brightness(double Brightest = 128, double Luminance = 128, double LogBrightest = 128);

    /// <summary>The stages in Camera Raw's order, leaving out sliders at zero.</summary>
    public static List<CameraRawStage> Stages(CameraRawSettings settings, Brightness brightness)
    {
        var stages = new List<CameraRawStage>();
        var c = settings.Calibration;
        var calibration = new[] { c.RedHue, c.RedSaturation, c.GreenHue, c.GreenSaturation, c.BlueHue, c.BlueSaturation };
        for (int i = 0; i < calibration.Length; i++) if (calibration[i] != 0) stages.Add(Fifty(CalibrationStart + i * 5, calibration[i]));
        if (settings.Exposure != 0) stages.Add(Stage(ExposureStart, settings.Exposure, -5, 0.5, 21));
        if (settings.Temperature != 0 || settings.Tint != 0) stages.Add(WhiteBalance(settings.Temperature, settings.Tint));
        if (settings.Contrast != 0) stages.Add(Adaptive(ContrastStart, settings.Contrast, brightness.Brightest));
        if (settings.Highlights != 0) stages.Add(Adaptive(HighlightsStart, settings.Highlights, brightness.LogBrightest));
        if (settings.Shadows != 0) stages.Add(Adaptive(ShadowsStart, settings.Shadows, brightness.Luminance));
        if (settings.Whites != 0) stages.Add(Stage(WhitesStart, settings.Whites, -100, 25, 9));
        if (settings.Blacks != 0) stages.Add(Stage(BlacksStart, settings.Blacks, -100, 25, 9));
        if (settings.Saturation != 0) stages.Add(Stage(SaturationStart, settings.Saturation, -100, 25, 9));
        if (settings.Vibrance != 0) stages.Add(Stage(VibranceStart, settings.Vibrance, -100, 25, 9));
        var curve = settings.Curve;
        if (curve.HasMeasuredDividers)
        {
            var values = new[] { curve.Shadows, curve.Darks, curve.Lights, curve.Highlights };
            for (int i = 0; i < 4; i++) if (values[i] != 0) stages.Add(Fifty(ParametricStart + i * 5, values[i]));
        }
        return stages;
    }

    /// <summary>
    /// Color Grading as Photoshop's draws it: the luminance sliders, then the global and midtone wheels, then
    /// highlights, then shadows, each blending the measured tables around its hue, saturation and Blending.
    /// </summary>
    public static List<CameraRawStage> GradingStages(CameraRawGradingSettings grading)
    {
        var stages = new List<CameraRawStage>();
        var wheels = new[] { grading.Shadows, grading.Midtones, grading.Highlights, grading.Global };
        for (int i = 0; i < 4; i++) if (wheels[i].Luminance != 0) stages.Add(Fifty(GradingLuminanceStart + i * 5, wheels[i].Luminance));
        double balance = grading.Balance / 100;
        double highlightShare = Math.Max(0, balance < 0 ? 1 + 1.4 * balance : 1 + 2 * balance);
        double shadowShare = Math.Max(0, balance < 0 ? 1 - 1.2 * balance : 1 - 1.4 * balance);
        var (blendLow, blendUp) = Between(grading.Blending, new double[] { 0, 50, 100 });
        int Section(int blendIndex, int wheel) => blendIndex switch
        {
            0 => GradingStart + wheel * 36,
            1 => GradingStart + 72 + wheel * 36,
            _ => GradingStart + 216 + wheel * 36,
        };
        foreach (var (wheel, color, share, blends) in new[] { (3, grading.Global, 1.0, false), (2, grading.Midtones, 1.0, false),
                     (1, grading.Highlights, highlightShare, true), (0, grading.Shadows, shadowShare, true) })
        {
            if (!(color.Saturation * share > 0)) continue;
            double position = color.Hue % 360 / 30;
            int first = (int)position % 12, second = (first + 1) % 12;
            double around = position - (int)position;
            var (level, up) = Between(color.Saturation * share, new double[] { 0, 25, 50, 100 });
            var parts = new List<(int, double)>();
            var blendings = blends ? new[] { (blendLow, 1 - blendUp), (blendLow + 1, blendUp) } : new[] { (1, 1.0) };
            foreach (var (blendIndex, blendWeight) in blendings)
            {
                int b = Section(blendIndex, wheel);
                foreach (var (hue, hueWeight) in new[] { (first, 1 - around), (second, around) })
                    foreach (var (saturation, saturationWeight) in new[] { (level, 1 - up), (level + 1, up) })
                        parts.Add((saturation == 0 ? -1 : Offset(b + hue * 3 + saturation - 1), blendWeight * hueWeight * saturationWeight));
            }
            stages.Add(Blend(parts.ToArray()));
        }
        return stages;
    }

    /// <summary>The Color Mixer's stages: luminance, then saturation, then hue.</summary>
    public static List<CameraRawStage> MixerStages(CameraRawMixerSettings mixer)
    {
        var stages = new List<CameraRawStage>();
        var kinds = new[] { mixer.Luminance, mixer.Saturation, mixer.Hue };
        for (int kind = 0; kind < 3; kind++)
            for (int color = 0; color < 8; color++)
                if (kinds[kind][color] != 0) stages.Add(Fifty(MixerStart + (kind * 8 + color) * 5, kinds[kind][color]));
        return stages;
    }

    /// <summary>No change, on the composing grid.</summary>
    public static float[]? Identity() => Data is null ? null : CameraRawPixels.Compose(Data, Grid, Array.Empty<CameraRawStage>(), Size);

    /// <summary>The stages run over the composing grid, or null when there are none.</summary>
    public static float[]? Compose(List<CameraRawStage> stages) =>
        stages.Count == 0 || Data is null ? null : CameraRawPixels.Compose(Data, Grid, stages, Size);

    /// <summary>
    /// The Temperature and Tint that turn a straight sRGB color (0…1) most nearly gray: a coarse search over both sliders,
    /// then finer ones around the best. Null when the tables are missing or the color has no channel to balance from.
    /// </summary>
    public static (double Temperature, double Tint)? Neutralize(double red, double green, double blue)
    {
        if (Data is not { } data || Math.Min(red, Math.Min(green, blue)) <= 1e-4) return null;
        double Cast(double temperature, double tint)
        {
            var color = new[] { red, green, blue };
            CameraRawPixels.StageColor(data, WhiteBalance(temperature, tint), Size, color);
            return color.Max() - color.Min();
        }
        var best = (Temperature: 0.0, Tint: 0.0, Cast: Cast(0, 0));
        foreach (var (step, reach) in new[] { (10.0, 100.0), (2.0, 10.0), (0.5, 2.0) })
        {
            var center = best;
            for (double temperature = center.Temperature - reach; temperature <= center.Temperature + reach + 1e-9; temperature += step)
                for (double tint = center.Tint - reach; tint <= center.Tint + reach + 1e-9; tint += step)
                {
                    if (Math.Abs(temperature) > 100 || Math.Abs(tint) > 100) continue;
                    double found = Cast(temperature, tint);
                    if (found < best.Cast) best = (temperature, tint, found);
                }
        }
        return (Math.Round(best.Temperature, 1), Math.Round(best.Tint, 1));
    }
}
