using BlueBubbles.Core.Diagnostics;
using BlueBubbles.Core.Services;

namespace BlueBubbles.Windows.Tests;

/// <summary>
/// Covers the B2b draw-timing rollup: the statistics themselves (which is why they live in Core),
/// the first-completion-wins phase semantics behind time-to-first-visible, and the guarantee that
/// the whole thing is inert while verbose logging is off.
/// </summary>
public class PerfStatsTests
{
    // ── DurationSeries maths ────────────────────────────────────────────────────────────────

    [Fact]
    public void DurationSeries_TotalCountsEverySample()
    {
        var series = new DurationSeries();
        foreach (var ms in new double[] { 5, 10, 15, 20 }) series.Add(ms);

        Assert.Equal(4, series.Count);
        Assert.Equal(50, series.TotalMs, 6);
        Assert.Equal(5, series.MinMs, 6);
        Assert.Equal(20, series.MaxMs, 6);
    }

    [Fact]
    public void DurationSeries_FirstSampleSetsBothBounds()
    {
        var series = new DurationSeries();
        series.Add(7);

        // Regression guard for a Min seeded at 0 (or a Max seeded at +inf): a single sample must
        // report itself for both, not 0.
        Assert.Equal(7, series.MinMs, 6);
        Assert.Equal(7, series.MaxMs, 6);
        Assert.Equal(7, series.MedianMs, 6);
    }

    [Fact]
    public void DurationSeries_MedianOfOddCountIsTheMiddleSample()
    {
        var series = new DurationSeries();
        // Deliberately out of order: the median must sort, not take the middle of insertion order.
        foreach (var ms in new double[] { 90, 10, 50, 70, 30 }) series.Add(ms);

        Assert.Equal(50, series.MedianMs, 6);
    }

    [Fact]
    public void DurationSeries_MedianOfEvenCountInterpolatesTheTwoMiddleSamples()
    {
        var series = new DurationSeries();
        foreach (var ms in new double[] { 10, 20, 30, 40 }) series.Add(ms);

        // Ranks 0..3; rank 1.5 sits halfway between 20 and 30.
        Assert.Equal(25, series.MedianMs, 6);
    }

    [Fact]
    public void DurationSeries_PercentilesPinTheRankFormula()
    {
        var series = new DurationSeries();
        for (var i = 1; i <= 101; i++) series.Add(i);   // 1..101, ranks 0..100

        Assert.Equal(1, series.Percentile(0), 6);
        Assert.Equal(26, series.Percentile(25), 6);
        Assert.Equal(51, series.Percentile(50), 6);
        Assert.Equal(96, series.Percentile(95), 6);
        Assert.Equal(101, series.Percentile(100), 6);
    }

    [Fact]
    public void DurationSeries_EmptySeriesReportsZerosRatherThanThrowing()
    {
        var series = new DurationSeries();

        Assert.Equal(0, series.Count);
        Assert.Equal(0, series.MedianMs, 6);
        Assert.Equal(0, series.Percentile(95), 6);
        Assert.False(series.SamplesTruncated);
    }

    [Fact]
    public void DurationSeries_CountTotalAndBoundsStayExactPastTheRetentionCap()
    {
        var series = new DurationSeries();
        var samples = DurationSeries.MaxRetainedSamples + 10;
        for (var i = 0; i < samples; i++) series.Add(2);

        Assert.Equal(samples, series.Count);
        Assert.Equal(samples * 2.0, series.TotalMs, 6);
        Assert.True(series.SamplesTruncated);
    }

    // ── Session accumulation ────────────────────────────────────────────────────────────────

    [Fact]
    public void Session_GroupsDurationsByCategory()
    {
        var session = new PerfSession();
        session.RecordDuration("attach.decode.image", 10);
        session.RecordDuration("attach.decode.image", 30);
        session.RecordDuration("avatar.decode", 5);

        Assert.Equal(2, session.TryGetDurations("attach.decode.image")!.Count);
        Assert.Equal(40, session.TryGetDurations("attach.decode.image")!.TotalMs, 6);
        Assert.Equal(1, session.TryGetDurations("avatar.decode")!.Count);
        Assert.Null(session.TryGetDurations("nothing.here"));
    }

    [Fact]
    public void Session_CountersAccumulate()
    {
        var session = new PerfSession();
        session.RecordEvent("avatar.relayout");
        session.RecordEvent("avatar.relayout");
        session.RecordEvent("avatar.relayout", 3);

        Assert.Equal(5, session.GetCount("avatar.relayout"));
        Assert.Equal(0, session.GetCount("never.fired"));
    }

    // ── Phase semantics (time-to-first-visible) ─────────────────────────────────────────────

    [Fact]
    public void Phase_OnlyTheFirstCompletionAfterABeginIsRecorded()
    {
        var session = new PerfSession();
        var ticksPerMs = System.Diagnostics.Stopwatch.Frequency / 1000.0;

        session.BeginPhase("open", 0);
        Assert.True(session.TryCompletePhase("open", (long)(250 * ticksPerMs), out var first));
        Assert.Equal(250, first, 0);

        // A second image landing must not overwrite or re-add: this is what makes the number a
        // time-to-FIRST-visible rather than a per-image duration.
        Assert.False(session.TryCompletePhase("open", (long)(900 * ticksPerMs), out var second));
        Assert.Equal(0, second);
        Assert.Equal(1, session.TryGetDurations("open")!.Count);
        Assert.False(session.IsPhaseOpen("open"));
    }

    [Fact]
    public void Phase_ReopeningRestartsTheClock()
    {
        var session = new PerfSession();
        var ticksPerMs = System.Diagnostics.Stopwatch.Frequency / 1000.0;

        session.BeginPhase("open", 0);
        session.BeginPhase("open", (long)(1000 * ticksPerMs));   // thread re-opened; discard the first start
        Assert.True(session.TryCompletePhase("open", (long)(1100 * ticksPerMs), out var ms));

        Assert.Equal(100, ms, 0);
    }

    [Fact]
    public void Phase_CompletingWithoutABeginRecordsNothing()
    {
        var session = new PerfSession();

        Assert.False(session.TryCompletePhase("open", 12345, out _));
        Assert.Null(session.TryGetDurations("open"));
    }

    // ── Formatting ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Summary_ReportsCountsTotalsAndTheSpreadForEachCategory()
    {
        var session = new PerfSession();
        foreach (var ms in new double[] { 10, 20, 30 }) session.RecordDuration("attach.decode.image", ms);
        session.RecordEvent("avatar.relayout.by:AvatarImage", 7);

        var text = string.Join("\n", session.Summarize("probe"));

        Assert.Contains("==== probe ====", text);
        var row = session.Summarize("probe").Single(l => l.StartsWith("attach.decode.image", StringComparison.Ordinal));
        // n, total, min, median, max — the numbers a reader needs without counting log lines.
        Assert.Contains("3", row);
        Assert.Contains("60.0", row);
        Assert.Contains("10.0", row);
        Assert.Contains("20.0", row);
        Assert.Contains("30.0", row);
        Assert.Contains("avatar.relayout.by:AvatarImage", text);
        Assert.Contains("7", text);
        Assert.Contains("==== end ====", text);
    }

    [Fact]
    public void Summary_OfAnEmptySessionSaysSoInsteadOfPrintingAnEmptyTable()
    {
        var session = new PerfSession();

        var text = string.Join("\n", session.Summarize("probe"));

        Assert.Contains("no samples", text);
    }

    // ── The off path ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Facade_IsInertWhileVerboseLoggingIsOff()
    {
        var restore = AppLog.MinLevel;
        try
        {
            AppLog.MinLevel = LogLevel.Info;   // the shipped default: verbose logging off
            PerfStats.Session.Reset();

            Assert.False(PerfStats.IsEnabled);
            // The 0 sentinel is what keeps the hot path from allocating a Stopwatch at all.
            Assert.Equal(0, PerfStats.Timestamp());

            PerfStats.Duration("attach.decode.image", System.Diagnostics.Stopwatch.GetTimestamp());
            PerfStats.Count("avatar.relayout");
            PerfStats.BeginPhase(PerfStats.ThreadOpenToFirstImage);
            var completed = PerfStats.CompletePhase(PerfStats.ThreadOpenToFirstImage);

            Assert.Null(completed);
            Assert.False(PerfStats.Session.IsPhaseOpen(PerfStats.ThreadOpenToFirstImage));
            Assert.True(PerfStats.Session.IsEmpty);
        }
        finally
        {
            AppLog.MinLevel = restore;
            PerfStats.Session.Reset();
        }
    }

    [Fact]
    public void Facade_RecordsOnceVerboseLoggingIsOn()
    {
        var restore = AppLog.MinLevel;
        try
        {
            AppLog.MinLevel = LogLevel.Debug;
            PerfStats.Session.Reset();

            Assert.True(PerfStats.IsEnabled);
            Assert.NotEqual(0, PerfStats.Timestamp());

            PerfStats.Count("avatar.relayout", 2);
            PerfStats.Duration("attach.decode.image", PerfStats.Timestamp());
            PerfStats.BeginPhase(PerfStats.ThreadOpenToFirstImage);
            Assert.NotNull(PerfStats.CompletePhase(PerfStats.ThreadOpenToFirstImage));

            Assert.Equal(2, PerfStats.Session.GetCount("avatar.relayout"));
            Assert.Equal(1, PerfStats.Session.TryGetDurations("attach.decode.image")!.Count);
            Assert.Equal(1, PerfStats.Session.TryGetDurations(PerfStats.ThreadOpenToFirstImage)!.Count);
        }
        finally
        {
            AppLog.MinLevel = restore;
            PerfStats.Session.Reset();
        }
    }

    [Fact]
    public void Facade_DurationIgnoresTheZeroSentinelEvenWhenEnabled()
    {
        var restore = AppLog.MinLevel;
        try
        {
            AppLog.MinLevel = LogLevel.Debug;
            PerfStats.Session.Reset();

            // A start captured while logging was off must not be measured against a "now" taken
            // after it was switched on — that would report the whole gap as a decode.
            PerfStats.Duration("attach.decode.image", 0);

            Assert.True(PerfStats.Session.IsEmpty);
        }
        finally
        {
            AppLog.MinLevel = restore;
            PerfStats.Session.Reset();
        }
    }
}
