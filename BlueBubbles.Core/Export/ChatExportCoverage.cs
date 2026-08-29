using BlueBubbles.Core.Services;

namespace BlueBubbles.Core.Export;

/// <summary>How much of a conversation an export actually contains.</summary>
public enum ExportCoverageKind
{
    /// <summary>The local cache has been paged back to the first message in the conversation.</summary>
    ReachesBeginning,

    /// <summary>The cache holds messages back to a known watermark, and no further.</summary>
    PartialFromWatermark,

    /// <summary>The chat has never been paged backwards, so how much is missing is unknown.</summary>
    Unknown,

    /// <summary>No messages were exported at all.</summary>
    Empty
}

/// <summary>The export's statement about itself.</summary>
public sealed record ExportCoverage(
    ExportCoverageKind Kind,
    bool ReachesBeginning,
    string? OldestSynced,
    string? OldestExported,
    string? NewestExported,
    int MessageCount,
    bool OlderHistoryIsReachable,
    string Statement);

/// <summary>
/// Describes how far back an export actually reaches.
///
/// This exists because the local SQLite cache is <b>not</b> the full conversation:
/// <c>MessagesService.LoadMessagesAsync</c> reads the cache only, and
/// <c>ChatEntity.OldestSyncedMessageDate</c> is a per-chat pagination watermark. An export that
/// stays silent about this produces a partial archive that looks complete, which is the worst
/// outcome for someone keeping it as a record.
///
/// The watermark has <b>three</b> states, not two:
/// <list type="bullet">
/// <item><c>0</c> - the server returned no older page, so the cache reaches the beginning.</item>
/// <item><c>&gt; 0</c> - the oldest message fetched so far; older history exists.</item>
/// <item><c>null</c> - never paged backwards. Coverage is <b>unknown</b>, not complete.</item>
/// </list>
/// </summary>
public static class ChatExportCoverage
{
    public static ExportCoverage Describe(
        long? oldestSyncedMessageDate,
        long? oldestExportedDate,
        long? newestExportedDate,
        int messageCount,
        TimeSpan offset,
        DateTimeOffset now)
    {
        var oldestSynced = ExportTimestamp.ToIso(oldestSyncedMessageDate is > 0 ? oldestSyncedMessageDate : null, offset);
        var oldestExported = ExportTimestamp.ToIso(oldestExportedDate, offset);
        var newestExported = ExportTimestamp.ToIso(newestExportedDate, offset);

        if (messageCount <= 0)
        {
            return new ExportCoverage(
                ExportCoverageKind.Empty, false, oldestSynced, null, null, 0, false,
                "No messages were exported for this conversation. Nothing was found in the local cache.");
        }

        // Reached the beginning: the only state that may claim completeness.
        if (oldestSyncedMessageDate == 0)
        {
            return new ExportCoverage(
                ExportCoverageKind.ReachesBeginning, true, null, oldestExported, newestExported,
                messageCount, false,
                $"Complete: this export contains all {messageCount} message(s) known for this "
                + $"conversation, back to the first one ({oldestExported}).");
        }

        if (oldestSyncedMessageDate is null)
        {
            return new ExportCoverage(
                ExportCoverageKind.Unknown, false, null, oldestExported, newestExported,
                messageCount, true,
                $"INCOMPLETE - coverage unknown: this export contains {messageCount} message(s), "
                + $"the oldest dated {oldestExported}. This conversation has never been paged "
                + "backwards, so there is no record of how much earlier history exists. Older "
                + "messages are very likely missing.");
        }

        var reachable = IsOlderHistoryReachable(oldestSyncedMessageDate.Value, now);
        var statement =
            $"INCOMPLETE: this export contains {messageCount} message(s), the oldest dated "
            + $"{oldestExported}. The local cache has only been synced back to {oldestSynced}; "
            + "earlier messages exist on the server and are NOT in this export.";

        if (!reachable)
        {
            statement += $" Loading more history will not recover them: the app's older-message "
                + $"sync refuses to page back further than {MessagesService.MaxSyncHistoryDays} days.";
        }
        else
        {
            statement += " Load more history in the conversation before exporting to include more.";
        }

        return new ExportCoverage(
            ExportCoverageKind.PartialFromWatermark, false, oldestSynced, oldestExported,
            newestExported, messageCount, reachable, statement);
    }

    /// <summary>Whether the app's own older-message sync can still page back past the watermark.
    /// Mirrors the guard in <c>MessagesService.FetchOlderMessagesFromServerAsync</c>.</summary>
    public static bool IsOlderHistoryReachable(long oldestSyncedMessageDate, DateTimeOffset now)
    {
        if (oldestSyncedMessageDate <= 0) return false;
        var cutoff = now.AddDays(-MessagesService.MaxSyncHistoryDays).ToUnixTimeMilliseconds();
        return oldestSyncedMessageDate > cutoff;
    }
}
