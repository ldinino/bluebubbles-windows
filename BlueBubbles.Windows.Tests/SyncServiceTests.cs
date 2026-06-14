using System.Text.Json;
using BlueBubbles.Core.Configuration;
using BlueBubbles.Core.Data.Entities;
using BlueBubbles.Core.Models;
using BlueBubbles.Core.Services;

namespace BlueBubbles.Windows.Tests;

public class SyncServiceTests
{
    private static Handle MakeHandle(string address, string service = "iMessage") =>
        new(0, address, service, null, null, null, null, null, null);

    private static Chat MakeChat(string guid, List<Handle>? participants = null, Message? lastMessage = null) =>
        new(guid, guid, null, participants, lastMessage,
            false, false, false, "iMessage", null, null, null, null, null, null, false, false, null);

    private static Message MakeMessage(string guid, string? text = null,
        Handle? handle = null, long? dateCreated = null) =>
        new(null, guid, null, null, text, null, null, 0,
            dateCreated ?? 1700000000000, null, null,
            false, false, false, null, 0, null, 0, null, null, null, null, null,
            handle, false, false, null, null, null, null, null, null, null, null,
            null, false, null, false, false, false);

    private static (SyncService Svc, TestDbContextFactory Factory) CreateService(
        SyncMockApiService api, MockFirebaseService? firebase = null)
    {
        var factory = TestDbContextFactory.Create();
        firebase ??= new MockFirebaseService();
        var appSettings = new AppSettings();
        var settingsService = new MockSettingsService();
        var chatsService = new MockChatsService();
        return (new SyncService(api, factory, firebase, appSettings, settingsService, chatsService), factory);
    }

    [Fact]
    public async Task ChatPagination_BreaksOnEmptyPage()
    {
        var chats = Enumerable.Range(0, 250)
            .Select(i => MakeChat($"chat-{i}"))
            .ToList();

        var api = new SyncMockApiService(chats);
        var (svc, factory) = CreateService(api);

        await svc.RunFullSyncAsync(skipEmptyChats: false);

        using var db = factory.CreateDbContext();
        Assert.Equal(250, db.Chats.Count());
        Assert.True(api.QueryChatsCallCount >= 2,
            $"Expected at least 2 pagination calls, got {api.QueryChatsCallCount}");
    }

    [Fact]
    public async Task HandleDeduplication_SharedParticipants()
    {
        var sharedHandle = MakeHandle("+15551234567");
        var chats = new List<Chat>
        {
            MakeChat("chat-a", [sharedHandle, MakeHandle("+15559999999")]),
            MakeChat("chat-b", [sharedHandle, MakeHandle("+15558888888")])
        };

        var api = new SyncMockApiService(chats);
        var (svc, factory) = CreateService(api);

        await svc.RunFullSyncAsync();

        using var db = factory.CreateDbContext();
        var handles = db.Handles.Where(h => h.Address == "+15551234567").ToList();
        Assert.Single(handles);
        Assert.Equal(3, db.Handles.Count());
    }

    [Fact]
    public async Task IdempotentSync_NoDuplicates()
    {
        var handle = MakeHandle("+15551234567");
        var recentDate = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds();
        var lastMsg = MakeMessage("last-msg", "last", handle, recentDate);
        var chats = new List<Chat> { MakeChat("chat-1", [handle], lastMsg) };
        var messages = new Dictionary<string, List<Message>>
        {
            ["chat-1"] = [MakeMessage("msg-1", "hello", handle, recentDate - 1000)]
        };

        var api = new SyncMockApiService(chats, messages);
        var (svc, factory) = CreateService(api);

        await svc.RunFullSyncAsync();
        await svc.RunFullSyncAsync();

        using var db = factory.CreateDbContext();
        Assert.Equal(1, db.Chats.Count());
        Assert.Equal(1, db.Handles.Count());
        Assert.Equal(1, db.Messages.Count());
    }

    [Fact]
    public async Task CancellationToken_StopsSync()
    {
        var chats = Enumerable.Range(0, 10)
            .Select(i => MakeChat($"chat-{i}"))
            .ToList();

        var cts = new CancellationTokenSource();
        var api = new SyncMockApiService(chats, onQueryChats: () => cts.Cancel());
        var (svc, _) = CreateService(api);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => svc.RunFullSyncAsync(skipEmptyChats: false, ct: cts.Token));
    }

    [Fact]
    public async Task ProgressReporting_ReportsAllPhases()
    {
        var chats = new List<Chat> { MakeChat("chat-1") };
        var api = new SyncMockApiService(chats);
        var (svc, _) = CreateService(api);

        var phases = new List<SyncPhase>();
        var progress = new SyncTestProgress<SyncProgress>(p => phases.Add(p.Phase));

        await svc.RunFullSyncAsync(skipEmptyChats: false, progress: progress);

        Assert.Contains(SyncPhase.Starting, phases);
        Assert.Contains(SyncPhase.SyncingChats, phases);
        Assert.Contains(SyncPhase.SyncingMessages, phases);
        Assert.Contains(SyncPhase.FetchingFcmConfig, phases);
        Assert.Contains(SyncPhase.Complete, phases);
    }

    [Fact]
    public async Task SkipEmptyChats_SoftDeletesChatsWithNoMessages()
    {
        var handle = MakeHandle("+15551234567");
        var msgHandle = MakeHandle("+15559999999");
        var recentDate = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds();
        var lastMsg = MakeMessage("last-msg", "last", msgHandle, recentDate);
        var chats = new List<Chat>
        {
            MakeChat("chat-with-msgs", [handle], lastMsg),
            MakeChat("chat-empty", [handle]),
            MakeChat("chat-no-participants", []),
        };
        var messages = new Dictionary<string, List<Message>>
        {
            ["chat-with-msgs"] = [MakeMessage("msg-1", "hello", msgHandle, recentDate - 1000)]
        };

        var api = new SyncMockApiService(chats, messages);
        var (svc, factory) = CreateService(api);

        await svc.RunFullSyncAsync(skipEmptyChats: true);

        using var db = factory.CreateDbContext();
        var allChats = db.Chats.ToList();
        Assert.Equal(3, allChats.Count);

        var active = allChats.Where(c => c.DateDeleted == null).ToList();
        Assert.Single(active);
        Assert.Equal("chat-with-msgs", active[0].Guid);

        var deleted = allChats.Where(c => c.DateDeleted != null).ToList();
        Assert.Equal(2, deleted.Count);
        Assert.All(deleted, c => Assert.False(c.HasUnreadMessage));
    }

    [Fact]
    public async Task SkipEmptyChatsDisabled_KeepsEmptyChats()
    {
        var handle = MakeHandle("+15551234567");
        var chats = new List<Chat>
        {
            MakeChat("chat-empty", [handle]),
        };

        var api = new SyncMockApiService(chats);
        var (svc, factory) = CreateService(api);

        await svc.RunFullSyncAsync(skipEmptyChats: false);

        using var db = factory.CreateDbContext();
        var chat = db.Chats.Single();
        Assert.Null(chat.DateDeleted);
    }

    [Fact]
    public async Task InitialSync_FetchesLatestPage_NoAfterFloor()
    {
        // Regression: the old tiered window applied an `after` date floor that could exclude a
        // chat's most recent messages. The newest page must be fetched with no `after`.
        var handle = MakeHandle("+15551234567");
        var recentDate = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds();
        var lastMsg = MakeMessage("last-msg", "hi", handle, recentDate);
        var chats = new List<Chat> { MakeChat("chat-recent", [handle], lastMsg) };
        var messages = new Dictionary<string, List<Message>>
        {
            ["chat-recent"] = [MakeMessage("msg-1", "hello", handle, recentDate - 1000)]
        };

        var api = new SyncMockApiService(chats, messages);
        var (svc, _) = CreateService(api);

        await svc.RunFullSyncAsync();

        var call = api.ChatMessageCalls.First(c => c.Guid == "chat-recent");
        Assert.Null(call.After);
    }

    [Fact]
    public async Task InitialSync_IdleChat_NotSoftDeleted_AndMessagesSynced()
    {
        // Regression for S1/S3: a chat idle for far longer than the old 7-day floor must still
        // pull its latest messages and stay active rather than being pruned as "empty".
        var handle = MakeHandle("+15551234567");
        var olderDate = DateTimeOffset.UtcNow.AddDays(-90).ToUnixTimeMilliseconds();
        var lastMsg = MakeMessage("last-msg", "hi", handle, olderDate);
        var chats = new List<Chat> { MakeChat("chat-idle", [handle], lastMsg) };
        var messages = new Dictionary<string, List<Message>>
        {
            ["chat-idle"] = [MakeMessage("msg-1", "hello", handle, olderDate - 1000)]
        };

        var api = new SyncMockApiService(chats, messages);
        var (svc, factory) = CreateService(api);

        await svc.RunFullSyncAsync(skipEmptyChats: true);

        using var db = factory.CreateDbContext();
        var chat = db.Chats.Single();
        Assert.Null(chat.DateDeleted);                 // not pruned
        Assert.Equal(1, db.Messages.Count(m => m.ChatId == chat.Id));   // history synced
    }

    [Fact]
    public async Task InitialSync_SetsOldestSyncedMessageDate()
    {
        var handle = MakeHandle("+15551234567");
        var recentDate = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds();
        var msgDate = DateTimeOffset.UtcNow.AddDays(-10).ToUnixTimeMilliseconds();
        var lastMsg = MakeMessage("last-msg", "hi", handle, recentDate);
        var chats = new List<Chat> { MakeChat("chat-1", [handle], lastMsg) };
        var messages = new Dictionary<string, List<Message>>
        {
            ["chat-1"] = [
                MakeMessage("msg-1", "hello", handle, msgDate),
                MakeMessage("msg-2", "world", handle, msgDate + 5000)
            ]
        };

        var api = new SyncMockApiService(chats, messages);
        var (svc, factory) = CreateService(api);

        await svc.RunFullSyncAsync();

        using var db = factory.CreateDbContext();
        var chat = db.Chats.Single();
        Assert.NotNull(chat.OldestSyncedMessageDate);
        Assert.Equal(msgDate, chat.OldestSyncedMessageDate);
    }

    [Fact]
    public async Task InitialSync_CapsAtOnePagePerChat()
    {
        // Initial sync pulls a single newest page (~100) per chat; deeper history is on-demand.
        var handle = MakeHandle("+15551234567");
        var recentDate = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds();
        var lastMsg = MakeMessage("last-msg", "hi", handle, recentDate);
        var chats = new List<Chat> { MakeChat("chat-big", [handle], lastMsg) };

        var msgs = Enumerable.Range(0, 150)
            .Select(i => MakeMessage($"msg-{i}", $"text-{i}", handle,
                recentDate - (150 - i) * 1000))
            .ToList();
        var messages = new Dictionary<string, List<Message>> { ["chat-big"] = msgs };

        var api = new SyncMockApiService(chats, messages);
        var (svc, factory) = CreateService(api);

        await svc.RunFullSyncAsync();

        var calls = api.ChatMessageCalls.Where(c => c.Guid == "chat-big").ToList();
        Assert.Single(calls);
        Assert.Equal(100, calls[0].Limit);

        using var db = factory.CreateDbContext();
        Assert.Equal(100, db.Messages.Count());
    }

    [Fact]
    public async Task IncrementalSync_CheckpointsWatermarkPerBatch()
    {
        // A delta interrupted mid-backfill must persist the cursor for the batches it did complete,
        // so the next run resumes instead of refetching from the original cursor.
        var handle = MakeHandle("+15551234567");
        var chat = MakeChat("chat-delta", [handle]);

        // First batch: a full page (1000) so the loop continues; second call throws (simulated drop).
        var batch1 = Enumerable.Range(1, 1000)
            .Select(i => MakeMessage($"d-{i}", $"t{i}", handle, 1700000000000 + i) with
            {
                OriginalRowId = i,
                Chats = [chat]
            })
            .ToList();

        var api = new SyncMockApiService([], queryMessages: offset =>
            offset == 0 ? batch1 : throw new InvalidOperationException("network dropped"));

        var factory = TestDbContextFactory.Create();
        var appSettings = new AppSettings { LastIncrementalSync = 1700000000000 };
        var svc = new SyncService(api, factory, new MockFirebaseService(),
            appSettings, new MockSettingsService(), new MockChatsService());

        await svc.RunIncrementalSyncAsync();

        // The completed batch's max ROWID was checkpointed before the second batch failed.
        Assert.Equal(1000, appSettings.LastIncrementalSyncRowId);
        using var db = factory.CreateDbContext();
        Assert.Equal(1000, db.Messages.Count());
    }

    [Fact]
    public async Task IncrementalSync_ResurrectsSoftDeletedChat()
    {
        var handle = MakeHandle("+15551234567");
        var chat = MakeChat("chat-zombie", [handle]);

        var factory = TestDbContextFactory.Create();
        using (var db = factory.CreateDbContext())
        {
            db.Chats.Add(new ChatEntity { Guid = "chat-zombie", DateDeleted = 12345 });
            db.SaveChanges();
        }

        var batch = new List<Message>
        {
            MakeMessage("d-1", "back", handle, 1700000000500) with { OriginalRowId = 5, Chats = [chat] }
        };
        // The chat still exists on the server (it just received a message), so reconcile must keep it.
        var api = new SyncMockApiService([chat], queryMessages: offset => offset == 0 ? batch : []);

        var appSettings = new AppSettings { LastIncrementalSync = 1700000000000 };
        var svc = new SyncService(api, factory, new MockFirebaseService(),
            appSettings, new MockSettingsService(), new MockChatsService());

        await svc.RunIncrementalSyncAsync();

        using var db2 = factory.CreateDbContext();
        var resurrected = db2.Chats.Single(c => c.Guid == "chat-zombie");
        Assert.Null(resurrected.DateDeleted);
    }

    [Fact]
    public async Task IncrementalSync_PrunesChatDeletedOnServer()
    {
        // Server is the single source of truth: a chat present locally but absent from the server's
        // chat list was deleted there (or on another device) and must be soft-deleted here.
        var handle = MakeHandle("+15551234567");
        var keep = MakeChat("chat-keep", [handle]);

        var factory = TestDbContextFactory.Create();
        using (var db = factory.CreateDbContext())
        {
            db.Chats.Add(new ChatEntity { Guid = "chat-keep" });
            db.Chats.Add(new ChatEntity { Guid = "chat-gone", HasUnreadMessage = true });
            db.SaveChanges();
        }

        // Server still has chat-keep; chat-gone is gone. No new messages in the delta.
        var api = new SyncMockApiService([keep], queryMessages: _ => []);

        var appSettings = new AppSettings { LastIncrementalSync = 1700000000000 };
        var svc = new SyncService(api, factory, new MockFirebaseService(),
            appSettings, new MockSettingsService(), new MockChatsService());

        await svc.RunIncrementalSyncAsync();

        using var db2 = factory.CreateDbContext();
        Assert.Null(db2.Chats.Single(c => c.Guid == "chat-keep").DateDeleted);

        var gone = db2.Chats.Single(c => c.Guid == "chat-gone");
        Assert.NotNull(gone.DateDeleted);
        Assert.False(gone.HasUnreadMessage);
    }

    [Fact]
    public async Task FullSync_PrunesStaleChatNotOnServer()
    {
        // Re-running a full sync over an existing cache must drop chats the server no longer has.
        var handle = MakeHandle("+15551234567");
        var recentDate = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds();
        var lastMsg = MakeMessage("last-msg", "hi", handle, recentDate);
        var keep = MakeChat("chat-keep", [handle], lastMsg);
        var messages = new Dictionary<string, List<Message>>
        {
            ["chat-keep"] = [MakeMessage("msg-1", "hello", handle, recentDate - 1000)]
        };

        var api = new SyncMockApiService([keep], messages);
        var (svc, factory) = CreateService(api);

        using (var db = factory.CreateDbContext())
        {
            db.Chats.Add(new ChatEntity { Guid = "chat-stale" });
            db.SaveChanges();
        }

        await svc.RunFullSyncAsync();

        using var db2 = factory.CreateDbContext();
        Assert.Null(db2.Chats.Single(c => c.Guid == "chat-keep").DateDeleted);
        Assert.NotNull(db2.Chats.Single(c => c.Guid == "chat-stale").DateDeleted);
    }

    [Fact]
    public async Task IncrementalSync_ServerHasNoChats_PrunesAll()
    {
        // The honest "everything was deleted" case: an empty query AND a zero count -> prune all.
        var factory = TestDbContextFactory.Create();
        using (var db = factory.CreateDbContext())
        {
            db.Chats.Add(new ChatEntity { Guid = "chat-a" });
            db.Chats.Add(new ChatEntity { Guid = "chat-b" });
            db.SaveChanges();
        }

        var api = new SyncMockApiService([], queryMessages: _ => []); // count defaults to 0

        var appSettings = new AppSettings { LastIncrementalSync = 1700000000000 };
        var svc = new SyncService(api, factory, new MockFirebaseService(),
            appSettings, new MockSettingsService(), new MockChatsService());

        await svc.RunIncrementalSyncAsync();

        using var db2 = factory.CreateDbContext();
        Assert.All(db2.Chats.ToList(), c => Assert.NotNull(c.DateDeleted));
    }

    [Fact]
    public async Task IncrementalSync_EmptyServerListButNonZeroCount_DoesNotPrune()
    {
        // A flaky empty query response must NOT wipe the cache: pruning is skipped unless the count
        // endpoint independently confirms the server really has zero chats.
        var factory = TestDbContextFactory.Create();
        using (var db = factory.CreateDbContext())
        {
            db.Chats.Add(new ChatEntity { Guid = "chat-live" });
            db.SaveChanges();
        }

        // Query returns nothing, but the server insists it has chats — treat the empty list as a glitch.
        var api = new SyncMockApiService([], queryMessages: _ => [], chatCountOverride: 3);

        var appSettings = new AppSettings { LastIncrementalSync = 1700000000000 };
        var svc = new SyncService(api, factory, new MockFirebaseService(),
            appSettings, new MockSettingsService(), new MockChatsService());

        await svc.RunIncrementalSyncAsync();

        using var db2 = factory.CreateDbContext();
        Assert.Null(db2.Chats.Single(c => c.Guid == "chat-live").DateDeleted);
    }

    [Fact]
    public async Task IncrementalSync_ChatFetchFails_DoesNotPrune()
    {
        // If the chat-list fetch errors, reconcile must prune nothing rather than hide live chats.
        var factory = TestDbContextFactory.Create();
        using (var db = factory.CreateDbContext())
        {
            db.Chats.Add(new ChatEntity { Guid = "chat-live" });
            db.SaveChanges();
        }

        var api = new SyncMockApiService([], queryMessages: _ => [],
            onQueryChats: () => throw new InvalidOperationException("network dropped"));

        var appSettings = new AppSettings { LastIncrementalSync = 1700000000000 };
        var svc = new SyncService(api, factory, new MockFirebaseService(),
            appSettings, new MockSettingsService(), new MockChatsService());

        await svc.RunIncrementalSyncAsync();

        using var db2 = factory.CreateDbContext();
        Assert.Null(db2.Chats.Single(c => c.Guid == "chat-live").DateDeleted);
    }
}

internal class SyncTestProgress<T> : IProgress<T>
{
    private readonly Action<T> _handler;
    public SyncTestProgress(Action<T> handler) => _handler = handler;
    public void Report(T value) => _handler(value);
}

internal record ChatMessageCall(string Guid, long? Before, long? After, int Offset, int Limit);

internal class SyncMockApiService : IBlueBubblesApiService
{
    public string? OriginOverride { get; set; }

    private readonly List<Chat> _chats;
    private readonly Dictionary<string, List<Message>> _chatMessages;
    private readonly Action? _onQueryChats;
    private readonly Func<int, List<Message>>? _queryMessages;
    private readonly int? _chatCountOverride;

    public int QueryChatsCallCount { get; private set; }
    public List<ChatMessageCall> ChatMessageCalls { get; } = [];

    public SyncMockApiService(
        List<Chat> chats,
        Dictionary<string, List<Message>>? chatMessages = null,
        Action? onQueryChats = null,
        Func<int, List<Message>>? queryMessages = null,
        int? chatCountOverride = null)
    {
        _chats = chats;
        _chatMessages = chatMessages ?? new();
        _onQueryChats = onQueryChats;
        _queryMessages = queryMessages;
        // Lets a test decouple the reported count from the queryable list (e.g. count > 0 while the
        // query returns empty) to exercise reconcile's transient-empty-response guard.
        _chatCountOverride = chatCountOverride;
    }

    public Task<ApiResponse<JsonElement>> GetChatCountAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var total = _chatCountOverride ?? _chats.Count;
        var json = JsonSerializer.Deserialize<JsonElement>($"{{\"total\":{total}}}");
        return Task.FromResult(new ApiResponse<JsonElement>(200, "OK", json, null));
    }

    public Task<ApiResponse<List<Chat>>> QueryChatsAsync(
        List<string>? withQuery = null, int offset = 0, int limit = 100,
        string? sort = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        QueryChatsCallCount++;
        _onQueryChats?.Invoke();
        var page = _chats.Skip(offset).Take(limit).ToList();
        return Task.FromResult(new ApiResponse<List<Chat>>(200, "OK", page, null));
    }

    public Task<ApiResponse<List<Message>>> GetChatMessagesAsync(
        string guid, string? withQuery = null, string sort = "DESC",
        long? before = null, long? after = null,
        int offset = 0, int limit = 100, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ChatMessageCalls.Add(new(guid, before, after, offset, limit));

        var allMsgs = _chatMessages.TryGetValue(guid, out var m) ? m : [];

        IEnumerable<Message> filtered = allMsgs;
        if (after.HasValue)
            filtered = filtered.Where(msg => msg.DateCreated > after.Value);
        if (before.HasValue)
            filtered = filtered.Where(msg => msg.DateCreated < before.Value);

        // Mirror the server: DESC = newest first, so a limited page returns the most recent messages.
        filtered = sort.Equals("DESC", StringComparison.OrdinalIgnoreCase)
            ? filtered.OrderByDescending(msg => msg.DateCreated)
            : filtered.OrderBy(msg => msg.DateCreated);

        var page = filtered.Skip(offset).Take(limit).ToList();
        return Task.FromResult(new ApiResponse<List<Message>>(200, "OK", page, null));
    }

    // Stubs — only sync-related methods are implemented
    public Task<ApiResponse<JsonElement>> PingAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<ServerInfo>> GetServerInfoAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> SoftRestartAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> HardRestartAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> CheckUpdateAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> InstallUpdateAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> GetStatTotalsAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> GetStatMediaAsync(bool byChat = false, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> GetServerLogsAsync(int count = 10000, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> LockMacAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> RestartImessageAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> AddFcmDeviceAsync(string name, string identifier, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> GetFcmClientAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<Attachment>> GetAttachmentInfoAsync(string guid, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> DownloadAttachmentAsync(string guid, bool original = false, IProgress<double>? progress = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> DownloadLivePhotoAsync(string guid, IProgress<double>? progress = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> GetAttachmentBlurhashAsync(string guid, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> GetAttachmentCountAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<Chat>> GetChatAsync(string guid, string? withQuery = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<Chat>> CreateChatAsync(List<string> addresses, string? message, string service, string method = "private-api", CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<Chat>> UpdateChatAsync(string guid, string displayName, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> DeleteChatAsync(string guid, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> MarkChatReadAsync(string guid, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> MarkChatUnreadAsync(string guid, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> GetChatIconAsync(string guid, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> SetChatIconAsync(string guid, Stream iconStream, string fileName, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> DeleteChatIconAsync(string guid, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<Chat>> AddParticipantAsync(string chatGuid, string address, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<Chat>> RemoveParticipantAsync(string chatGuid, string address, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> LeaveChatAsync(string guid, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> DeleteMessageFromChatAsync(string chatGuid, string messageGuid, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<List<Message>>> QueryMessagesAsync(List<string>? withQuery = null, List<object>? where = null, string sort = "DESC", long? before = null, long? after = null, string? chatGuid = null, int offset = 0, int limit = 100, bool convertAttachments = true, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_queryMessages is null) throw new NotImplementedException();
        return Task.FromResult(new ApiResponse<List<Message>>(200, "OK", _queryMessages(offset), null));
    }
    public Task<ApiResponse<Message>> GetMessageAsync(string guid, string? withQuery = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> GetEmbeddedMediaAsync(string guid, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> GetMessageCountAsync(long? after = null, long? before = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> GetUpdatedMessageCountAsync(long? after = null, long? before = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> GetMyMessageCountAsync(long? after = null, long? before = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<Message>> SendTextAsync(string chatGuid, string tempGuid, string message, string? method = null, string? effectId = null, string? subject = null, string? selectedMessageGuid = null, int? partIndex = null, bool? ddScan = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<Message>> SendAttachmentAsync(string chatGuid, string tempGuid, Stream fileStream, string fileName, string? method = null, string? effectId = null, string? subject = null, string? selectedMessageGuid = null, int? partIndex = null, bool? isAudioMessage = null, IProgress<double>? progress = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<Message>> SendMultipartAsync(string chatGuid, string tempGuid, List<Dictionary<string, object?>> parts, string? effectId = null, string? subject = null, string? selectedMessageGuid = null, int? partIndex = null, bool? ddScan = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<Message>> SendTapbackAsync(string chatGuid, string selectedMessageText, string selectedMessageGuid, string reaction, int? partIndex = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<Message>> UnsendMessageAsync(string messageGuid, int partIndex = 0, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<Message>> EditMessageAsync(string messageGuid, string editedMessage, string backwardsCompatMessage, int partIndex = 0, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> NotifyMessageAsync(string messageGuid, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<List<ScheduledMessage>>> GetScheduledMessagesAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<ScheduledMessage>> CreateScheduledMessageAsync(string chatGuid, string message, long scheduledForMs, string method = "private-api", string? effectId = null, string? subject = null, string? selectedMessageGuid = null, int? partIndex = null, Dictionary<string, object?>? schedule = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<ScheduledMessage>> UpdateScheduledMessageAsync(int id, string chatGuid, string message, long scheduledForMs, string method = "private-api", string? effectId = null, string? subject = null, string? selectedMessageGuid = null, int? partIndex = null, Dictionary<string, object?>? schedule = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> DeleteScheduledMessageAsync(int id, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<List<Handle>>> QueryHandlesAsync(List<string>? withQuery = null, string? address = null, int offset = 0, int limit = 100, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<Handle>> GetHandleAsync(string guid, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> GetHandleFocusStateAsync(string address, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> GetIMessageAvailabilityAsync(string address, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> GetFaceTimeAvailabilityAsync(string address, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> GetHandleCountAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<List<FindMyDevice>>> GetFindMyDevicesAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<List<FindMyDevice>>> RefreshFindMyDevicesAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<List<FindMyFriend>>> GetFindMyFriendsAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<List<FindMyFriend>>> RefreshFindMyFriendsAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> GetAccountInfoAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> GetAccountContactAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> SetAccountAliasAsync(string alias, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> AnswerFaceTimeAsync(string callUuid, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> LeaveFaceTimeAsync(string callUuid, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> GetThemeAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> SetThemeAsync(string name, Dictionary<string, object?> data, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> DeleteThemeAsync(string name, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> GetSettingsBackupAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> SetSettingsBackupAsync(string name, Dictionary<string, object?> data, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> DeleteSettingsBackupAsync(string name, CancellationToken ct = default) => throw new NotImplementedException();
}

internal class MockFirebaseService : IFirebaseService
{
    public Task FetchAndStoreConfigAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<string?> FetchNewServerUrlAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
}

internal class MockSettingsService : ISettingsService
{
    public void Save() { }
    public void Load() { }
}

internal class MockChatsService : IChatsService
{
    public List<string> PersistedNotifications { get; } = [];
    public IReadOnlyList<ChatWithParticipants> Chats => [];
    public IReadOnlyList<ChatWithParticipants> ArchivedChats => [];
    public event EventHandler? ChatsChanged;
    public event EventHandler<string>? ChatUpdated;
    public event EventHandler? ArchivedChatsChanged;
    public event EventHandler<string>? MessagesPersisted;
    public Task LoadChatsAsync() => Task.CompletedTask;
    public Task LoadArchivedChatsAsync() => Task.CompletedTask;
    public Task HandleNewMessageAsync(string chatGuid, string? messageText, long dateCreated, bool isFromMe, string? senderAddress = null) => Task.CompletedTask;
    public Task MarkChatReadAsync(string chatGuid, bool read, bool notifyServer = true) => Task.CompletedTask;
    public Task TogglePinAsync(string chatGuid) => Task.CompletedTask;
    public Task ReorderPinsAsync(List<string> chatGuids) => Task.CompletedTask;
    public Task ArchiveChatAsync(string chatGuid) => Task.CompletedTask;
    public Task UnarchiveChatAsync(string chatGuid) => Task.CompletedTask;
    public Task<bool> DeleteChatAsync(string chatGuid) => Task.FromResult(true);
    public Task<bool> RenameChatAsync(string chatGuid, string newName) => Task.FromResult(true);
    public Task ToggleMuteAsync(string chatGuid) => Task.CompletedTask;
    public Task<bool> AddParticipantAsync(string chatGuid, string address) => Task.FromResult(true);
    public Task<bool> RemoveParticipantAsync(string chatGuid, string address) => Task.FromResult(true);
    public Task<bool> LeaveChatAsync(string chatGuid) => Task.FromResult(true);
    public Task<bool> SetChatIconAsync(string chatGuid, Stream iconStream, string fileName) => Task.FromResult(true);
    public Task<bool> DeleteChatIconAsync(string chatGuid) => Task.FromResult(true);
    public string? FindExistingChatGuid(IEnumerable<string> addresses) => null;
    public Task EnsureChatInDatabaseAsync(Chat chat, string? messageText) => Task.CompletedTask;
    public Task EnsureChatExistsAsync(Chat chatData) => Task.CompletedTask;
    public void NotifyMessagesPersisted(string chatGuid)
    {
        PersistedNotifications.Add(chatGuid);
        MessagesPersisted?.Invoke(this, chatGuid);
    }
}
