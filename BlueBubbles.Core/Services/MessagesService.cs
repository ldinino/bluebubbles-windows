using System.Text.Json;
using BlueBubbles.Core.Data;
using BlueBubbles.Core.Data.Entities;
using BlueBubbles.Core.Models;
using BlueBubbles.Core.Utils;
using Microsoft.EntityFrameworkCore;

namespace BlueBubbles.Core.Services;

public class MessagesService : IMessagesService
{
    private readonly IDbContextFactory<BlueBubblesDbContext> _dbFactory;
    private readonly IBlueBubblesApiService _api;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    private const int MaxSyncHistoryDays = 365;
    private const string MessageWithQuery = "attachment,handle,attributedBody,messageSummaryInfo,payloadData";

    public MessagesService(IDbContextFactory<BlueBubblesDbContext> dbFactory, IBlueBubblesApiService api)
    {
        _dbFactory = dbFactory;
        _api = api;
    }

    public async Task<List<MessageEntity>> LoadMessagesAsync(int chatId, int limit = 50, long? beforeDate = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var query = db.Messages
            .Include(m => m.Handle)
            .Include(m => m.Attachments)
            .Where(m => m.ChatId == chatId && m.DateDeleted == null && m.AssociatedMessageGuid == null);

        if (beforeDate.HasValue)
            query = query.Where(m => m.DateCreated < beforeDate.Value);

        var messages = await query
            .OrderByDescending(m => m.DateCreated)
            .Take(limit)
            .ToListAsync();

        messages.Reverse();
        return messages;
    }

    public async Task<List<MessageEntity>> LoadMessagesAfterAsync(int chatId, long afterDate)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        return await db.Messages
            .Include(m => m.Handle)
            .Include(m => m.Attachments)
            .Where(m => m.ChatId == chatId && m.DateDeleted == null
                && m.AssociatedMessageGuid == null && m.DateCreated > afterDate)
            .OrderBy(m => m.DateCreated)
            .ToListAsync();
    }

    public async Task<List<MessageEntity>> FetchOlderMessagesFromServerAsync(
        int chatId, string chatGuid, int limit = 25, CancellationToken ct = default)
    {
        long? oldestSynced;
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var chat = await db.Chats.FindAsync([chatId], ct);
            if (chat is null) return [];
            oldestSynced = chat.OldestSyncedMessageDate;
        }

        if (oldestSynced is null or <= 0) return [];

        var oneYearAgo = DateTimeOffset.UtcNow.AddDays(-MaxSyncHistoryDays).ToUnixTimeMilliseconds();
        if (oldestSynced <= oneYearAgo) return [];

        var response = await _api.GetChatMessagesAsync(
            chatGuid,
            withQuery: MessageWithQuery,
            sort: "DESC",
            before: oldestSynced,
            limit: limit,
            ct: ct);

        var messages = response.Data ?? [];

        await _saveLock.WaitAsync(ct);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var chat = await db.Chats.FindAsync([chatId], ct);
            if (chat is null) return [];

            if (messages.Count == 0)
            {
                chat.OldestSyncedMessageDate = 0;
                await db.SaveChangesAsync(ct);
                return [];
            }

            var handleCache = new Dictionary<string, int>();
            var (batchOldest, _, _) = await MessagePersistenceHelper.SaveMessagesAsync(
                db, chatId, messages, handleCache, ct);

            if (batchOldest.HasValue)
                chat.OldestSyncedMessageDate = batchOldest;

            await db.SaveChangesAsync(ct);
        }
        finally
        {
            _saveLock.Release();
        }

        return await LoadMessagesAsync(chatId, limit, oldestSynced);
    }

    public async Task<bool> EnsureChatHydratedAsync(
        int chatId, string chatGuid, int limit = 50, CancellationToken ct = default)
    {
        // Only hydrate when the chat is locally empty — never re-fetch a chat that already has
        // history (older pages are the job of FetchOlderMessagesFromServerAsync).
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var hasAny = await db.Messages.AnyAsync(
                m => m.ChatId == chatId && m.DateDeleted == null && m.AssociatedMessageGuid == null, ct);
            if (hasAny) return false;
        }

        List<Message> messages;
        try
        {
            var response = await _api.GetChatMessagesAsync(
                chatGuid,
                withQuery: MessageWithQuery,
                sort: "DESC",
                limit: limit,
                ct: ct);
            messages = response.Data ?? [];
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogCategory.Sync, $"Hydrate fetch failed for chat {chatGuid}: {ex.Message}");
            return false;
        }

        if (messages.Count == 0) return false;

        await _saveLock.WaitAsync(ct);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var chat = await db.Chats.FindAsync([chatId], ct);
            if (chat is null) return false;

            var handleCache = new Dictionary<string, int>();
            var (batchOldest, _, _) = await MessagePersistenceHelper.SaveMessagesAsync(
                db, chatId, messages, handleCache, ct);

            if (batchOldest.HasValue &&
                (chat.OldestSyncedMessageDate is null || batchOldest < chat.OldestSyncedMessageDate))
                chat.OldestSyncedMessageDate = batchOldest;

            await db.SaveChangesAsync(ct);
        }
        finally
        {
            _saveLock.Release();
        }

        return true;
    }

    public async Task<bool> RefreshLatestFromServerAsync(
        int chatId, string chatGuid, int limit = 50, CancellationToken ct = default)
    {
        List<Message> messages;
        try
        {
            var response = await _api.GetChatMessagesAsync(
                chatGuid,
                withQuery: MessageWithQuery,
                sort: "DESC",
                limit: limit,
                ct: ct);
            messages = response.Data ?? [];
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogCategory.Sync, $"Refresh fetch failed for chat {chatGuid}: {ex.Message}");
            return false;
        }

        if (messages.Count == 0) return false;

        await _saveLock.WaitAsync(ct);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            if (await db.Chats.FindAsync([chatId], ct) is null) return false;

            // SaveMessagesAsync upserts by GUID, so re-fetched rows reconcile their edited text,
            // retracted parts, and read/delivery timestamps onto what's already stored.
            var handleCache = new Dictionary<string, int>();
            await MessagePersistenceHelper.SaveMessagesAsync(db, chatId, messages, handleCache, ct);
        }
        finally
        {
            _saveLock.Release();
        }

        return true;
    }

    public async Task SaveIncomingMessageAsync(string chatGuid, Message message)
    {
        await _saveLock.WaitAsync();
        try
        {
            await SaveMessageCoreAsync(chatGuid, message);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private async Task SaveMessageCoreAsync(string chatGuid, Message message)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        if (await db.Messages.AnyAsync(m => m.Guid == message.Guid))
            return;

        var chat = await db.Chats.FirstOrDefaultAsync(c => c.Guid == chatGuid);
        if (chat is null) return;

        int? handleId = null;
        if (message.Handle is not null)
        {
            var handle = await db.Handles.FirstOrDefaultAsync(
                h => h.Address == message.Handle.Address && h.Service == message.Handle.Service);

            if (handle is null)
            {
                handle = new HandleEntity
                {
                    Address = message.Handle.Address,
                    Service = message.Handle.Service,
                    Country = message.Handle.Country,
                    FormattedAddress = message.Handle.FormattedAddress
                };
                db.Handles.Add(handle);
                await db.SaveChangesAsync();
            }

            handleId = handle.Id;
        }

        var entity = new MessageEntity
        {
            Guid = message.Guid,
            ChatId = chat.Id,
            HandleId = handleId,
            OriginalRowId = message.OriginalRowId,
            OtherHandle = message.OtherHandle,
            Text = message.Text,
            Subject = message.Subject,
            Country = message.Country,
            Error = message.Error,
            DateCreated = message.DateCreated,
            DateRead = message.DateRead,
            DateDelivered = message.DateDelivered,
            IsDelivered = message.IsDelivered,
            IsFromMe = message.IsFromMe,
            HasDdResults = message.HasDdResults,
            DatePlayed = message.DatePlayed,
            ItemType = message.ItemType,
            GroupTitle = message.GroupTitle,
            GroupActionType = message.GroupActionType,
            BalloonBundleId = message.BalloonBundleId,
            AssociatedMessageGuid = ReactionTypes.NormalizeAssociatedGuid(message.AssociatedMessageGuid),
            AssociatedMessagePart = message.AssociatedMessageGuid is not null
                ? ReactionTypes.ResolveAssociatedPart(message.AssociatedMessageGuid, message.AssociatedMessagePart)
                : message.AssociatedMessagePart,
            AssociatedMessageType = message.AssociatedMessageType,
            ExpressiveSendStyleId = message.ExpressiveSendStyleId,
            HasAttachments = message.HasAttachments,
            HasReactions = message.HasReactions,
            DateDeleted = message.DateDeleted,
            ThreadOriginatorGuid = message.ThreadOriginatorGuid,
            ThreadOriginatorPart = message.ThreadOriginatorPart,
            HasApplePayloadData = message.HasApplePayloadData,
            DateEdited = message.DateEdited,
            WasDeliveredQuietly = message.WasDeliveredQuietly,
            DidNotifyRecipient = message.DidNotifyRecipient,
            IsBookmarked = message.IsBookmarked,
            MetadataJson = Serialize(message.Metadata),
            AttributedBodyJson = Serialize(message.AttributedBody),
            MessageSummaryInfoJson = Serialize(message.MessageSummaryInfo),
            PayloadDataJson = Serialize(message.PayloadData)
        };

        db.Messages.Add(entity);

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Duplicate GUID — another concurrent save won the race
        }
    }

    public async Task UpdateMessageAsync(Message message)
    {
        await _saveLock.WaitAsync();
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var entity = await db.Messages.FirstOrDefaultAsync(m => m.Guid == message.Guid);
            if (entity is null) return;

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

            await db.SaveChangesAsync();
        }
        finally
        {
            _saveLock.Release();
        }
    }

    public async Task<bool> DeleteMessageAsync(string chatGuid, string messageGuid)
    {
        // Server first: a local-only soft delete is overwritten by the next sync (the server's
        // copy still has DateDeleted = null), so only touch the cache once the server has deleted.
        try
        {
            var response = await _api.DeleteMessageFromChatAsync(chatGuid, messageGuid);
            if (response.Status is < 200 or >= 300)
            {
                AppLog.Warn(LogCategory.Api,
                    $"Delete message failed for {messageGuid}: server returned {response.Status}");
                return false;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogCategory.Api, $"Delete message failed for {messageGuid}: {ex.Message}");
            return false;
        }

        await _saveLock.WaitAsync();
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var entity = await db.Messages.FirstOrDefaultAsync(m => m.Guid == messageGuid);
            if (entity is null) return true;

            entity.DateDeleted = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await db.SaveChangesAsync();
        }
        finally
        {
            _saveLock.Release();
        }
        return true;
    }

    public async Task<List<MessageEntity>> LoadReactionsAsync(IReadOnlyCollection<string> parentGuids)
    {
        if (parentGuids.Count == 0) return [];

        await using var db = await _dbFactory.CreateDbContextAsync();

        return await db.Messages
            .Include(m => m.Handle)
            .Where(m => m.AssociatedMessageGuid != null
                && parentGuids.Contains(m.AssociatedMessageGuid)
                && m.AssociatedMessageType != null
                && m.DateDeleted == null)
            .OrderBy(m => m.DateCreated)
            .ToListAsync();
    }

    public async Task<List<MessageEntity>> GetMessagesByGuidsAsync(IReadOnlyCollection<string> guids)
    {
        if (guids.Count == 0) return [];

        await using var db = await _dbFactory.CreateDbContextAsync();

        return await db.Messages
            .Include(m => m.Handle)
            .Where(m => guids.Contains(m.Guid))
            .ToListAsync();
    }

    public async Task SaveReactionAsync(string chatGuid, Message reaction)
    {
        await _saveLock.WaitAsync();
        try
        {
            await SaveMessageCoreAsync(chatGuid, reaction);

            var parentGuid = ReactionTypes.NormalizeAssociatedGuid(reaction.AssociatedMessageGuid);
            if (parentGuid is null) return;

            await using var db = await _dbFactory.CreateDbContextAsync();
            var parent = await db.Messages.FirstOrDefaultAsync(m => m.Guid == parentGuid);
            if (parent is not null && !parent.HasReactions)
            {
                parent.HasReactions = true;
                await db.SaveChangesAsync();
            }
        }
        finally
        {
            _saveLock.Release();
        }
    }

    public async Task<List<AttachmentEntity>> LoadMediaAttachmentsAsync(int chatId, int limit = 50, int offset = 0)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        return await db.Attachments
            .Where(a => a.Message.ChatId == chatId
                && a.Message.DateDeleted == null
                && a.MimeType != null
                && (a.MimeType.StartsWith("image/") || a.MimeType.StartsWith("video/")))
            .OrderByDescending(a => a.Message.DateCreated)
            .Skip(offset)
            .Take(limit)
            .ToListAsync();
    }

    private static string? Serialize<T>(T? value) where T : class =>
        value is null ? null : JsonSerializer.Serialize(value, JsonDefaults.Options);
}
