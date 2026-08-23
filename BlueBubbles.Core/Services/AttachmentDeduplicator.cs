using BlueBubbles.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace BlueBubbles.Core.Services;

/// <summary>
/// Repairs caches written before the attachment identity fix (B7). Until then the write path
/// deduped on the attachment GUID alone, so an attachment whose GUID Apple rewrote mid-transfer
/// (a plain UUID becoming <c>at_&lt;n&gt;_&lt;messageGuid&gt;</c>) was stored twice and rendered twice.
/// </summary>
public static class AttachmentDeduplicator
{
    /// <summary>
    /// Collapses attachment rows that share a message and a server <c>OriginalRowId</c> — the same
    /// server attachment stored more than once. Rows with distinct <c>OriginalRowId</c>s are left
    /// alone even when the file is identical, because a message legitimately can carry the same
    /// file twice. The most recently written row wins: it carries the GUID the server is currently
    /// serving, so the survivor is the one that can still be downloaded. Idempotent — a second run
    /// finds no groups. Returns the number of rows removed.
    /// </summary>
    /// <remarks>Cached files are deliberately left on disk. They live in per-GUID directories
    /// under the attachment cache root, so a discarded row's file is inert rather than dangling,
    /// and deleting it could not be undone if the survivor choice were ever wrong.</remarks>
    public static async Task<int> CollapseDuplicatesAsync(
        BlueBubblesDbContext db, CancellationToken ct = default)
    {
        var affectedMessageIds = await db.Attachments
            .Where(a => a.OriginalRowId != null)
            .GroupBy(a => new { a.MessageId, a.OriginalRowId })
            .Where(g => g.Count() > 1)
            .Select(g => g.Key.MessageId)
            .Distinct()
            .ToListAsync(ct);

        if (affectedMessageIds.Count == 0) return 0;

        var rows = await db.Attachments
            .Where(a => a.OriginalRowId != null && affectedMessageIds.Contains(a.MessageId))
            .ToListAsync(ct);

        var surplus = rows
            .GroupBy(a => (a.MessageId, a.OriginalRowId))
            .Where(g => g.Count() > 1)
            .SelectMany(g => g.OrderBy(a => a.Id).SkipLast(1))
            .ToList();

        if (surplus.Count == 0) return 0;

        foreach (var row in surplus)
        {
            AppLog.Debug(LogCategory.Sync,
                $"Collapsing duplicate attachment row: id={row.Id} messageId={row.MessageId} " +
                $"rowId={row.OriginalRowId} guid={row.Guid}");
        }

        db.Attachments.RemoveRange(surplus);
        await db.SaveChangesAsync(ct);

        AppLog.Info(LogCategory.Sync,
            $"Collapsed {surplus.Count} duplicate attachment row(s) across {affectedMessageIds.Count} message(s).");

        return surplus.Count;
    }
}
