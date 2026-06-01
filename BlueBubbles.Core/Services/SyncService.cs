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
    private const int TieredMessagePageSize = 100;
    private const int RecentChatDays = 7;
    private const int RecentSyncWindowDays = 30;
    private const int OlderSyncWindowDays = 7;
    private const int MaxSyncHistoryDays = 365;
    private const int MaxMessagesPerChat = 500;

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
        var chatEntityIds = new List<(string Guid, int Id, bool HasParticipants, long? LatestMessageDate)>();
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

                chatEntityIds.Add((chat.Guid, chatEntity.Id, chat.Participants is { Count: > 0 }, chat.LastMessage?.DateCreated));
                chatsProcessed++;
            }

            progress?.Report(new(SyncPhase.SyncingChats, chatsProcessed, totalChats,
                $"Synced {chatsProcessed}/{totalChats} chats"));
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var oneYearAgo = DateTimeOffset.UtcNow.AddDays(-MaxSyncHistoryDays).ToUnixTimeMilliseconds();

        for (int i = 0; i < chatEntityIds.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (chatGuid, chatId, hasParticipants, latestMessageDate) = chatEntityIds[i];

            progress?.Report(new(SyncPhase.SyncingMessages, i + 1, chatEntityIds.Count,
                $"Messages for chat {i + 1}/{chatEntityIds.Count}"));

            if (!hasParticipants)
            {
                await SoftDeleteChatAsync(chatId, ct);
                continue;
            }

            try
            {
                var isRecent = latestMessageDate.HasValue &&
                    (now - latestMessageDate.Value) < TimeSpan.FromDays(RecentChatDays).TotalMilliseconds;
                var windowDays = isRecent ? RecentSyncWindowDays : OlderSyncWindowDays;
                var afterTimestamp = Math.Max(
                    DateTimeOffset.UtcNow.AddDays(-windowDays).ToUnixTimeMilliseconds(),
                    oneYearAgo);

                int offset = 0;
                int totalSaved = 0;
                long? overallOldestDate = null;
                bool isEmpty = true;

                while (totalSaved < MaxMessagesPerChat)
                {
                    var msgResp = await _api.GetChatMessagesAsync(
                        chatGuid,
                        withQuery: "attachment,handle,attributedBody,messageSummaryInfo,payloadData",
                        sort: "DESC",
                        after: afterTimestamp,
                        offset: offset,
                        limit: TieredMessagePageSize,
                        ct: ct);

                    var messages = msgResp.Data ?? [];
                    if (messages.Count == 0) break;

                    isEmpty = false;
                    await SaveMessagesAsync(chatId, messages, handleCache, ct);

                    var batchOldest = messages
                        .Where(m => m.DateCreated.HasValue)
                        .Min(m => m.DateCreated);
                    if (batchOldest.HasValue &&
                        (overallOldestDate is null || batchOldest < overallOldestDate))
                        overallOldestDate = batchOldest;

                    totalSaved += messages.Count;
                    offset += TieredMessagePageSize;

                    if (messages.Count < TieredMessagePageSize) break;
                }

                if (isEmpty)
                {
                    if (skipEmptyChats)
                        await SoftDeleteChatAsync(chatId, ct);
                }
                else
                {
                    await using var db = await _dbFactory.CreateDbContextAsync(ct);
                    var chatEntity = await db.Chats.FindAsync([chatId], ct);
                    if (chatEntity is not null)
                    {
                        chatEntity.OldestSyncedMessageDate = overallOldestDate ?? afterTimestamp;
                        await db.SaveChangesAsync(ct);
                    }
                }
            }
            catch (Exception ex) { AppLog.Warn($"Failed to sync messages for chat {chatGuid}: {ex.Message}"); }
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
            var syncStartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
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
                }

                offset += IncrementalBatchSize;
                hasMore = messages.Count == IncrementalBatchSize;
            }

            if (syncStart > 0)
                _appSettings.LastIncrementalSync = syncStartedAt;
            else if (lastSyncedTimestamp > syncStart)
                _appSettings.LastIncrementalSync = lastSyncedTimestamp;

            if (lastSyncedRowId > startRowId)
                _appSettings.LastIncrementalSyncRowId = lastSyncedRowId;

            _settingsService.Save();
            await _chatsService.LoadChatsAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Incremental sync failed: {ex.Message}");
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

        var chat = await db.Chats.FirstOrDefaultAsync(c => c.Guid == chatGuid, ct);
        if (chat is not null) return chat.Id;

        var chatData = sampleMessage.Chats?.FirstOrDefault(c => c.Guid == chatGuid);
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
        catch (Exception ex) { AppLog.Warn($"FCM config fetch failed: {ex.Message}"); }
    }

    private static string? Serialize<T>(T? value) where T : class =>
        value is null ? null : JsonSerializer.Serialize(value, JsonDefaults.Options);
}
