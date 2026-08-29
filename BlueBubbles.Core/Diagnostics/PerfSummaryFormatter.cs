using System.Globalization;

namespace BlueBubbles.Core.Diagnostics;

/// <summary>
/// Turns a <see cref="PerfSession"/> rollup into fixed-width text. Separated from the session so
/// the layout is unit-testable without touching the accumulation, and so the whole thing stays in
/// Core where the net8.0 suite can reach it.
/// </summary>
public static class PerfSummaryFormatter
{
    private const int NameWidth = 42;

    public static IReadOnlyList<string> Format(
        string title,
        IReadOnlyDictionary<string, DurationSeries> durations,
        IReadOnlyDictionary<string, int> counters)
    {
        var lines = new List<string> { $"==== {title} ====" };

        if (durations.Count == 0 && counters.Count == 0)
        {
            lines.Add("(no samples — verbose logging was off for this session)");
            lines.Add("==== end ====");
            return lines;
        }

        if (durations.Count > 0)
        {
            lines.Add($"{Pad("timings (ms)")}{Num("n")}{Num("total")}{Num("min")}{Num("median")}{Num("p95")}{Num("max")}");
            foreach (var name in durations.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                var s = durations[name];
                lines.Add(
                    Pad(name)
                    + Num(s.Count.ToString(CultureInfo.InvariantCulture))
                    + Num(Ms(s.TotalMs))
                    + Num(Ms(s.MinMs))
                    + Num(Ms(s.MedianMs))
                    + Num(Ms(s.Percentile(95)))
                    + Num(Ms(s.MaxMs))
                    + (s.SamplesTruncated ? "  (percentiles over first 4096)" : string.Empty));
            }
        }

        if (counters.Count > 0)
        {
            lines.Add($"{Pad("counters")}{Num("n")}");
            foreach (var name in counters.Keys.OrderBy(k => k, StringComparer.Ordinal))
                lines.Add(Pad(name) + Num(counters[name].ToString(CultureInfo.InvariantCulture)));
        }

        lines.Add("==== end ====");
        return lines;
    }

    private static string Pad(string text) =>
        text.Length >= NameWidth ? text + " " : text.PadRight(NameWidth);

    private static string Num(string text) => text.PadLeft(10);

    private static string Ms(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);
}
