using System.Diagnostics;

namespace BlueBubbles.Core.Diagnostics;

/// <summary>
/// Per-session rollup of UI draw measurements: timed categories (decode start -> landed,
/// thread-open -> first image visible) and plain counters (relayouts, cache hits, stale decodes),
/// plus one-shot "phases" whose first completion wins.
/// <para>Pure and UI-free on purpose: it lives in <c>BlueBubbles.Core</c> so the net8.0 test suite
/// can reach the rollup maths. The view layer only supplies samples. See B2b.</para>
/// </summary>
public sealed class PerfSession
{
    private readonly object _lock = new();
    private readonly Dictionary<string, DurationSeries> _durations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _counters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _openPhases = new(StringComparer.Ordinal);

    /// <summary>Converts a <see cref="Stopwatch"/> tick delta to milliseconds.</summary>
    public static double TicksToMs(long tickDelta) => tickDelta * 1000.0 / Stopwatch.Frequency;

    public void RecordDuration(string category, double milliseconds)
    {
        lock (_lock)
        {
            if (!_durations.TryGetValue(category, out var series))
                _durations[category] = series = new DurationSeries();
            series.Add(milliseconds);
        }
    }

    public void RecordEvent(string category, int increment = 1)
    {
        lock (_lock)
        {
            _counters.TryGetValue(category, out var current);
            _counters[category] = current + increment;
        }
    }

    /// <summary>Starts (or restarts) a one-shot phase. Restarting discards the previous start, so
    /// re-opening a thread measures the new open rather than the abandoned one.</summary>
    public void BeginPhase(string phase, long startTicks)
    {
        lock (_lock) _openPhases[phase] = startTicks;
    }

    /// <summary>Completes a phase started by <see cref="BeginPhase"/>, recording its duration under
    /// the same name. Returns false — recording nothing — when the phase is not open, which is what
    /// makes only the *first* completion after a begin count.</summary>
    public bool TryCompletePhase(string phase, long nowTicks, out double milliseconds)
    {
        milliseconds = 0;
        lock (_lock)
        {
            if (!_openPhases.TryGetValue(phase, out var startTicks)) return false;
            _openPhases.Remove(phase);

            milliseconds = TicksToMs(nowTicks - startTicks);

            if (!_durations.TryGetValue(phase, out var series))
                _durations[phase] = series = new DurationSeries();
            series.Add(milliseconds);
        }
        return true;
    }

    public bool IsPhaseOpen(string phase)
    {
        lock (_lock) return _openPhases.ContainsKey(phase);
    }

    public DurationSeries? TryGetDurations(string category)
    {
        lock (_lock) return _durations.TryGetValue(category, out var series) ? series : null;
    }

    public int GetCount(string category)
    {
        lock (_lock) return _counters.TryGetValue(category, out var value) ? value : 0;
    }

    public bool IsEmpty
    {
        get { lock (_lock) return _durations.Count == 0 && _counters.Count == 0; }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _durations.Clear();
            _counters.Clear();
            _openPhases.Clear();
        }
    }

    /// <summary>Renders the rollup as log-ready lines: one timing table, one counter table.</summary>
    public IReadOnlyList<string> Summarize(string title)
    {
        lock (_lock)
        {
            return PerfSummaryFormatter.Format(title, _durations, _counters);
        }
    }
}
