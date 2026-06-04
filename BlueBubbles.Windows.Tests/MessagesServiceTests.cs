using BlueBubbles.Core.Data.Entities;
using BlueBubbles.Core.Models;
using BlueBubbles.Core.Services;

namespace BlueBubbles.Windows.Tests;

public class MessagesServiceTests
{
    private static (MessagesService Service, TestDbContextFactory Factory, SyncMockApiService Api) CreateService(
        SyncMockApiService? api = null)
    {
        var factory = TestDbContextFactory.Create();
        api ??= new SyncMockApiService([]);
        return (new MessagesService(factory, api), factory, api);
    }

    private static ChatEntity SeedChat(TestDbContextFactory factory, string guid = "chat;+11234567890")
    {
        using var db = factory.CreateDbContext();
        var chat = new ChatEntity { Guid = guid };
        db.Chats.Add(chat);
        db.SaveChanges();
        return chat;
    }

    private static void SeedMessages(TestDbContextFactory factory, int chatId, int count, long startDate = 1000000)
    {
        using var db = factory.CreateDbContext();
        for (var i = 0; i < count; i++)
        {
            db.Messages.Add(new MessageEntity
            {
                Guid = $"msg-{i}",
                ChatId = chatId,
                Text = $"Message {i}",
                DateCreated = startDate + i * 1000,
                IsFromMe = i % 2 == 0
            });
        }
        db.SaveChanges();
    }

    [Fact]
    public async Task LoadMessages_ReturnsSortedOldestFirst()
    {
        var (svc, factory, _) = CreateService();
        var chat = SeedChat(factory);
        SeedMessages(factory, chat.Id, 5);

        var messages = await svc.LoadMessagesAsync(chat.Id);

        Assert.Equal(5, messages.Count);
        Assert.True(messages[0].DateCreated < messages[^1].DateCreated);
    }

    [Fact]
    public async Task LoadMessages_RespectsLimit()
    {
        var (svc, factory, _) = CreateService();
        var chat = SeedChat(factory);
        SeedMessages(factory, chat.Id, 20);

        var messages = await svc.LoadMessagesAsync(chat.Id, limit: 10);

        Assert.Equal(10, messages.Count);
    }

    [Fact]
    public async Task LoadMessages_ReturnsNewestFirst_WhenLimited()
    {
        var (svc, factory, _) = CreateService();
        var chat = SeedChat(factory);
        SeedMessages(factory, chat.Id, 20);

        var messages = await svc.LoadMessagesAsync(chat.Id, limit: 5);

        // Should return the 5 newest messages (reversed to oldest-first)
        Assert.Equal("msg-15", messages[0].Guid);
        Assert.Equal("msg-19", messages[^1].Guid);
    }

    [Fact]
    public async Task LoadMessages_BeforeDate_ReturnsOlderMessages()
    {
        var (svc, factory, _) = CreateService();
        var chat = SeedChat(factory);
        SeedMessages(factory, chat.Id, 10, startDate: 1000000);

        // Load messages before the 6th message (date = 1005000)
        var messages = await svc.LoadMessagesAsync(chat.Id, limit: 50, beforeDate: 1005000);

        Assert.Equal(5, messages.Count);
        Assert.True(messages.All(m => m.DateCreated < 1005000));
    }

    [Fact]
    public async Task LoadMessages_ExcludesDeleted()
    {
        var (svc, factory, _) = CreateService();
        var chat = SeedChat(factory);

        using var db = factory.CreateDbContext();
        db.Messages.Add(new MessageEntity
        {
            Guid = "msg-alive", ChatId = chat.Id, Text = "alive", DateCreated = 1000
        });
        db.Messages.Add(new MessageEntity
        {
            Guid = "msg-deleted", ChatId = chat.Id, Text = "deleted",
            DateCreated = 2000, DateDeleted = 3000
        });
        db.SaveChanges();

        var messages = await svc.LoadMessagesAsync(chat.Id);

        Assert.Single(messages);
        Assert.Equal("msg-alive", messages[0].Guid);
    }

    [Fact]
    public async Task LoadMessages_ExcludesReactions()
    {
        var (svc, factory, _) = CreateService();
        var chat = SeedChat(factory);

        using var db = factory.CreateDbContext();
        db.Messages.Add(new MessageEntity
        {
            Guid = "msg-normal", ChatId = chat.Id, Text = "hello", DateCreated = 1000
        });
        db.Messages.Add(new MessageEntity
        {
            Guid = "msg-reaction", ChatId = chat.Id, Text = "Loved",
            DateCreated = 2000, AssociatedMessageGuid = "msg-normal"
        });
        db.SaveChanges();

        var messages = await svc.LoadMessagesAsync(chat.Id);

        Assert.Single(messages);
        Assert.Equal("msg-normal", messages[0].Guid);
    }

    [Fact]
    public async Task LoadMessages_IncludesHandle()
    {
        var (svc, factory, _) = CreateService();
        var chat = SeedChat(factory);

        using var db = factory.CreateDbContext();
        var handle = new HandleEntity { Address = "+11234567890", Service = "iMessage" };
        db.Handles.Add(handle);
        db.SaveChanges();

        db.Messages.Add(new MessageEntity
        {
            Guid = "msg-with-handle", ChatId = chat.Id, Text = "hi",
            DateCreated = 1000, HandleId = handle.Id
        });
        db.SaveChanges();

        var messages = await svc.LoadMessagesAsync(chat.Id);

        Assert.Single(messages);
        Assert.NotNull(messages[0].Handle);
        Assert.Equal("+11234567890", messages[0].Handle!.Address);
    }

    [Fact]
    public async Task LoadMessages_OnlyReturnsChatMessages()
    {
        var (svc, factory, _) = CreateService();
        var chat1 = SeedChat(factory, "chat-1");
        var chat2 = SeedChat(factory, "chat-2");

        using var db = factory.CreateDbContext();
        db.Messages.Add(new MessageEntity
        {
            Guid = "msg-chat1", ChatId = chat1.Id, Text = "in chat 1", DateCreated = 1000
        });
        db.Messages.Add(new MessageEntity
        {
            Guid = "msg-chat2", ChatId = chat2.Id, Text = "in chat 2", DateCreated = 2000
        });
        db.SaveChanges();

        var messages = await svc.LoadMessagesAsync(chat1.Id);

        Assert.Single(messages);
        Assert.Equal("msg-chat1", messages[0].Guid);
    }

    [Fact]
    public async Task LoadMessages_EmptyChat_ReturnsEmptyList()
    {
        var (svc, factory, _) = CreateService();
        var chat = SeedChat(factory);

        var messages = await svc.LoadMessagesAsync(chat.Id);

        Assert.Empty(messages);
    }

    // ── SaveIncomingMessageAsync ──

    private static Message MakeMessage(string guid, string? text = null,
        Handle? handle = null, long? dateCreated = null) =>
        new(null, guid, null, null, text, null, null, 0,
            dateCreated ?? 1700000000000, null, null,
            false, false, false, null, 0, null, 0, null, null, null, null, null,
            handle, false, false, null, null, null, null, null, null, null, null,
            null, false, null, false, false, false);

    [Fact]
    public async Task SaveIncoming_DuplicateGuid_IsNoOp()
    {
        var (svc, factory, _) = CreateService();
        var chat = SeedChat(factory);

        using (var db = factory.CreateDbContext())
        {
            db.Messages.Add(new MessageEntity
            {
                Guid = "msg-dup", ChatId = chat.Id, Text = "existing", DateCreated = 1000
            });
            db.SaveChanges();
        }

        await svc.SaveIncomingMessageAsync(chat.Guid, MakeMessage("msg-dup", "new text"));

        using (var db = factory.CreateDbContext())
        {
            var msgs = db.Messages.Where(m => m.Guid == "msg-dup").ToList();
            Assert.Single(msgs);
            Assert.Equal("existing", msgs[0].Text);
        }
    }

    [Fact]
    public async Task SaveIncoming_NewHandle_CreatesHandleEntity()
    {
        var (svc, factory, _) = CreateService();
        var chat = SeedChat(factory);

        var handle = new Handle(0, "+15551234567", "iMessage", "US", null, null, null, null, null);
        await svc.SaveIncomingMessageAsync(chat.Guid, MakeMessage("msg-h1", "hello", handle));

        using var db = factory.CreateDbContext();
        var savedHandle = db.Handles.FirstOrDefault(h => h.Address == "+15551234567");
        Assert.NotNull(savedHandle);
        Assert.Equal("iMessage", savedHandle.Service);

        var savedMsg = db.Messages.First(m => m.Guid == "msg-h1");
        Assert.Equal(savedHandle.Id, savedMsg.HandleId);
    }

    [Fact]
    public async Task SaveIncoming_ExistingHandle_ReusesIt()
    {
        var (svc, factory, _) = CreateService();
        var chat = SeedChat(factory);

        using (var db = factory.CreateDbContext())
        {
            db.Handles.Add(new HandleEntity { Address = "+15551234567", Service = "iMessage" });
            db.SaveChanges();
        }

        var handle = new Handle(0, "+15551234567", "iMessage", null, null, null, null, null, null);
        await svc.SaveIncomingMessageAsync(chat.Guid, MakeMessage("msg-reuse", "hello", handle));

        using var db2 = factory.CreateDbContext();
        Assert.Equal(1, db2.Handles.Count());
        var savedMsg = db2.Messages.First(m => m.Guid == "msg-reuse");
        Assert.NotNull(savedMsg.HandleId);
    }

    [Fact]
    public async Task SaveIncoming_UnknownChat_IsNoOp()
    {
        var (svc, factory, _) = CreateService();

        await svc.SaveIncomingMessageAsync("nonexistent-chat", MakeMessage("msg-orphan", "hello"));

        using var db = factory.CreateDbContext();
        Assert.Empty(db.Messages);
    }

    [Fact]
    public async Task SaveIncoming_DoesNotUpdateLatestMessageDateOrHasUnread()
    {
        var (svc, factory, _) = CreateService();

        using (var db = factory.CreateDbContext())
        {
            db.Chats.Add(new ChatEntity
            {
                Guid = "chat-noupdate", LatestMessageDate = 1000, HasUnreadMessage = false
            });
            db.SaveChanges();
        }

        await svc.SaveIncomingMessageAsync("chat-noupdate",
            MakeMessage("msg-noupdate", "hello", dateCreated: 9999));

        using (var db = factory.CreateDbContext())
        {
            var chat = db.Chats.First(c => c.Guid == "chat-noupdate");
            Assert.Equal(1000, chat.LatestMessageDate);
            Assert.False(chat.HasUnreadMessage);
        }
    }

    [Fact]
    public async Task SaveIncoming_ConcurrentSaves_AllSerialized()
    {
        var (svc, factory, _) = CreateService();
        var chat = SeedChat(factory);

        var tasks = Enumerable.Range(0, 10)
            .Select(i => svc.SaveIncomingMessageAsync(
                chat.Guid, MakeMessage($"msg-concurrent-{i}", $"text {i}")))
            .ToArray();

        await Task.WhenAll(tasks);

        using var db = factory.CreateDbContext();
        Assert.Equal(10, db.Messages.Count(m => m.ChatId == chat.Id));
    }

    // ── FetchOlderMessagesFromServerAsync ──

    private static Handle MakeHandle(string address, string service = "iMessage") =>
        new(0, address, service, null, null, null, null, null, null);

    [Fact]
    public async Task FetchOlder_NullWatermark_ReturnsEmpty()
    {
        var (svc, factory, api) = CreateService();
        var chat = SeedChat(factory);
        // OldestSyncedMessageDate is null by default

        var result = await svc.FetchOlderMessagesFromServerAsync(chat.Id, chat.Guid);

        Assert.Empty(result);
        Assert.Empty(api.ChatMessageCalls);
    }

    [Fact]
    public async Task FetchOlder_ExhaustedWatermark_ReturnsEmpty()
    {
        var (svc, factory, api) = CreateService();
        using (var db = factory.CreateDbContext())
        {
            var chat = new ChatEntity { Guid = "chat-exhausted", OldestSyncedMessageDate = 0 };
            db.Chats.Add(chat);
            db.SaveChanges();
        }

        using (var db = factory.CreateDbContext())
        {
            var chat = db.Chats.First(c => c.Guid == "chat-exhausted");
            var result = await svc.FetchOlderMessagesFromServerAsync(chat.Id, chat.Guid);
            Assert.Empty(result);
        }

        Assert.Empty(api.ChatMessageCalls);
    }

    [Fact]
    public async Task FetchOlder_PastOneYear_ReturnsEmpty()
    {
        var twoYearsAgo = DateTimeOffset.UtcNow.AddDays(-730).ToUnixTimeMilliseconds();
        var (svc, factory, api) = CreateService();
        using (var db = factory.CreateDbContext())
        {
            db.Chats.Add(new ChatEntity
            {
                Guid = "chat-old",
                OldestSyncedMessageDate = twoYearsAgo
            });
            db.SaveChanges();
        }

        using (var db = factory.CreateDbContext())
        {
            var chat = db.Chats.First(c => c.Guid == "chat-old");
            var result = await svc.FetchOlderMessagesFromServerAsync(chat.Id, chat.Guid);
            Assert.Empty(result);
        }

        Assert.Empty(api.ChatMessageCalls);
    }

    [Fact]
    public async Task FetchOlder_ServerReturnsMessages_SavesAndUpdatesWatermark()
    {
        var handle = MakeHandle("+15551234567");
        var oldestSynced = DateTimeOffset.UtcNow.AddDays(-5).ToUnixTimeMilliseconds();
        var olderMsgDate = oldestSynced - 60000;

        var chatMessages = new Dictionary<string, List<Message>>
        {
            ["chat-fetch"] = [MakeMessage("server-msg-1", "older msg", handle, olderMsgDate)]
        };
        var api = new SyncMockApiService([], chatMessages);
        var (svc, factory, _) = CreateService(api);

        using (var db = factory.CreateDbContext())
        {
            db.Chats.Add(new ChatEntity
            {
                Guid = "chat-fetch",
                OldestSyncedMessageDate = oldestSynced
            });
            db.SaveChanges();
        }

        List<MessageEntity> result;
        using (var db = factory.CreateDbContext())
        {
            var chat = db.Chats.First(c => c.Guid == "chat-fetch");
            result = await svc.FetchOlderMessagesFromServerAsync(chat.Id, chat.Guid);
        }

        Assert.Single(result);

        using (var db = factory.CreateDbContext())
        {
            Assert.Equal(1, db.Messages.Count());
            var chat = db.Chats.First(c => c.Guid == "chat-fetch");
            Assert.Equal(olderMsgDate, chat.OldestSyncedMessageDate);
        }

        var call = Assert.Single(api.ChatMessageCalls);
        Assert.Equal(oldestSynced, call.Before);
    }

    // ── EnsureChatHydratedAsync (on-open backfill safety net) ──

    [Fact]
    public async Task EnsureHydrated_EmptyChat_FetchesAndSaves()
    {
        var handle = MakeHandle("+15551234567");
        var msgDate = DateTimeOffset.UtcNow.AddDays(-3).ToUnixTimeMilliseconds();
        var chatMessages = new Dictionary<string, List<Message>>
        {
            ["chat-hydrate"] = [
                MakeMessage("h-1", "hi", handle, msgDate),
                MakeMessage("h-2", "there", handle, msgDate + 1000)
            ]
        };
        var api = new SyncMockApiService([], chatMessages);
        var (svc, factory, _) = CreateService(api);
        var chat = SeedChat(factory, "chat-hydrate");

        var hydrated = await svc.EnsureChatHydratedAsync(chat.Id, chat.Guid);

        Assert.True(hydrated);
        using var db = factory.CreateDbContext();
        Assert.Equal(2, db.Messages.Count(m => m.ChatId == chat.Id));
        Assert.Equal(msgDate, db.Chats.First(c => c.Id == chat.Id).OldestSyncedMessageDate);
    }

    [Fact]
    public async Task EnsureHydrated_ChatWithMessages_IsNoOp()
    {
        var api = new SyncMockApiService([]);
        var (svc, factory, _) = CreateService(api);
        var chat = SeedChat(factory, "chat-has-msgs");
        SeedMessages(factory, chat.Id, 3);

        var hydrated = await svc.EnsureChatHydratedAsync(chat.Id, chat.Guid);

        Assert.False(hydrated);
        Assert.Empty(api.ChatMessageCalls);   // never hit the server
    }

    [Fact]
    public async Task EnsureHydrated_ServerEmpty_ReturnsFalse()
    {
        var api = new SyncMockApiService([]);
        var (svc, factory, _) = CreateService(api);
        var chat = SeedChat(factory, "chat-still-empty");

        var hydrated = await svc.EnsureChatHydratedAsync(chat.Id, chat.Guid);

        Assert.False(hydrated);
        using var db = factory.CreateDbContext();
        Assert.Empty(db.Messages);
    }

    [Fact]
    public async Task FetchOlder_ServerReturnsEmpty_SetsExhaustedSentinel()
    {
        var oldestSynced = DateTimeOffset.UtcNow.AddDays(-5).ToUnixTimeMilliseconds();

        var api = new SyncMockApiService([]);
        var (svc, factory, _) = CreateService(api);

        using (var db = factory.CreateDbContext())
        {
            db.Chats.Add(new ChatEntity
            {
                Guid = "chat-empty-server",
                OldestSyncedMessageDate = oldestSynced
            });
            db.SaveChanges();
        }

        using (var db = factory.CreateDbContext())
        {
            var chat = db.Chats.First(c => c.Guid == "chat-empty-server");
            var result = await svc.FetchOlderMessagesFromServerAsync(chat.Id, chat.Guid);
            Assert.Empty(result);
        }

        using (var db = factory.CreateDbContext())
        {
            var chat = db.Chats.First(c => c.Guid == "chat-empty-server");
            Assert.Equal(0, chat.OldestSyncedMessageDate);
        }
    }
}
