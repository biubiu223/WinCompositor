using Compositor.Core.Localization;

namespace Compositor.Core.Adjustments;

/// <summary>The Levels eyedroppers: set the black, gray or white point from a clicked color.</summary>
public enum LevelsSample { Black, Gray, White }

/// <summary>Auto options for Levels. Mirrors LevelsAuto in LevelsAutomatic.swift.</summary>
public enum LevelsAuto { Contrast, Color, Neutral }

public static class LevelsAutomatic
{
    public static string ToDisplayName(this LevelsAuto mode) => mode switch
    {
        LevelsAuto.Contrast => L.T("增强单色对比度", "Enhance Monochromatic Contrast"),
        LevelsAuto.Color => L.T("增强每通道的对比度", "Enhance Per Channel Contrast"),
        _ => L.T("查找深色与浅色并对齐中性中间调", "Find Dark & Light Colors, Snap Neutral Midtones"),
    };

    /// <summary>Clips 0.1% at each end: one shared interval (Contrast) or one per channel (Color, Neutral).</summary>
    public static LevelsSettings Settings(this LevelsAuto mode, double[][] histogram)
    {
        var result = new LevelsSettings();
        static (double Low, double High)? Endpoints(double[] bins)
        {
            double total = bins.Sum();
            if (total <= 0) return null;
            double sum = 0;
            int low = 0, high = 255;
            for (int i = 0; i < 256; i++) { sum += bins[i]; if (sum > total * 0.001) { low = i; break; } }
            sum = 0;
            for (int i = 255; i >= 0; i--) { sum += bins[i]; if (sum > total * 0.001) { high = i; break; } }
            return low < high ? (low, high) : null;
        }
        if (mode == LevelsAuto.Contrast)
        {
            // A shared interval keeps the channels' relationships.
            var limits = histogram.Skip(1).Select(Endpoints).Where(l => l is not null).Select(l => l!.Value).ToList();
            if (limits.Count > 0)
            {
                double low = limits.Min(l => l.Low), high = limits.Max(l => l.High);
                if (low < high) result.Ranges[0] = new LevelRange(Black: low, White: high);
            }
            return result;
        }
        for (int c = 1; c <= 3; c++)
        {
            if (Endpoints(histogram[c]) is not { } ends) continue;
            var range = new LevelRange(Black: ends.Low, White: ends.High);
            if (mode == LevelsAuto.Neutral)
            {
                double total = histogram[c].Sum();
                double mean = 0;
                for (int i = 0; i < 256; i++) mean += range.Apply(i / 255.0) * histogram[c][i];
                mean /= total;
                if (mean > 0 && mean < 1) range = range with { Gamma = Math.Clamp(Math.Log(mean) / Math.Log(0.5), 0.1, 9.99) };
            }
            result.Ranges[c] = range;
        }
        return result;
    }

    /// <summary>
    /// The settings with a point taken from an unpremultiplied color (0–1 per channel): all three channels are
    /// calibrated together and the composite range reset.
    /// </summary>
    public static LevelsSettings Sampling(this LevelsSettings settings, double red, double green, double blue, LevelsSample mode)
    {
        var result = settings.Clone();
        result.Ranges[0] = new LevelRange();
        double[] rgb = { red, green, blue };
        for (int c = 1; c <= 3; c++)
        {
            var range = result.Ranges[c];
            double v = rgb[c - 1] * 255;
            switch (mode)
            {
                case LevelsSample.Black: range = range with { Black = Math.Min(range.White - 1, Math.Max(0, v)) }; break;
                case LevelsSample.White: range = range with { White = Math.Max(range.Black + 1, Math.Min(255, v)) }; break;
                default:
                    double fraction = (v - range.Black) / (range.White - range.Black);
                    if (fraction <= 0 || fraction >= 1) continue;
                    range = range with { Gamma = Math.Log(fraction) / Math.Log(0.5) };
                    break;
            }
            result.Ranges[c] = (range with { OutputBlack = 0, OutputWhite = 255 }).Normalized;
        }
        return result;
    }

    /// <summary>Display-only vertical scale: linear, but isolated spikes are capped so a solid background can't flatten the rest.</summary>
    public static double DisplayScale(double[] bins)
    {
        double peak = bins.Where(b => double.IsFinite(b) && b > 0).DefaultIfEmpty(0).Max();
        if (peak <= 0) return 0;
        var interior = bins.Skip(1).Take(254).Where(b => double.IsFinite(b) && b > 0).OrderBy(b => b).ToList();
        if (interior.Count == 0) return peak;
        double typical = interior[(int)((interior.Count - 1) * 0.95)];
        return Math.Min(peak, typical * 4);
    }
}
