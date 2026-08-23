using System.Text.Json;
using BlueBubbles.Core.Data;
using BlueBubbles.Core.Data.Entities;
using BlueBubbles.Core.Models;
using BlueBubbles.Core.Utils;
using Microsoft.EntityFrameworkCore;

namespace BlueBubbles.Core.Services;

/// <summary>The single writer of message rows. Every path that creates or mutates a
/// <see cref="MessageEntity"/> — bulk sync, window reconcile, live socket save, edit/unsend
/// update, soft delete — goes through this file, so the server-field set and the client-owned
/// field rule have exactly one definition. Identity is the server <c>Guid</c> on every path;
/// the only other key in the cache is the <c>temp-</c> prefix of an unconfirmed optimistic
/// send, which no writer here matches. Two adds that miss each other's field list is the shape
/// of B2 (attachments written on one path only) and B7 (two identity schemes).</summary>
internal static class MessagePersistenceHelper
{
    public static async Task<(long? OldestDate, long? LatestDate, long MaxRowId)> SaveMessagesAsync(
        BlueBubblesDbContext db, int chatId, List<Message> messages,
        Dictionary<string, int> handleCache, CancellationToken ct,
        IReadOnlySet<string>? protectedGuids = null)
    {
        long? latestDate = null;
        long? oldestDate = null;
        long maxRowId = 0;

        // A true-up must never overwrite a message that has an un-acked local mutation pending
        // (an edit/unsend/delete sitting in the outbox). The server's copy doesn't reflect that
        // change yet, so applying it would visibly revert the user's action until the op confirms.
        if (protectedGuids is { Count: > 0 })
            messages = messages.Where(m => !protectedGuids.Contains(m.Guid)).ToList();

        foreach (var msg in messages)
        {
            if (msg.OriginalRowId.HasValue && msg.OriginalRowId.Value > maxRowId)
                maxRowId = msg.OriginalRowId.Value;

            int? msgHandleId = null;
            if (msg.Handle is not null)
            {
                var key = msg.Handle.Address + "|" + msg.Handle.Service;
                if (!handleCache.TryGetValue(key, out var hId) || hId <= 0)
                {
                    var existing = await db.Handles.FirstOrDefaultAsync(
                        h => h.Address == msg.Handle.Address && h.Service == msg.Handle.Service, ct);
                    if (existing is not null)
                    {
                        hId = existing.Id;
                    }
                    else
                    {
                        var newHandle = new HandleEntity
                        {
                            Address = msg.Handle.Address,
                            Service = msg.Handle.Service,
                            Country = msg.Handle.Country,
                            FormattedAddress = msg.Handle.FormattedAddress
                        };
                        db.Handles.Add(newHandle);
                        await db.SaveChangesAsync(ct);
                        hId = newHandle.Id;
                    }
                    handleCache[key] = hId;
                }
                msgHandleId = hId > 0 ? hId : null;
            }

            var entity = await db.Messages.FirstOrDefaultAsync(
                m => m.Guid == msg.Guid, ct);

            var isNew = entity is null;
            if (entity is null)
            {
                entity = new MessageEntity { Guid = msg.Guid };
                db.Messages.Add(entity);
            }

            ApplyServerFields(entity, msg, chatId, msgHandleId, isNew);

            if (msg.DateCreated.HasValue)
            {
                if (latestDate is null || msg.DateCreated > latestDate)
                    latestDate = msg.DateCreated;
                if (oldestDate is null || msg.DateCreated < oldestDate)
                    oldestDate = msg.DateCreated;
            }
        }

        await db.SaveChangesAsync(ct);

        await SaveAttachmentsAsync(db, messages, ct);

        if (latestDate.HasValue)
        {
            var chatEntity = await db.Chats.FindAsync([chatId], ct);
            if (chatEntity is not null &&
                (chatEntity.LatestMessageDate is null || latestDate > chatEntity.LatestMessageDate))
            {
                chatEntity.LatestMessageDate = latestDate;
                await db.SaveChangesAsync(ct);
            }
        }

        return (oldestDate, latestDate, maxRowId);
    }

    /// <summary>Copies the server-owned fields of <paramref name="msg"/> onto
    /// <paramref name="entity"/>. The one definition of what a message row holds: both the sync
    /// upsert above and the live insert below call it, so a field can no longer be written on one
    /// path and forgotten on the other.</summary>
    private static void ApplyServerFields(
        MessageEntity entity, Message msg, int chatId, int? handleId, bool isNew)
    {
        entity.ChatId = chatId;
        entity.HandleId = handleId;
        entity.OriginalRowId = msg.OriginalRowId;
        entity.OtherHandle = msg.OtherHandle;
        entity.Text = msg.Text;
        entity.Subject = msg.Subject;
        entity.Country = msg.Country;
        entity.Error = msg.Error;
        entity.DateCreated = msg.DateCreated;
        entity.DateRead = msg.DateRead;
        entity.DateDelivered = msg.DateDelivered;
        entity.IsDelivered = msg.IsDelivered;
        entity.IsFromMe = msg.IsFromMe;
        entity.HasDdResults = msg.HasDdResults;
        entity.DatePlayed = msg.DatePlayed;
        entity.ItemType = msg.ItemType;
        entity.GroupTitle = msg.GroupTitle;
        entity.GroupActionType = msg.GroupActionType;
        entity.BalloonBundleId = msg.BalloonBundleId;
        entity.AssociatedMessageGuid = ReactionTypes.NormalizeAssociatedGuid(msg.AssociatedMessageGuid);
        entity.AssociatedMessagePart = msg.AssociatedMessageGuid is not null
            ? ReactionTypes.ResolveAssociatedPart(msg.AssociatedMessageGuid, msg.AssociatedMessagePart)
            : msg.AssociatedMessagePart;
        entity.AssociatedMessageType = msg.AssociatedMessageType;
        entity.ExpressiveSendStyleId = msg.ExpressiveSendStyleId;
        entity.HasAttachments = msg.HasAttachments;
        entity.HasReactions = msg.HasReactions;
        entity.DateDeleted = msg.DateDeleted;
        entity.ThreadOriginatorGuid = msg.ThreadOriginatorGuid;
        entity.ThreadOriginatorPart = msg.ThreadOriginatorPart;
        entity.HasApplePayloadData = msg.HasApplePayloadData;
        entity.DateEdited = msg.DateEdited;
        entity.WasDeliveredQuietly = msg.WasDeliveredQuietly;
        entity.DidNotifyRecipient = msg.DidNotifyRecipient;
        // IsBookmarked is client-owned (the server has no concept of it and defaults it false).
        // Seed it on insert, but never let a re-fetch/true-up clear a locally-set bookmark.
        if (isNew) entity.IsBookmarked = msg.IsBookmarked;
        entity.MetadataJson = Serialize(msg.Metadata);
        entity.AttributedBodyJson = Serialize(msg.AttributedBody);
        entity.MessageSummaryInfoJson = Serialize(msg.MessageSummaryInfo);
        entity.PayloadDataJson = Serialize(msg.PayloadData);
    }

    /// <summary>Inserts a single live-socket message. Insert-only by design: the caller has already
    /// established the GUID is absent, and unlike the sync upsert this path must not overwrite a row
    /// (a socket replay would revert an edit the update path already applied). Returns false when a
    /// concurrent save won the GUID race, in which case the winner owns the attachment write.</summary>
    public static async Task<bool> InsertIncomingAsync(
        BlueBubblesDbContext db, int chatId, int? handleId, Message message, CancellationToken ct)
    {
        var entity = new MessageEntity { Guid = message.Guid };
        ApplyServerFields(entity, message, chatId, handleId, isNew: true);
        db.Messages.Add(entity);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Duplicate GUID — another concurrent save won the race
            return false;
        }

        return true;
    }

    /// <summary>Applies an in-place server update (edit, unsend, delivery/read receipt) to an
    /// existing row. Deliberately a narrower field set than <see cref="ApplyServerFields"/>: an
    /// update payload omits most fields, so copying them wholesale would blank them.</summary>
    public static async Task<MessageEntity?> ApplyUpdateAsync(
        BlueBubblesDbContext db, Message message, CancellationToken ct)
    {
        var entity = await db.Messages.FirstOrDefaultAsync(m => m.Guid == message.Guid, ct);
        if (entity is null) return null;

        entity.DateRead = message.DateRead;
        entity.DateDelivered = message.DateDelivered;
        entity.IsDelivered = message.IsDelivered;
        entity.DateDeleted = message.DateDeleted;
        entity.Subject = message.Subject;
        entity.Error = message.Error;
        entity.HasReactions = message.HasReactions;

        // Guard text/dateEdited so a later delivery-only update can't wipe an edit
        // (mirrors the Flutter merge, which only overwrites when the new value is present).
        if (message.Text != null) entity.Text = message.Text;
        if (message.DateEdited != null) entity.DateEdited = message.DateEdited;

        // Persist edit history / retracted parts so an unsend survives a reload.
        if (message.MessageSummaryInfo is { Count: > 0 })
            entity.MessageSummaryInfoJson = Serialize(message.MessageSummaryInfo);

        await db.SaveChangesAsync(ct);
        return entity;
    }

    /// <summary>The only assignment of <see cref="MessageEntity.DateDeleted"/> outside a server
    /// payload — used by the explicit delete path and by the window reconciler's prune.</summary>
    public static void MarkDeleted(MessageEntity entity, long whenMs) => entity.DateDeleted = whenMs;

    /// <summary>Flags a reaction's parent so a reload renders the tapback without a re-fetch.</summary>
    public static async Task MarkParentHasReactionsAsync(
        BlueBubblesDbContext db, string parentGuid, CancellationToken ct)
    {
        var parent = await db.Messages.FirstOrDefaultAsync(m => m.Guid == parentGuid, ct);
        if (parent is null || parent.HasReactions) return;

        parent.HasReactions = true;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The single writer of attachment rows. Every persist path (bulk sync, window
    /// reconcile, live socket save) must go through here so the identity dedupe below stays the
    /// one rule that keeps a re-fetched window from duplicating rows. Requires the owning message
    /// rows to already be saved.</summary>
    public static async Task SaveAttachmentsAsync(
        BlueBubblesDbContext db, IEnumerable<Message> messages, CancellationToken ct)
    {
        foreach (var msg in messages)
        {
            if (msg.Attachments is null or { Count: 0 }) continue;

            var msgEntity = await db.Messages.FirstOrDefaultAsync(m => m.Guid == msg.Guid, ct);
            if (msgEntity is null) continue;

            foreach (var att in msg.Attachments)
            {
                // Identity is the server's `originalROWID` (Apple's chat.db attachment ROWID,
                // passed through verbatim by the server's AttachmentSerializer), NOT the GUID:
                // Apple rewrites an attachment's GUID as a transfer completes, so one attachment
                // is seen first as a plain UUID and later as `at_<n>_<messageGuid>`. Keying only
                // on the GUID let the second form insert a second row for the same file (B7).
                // The GUID check is kept as well, so this is a strict superset of the old rule;
                // two rows with *different* ROWIDs stay distinct, because a message legitimately
                // can carry the same file twice.
                var isDuplicate =
                    (att.Guid is not null &&
                        (db.Attachments.Local.Any(a => a.Guid == att.Guid) ||
                         await db.Attachments.AnyAsync(a => a.Guid == att.Guid, ct)))
                    || (att.OriginalRowId.HasValue &&
                        (db.Attachments.Local.Any(a =>
                             a.MessageId == msgEntity.Id && a.OriginalRowId == att.OriginalRowId) ||
                         await db.Attachments.AnyAsync(a =>
                             a.MessageId == msgEntity.Id && a.OriginalRowId == att.OriginalRowId, ct)));

                if (isDuplicate)
                {
                    AppLog.Debug(LogCategory.Sync,
                        $"Attachment write skipped as duplicate: message={msg.Guid} " +
                        $"rowId={att.OriginalRowId?.ToString() ?? "none"} guid={att.Guid ?? "none"}");
                    continue;
                }

                db.Attachments.Add(new AttachmentEntity
                {
                    Guid = att.Guid!,
                    MessageId = msgEntity.Id,
                    OriginalRowId = att.OriginalRowId,
                    Uti = att.Uti,
                    MimeType = att.MimeType,
                    IsOutgoing = att.IsOutgoing,
                    TransferName = att.TransferName,
                    TotalBytes = att.TotalBytes,
                    Height = att.Height,
                    Width = att.Width,
                    HasLivePhoto = att.HasLivePhoto,
                    MetadataJson = Serialize(att.Metadata)
                });
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private static string? Serialize<T>(T? value) where T : class =>
        value is null ? null : JsonSerializer.Serialize(value, JsonDefaults.Options);
}
