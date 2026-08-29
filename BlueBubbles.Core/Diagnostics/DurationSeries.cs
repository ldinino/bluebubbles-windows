namespace BlueBubbles.Core.Diagnostics;

/// <summary>
/// Accumulated timing samples for one measurement category. Count/total/min/max are exact for
/// every sample ever added; percentiles are computed over a bounded retained window so a long
/// session cannot grow without limit (<see cref="SamplesTruncated"/> reports when that kicked in).
/// Not thread-safe on its own — <see cref="PerfSession"/> owns the lock.
/// </summary>
public sealed class DurationSeries
{
    public const int MaxRetainedSamples = 4096;

    private readonly List<double> _samples = new();

    public int Count { get; private set; }
    public double TotalMs { get; private set; }
    public double MinMs { get; private set; }
    public double MaxMs { get; private set; }

    /// <summary>True once samples beyond <see cref="MaxRetainedSamples"/> were dropped, so the
    /// percentiles describe only the retained prefix.</summary>
    public bool SamplesTruncated => Count > MaxRetainedSamples;

    public void Add(double milliseconds)
    {
        if (Count == 0)
        {
            MinMs = milliseconds;
            MaxMs = milliseconds;
        }
        else
        {
            if (milliseconds < MinMs) MinMs = milliseconds;
            if (milliseconds > MaxMs) MaxMs = milliseconds;
        }

        Count++;
        TotalMs += milliseconds;

        if (_samples.Count < MaxRetainedSamples) _samples.Add(milliseconds);
    }

    public double MedianMs => Percentile(50);

    /// <summary>Linearly interpolated percentile over the retained samples. Returns 0 when empty.</summary>
    public double Percentile(double percentile)
    {
        if (_samples.Count == 0) return 0;

        var sorted = _samples.ToArray();
        Array.Sort(sorted);

        if (percentile <= 0) return sorted[0];
        if (percentile >= 100) return sorted[^1];

        var rank = percentile / 100.0 * (sorted.Length - 1);
        var low = (int)Math.Floor(rank);
        var high = (int)Math.Ceiling(rank);
        if (low == high) return sorted[low];

        return sorted[low] + (sorted[high] - sorted[low]) * (rank - low);
    }
}
