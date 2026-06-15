using BlueBubbles.Core.Data;
using BlueBubbles.Core.Models;
using BlueBubbles.Core.Utils;
using Microsoft.EntityFrameworkCore;

namespace BlueBubbles.Core.Services;

/// <summary>
/// Applies a freshly-fetched server window to the local cache as the authority for its range:
/// it upserts everything the server returned AND soft-deletes any local message inside that
/// [oldest..newest] span the server omitted. This is how a delete made on the server (or another
/// device) that we never saw over the socket finally converges — a plain upsert can only ever
/// add/modify rows, never remove them, so without this a server-side delete lives forever locally.
/// </summary>
internal static class MessageWindowReconciler
{
    /// <summary>Reconciles <paramref name="serverMessages"/> (a contiguous newest-or-range page for a
    /// single chat) into the cache and returns the number of locally soft-deleted messages. The caller
    /// owns the fetch and its guards (non-error response, non-empty page) and the surrounding save
    /// lock / db context.</summary>
    public static async Task<int> ReconcileWindowAsync(
        BlueBubblesDbContext db, int chatId, List<Message> serverMessages,
        Dictionary<string, int> handleCache, CancellationToken ct,
        IReadOnlySet<string>? protectedGuids = null)
    {
        await MessagePersistenceHelper.SaveMessagesAsync(
            db, chatId, serverMessages, handleCache, ct, protectedGuids);

        // The returned set defines the authoritative range. Bounding deletion to [oldest..newest] of
        // THIS page is what makes a partial/short page safe: messages older than the page (we never
        // asked about them) and a just-sent message newer than the page are both outside the span, so
        // only genuinely-removed messages inside a fully-covered span get pruned.
        var dated = serverMessages.Where(m => m.DateCreated.HasValue).ToList();
        if (dated.Count == 0) return 0;

        var serverOldest = dated.Min(m => m.DateCreated!.Value);
        var serverNewest = dated.Max(m => m.DateCreated!.Value);
        var serverGuids = serverMessages.Select(m => m.Guid).ToHashSet();

        var candidates = await db.Messages
            .Where(m => m.ChatId == chatId
                && m.DateDeleted == null
                && m.AssociatedMessageGuid == null          // reactions reconcile via their own rebuild path
                && m.DateCreated >= serverOldest
                && m.DateCreated <= serverNewest
                && !m.Guid.StartsWith("temp-"))             // never prune an unconfirmed optimistic send
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var pruned = 0;
        foreach (var m in candidates)
        {
            if (serverGuids.Contains(m.Guid)) continue;
            if (protectedGuids?.Contains(m.Guid) == true) continue;
            m.DateDeleted = now;
            pruned++;
        }

        if (pruned > 0)
        {
            await db.SaveChangesAsync(ct);
            AppLog.Info(LogCategory.Sync,
                $"Window reconcile soft-deleted {pruned} message(s) the server no longer has");
        }

        return pruned;
    }
}
