using BlueBubbles.Core.Data.Entities;
using BlueBubbles.Core.Export;

namespace BlueBubbles.Windows.Tests;

/// <summary>
/// The honesty requirement. The local cache is not the full history, so an export must state its
/// own coverage; a partial archive that looks complete is the worst outcome for someone keeping
/// it as a record. These tests are the guard on that claim.
/// </summary>
public class ChatExportCoverageTests
{
    private static readonly TimeSpan Offset = TimeSpan.FromHours(-5);
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    private const long Recent = 1785715004204;  // 2026-08-02, inside the 365-day sync window
    private const long Ancient = 1500000000000; // 2017-07-14, outside it

    private static ExportCoverage Describe(long? watermark, int count = 10) =>
        ChatExportCoverage.Describe(watermark, Recent - 1000, Recent, count, Offset, Now);

    [Fact]
    public void WatermarkZero_IsTheOnlyStateThatMayClaimCompleteness()
    {
        var c = Describe(0);

        Assert.Equal(ExportCoverageKind.ReachesBeginning, c.Kind);
        Assert.True(c.ReachesBeginning);
        Assert.Contains("Complete", c.Statement);
    }

    [Fact]
    public void WatermarkAboveZero_MustNotClaimCompleteness()
    {
        var c = Describe(Recent);

        Assert.Equal(ExportCoverageKind.PartialFromWatermark, c.Kind);
        Assert.False(c.ReachesBeginning);
        Assert.Contains("INCOMPLETE", c.Statement);
        Assert.DoesNotContain("Complete:", c.Statement);
    }

    [Fact]
    public void WatermarkNull_IsUnknownCoverage_NotCompleteness()
    {
        // The third state. 381 of 483 chats in the real cache have a NULL watermark; treating
        // null as "nothing older to fetch" (which FetchOlderMessagesFromServerAsync does) would
        // report almost every chat as a complete archive.
        var c = Describe(null);

        Assert.Equal(ExportCoverageKind.Unknown, c.Kind);
        Assert.False(c.ReachesBeginning);
        Assert.Contains("coverage unknown", c.Statement);
        Assert.DoesNotContain("Complete:", c.Statement);
    }

    [Fact]
    public void EmptyChat_IsNotReportedAsComplete()
    {
        var c = ChatExportCoverage.Describe(0, null, null, 0, Offset, Now);

        Assert.Equal(ExportCoverageKind.Empty, c.Kind);
        Assert.False(c.ReachesBeginning);
    }

    [Fact]
    public void PartialBeyondTheSyncCeiling_SaysMoreHistoryCannotBeRecovered()
    {
        // MessagesService.FetchOlderMessagesFromServerAsync refuses to page back past
        // MaxSyncHistoryDays, so "load more history" is not a remedy for these chats.
        var c = Describe(Ancient);

        Assert.False(c.OlderHistoryIsReachable);
        Assert.Contains("will not recover them", c.Statement);
        Assert.Contains($"{BlueBubbles.Core.Services.MessagesService.MaxSyncHistoryDays} days", c.Statement);
    }

    [Fact]
    public void PartialWithinTheSyncCeiling_TellsTheUserToLoadMoreFirst()
    {
        var c = Describe(Recent);

        Assert.True(c.OlderHistoryIsReachable);
        Assert.Contains("Load more history", c.Statement);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(Recent)]
    [InlineData(Ancient)]
    public void AnyNonZeroWatermark_YieldsAnExportThatDeclaresItselfIncomplete(long? watermark)
    {
        var export = ChatExportBuilder.Build(
            "iMessage;-;+15550001111", "Test Chat", [], watermark,
            [new MessageEntity { Guid = "m1", Text = "hi", DateCreated = Recent }],
            Offset, Now);

        Assert.False(export.Coverage.ReachesBeginning);

        // The statement is surfaced at the top of the transcript, above any message.
        var lines = ChatExportTranscript.Render(export);
        var coverageAt = lines.ToList().FindIndex(l => l == "COVERAGE");
        var firstMessageAt = lines.ToList().FindIndex(l => l.Contains("hi"));

        Assert.True(coverageAt >= 0);
        Assert.True(coverageAt < firstMessageAt);
        Assert.Contains(lines, l => l.Contains("INCOMPLETE"));
    }

    [Fact]
    public void Manifest_CountsIncompleteChats()
    {
        var complete = ChatExportBuilder.Build(
            "chat-a", "A", [], 0,
            [new MessageEntity { Guid = "m1", Text = "hi", DateCreated = Recent }], Offset, Now);
        var partial = ChatExportBuilder.Build(
            "chat-b", "B", [], Recent,
            [new MessageEntity { Guid = "m2", Text = "hi", DateCreated = Recent }], Offset, Now);

        var json = ChatExportSerializer.ToManifestJson(
        [
            ChatExportSerializer.ToManifestEntry(complete, "a.jsonl", "a.txt"),
            ChatExportSerializer.ToManifestEntry(partial, "b.jsonl", "b.txt"),
        ], Now);

        Assert.Contains("\"incompleteChatCount\": 1", json);
        Assert.Contains("unencrypted plain text", json);
    }
}
