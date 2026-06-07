using System.Text.Json;
using BlueBubbles.Core.Configuration;
using BlueBubbles.Core.Data;
using BlueBubbles.Core.Data.Entities;
using BlueBubbles.Core.Models;
using BlueBubbles.Core.Utils;
using Microsoft.EntityFrameworkCore;

namespace BlueBubbles.Core.Services;

public class SyncService : ISyncService
{
    private readonly IBlueBubblesApiService _api;
    private readonly IDbContextFactory<BlueBubblesDbContext> _dbFactory;
    private readonly IFirebaseService _firebase;
    private readonly AppSettings _appSettings;
    private readonly ISettingsService _settingsService;
    private readonly IChatsService _chatsService;

    private int _isIncrementalSyncing;

    public bool IsSyncing { get; private set; }
    public event EventHandler<bool>? SyncStateChanged;

    private const int ChatPageSize = 200;
    private const int IncrementalBatchSize = 1000;
    // Initial sync pulls the newest page per chat regardless of age — deeper history loads
    // on demand via MessagesService.FetchOlderMessagesFromServerAsync (scroll-up). The old
    // tiered date-window approach is gone: an absolute `after` floor could exclude a chat's
    // entire (idle) history and get it mis-classified as empty and soft-deleted.
    private const int InitialMessagesPerChat = 100;

    private static readonly List<string> IncrementalWithQuery =
        ["chats", "chats.participants", "attachment", "handle", "attributedBody", "messageSummaryInfo", "payloadData"];

    public SyncService(
        IBlueBubblesApiService api,
        IDbContextFactory<BlueBubblesDbContext> dbFactory,
        IFirebaseService firebase,
        AppSettings appSettings,
        ISettingsService settingsService,
        IChatsService chatsService)
    {
        _api = api;
        _dbFactory = dbFactory;
        _firebase = firebase;
        _appSettings = appSettings;
        _settingsService = settingsService;
        _chatsService = chatsService;
    }

    public async Task RunFullSyncAsync(
        bool skipEmptyChats = true, IProgress<SyncProgress>? progress = null, CancellationToken ct = default)
    {
        await using (var initDb = await _dbFactory.CreateDbContextAsync(ct))
        {
            await initDb.Database.EnsureCreatedAsync(ct);
        }

        await EnsureSchemaExtensionsAsync(ct);

        _appSettings.LastIncrementalSync = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        progress?.Report(new(SyncPhase.Starting, 0, 0, "Getting chat count..."));

        var countResp = await _api.GetChatCountAsync(ct);
        var totalChats = countResp.Data!.GetProperty("total").GetInt32();

        var handleCache = new Dictionary<string, int>();
        var chatEntityIds = new List<(string Guid, int Id, bool HasParticipants)>();
        int chatsProcessed = 0;

        for (int offset = 0; offset < totalChats; offset += ChatPageSize)
        {
            ct.ThrowIfCancellationRequested();

            var chatResp = await _api.QueryChatsAsync(
                withQuery: ["participants", "lastmessage"],
                offset: offset,
                limit: ChatPageSize,
                sort: "lastmessage",
                ct: ct);

            var chats = chatResp.Data ?? [];
            if (chats.Count == 0) break;

            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            foreach (var chat in chats)
            {
                await SaveHandlesAsync(db, chat.Participants, handleCache, ct);

                var chatEntity = await db.Chats.FirstOrDefaultAsync(
                    c => c.Guid == chat.Guid, ct);

                if (chatEntity is null)
                {
                    chatEntity = new ChatEntity { Guid = chat.Guid };
                    db.Chats.Add(chatEntity);
                }

                chatEntity.ChatIdentifier = chat.ChatIdentifier;
                chatEntity.DisplayName = chat.DisplayName;
                chatEntity.IsArchived = chat.IsArchived;
                chatEntity.IsPinned = chat.IsPinned;
                chatEntity.HasUnreadMessage = chat.HasUnreadMessage;
                chatEntity.Service = chat.Service;
                chatEntity.MuteType = chat.MuteType;
                chatEntity.MuteArgs = chat.MuteArgs;
                chatEntity.AutoSendReadReceipts = chat.AutoSendReadReceipts;
                chatEntity.AutoSendTypingIndicators = chat.AutoSendTypingIndicators;
                chatEntity.DateDeleted = chat.DateDeleted;
                chatEntity.Style = chat.Style;
                chatEntity.LockChatName = chat.LockChatName;
                chatEntity.LockChatIcon = chat.LockChatIcon;
                chatEntity.LastReadMessageGuid = chat.LastReadMessageGuid;
                chatEntity.LatestMessageDate = chat.LastMessage?.DateCreated;
                await db.SaveChangesAsync(ct);

                if (chat.Participants is not null)
                {
                    foreach (var h in chat.Participants)
                    {
                        var key = h.Address + "|" + h.Service;
                        if (handleCache.TryGetValue(key, out var handleId))
                        {
                            var exists = await db.ChatParticipants.AnyAsync(
                                cp => cp.ChatId == chatEntity.Id && cp.HandleId == handleId, ct);
                            if (!exists)
                            {
                                db.ChatParticipants.Add(new ChatParticipant
                                {
                                    ChatId = chatEntity.Id,
                                    HandleId = handleId
                                });
                            }
                        }
                    }
                    await db.SaveChangesAsync(ct);
                }

                chatEntityIds.Add((chat.Guid, chatEntity.Id, chat.Participants is { Count: > 0 }));
                chatsProcessed++;
            }

            progress?.Report(new(SyncPhase.SyncingChats, chatsProcessed, totalChats,
                $"Synced {chatsProcessed}/{totalChats} chats"));
        }

        for (int i = 0; i < chatEntityIds.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (chatGuid, chatId, hasParticipants) = chatEntityIds[i];

            progress?.Report(new(SyncPhase.SyncingMessages, i + 1, chatEntityIds.Count,
                $"Messages for chat {i + 1}/{chatEntityIds.Count}"));

            if (!hasParticipants)
            {
                await SoftDeleteChatAsync(chatId, ct);
                continue;
            }

            try
            {
                // Fetch the newest page for this chat with NO date floor, so a chat that's been
                // idle for months still pulls its latest messages instead of coming back empty.
                var msgResp = await _api.GetChatMessagesAsync(
                    chatGuid,
                    withQuery: "attachment,handle,attributedBody,messageSummaryInfo,payloadData",
                    sort: "DESC",
                    offset: 0,
                    limit: InitialMessagesPerChat,
                    ct: ct);

                var messages = msgResp.Data ?? [];

                // "Empty" means the server has no messages for this chat — not "nothing in an
                // arbitrary recent window". Only genuinely-empty chats are soft-deleted.
                if (messages.Count == 0)
                {
                    if (skipEmptyChats)
                        await SoftDeleteChatAsync(chatId, ct);
                    continue;
                }

                await SaveMessagesAsync(chatId, messages, handleCache, ct);

                var oldestDate = messages
                    .Where(m => m.DateCreated.HasValue)
                    .Min(m => m.DateCreated);

                await using var db = await _dbFactory.CreateDbContextAsync(ct);
                var chatEntity = await db.Chats.FindAsync([chatId], ct);
                if (chatEntity is not null)
                {
                    chatEntity.OldestSyncedMessageDate = oldestDate;
                    await db.SaveChangesAsync(ct);
                }
            }
            catch (Exception ex) { AppLog.Warn(LogCategory.Sync, $"Failed to sync messages for chat {chatGuid}: {ex.Message}"); }
        }

        progress?.Report(new(SyncPhase.FetchingFcmConfig, 0, 0, "Fetching Firebase config..."));
        await FetchFcmConfigAsync(ct);

        if (_maxSyncedRowId > 0)
            _appSettings.LastIncrementalSyncRowId = _maxSyncedRowId;
        _settingsService.Save();

        progress?.Report(new(SyncPhase.Complete, 0, 0, "Sync complete!"));
    }

    public async Task RunIncrementalSyncAsync(CancellationToken ct = default)
    {
        var syncStart = _appSettings.LastIncrementalSync;
        var startRowId = _appSettings.LastIncrementalSyncRowId;
        if (syncStart == 0 && startRowId == 0) return;

        if (Interlocked.Exchange(ref _isIncrementalSyncing, 1) == 1) return;

        IsSyncing = true;
        SyncStateChanged?.Invoke(this, true);

        try
        {
            var info = await _api.GetServerInfoAsync(ct);
            if (info.Status == 200 && info.Data is not null)
                _appSettings.ServerPrivateAPI = info.Data.PrivateApi;
        }
        catch { }
        try
        {
            long lastSyncedRowId = startRowId;
            long lastSyncedTimestamp = syncStart;

            int offset = 0;
            bool hasMore = true;
            var handleCache = new Dictionary<string, int>();

            while (hasMore)
            {
                ct.ThrowIfCancellationRequested();

                ApiResponse<List<Message>> response;
                if (startRowId > 0)
                {
                    var where = new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["statement"] = "message.ROWID > :startRowId",
                            ["args"] = new Dictionary<string, object> { ["startRowId"] = startRowId }
                        }
                    };
                    response = await _api.QueryMessagesAsync(
                        withQuery: IncrementalWithQuery,
                        where: where,
                        sort: "ASC",
                        offset: offset,
                        limit: IncrementalBatchSize,
                        ct: ct);
                }
                else
                {
                    response = await _api.QueryMessagesAsync(
                        withQuery: IncrementalWithQuery,
                        after: syncStart,
                        sort: "ASC",
                        offset: offset,
                        limit: IncrementalBatchSize,
                        ct: ct);
                }

                var messages = response.Data ?? [];
                if (messages.Count == 0) break;

                var messagesByChat = new Dictionary<string, List<Message>>();
                foreach (var msg in messages)
                {
                    var chatData = msg.Chats?.FirstOrDefault();
                    if (chatData is null) continue;

                    if (!messagesByChat.ContainsKey(chatData.Guid))
                        messagesByChat[chatData.Guid] = [];
                    messagesByChat[chatData.Guid].Add(msg);

                    if (msg.OriginalRowId.HasValue && msg.OriginalRowId.Value > lastSyncedRowId)
                        lastSyncedRowId = msg.OriginalRowId.Value;
                    if (msg.DateCreated.HasValue && msg.DateCreated.Value > lastSyncedTimestamp)
                        lastSyncedTimestamp = msg.DateCreated.Value;
                }

                foreach (var (chatGuid, chatMessages) in messagesByChat)
                {
                    var chatId = await EnsureChatExistsAsync(
                        chatGuid, chatMessages[0], handleCache, ct);
                    await SaveMessagesAsync(chatId, chatMessages, handleCache, ct);

                    // The delta writes straight to the DB, bypassing the live socket event — tell the
                    // open thread so it can append these rows without waiting for the end-of-sync pulse.
                    _chatsService.NotifyMessagesPersisted(chatGuid);
                }

                // Checkpoint the watermark after every persisted batch, not just at the very
                // end. If the delta is interrupted (sleep, network drop, app exit) mid-backfill,
                // the next run resumes from here instead of refetching from the original cursor.
                if (lastSyncedRowId > _appSettings.LastIncrementalSyncRowId)
                    _appSettings.LastIncrementalSyncRowId = lastSyncedRowId;
                if (lastSyncedTimestamp > _appSettings.LastIncrementalSync)
                    _appSettings.LastIncrementalSync = lastSyncedTimestamp;
                _settingsService.Save();

                offset += IncrementalBatchSize;
                hasMore = messages.Count == IncrementalBatchSize;
            }

            await _chatsService.LoadChatsAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogCategory.Sync, $"Incremental sync failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _isIncrementalSyncing, 0);
            IsSyncing = false;
            SyncStateChanged?.Invoke(this, false);
        }
    }

    private async Task<int> EnsureChatExistsAsync(
        string chatGuid, Message sampleMessage,
        Dictionary<string, int> handleCache, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var chatData = sampleMessage.Chats?.FirstOrDefault(c => c.Guid == chatGuid);

        var chat = await db.Chats
            .Include(c => c.ChatParticipants)
            .FirstOrDefaultAsync(c => c.Guid == chatGuid, ct);
        if (chat is not null)
        {
            // A new message means the chat is live again — undo any prior soft-delete (e.g. it
            // had been pruned as empty) so it resurfaces in the list.
            var dirty = false;
            if (chat.DateDeleted is not null)
            {
                chat.DateDeleted = null;
                dirty = true;
            }

            // Backfill participants if we have them and the stored set is empty (a chat first
            // created from a sparse payload can land without participants → renders blank).
            if (chat.ChatParticipants.Count == 0 && chatData?.Participants is { Count: > 0 })
            {
                await SaveHandlesAsync(db, chatData.Participants, handleCache, ct);
                foreach (var h in chatData.Participants)
                {
                    var key = h.Address + "|" + h.Service;
                    if (handleCache.TryGetValue(key, out var handleId))
                    {
                        chat.ChatParticipants.Add(new ChatParticipant
                        {
                            ChatId = chat.Id,
                            HandleId = handleId
                        });
                        dirty = true;
                    }
                }
            }

            if (dirty) await db.SaveChangesAsync(ct);
            return chat.Id;
        }

        chat = new ChatEntity
        {
            Guid = chatGuid,
            ChatIdentifier = chatData?.ChatIdentifier,
            DisplayName = chatData?.DisplayName,
            Service = chatData?.Service,
            Style = chatData?.Style,
            HasUnreadMessage = true
        };
        db.Chats.Add(chat);
        await db.SaveChangesAsync(ct);

        if (chatData?.Participants is not null)
        {
            await SaveHandlesAsync(db, chatData.Participants, handleCache, ct);
            foreach (var h in chatData.Participants)
            {
                var key = h.Address + "|" + h.Service;
                if (handleCache.TryGetValue(key, out var handleId))
                {
                    if (!await db.ChatParticipants.AnyAsync(
                            cp => cp.ChatId == chat.Id && cp.HandleId == handleId, ct))
                    {
                        db.ChatParticipants.Add(new ChatParticipant
                        {
                            ChatId = chat.Id,
                            HandleId = handleId
                        });
                    }
                }
            }
            await db.SaveChangesAsync(ct);
        }

        return chat.Id;
    }

    private async Task SaveHandlesAsync(
        BlueBubblesDbContext db, List<Handle>? handles,
        Dictionary<string, int> cache, CancellationToken ct)
    {
        if (handles is null) return;

        foreach (var h in handles)
        {
            var key = h.Address + "|" + h.Service;
            if (cache.ContainsKey(key)) continue;

            var entity = await db.Handles.FirstOrDefaultAsync(
                x => x.Address == h.Address && x.Service == h.Service, ct);

            if (entity is null)
            {
                entity = new HandleEntity { Address = h.Address, Service = h.Service };
                db.Handles.Add(entity);
            }

            entity.OriginalRowId = h.OriginalRowId;
            entity.Country = h.Country;
            entity.FormattedAddress = h.FormattedAddress;
            entity.Color = h.Color;
            entity.UniqueAddressAndService = h.UniqueAddressAndService;
            entity.DefaultPhone = h.DefaultPhone;
            entity.DefaultEmail = h.DefaultEmail;
            await db.SaveChangesAsync(ct);
            cache[key] = entity.Id;
        }
    }

    private async Task SoftDeleteChatAsync(int chatId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var chat = await db.Chats.FindAsync([chatId], ct);
        if (chat is not null)
        {
            chat.DateDeleted = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            chat.HasUnreadMessage = false;
            await db.SaveChangesAsync(ct);
        }
    }

    private long _maxSyncedRowId;

    private async Task SaveMessagesAsync(
        int chatId, List<Message> messages,
        Dictionary<string, int> handleCache, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var (_, _, maxRowId) = await MessagePersistenceHelper.SaveMessagesAsync(
            db, chatId, messages, handleCache, ct);
        if (maxRowId > _maxSyncedRowId)
            _maxSyncedRowId = maxRowId;
    }

    private async Task EnsureSchemaExtensionsAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE Chats ADD COLUMN OldestSyncedMessageDate INTEGER NULL", ct);
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { }
    }

    private async Task FetchFcmConfigAsync(CancellationToken ct)
    {
        try { await _firebase.FetchAndStoreConfigAsync(ct); }
        catch (Exception ex) { AppLog.Warn(LogCategory.Sync, $"FCM config fetch failed: {ex.Message}"); }
    }

    private static string? Serialize<T>(T? value) where T : class =>
        value is null ? null : JsonSerializer.Serialize(value, JsonDefaults.Options);
}
