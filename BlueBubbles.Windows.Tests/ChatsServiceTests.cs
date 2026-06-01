using BlueBubbles.Core.Configuration;
using BlueBubbles.Core.Data.Entities;
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
}
