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

    public Task<List<MessageEntity>> LoadMessagesAsync(int chatId, int limit = 50, long? beforeDate = null)
        => LoadMessagesAsync(new[] { chatId }, limit, beforeDate);

    public async Task<List<MessageEntity>> LoadMessagesAsync(IReadOnlyList<int> chatIds, int limit = 50, long? beforeDate = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var query = db.Messages
            .Include(m => m.Handle)
            .Include(m => m.Attachments)
            .Where(m => chatIds.Contains(m.ChatId) && m.DateDeleted == null && m.AssociatedMessageGuid == null);

        if (beforeDate.HasValue)
            query = query.Where(m => m.DateCreated < beforeDate.Value);

        var messages = await query
            .OrderByDescending(m => m.DateCreated)
            // Sending a photo with a caption produces two messages stamped with the SAME time, so
            // date alone leaves their order to chance and the caption could land above the photo.
            // ROWID is the iMessage database's insertion order, i.e. the real send order. Locally
            // created messages have no ROWID yet and sort newest, which is where they belong.
            .ThenByDescending(m => m.OriginalRowId ?? int.MaxValue)
            .Take(limit)
            .ToListAsync();

        messages.Reverse();
        return messages;
    }

    public Task<List<MessageEntity>> LoadMessagesAfterAsync(int chatId, long afterDate)
        => LoadMessagesAfterAsync(new[] { chatId }, afterDate);

    public async Task<List<MessageEntity>> LoadMessagesAfterAsync(IReadOnlyList<int> chatIds, long afterDate)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        return await db.Messages
            .Include(m => m.Handle)
            .Include(m => m.Attachments)
            .Where(m => chatIds.Contains(m.ChatId) && m.DateDeleted == null
                && m.AssociatedMessageGuid == null && m.DateCreated > afterDate)
            .OrderBy(m => m.DateCreated)
            .ThenBy(m => m.OriginalRowId ?? int.MaxValue)
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

            // Reconcile (not just upsert) the fetched window: re-fetched rows pick up their edited
            // text, retracted parts, and read/delivery timestamps, AND any local message inside the
            // server's returned range that the server no longer has is soft-deleted — so a delete we
            // missed over the socket finally converges. See MessageWindowReconciler.
            var handleCache = new Dictionary<string, int>();
            await MessageWindowReconciler.ReconcileWindowAsync(db, chatId, messages, handleCache, ct);
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
            handleId = await HandlePersistenceHelper.EnsureHandleAsync(
                db, message.Handle, cache: null, refreshExisting: false);
        }

        if (!await MessagePersistenceHelper.InsertIncomingAsync(
                db, chat.Id, handleId, message, CancellationToken.None))
            return;

        // Without this the live socket path stored HasAttachments = true and zero rows, so an
        // image only appeared after a sync re-fetched the window (PUNCHLIST B2).
        await MessagePersistenceHelper.SaveAttachmentsAsync(db, [message], CancellationToken.None);
    }

    public async Task<string?> UpdateMessageAsync(Message message)
    {
        await _saveLock.WaitAsync();
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var entity = await MessagePersistenceHelper.ApplyUpdateAsync(
                db, message, CancellationToken.None);
            if (entity is null) return null;

            return await db.Chats
                .Where(c => c.Id == entity.ChatId)
                .Select(c => c.Guid)
                .FirstOrDefaultAsync();
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

            MessagePersistenceHelper.MarkDeleted(
                entity, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
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
            await MessagePersistenceHelper.MarkParentHasReactionsAsync(
                db, parentGuid, CancellationToken.None);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    public Task<List<AttachmentEntity>> LoadMediaAttachmentsAsync(int chatId, int limit = 50, int offset = 0)
        => LoadMediaAttachmentsAsync(new[] { chatId }, limit, offset);

    public async Task<List<AttachmentEntity>> LoadMediaAttachmentsAsync(IReadOnlyList<int> chatIds, int limit = 50, int offset = 0)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        return await db.Attachments
            .Where(a => chatIds.Contains(a.Message.ChatId)
                && a.Message.DateDeleted == null
                && a.MimeType != null
                && (a.MimeType.StartsWith("image/") || a.MimeType.StartsWith("video/")))
            .OrderByDescending(a => a.Message.DateCreated)
            .Skip(offset)
            .Take(limit)
            .ToListAsync();
    }
}
