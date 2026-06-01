using System.Text.Json;
using BlueBubbles.Core.Data;
using BlueBubbles.Core.Data.Entities;
using BlueBubbles.Core.Models;
using BlueBubbles.Core.Utils;
using Microsoft.EntityFrameworkCore;

namespace BlueBubbles.Core.Services;

internal static class MessagePersistenceHelper
{
    public static async Task<(long? OldestDate, long? LatestDate, long MaxRowId)> SaveMessagesAsync(
        BlueBubblesDbContext db, int chatId, List<Message> messages,
        Dictionary<string, int> handleCache, CancellationToken ct)
    {
        long? latestDate = null;
        long? oldestDate = null;
        long maxRowId = 0;

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

            if (entity is null)
            {
                entity = new MessageEntity { Guid = msg.Guid };
                db.Messages.Add(entity);
            }

            entity.ChatId = chatId;
            entity.HandleId = msgHandleId;
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
            entity.IsBookmarked = msg.IsBookmarked;
            entity.MetadataJson = Serialize(msg.Metadata);
            entity.AttributedBodyJson = Serialize(msg.AttributedBody);
            entity.MessageSummaryInfoJson = Serialize(msg.MessageSummaryInfo);
            entity.PayloadDataJson = Serialize(msg.PayloadData);

            if (msg.DateCreated.HasValue)
            {
                if (latestDate is null || msg.DateCreated > latestDate)
                    latestDate = msg.DateCreated;
                if (oldestDate is null || msg.DateCreated < oldestDate)
                    oldestDate = msg.DateCreated;
            }
        }

        await db.SaveChangesAsync(ct);

        foreach (var msg in messages)
        {
            if (msg.Attachments is null or { Count: 0 }) continue;

            var msgEntity = await db.Messages.FirstAsync(m => m.Guid == msg.Guid, ct);

            foreach (var att in msg.Attachments)
            {
                if (att.Guid is not null &&
                    await db.Attachments.AnyAsync(a => a.Guid == att.Guid, ct))
                    continue;

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

    private static string? Serialize<T>(T? value) where T : class =>
        value is null ? null : JsonSerializer.Serialize(value, JsonDefaults.Options);
}
