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
    private int _isReconcilingChats;

    public bool IsSyncing { get; private set; }
    public event EventHandler<bool>? SyncStateChanged;

    // Bumped when an upgrade needs a one-time full true-up to converge caches written by an older,
    // less-authoritative sync (e.g. one that never applied server-side message deletes). See
    // RunHealIfNeededAsync. v1 = first server-authoritative model (delete-aware reconcile).
    public const int CurrentSyncModelVersion = 1;

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

        var syncStartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _appSettings.LastIncrementalSync = syncStartedAt;
        // A full sync just pulled every chat's newest page fresh, so the in-place "updated-since"
        // sweep should start from now rather than re-examining all history.
        _appSettings.LastUpdatedSync = syncStartedAt;

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
                    chatEntity = ChatFieldMerge.InsertFromServer(db, chat);
                }
                else
                {
                    // Server-owned fields only; client-owned pin/mute/archive are preserved (the
                    // server has no endpoint for them and returns defaults). See ChatFieldMerge.
                    ChatFieldMerge.ApplyServerOwnedFields(chatEntity, chat);
                }

                chatEntity.LatestMessageDate = chat.LastMessage?.DateCreated;
                await db.SaveChangesAsync(ct);

                if (chat.Participants is not null)
                {
                    await HandlePersistenceHelper.LinkParticipantsAsync(
                        db, chatEntity.Id, chat.Participants, handleCache, refreshExisting: true, ct);
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

                var pruned = await ReconcileChatWindowAsync(chatId, messages, handleCache, ct);
                // List-neutral unless the reconcile actually removed a row: only a removal can change
                // the tile's newest-message preview (PUNCHLIST B8).
                _chatsService.NotifyMessagesPersisted(chatGuid,
                    pruned > 0 ? MessagePersistKind.NewOrUpdated : MessagePersistKind.ServerTrueUp);

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

        // Server is the single source of truth for which chats exist: the paginated query above is
        // the authoritative list, so any local chat whose GUID it didn't return was deleted on the
        // server (or another device) and is pruned here. Guard against a transient empty response
        // wiping the list — only prune when chats were collected, or the server genuinely has none.
        if (totalChats == 0 || chatEntityIds.Count > 0)
            await PruneDeletedChatsAsync(chatEntityIds.Select(c => c.Guid).ToHashSet(), ct);

        progress?.Report(new(SyncPhase.FetchingFcmConfig, 0, 0, "Fetching Firebase config..."));
        await FetchFcmConfigAsync(ct);

        if (_maxSyncedRowId > 0)
            _appSettings.LastIncrementalSyncRowId = _maxSyncedRowId;
        // A full sync is itself a complete server-authoritative true-up, so mark the cache current —
        // this is also what the one-time upgrade heal sets, so a fresh setup never re-heals.
        _appSettings.SyncModelVersion = CurrentSyncModelVersion;
        _settingsService.Save();

        progress?.Report(new(SyncPhase.Complete, 0, 0, "Sync complete!"));
    }

    /// <summary>Runs a one-time full true-up after an upgrade that changed how the cache converges
    /// (e.g. to apply server-side deletes an older build never reconciled). Gated by
    /// <see cref="CurrentSyncModelVersion"/> so it fires once; returns true if a heal ran.</summary>
    public async Task<bool> RunHealIfNeededAsync(CancellationToken ct = default)
    {
        if (_appSettings.SyncModelVersion >= CurrentSyncModelVersion) return false;

        AppLog.Info(LogCategory.Sync,
            "Running one-time sync heal (full server-authoritative true-up) after upgrade");
        await RunFullSyncAsync(skipEmptyChats: true, ct: ct); // sets SyncModelVersion on completion
        return true;
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
        catch (Exception ex)
        {
            // Best-effort capability refresh; sync proceeds either way. Trace it so a stale
            // ServerPrivateAPI flag can be tied back to this fetch failing.
            AppLog.Debug(LogCategory.Sync, $"Server info refresh skipped: {ex.Message}");
        }
        try
        {
            // Caches written before the B7 identity fix hold the same server attachment twice
            // under two Apple GUID forms, which renders every affected photo twice. Runs here
            // rather than behind a version stamp because the cache has none (EnsureCreatedAsync,
            // no migrations history) and the pass is idempotent and cheap by construction.
            await using (var repairDb = await _dbFactory.CreateDbContextAsync(ct))
            {
                await AttachmentDeduplicator.CollapseDuplicatesAsync(repairDb, ct);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogCategory.Sync, $"Attachment duplicate repair skipped: {ex.Message}");
        }

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
                    _chatsService.NotifyMessagesPersisted(chatGuid, MessagePersistKind.NewOrUpdated);
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

            // Catch in-place changes the ROWID delta can't see: an edit/unsend to an already-synced
            // message updates its row without bumping ROWID, so it never appears in the delta above.
            await UpdatedSinceSweepAsync(ct);

            // Pull deletions down too: a chat removed on the server (or another device) emits no
            // socket event and arrives in no message delta, so the only way to learn it's gone is to
            // reconcile against the server's current chat list. Runs on every delta (launch, socket
            // reconnect, sleep/network recovery) so deletions propagate without a manual full resync.
            await ReconcileChatsCoreAsync(ct);

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

    // chat.db stores date_* columns as nanoseconds since 2001-01-01 UTC (Apple epoch). A raw
    // where-clause compares against those raw values, so a unix-ms watermark must be converted.
    private const long AppleEpochUnixMs = 978307200000L;

    /// <summary>Trues up in-place message changes (edits, unsends) that the ROWID delta misses because
    /// editing a message doesn't bump its ROWID. Gated by the cheap <c>message/count/updated</c>
    /// endpoint so the common "nothing changed" cycle costs a single lightweight call.</summary>
    /// <remarks>The where-clause runs as raw SQL against the live chat.db (same mechanism as the
    /// delta's <c>message.ROWID &gt; :id</c>), so the date column is Apple-epoch nanoseconds. The exact
    /// column token is the one piece that needs validation against a live server; every failure mode
    /// here is non-destructive (a bad token errors out or fetches an idempotently-upserted superset,
    /// never deletes), and the count-gate keeps it from running unless the server reports changes.</remarks>
    private async Task UpdatedSinceSweepAsync(CancellationToken ct)
    {
        var watermark = _appSettings.LastUpdatedSync;
        if (watermark == 0)
        {
            // First run: seed from the delta watermark so we don't sweep all history. If we've never
            // synced, there's nothing to true up.
            watermark = _appSettings.LastIncrementalSync;
            if (watermark == 0) return;
            _appSettings.LastUpdatedSync = watermark;
            _settingsService.Save();
        }

        var sweepStart = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        int updatedCount;
        try
        {
            var countResp = await _api.GetUpdatedMessageCountAsync(after: watermark, ct: ct);
            if (countResp.Status is < 200 or >= 300) return;
            updatedCount = countResp.Data.TryGetProperty("total", out var total)
                ? total.GetInt32() : 0;
        }
        catch (Exception ex)
        {
            AppLog.Debug(LogCategory.Sync, $"Updated-message count probe failed: {ex.Message}");
            return;
        }

        if (updatedCount <= 0)
        {
            // Nothing changed in-place; advance the watermark so the query window can't grow unbounded.
            _appSettings.LastUpdatedSync = sweepStart;
            _settingsService.Save();
            return;
        }

        var appleNs = (watermark - AppleEpochUnixMs) * 1_000_000L;
        var where = new List<object>
        {
            new Dictionary<string, object>
            {
                ["statement"] = "message.date_edited > :updatedAfter",
                ["args"] = new Dictionary<string, object> { ["updatedAfter"] = appleNs }
            }
        };

        var handleCache = new Dictionary<string, int>();
        int offset = 0;
        bool hasMore = true;

        while (hasMore)
        {
            ct.ThrowIfCancellationRequested();

            ApiResponse<List<Message>> response;
            try
            {
                response = await _api.QueryMessagesAsync(
                    withQuery: IncrementalWithQuery, where: where, sort: "ASC",
                    offset: offset, limit: IncrementalBatchSize, ct: ct);
            }
            catch (Exception ex)
            {
                AppLog.Warn(LogCategory.Sync, $"Updated-since fetch failed: {ex.Message}");
                return; // leave the watermark untouched so a transient failure retries next cycle
            }

            if (response.Status is < 200 or >= 300 || response.Data is null) return;
            var messages = response.Data;
            if (messages.Count == 0) break;

            var messagesByChat = new Dictionary<string, List<Message>>();
            foreach (var msg in messages)
            {
                var chatData = msg.Chats?.FirstOrDefault();
                if (chatData is null) continue;
                if (!messagesByChat.TryGetValue(chatData.Guid, out var list))
                    messagesByChat[chatData.Guid] = list = [];
                list.Add(msg);
            }

            foreach (var (chatGuid, chatMessages) in messagesByChat)
            {
                var chatId = await EnsureChatExistsAsync(chatGuid, chatMessages[0], handleCache, ct);
                await SaveMessagesAsync(chatId, chatMessages, handleCache, ct);
                _chatsService.NotifyMessagesPersisted(chatGuid, MessagePersistKind.NewOrUpdated);
            }

            offset += IncrementalBatchSize;
            hasMore = messages.Count == IncrementalBatchSize;
        }

        _appSettings.LastUpdatedSync = sweepStart;
        _settingsService.Save();
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
            var dirty = false;

            // Refresh server-owned metadata (display name, service, read state, etc.) from the
            // delta's embedded chat so a rename/read change made on another device propagates.
            // Client-owned pin/mute/archive are left untouched. See ChatFieldMerge.
            if (chatData is not null)
            {
                ChatFieldMerge.ApplyServerOwnedFields(chat, chatData);
                dirty = true;
            }

            // A new message means the chat is live again — undo any prior soft-delete (e.g. it
            // had been pruned as empty) so it resurfaces in the list. ApplyServerOwnedFields already
            // copies the server's DateDeleted, but the delta's embedded chat can lag, so force it
            // clear: we are holding a live message for this chat right now.
            if (chat.DateDeleted is not null)
            {
                chat.DateDeleted = null;
                dirty = true;
            }

            // Backfill participants if we have them and the stored set is empty (a chat first
            // created from a sparse payload can land without participants → renders blank).
            if (chat.ChatParticipants.Count == 0 && chatData?.Participants is { Count: > 0 })
            {
                if (await HandlePersistenceHelper.LinkParticipantsAsync(
                        db, chat.Id, chatData.Participants, handleCache, refreshExisting: true, ct))
                    dirty = true;
            }

            if (dirty) await db.SaveChangesAsync(ct);
            return chat.Id;
        }

        chat = ChatFieldMerge.InsertForLiveMessage(db, chatGuid, chatData);
        await db.SaveChangesAsync(ct);

        if (chatData?.Participants is not null)
        {
            await HandlePersistenceHelper.LinkParticipantsAsync(
                db, chat.Id, chatData.Participants, handleCache, refreshExisting: true, ct);
            await db.SaveChangesAsync(ct);
        }

        return chat.Id;
    }

    private static async Task SaveHandlesAsync(
        BlueBubblesDbContext db, List<Handle>? handles,
        Dictionary<string, int> cache, CancellationToken ct)
    {
        if (handles is null) return;

        foreach (var h in handles)
            await HandlePersistenceHelper.EnsureHandleAsync(db, h, cache, refreshExisting: true, ct);
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

    /// <summary>Lean, standalone chat-deletion reconcile for foreground use: diffs the server's chat
    /// GUID list against the cache and reloads the list only if something was actually pruned. The
    /// server emits no socket event for a conversation delete (a server limitation the Flutter app
    /// shares), so this is the only way to catch one while the app sits open. Cheap: a GUID-only
    /// query plus an in-memory diff, no message refetch. Skipped while a full delta is in flight
    /// (that already reconciles) and never overlaps itself.</summary>
    public async Task ReconcileChatsAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _isIncrementalSyncing) == 1) return;
        if (Interlocked.Exchange(ref _isReconcilingChats, 1) == 1) return;
        try
        {
            if (await ReconcileChatsCoreAsync(ct) > 0)
                await _chatsService.LoadChatsAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogCategory.Sync, $"Foreground chat reconcile failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _isReconcilingChats, 0);
        }
    }

    /// <summary>Fetches the server's full chat list and prunes any local chat it no longer returns,
    /// keeping the cache in step with deletions made on the server or another device. Best-effort and
    /// self-guarding: a failed or partial fetch prunes nothing (it would otherwise hide live chats).
    /// Returns the number of chats soft-deleted so callers can skip a needless list reload.</summary>
    private async Task<int> ReconcileChatsCoreAsync(CancellationToken ct)
    {
        var serverGuids = new HashSet<string>();
        var offset = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            ApiResponse<List<Chat>> resp;
            try
            {
                // Bare query (no participants/lastmessage expansion) — we only need GUIDs, and a
                // lean payload keeps this cheap to run on every delta.
                resp = await _api.QueryChatsAsync(
                    withQuery: [], offset: offset, limit: ChatPageSize, ct: ct);
            }
            catch (Exception ex)
            {
                AppLog.Warn(LogCategory.Sync,
                    $"Chat reconcile fetch failed at offset {offset}; skipping prune: {ex.Message}");
                return 0;
            }

            if (resp.Status is < 200 or >= 300 || resp.Data is null)
            {
                AppLog.Warn(LogCategory.Sync,
                    $"Chat reconcile fetch returned status {resp.Status}; skipping prune");
                return 0;
            }

            foreach (var chat in resp.Data)
                serverGuids.Add(chat.Guid);

            // Page until the server returns a short page; trusting /chat/count for the bound could
            // let an undercount truncate the snapshot and over-prune.
            if (resp.Data.Count < ChatPageSize) break;
            offset += ChatPageSize;
        }

        // An empty list is indistinguishable from a transient server hiccup, so only trust it (and
        // prune everything) when the count endpoint independently confirms the server has no chats.
        if (serverGuids.Count == 0 && !await ServerReportsZeroChatsAsync(ct))
        {
            AppLog.Warn(LogCategory.Sync,
                "Chat reconcile got an empty list but the server count is non-zero/unknown; skipping prune");
            return 0;
        }

        return await PruneDeletedChatsAsync(serverGuids, ct);
    }

    /// <summary>Confirms via the count endpoint that the server genuinely has zero chats, used to
    /// distinguish a real "all chats deleted" from a flaky empty query response. Conservative: any
    /// failure returns false so the caller skips pruning rather than risk wiping the list.</summary>
    private async Task<bool> ServerReportsZeroChatsAsync(CancellationToken ct)
    {
        try
        {
            var resp = await _api.GetChatCountAsync(ct);
            if (resp.Status is < 200 or >= 300) return false;
            return resp.Data!.TryGetProperty("total", out var total) && total.GetInt32() == 0;
        }
        catch (Exception ex)
        {
            AppLog.Debug(LogCategory.Sync, $"Chat count probe failed during reconcile: {ex.Message}");
            return false;
        }
    }

    /// <summary>Soft-deletes every visible local chat whose GUID is absent from <paramref name="serverChatGuids"/>.
    /// Soft delete (not a row removal) mirrors the empty-chat prune and is reversible: if the chat
    /// reappears — iMessage chat GUIDs are deterministic — the next message resurrects it via
    /// EnsureChatExistsAsync/HandleNewMessageAsync. Callers MUST pass a complete, trusted server
    /// snapshot; pruning against a partial fetch would wrongly hide live chats.</summary>
    private async Task<int> PruneDeletedChatsAsync(IReadOnlySet<string> serverChatGuids, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // Diff in memory: keeps the GUID comparison case-exact and avoids translating a (possibly
        // large) IN-clause over the server set. Already soft-deleted chats are skipped — re-pruning
        // them would needlessly bump DateDeleted.
        var visible = await db.Chats
            .Where(c => c.DateDeleted == null)
            .Select(c => new { c.Id, c.Guid })
            .ToListAsync(ct);

        var staleIds = visible
            .Where(c => !serverChatGuids.Contains(c.Guid))
            .Select(c => c.Id)
            .ToList();

        if (staleIds.Count == 0) return 0;

        var staleChats = await db.Chats.Where(c => staleIds.Contains(c.Id)).ToListAsync(ct);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var chat in staleChats)
        {
            chat.DateDeleted = now;
            chat.HasUnreadMessage = false;
        }

        await db.SaveChangesAsync(ct);
        AppLog.Info(LogCategory.Sync, $"Pruned {staleChats.Count} chat(s) deleted on the server");
        return staleChats.Count;
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

    /// <summary>Full-sync per-chat persistence that is delete-aware: the fetched newest page is
    /// authoritative for its range, so a server-side delete inside it is applied (not just upserts).
    /// This is what lets the one-time upgrade heal converge caches an older build left stale.
    /// Returns the number of locally soft-deleted messages.</summary>
    private async Task<int> ReconcileChatWindowAsync(
        int chatId, List<Message> messages,
        Dictionary<string, int> handleCache, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var pruned = await MessageWindowReconciler.ReconcileWindowAsync(db, chatId, messages, handleCache, ct);

        var maxRowId = messages
            .Where(m => m.OriginalRowId.HasValue)
            .Select(m => m.OriginalRowId!.Value)
            .DefaultIfEmpty(0)
            .Max();
        if (maxRowId > _maxSyncedRowId)
            _maxSyncedRowId = maxRowId;

        return pruned;
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
