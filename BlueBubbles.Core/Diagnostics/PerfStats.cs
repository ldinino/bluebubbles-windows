using System.Diagnostics;
using BlueBubbles.Core.Services;

namespace BlueBubbles.Core.Diagnostics;

/// <summary>
/// Process-wide entry point for draw/decode measurement (B2b). Every method is a no-op unless
/// verbose logging is on, so the instrumentation costs nothing on the recycled-container hot path
/// when it is off: <see cref="Timestamp"/> returns the 0 sentinel, and the record methods return
/// before touching the session.
/// <para>Call sites should still wrap message building in
/// <c>if (AppLog.IsEnabled(LogLevel.Debug))</c> — this gate stops the aggregation, not the string
/// interpolation the caller would do.</para>
/// </summary>
public static class PerfStats
{
    /// <summary>Name of the thread-open phase whose first completion is time-to-first-visible.</summary>
    public const string ThreadOpenToFirstImage = "thread.open->first-image";

    public static PerfSession Session { get; } = new();

    public static bool IsEnabled => AppLog.IsEnabled(LogLevel.Debug);

    /// <summary>A start timestamp for a later <see cref="Duration"/>, or the 0 sentinel ("not
    /// measuring") when disabled. Returns a raw <c>long</c> rather than a Stopwatch so the off path
    /// allocates nothing.</summary>
    public static long Timestamp() => IsEnabled ? Stopwatch.GetTimestamp() : 0;

    public static void Duration(string category, long startTimestamp)
    {
        if (startTimestamp == 0 || !IsEnabled) return;
        Session.RecordDuration(category, PerfSession.TicksToMs(Stopwatch.GetTimestamp() - startTimestamp));
    }

    public static void Count(string category, int increment = 1)
    {
        if (!IsEnabled) return;
        Session.RecordEvent(category, increment);
    }

    public static void BeginPhase(string phase)
    {
        if (!IsEnabled) return;
        Session.BeginPhase(phase, Stopwatch.GetTimestamp());
    }

    /// <summary>Completes <paramref name="phase"/> if it is open, returning the elapsed
    /// milliseconds. Null means nothing was recorded (phase not open, or measurement disabled).</summary>
    public static double? CompletePhase(string phase)
    {
        if (!IsEnabled) return null;
        return Session.TryCompletePhase(phase, Stopwatch.GetTimestamp(), out var ms) ? ms : null;
    }

    /// <summary>Writes the session rollup to the log at Info, so the dump is readable even after
    /// verbose logging has been switched back off.</summary>
    public static void Dump(string title = "Perf summary (session)")
    {
        foreach (var line in Session.Summarize(title))
            AppLog.Info(LogCategory.Ui, line);
    }
}
