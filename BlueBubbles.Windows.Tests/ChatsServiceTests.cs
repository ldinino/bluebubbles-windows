using BlueBubbles.Core.Configuration;
using BlueBubbles.Core.Data.Entities;
using BlueBubbles.Core.Models;
using BlueBubbles.Core.Services;

namespace BlueBubbles.Windows.Tests;

public class ChatsServiceTests
{
    private static (ChatsService svc, TestDbContextFactory factory) CreateService()
    {
        var factory = TestDbContextFactory.Create();
        var api = new MockApiService();
        var settings = new AppSettings();
        return (new ChatsService(factory, api, settings), factory);
    }

    private static (ChatsService svc, TestDbContextFactory factory, MockApiService api) CreateServiceWithApi()
    {
        var factory = TestDbContextFactory.Create();
        var api = new MockApiService();
        return (new ChatsService(factory, api, new AppSettings()), factory, api);
    }

    private static Handle MockHandle(string address, string service = "SMS") =>
        new(0, address, service, null, null, null, null, null, null);

    private static Chat MockChat(string guid, List<Handle>? participants) =>
        new(guid, guid, null, participants, null, false, false, false, "SMS",
            null, null, null, null, null, 43, false, false, null);

    private static async Task SeedChat(TestDbContextFactory factory, string guid, long? latestDate = null,
        bool isPinned = false, int? pinIndex = null, bool hasUnread = false, string? lastMessageText = null)
    {
        using var db = factory.CreateDbContext();
        var chat = new ChatEntity
        {
            Guid = guid,
            LatestMessageDate = latestDate,
            IsPinned = isPinned,
            PinIndex = pinIndex,
            HasUnreadMessage = hasUnread
        };
        db.Chats.Add(chat);
        await db.SaveChangesAsync();

        if (lastMessageText is not null)
        {
            db.Messages.Add(new MessageEntity
            {
                Guid = $"msg-{guid}",
                ChatId = chat.Id,
                Text = lastMessageText,
                DateCreated = latestDate
            });
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task LoadChatsAsync_ReturnsSortedChats()
    {
        var (svc, factory) = CreateService();
        await SeedChat(factory, "chat-old", latestDate: 1000, lastMessageText: "old");
        await SeedChat(factory, "chat-new", latestDate: 2000, lastMessageText: "new");

        await svc.LoadChatsAsync();

        Assert.Equal(2, svc.Chats.Count);
        Assert.Equal("chat-new", svc.Chats[0].Chat.Guid);
        Assert.Equal("chat-old", svc.Chats[1].Chat.Guid);
    }

    [Fact]
    public async Task LoadChatsAsync_PinnedChatsFirst()
    {
        var (svc, factory) = CreateService();
        await SeedChat(factory, "chat-regular", latestDate: 3000);
        await SeedChat(factory, "chat-pinned", latestDate: 1000, isPinned: true, pinIndex: 0);

        await svc.LoadChatsAsync();

        Assert.Equal("chat-pinned", svc.Chats[0].Chat.Guid);
        Assert.Equal("chat-regular", svc.Chats[1].Chat.Guid);
    }

    [Fact]
    public async Task LoadChatsAsync_ExcludesArchivedAndDeleted()
    {
        var (svc, factory) = CreateService();
        await SeedChat(factory, "chat-normal", latestDate: 1000);

        using (var db = factory.CreateDbContext())
        {
            db.Chats.Add(new ChatEntity { Guid = "chat-archived", IsArchived = true });
            db.Chats.Add(new ChatEntity { Guid = "chat-deleted", DateDeleted = 12345 });
            await db.SaveChangesAsync();
        }

        await svc.LoadChatsAsync();

        Assert.Single(svc.Chats);
        Assert.Equal("chat-normal", svc.Chats[0].Chat.Guid);
    }

    [Fact]
    public async Task LoadChatsAsync_IncludesLastMessageText()
    {
        var (svc, factory) = CreateService();
        await SeedChat(factory, "chat1", latestDate: 1000, lastMessageText: "Hello!");

        await svc.LoadChatsAsync();

        Assert.Equal("Hello!", svc.Chats[0].LastMessageText);
    }

    [Fact]
    public async Task HandleNewMessageAsync_ReordersList()
    {
        var (svc, factory) = CreateService();
        await SeedChat(factory, "chat-a", latestDate: 1000);
        await SeedChat(factory, "chat-b", latestDate: 2000);
        await svc.LoadChatsAsync();

        Assert.Equal("chat-b", svc.Chats[0].Chat.Guid);

        await svc.HandleNewMessageAsync("chat-a", "new msg", 3000, false);

        Assert.Equal("chat-a", svc.Chats[0].Chat.Guid);
        Assert.Equal("new msg", svc.Chats[0].LastMessageText);
        Assert.True(svc.Chats[0].Chat.HasUnreadMessage);
    }

    [Fact]
    public async Task HandleNewMessageAsync_FromMe_DoesNotMarkUnread()
    {
        var (svc, factory) = CreateService();
        await SeedChat(factory, "chat1", latestDate: 1000);
        await svc.LoadChatsAsync();

        await svc.HandleNewMessageAsync("chat1", "my msg", 2000, true);

        Assert.False(svc.Chats[0].Chat.HasUnreadMessage);
    }

    [Fact]
    public async Task HandleNewMessageAsync_ResurrectsSoftDeletedChat()
    {
        var (svc, factory) = CreateService();
        using (var db = factory.CreateDbContext())
        {
            db.Chats.Add(new ChatEntity { Guid = "chat-zombie", DateDeleted = 12345, LatestMessageDate = 1000 });
            await db.SaveChangesAsync();
        }
        await svc.LoadChatsAsync();
        Assert.Empty(svc.Chats);   // soft-deleted, hidden

        await svc.HandleNewMessageAsync("chat-zombie", "back from the dead", 5000, false);

        Assert.Single(svc.Chats);
        Assert.Equal("chat-zombie", svc.Chats[0].Chat.Guid);
        using var db2 = factory.CreateDbContext();
        Assert.Null(db2.Chats.Single(c => c.Guid == "chat-zombie").DateDeleted);
    }

    [Fact]
    public async Task MarkChatReadAsync_ClearsUnread()
    {
        var (svc, factory) = CreateService();
        await SeedChat(factory, "chat1", latestDate: 1000, hasUnread: true);
        await svc.LoadChatsAsync();

        Assert.True(svc.Chats[0].Chat.HasUnreadMessage);

        await svc.MarkChatReadAsync("chat1", true);

        Assert.False(svc.Chats[0].Chat.HasUnreadMessage);
    }

    [Fact]
    public async Task TogglePinAsync_PinsAndUnpins()
    {
        var (svc, factory) = CreateService();
        await SeedChat(factory, "chat1", latestDate: 1000);
        await svc.LoadChatsAsync();

        Assert.False(svc.Chats[0].Chat.IsPinned);

        await svc.TogglePinAsync("chat1");
        Assert.True(svc.Chats[0].Chat.IsPinned);

        await svc.TogglePinAsync("chat1");
        Assert.False(svc.Chats[0].Chat.IsPinned);
    }

    [Fact]
    public async Task LoadChatsAsync_PinnedOrderedByPinIndex_NotMessageDate()
    {
        // The pin with the OLDER message (pinB) has the lower PinIndex, so it must come first.
        // Regression: pins used to be ordered by message date, silently dropping the manual order.
        var (svc, factory) = CreateService();
        await SeedChat(factory, "pinA", latestDate: 5000, isPinned: true, pinIndex: 1);
        await SeedChat(factory, "pinB", latestDate: 1000, isPinned: true, pinIndex: 0);

        await svc.LoadChatsAsync();

        Assert.Equal("pinB", svc.Chats[0].Chat.Guid);
        Assert.Equal("pinA", svc.Chats[1].Chat.Guid);
    }

    [Fact]
    public async Task ReorderPinsAsync_UpdatesInMemoryOrder_AndPersists()
    {
        var (svc, factory) = CreateService();
        await SeedChat(factory, "pinA", latestDate: 5000, isPinned: true, pinIndex: 0);
        await SeedChat(factory, "pinB", latestDate: 1000, isPinned: true, pinIndex: 1);
        await svc.LoadChatsAsync();

        await svc.ReorderPinsAsync(["pinB", "pinA"]);

        // In-memory cache reflects the new order without a reload...
        Assert.Equal("pinB", svc.Chats[0].Chat.Guid);
        Assert.Equal("pinA", svc.Chats[1].Chat.Guid);

        // ...and the order survives a fresh load from the DB.
        await svc.LoadChatsAsync();
        Assert.Equal("pinB", svc.Chats[0].Chat.Guid);
        Assert.Equal("pinA", svc.Chats[1].Chat.Guid);
    }

    [Fact]
    public async Task ArchiveChatAsync_RemovesFromList()
    {
        var (svc, factory) = CreateService();
        await SeedChat(factory, "chat1", latestDate: 1000);
        await svc.LoadChatsAsync();

        Assert.Single(svc.Chats);

        await svc.ArchiveChatAsync("chat1");

        Assert.Empty(svc.Chats);
    }

    [Fact]
    public async Task DeleteChatAsync_RemovesFromDbAndList()
    {
        var (svc, factory) = CreateService();
        await SeedChat(factory, "chat1", latestDate: 1000);
        await svc.LoadChatsAsync();

        await svc.DeleteChatAsync("chat1");

        Assert.Empty(svc.Chats);

        using var db = factory.CreateDbContext();
        Assert.Empty(db.Chats);
    }

    [Fact]
    public async Task ChatsChanged_FiresOnLoad()
    {
        var (svc, factory) = CreateService();
        await SeedChat(factory, "chat1", latestDate: 1000);

        var fired = false;
        svc.ChatsChanged += (_, _) => fired = true;

        await svc.LoadChatsAsync();

        Assert.True(fired);
    }

    [Fact]
    public async Task LoadChatsAsync_IncludesParticipants()
    {
        var factory = TestDbContextFactory.Create();
        using (var db = factory.CreateDbContext())
        {
            var handle = new HandleEntity { Address = "test@example.com", Service = "iMessage" };
            db.Handles.Add(handle);
            var chat = new ChatEntity { Guid = "chat-with-handle", LatestMessageDate = 1000 };
            db.Chats.Add(chat);
            await db.SaveChangesAsync();

            db.ChatParticipants.Add(new ChatParticipant { ChatId = chat.Id, HandleId = handle.Id });
            await db.SaveChangesAsync();
        }

        var svc = new ChatsService(factory, new MockApiService(), new AppSettings());
        await svc.LoadChatsAsync();

        Assert.Single(svc.Chats);
        Assert.Single(svc.Chats[0].Participants);
        Assert.Equal("test@example.com", svc.Chats[0].Participants[0].Address);
    }

    [Fact]
    public async Task EnsureChatExistsAsync_SparsePayload_FetchesParticipantsFromServer()
    {
        // The live socket new-message payload carries the chat but no participants, so a chat
        // created from it would render as "Unknown". The service must fetch the full participant
        // list from the server to resolve who's in the chat.
        var (svc, factory, api) = CreateServiceWithApi();
        api.GetChatFunc = guid => Task.FromResult(new ApiResponse<Chat>(200, "OK",
            MockChat(guid, [MockHandle("+15551234567"), MockHandle("+15559876543")]), null));

        await svc.EnsureChatExistsAsync(MockChat("SMS;+;chat-group", participants: null));

        using var db = factory.CreateDbContext();
        var chat = db.Chats.Single(c => c.Guid == "SMS;+;chat-group");
        var linked = db.ChatParticipants.Count(cp => cp.ChatId == chat.Id);
        Assert.Equal(2, linked);
    }

    [Fact]
    public async Task EnsureChatExistsAsync_ServerFetchFails_CreatesChatWithoutCrashing()
    {
        // Offline / server error: the chat must still be created (so it surfaces) and simply land
        // empty — the next incremental sync backfills its participants.
        var (svc, factory, api) = CreateServiceWithApi();
        api.GetChatFunc = _ => throw new HttpRequestException("offline");

        await svc.EnsureChatExistsAsync(MockChat("SMS;+;chat-offline", participants: null));

        using var db = factory.CreateDbContext();
        var chat = db.Chats.Single(c => c.Guid == "SMS;+;chat-offline");
        Assert.Empty(db.ChatParticipants.Where(cp => cp.ChatId == chat.Id));
    }

    [Fact]
    public async Task EnsureChatExistsAsync_ExistingEmptyChat_BackfillsFromServer()
    {
        // A chat previously created empty (sparse payload, server was unreachable) gets its
        // participants backfilled when a later message arrives and the server is reachable.
        var (svc, factory, api) = CreateServiceWithApi();
        using (var db = factory.CreateDbContext())
        {
            db.Chats.Add(new ChatEntity { Guid = "SMS;+;chat-empty" });
            await db.SaveChangesAsync();
        }

        api.GetChatFunc = guid => Task.FromResult(new ApiResponse<Chat>(200, "OK",
            MockChat(guid, [MockHandle("+15551112222")]), null));

        await svc.EnsureChatExistsAsync(MockChat("SMS;+;chat-empty", participants: null));

        using var db2 = factory.CreateDbContext();
        var chat = db2.Chats.Single(c => c.Guid == "SMS;+;chat-empty");
        Assert.Single(db2.ChatParticipants.Where(cp => cp.ChatId == chat.Id));
    }

    [Fact]
    public async Task EnsureChatExistsAsync_PayloadHasParticipants_SkipsServerFetch()
    {
        // When the payload already carries participants (e.g. from an incremental-sync message),
        // no server round-trip is needed. GetChatFunc left null would throw if hit.
        var (svc, factory, api) = CreateServiceWithApi();

        await svc.EnsureChatExistsAsync(
            MockChat("SMS;+;chat-has-parts", [MockHandle("+15553334444")]));

        using var db = factory.CreateDbContext();
        var chat = db.Chats.Single(c => c.Guid == "SMS;+;chat-has-parts");
        Assert.Single(db.ChatParticipants.Where(cp => cp.ChatId == chat.Id));
    }
}
