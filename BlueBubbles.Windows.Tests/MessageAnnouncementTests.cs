using BlueBubbles.Core.Configuration;
using BlueBubbles.Core.Data.Entities;
using BlueBubbles.Core.Models;
using BlueBubbles.Core.Services;

namespace BlueBubbles.Windows.Tests;

/// <summary>Characterization of who announces <c>MessagesPersisted</c> and at what granularity.
/// Written against pre-refactor behaviour so the W1a-2 announcer consolidation can be proven not
/// to change it.</summary>
public class MessageAnnouncementTests
{
    private static Handle MakeHandle(string address, string service = "iMessage") =>
        new(0, address, service, null, null, null, null, null, null);

    private static Chat MakeChat(string guid, List<Handle>? participants = null) =>
        new(guid, guid, null, participants, null,
            false, false, false, "iMessage", null, null, null, null, null, null, false, false, null);

    private static Message MakeMessage(string guid, string? text = null,
        Handle? handle = null, long? dateCreated = null) =>
        new(null, guid, null, null, text, null, null, 0,
            dateCreated ?? 1700000000000, null, null,
            false, false, false, null, 0, null, 0, null, null, null, null, null,
            handle, false, false, null, null, null, null, null, null, null, null,
            null, false, null, false, false, false);

    private static SyncService CreateSync(
        SyncMockApiService api, TestDbContextFactory factory,
        MockChatsService chats, AppSettings settings) =>
        new(api, factory, new MockFirebaseService(), settings, new MockSettingsService(), chats);

    [Fact]
    public async Task IncrementalSync_AnnouncesOncePerChatInABatch()
    {
        var handle = MakeHandle("+15551234567");
        var chatA = MakeChat("chat-a", [handle]);
        var chatB = MakeChat("chat-b", [handle]);

        var batch = new List<Message>
        {
            MakeMessage("m-a1", "a1", handle, 1700000000001) with { OriginalRowId = 1, Chats = [chatA] },
            MakeMessage("m-b1", "b1", handle, 1700000000002) with { OriginalRowId = 2, Chats = [chatB] },
            MakeMessage("m-a2", "a2", handle, 1700000000003) with { OriginalRowId = 3, Chats = [chatA] },
        };

        var api = new SyncMockApiService([], queryMessages: offset => offset == 0 ? batch : []);
        var chats = new MockChatsService();
        var factory = TestDbContextFactory.Create();
        var svc = CreateSync(api, factory, chats, new AppSettings { LastIncrementalSync = 1700000000000 });

        await svc.RunIncrementalSyncAsync();

        // One announcement per chat per batch - NOT one per message.
        Assert.Equal(["chat-a", "chat-b"], chats.PersistedNotifications.Order().ToArray());
    }

    [Fact]
    public async Task IncrementalSync_SameChatAcrossTwoBatches_AnnouncesTwice()
    {
        var handle = MakeHandle("+15551234567");
        var chat = MakeChat("chat-a", [handle]);

        // A full page keeps the delta loop going, so the same chat is persisted in two batches.
        var batch1 = Enumerable.Range(1, 1000)
            .Select(i => MakeMessage($"d-{i}", $"t{i}", handle, 1700000000000 + i) with
            {
                OriginalRowId = i,
                Chats = [chat]
            })
            .ToList();
        var batch2 = new List<Message>
        {
            MakeMessage("d-1001", "t1001", handle, 1700000001001) with { OriginalRowId = 1001, Chats = [chat] }
        };

        var api = new SyncMockApiService([], queryMessages: offset =>
            offset == 0 ? batch1 : offset == 1000 ? batch2 : []);
        var chats = new MockChatsService();
        var factory = TestDbContextFactory.Create();
        var svc = CreateSync(api, factory, chats, new AppSettings { LastIncrementalSync = 1700000000000 });

        await svc.RunIncrementalSyncAsync();

        Assert.Equal(["chat-a", "chat-a"], chats.PersistedNotifications.ToArray());
    }

    [Fact]
    public async Task IncrementalSync_NoNewMessages_AnnouncesNothing()
    {
        var api = new SyncMockApiService([], queryMessages: _ => []);
        var chats = new MockChatsService();
        var factory = TestDbContextFactory.Create();
        var svc = CreateSync(api, factory, chats, new AppSettings { LastIncrementalSync = 1700000000000 });

        await svc.RunIncrementalSyncAsync();

        Assert.Empty(chats.PersistedNotifications);
    }

    // ---- W1a-2: every write path announces, and the kind decides the consequence ----

    [Fact]
    public void OnlyNewOrUpdated_AffectsTheConversationList()
    {
        // The anti-regression pin. ConversationListViewModel reloads the whole list off this flag, so
        // collapsing the kinds back into one would make every backfilled history page reload the list.
        Assert.True(new MessagesPersistedEventArgs("c", MessagePersistKind.NewOrUpdated)
            .AffectsConversationList);
        Assert.False(new MessagesPersistedEventArgs("c", MessagePersistKind.ServerTrueUp)
            .AffectsConversationList);
    }

    [Fact]
    public async Task IncrementalSync_AnnouncesNewOrUpdated()
    {
        var handle = MakeHandle("+15551234567");
        var chat = MakeChat("chat-a", [handle]);
        var batch = new List<Message>
        {
            MakeMessage("m-1", "hi", handle, 1700000000001) with { OriginalRowId = 1, Chats = [chat] }
        };

        var api = new SyncMockApiService([], queryMessages: offset => offset == 0 ? batch : []);
        var chats = new MockChatsService();
        var svc = CreateSync(api, TestDbContextFactory.Create(), chats,
            new AppSettings { LastIncrementalSync = 1700000000000 });

        await svc.RunIncrementalSyncAsync();

        Assert.Equal(MessagePersistKind.NewOrUpdated, Assert.Single(chats.PersistedKinds));
    }

    [Fact]
    public async Task FullSync_WindowReconcile_AnnouncesServerTrueUp()
    {
        var handle = MakeHandle("+15551234567");
        var chat = MakeChat("chat-a", [handle]);
        var api = new SyncMockApiService([chat], chatMessages: new()
        {
            ["chat-a"] = [MakeMessage("m-1", "hi", handle, 1700000000001)]
        });
        var chats = new MockChatsService();
        var svc = CreateSync(api, TestDbContextFactory.Create(), chats, new AppSettings());

        await svc.RunFullSyncAsync(skipEmptyChats: false);

        // A true-up of the newest window is not a new message: announced, but list-neutral.
        Assert.Equal(MessagePersistKind.ServerTrueUp, Assert.Single(chats.PersistedKinds));
    }

    private static (MessagesService Svc, TestDbContextFactory Factory, MockChatsService Chats) CreateMessages(
        SyncMockApiService api)
    {
        var factory = TestDbContextFactory.Create();
        var chats = new MockChatsService();
        return (new MessagesService(factory, api, chats), factory, chats);
    }

    [Fact]
    public async Task FetchOlderMessages_AnnouncesServerTrueUp()
    {
        var handle = MakeHandle("+15551234567");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var api = new SyncMockApiService([], chatMessages: new()
        {
            ["chat-a"] = [MakeMessage("older-1", "older", handle, now - 200_000)]
        });
        var (svc, factory, chats) = CreateMessages(api);

        int chatId;
        using (var db = factory.CreateDbContext())
        {
            var c = new ChatEntity { Guid = "chat-a", OldestSyncedMessageDate = now - 100_000 };
            db.Chats.Add(c);
            db.SaveChanges();
            chatId = c.Id;
        }

        await svc.FetchOlderMessagesFromServerAsync(chatId, "chat-a");

        Assert.Equal("chat-a", Assert.Single(chats.PersistedNotifications));
        Assert.Equal(MessagePersistKind.ServerTrueUp, Assert.Single(chats.PersistedKinds));
    }

    [Fact]
    public async Task HydrateEmptyChat_AnnouncesServerTrueUp()
    {
        var handle = MakeHandle("+15551234567");
        var api = new SyncMockApiService([], chatMessages: new()
        {
            ["chat-a"] = [MakeMessage("m-1", "hi", handle, 1700000000001)]
        });
        var (svc, factory, chats) = CreateMessages(api);

        int chatId;
        using (var db = factory.CreateDbContext())
        {
            var c = new ChatEntity { Guid = "chat-a" };
            db.Chats.Add(c);
            db.SaveChanges();
            chatId = c.Id;
        }

        Assert.True(await svc.EnsureChatHydratedAsync(chatId, "chat-a"));
        Assert.Equal(MessagePersistKind.ServerTrueUp, Assert.Single(chats.PersistedKinds));
    }

    [Fact]
    public async Task RefreshLatestFromServer_AnnouncesServerTrueUp()
    {
        var handle = MakeHandle("+15551234567");
        var api = new SyncMockApiService([], chatMessages: new()
        {
            ["chat-a"] = [MakeMessage("m-1", "hi", handle, 1700000000001)]
        });
        var (svc, factory, chats) = CreateMessages(api);

        int chatId;
        using (var db = factory.CreateDbContext())
        {
            var c = new ChatEntity { Guid = "chat-a" };
            db.Chats.Add(c);
            db.SaveChanges();
            chatId = c.Id;
        }

        Assert.True(await svc.RefreshLatestFromServerAsync(chatId, "chat-a"));
        Assert.Equal(MessagePersistKind.ServerTrueUp, Assert.Single(chats.PersistedKinds));
    }

    [Fact]
    public async Task DeleteMessage_AnnouncesTheSoftDelete()
    {
        // PUNCHLIST B8: the soft delete used to be persisted with no announcement at all.
        var api = new SyncMockApiService([]);
        var (svc, factory, chats) = CreateMessages(api);

        using (var db = factory.CreateDbContext())
        {
            var c = new ChatEntity { Guid = "chat-a" };
            db.Chats.Add(c);
            db.SaveChanges();
            db.Messages.Add(new MessageEntity
            {
                Guid = "m-1", ChatId = c.Id, Text = "bye", DateCreated = 1700000000001
            });
            db.SaveChanges();
        }

        Assert.True(await svc.DeleteMessageAsync("chat-a", "m-1"));

        using var db2 = factory.CreateDbContext();
        Assert.NotNull(db2.Messages.Single(m => m.Guid == "m-1").DateDeleted);
        Assert.Equal("chat-a", Assert.Single(chats.PersistedNotifications));
        Assert.Equal(MessagePersistKind.ServerTrueUp, Assert.Single(chats.PersistedKinds));
    }
}
