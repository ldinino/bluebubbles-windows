using System.Text.Json;
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
    public async Task LoadChatsAsync_AttachmentOnlyLastMessage_DerivesPreview()
    {
        var (svc, factory) = CreateService();
        // Attachment-only: text is just the U+FFFC placeholder iMessage leaves for attachments.
        await SeedChat(factory, "chat1", latestDate: 1000, lastMessageText: "￼");
        using (var db = factory.CreateDbContext())
        {
            var msg = db.Messages.First(m => m.Guid == "msg-chat1");
            db.Attachments.Add(new AttachmentEntity
            {
                Guid = "att-1",
                MessageId = msg.Id,
                MimeType = "image/jpeg"
            });
            await db.SaveChangesAsync();
        }

        await svc.LoadChatsAsync();

        Assert.Equal("Image", svc.Chats[0].LastMessageText);
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
    public async Task DeleteChatAsync_CallsServer_ThenRemovesFromDbAndList()
    {
        var (svc, factory, api) = CreateServiceWithApi();
        string? deletedGuid = null;
        api.DeleteChatFunc = guid =>
        {
            deletedGuid = guid;
            return Task.FromResult(new ApiResponse<JsonElement>(200, "OK", default, null));
        };
        await SeedChat(factory, "chat1", latestDate: 1000);
        await svc.LoadChatsAsync();

        Assert.True(await svc.DeleteChatAsync("chat1"));

        Assert.Equal("chat1", deletedGuid);
        Assert.Empty(svc.Chats);

        using var db = factory.CreateDbContext();
        Assert.Empty(db.Chats);
    }

    [Fact]
    public async Task DeleteChatAsync_ServerError_LeavesLocalStateUntouched()
    {
        // A local-only delete would just be re-pulled by the next sync, so a failed server call
        // must leave the cache alone and report failure.
        var (svc, factory, api) = CreateServiceWithApi();
        api.DeleteChatFunc = _ => Task.FromResult(new ApiResponse<JsonElement>(500, "error", default, null));
        await SeedChat(factory, "chat1", latestDate: 1000);
        await svc.LoadChatsAsync();

        Assert.False(await svc.DeleteChatAsync("chat1"));

        Assert.Single(svc.Chats);
        using var db = factory.CreateDbContext();
        Assert.Single(db.Chats);
    }

    [Fact]
    public async Task DeleteChatAsync_ServerUnreachable_LeavesLocalStateUntouched()
    {
        var (svc, factory, api) = CreateServiceWithApi();
        api.DeleteChatFunc = _ => throw new HttpRequestException("offline");
        await SeedChat(factory, "chat1", latestDate: 1000);
        await svc.LoadChatsAsync();

        Assert.False(await svc.DeleteChatAsync("chat1"));

        Assert.Single(svc.Chats);
        using var db = factory.CreateDbContext();
        Assert.Single(db.Chats);
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

    [Fact]
    public async Task EnsureChatExistsAsync_NewChat_AppearsInInMemoryList()
    {
        // The conversation list renders the in-memory list, so a chat that exists only in the DB is
        // invisible until something else forces a reload.
        var (svc, _, api) = CreateServiceWithApi();
        api.GetChatFunc = guid => Task.FromResult(new ApiResponse<Chat>(200, "OK",
            MockChat(guid, [MockHandle("+15551234567")]), null));

        await svc.EnsureChatExistsAsync(MockChat("SMS;+;chat-brand-new", participants: null));

        Assert.Contains(svc.Chats, c => c.Chat.Guid == "SMS;+;chat-brand-new");
    }

    [Fact]
    public async Task HandleNewMessageAsync_UnknownChat_StillRaisesChatsChanged()
    {
        // Losing the race with the chat's creation must not leave the list un-notified — that is the
        // "message saved but nothing on screen changed" failure.
        var (svc, factory) = CreateService();
        await svc.LoadChatsAsync();

        var raised = 0;
        svc.ChatsChanged += (_, _) => raised++;

        // The row lands after the in-memory list was loaded, mimicking a concurrent writer.
        await SeedChat(factory, "chat-late", latestDate: 1000);
        await svc.HandleNewMessageAsync("chat-missing", "orphan", 2000, false);

        Assert.True(raised > 0, "ChatsChanged was not raised for a message on an unknown chat");
        Assert.Contains(svc.Chats, c => c.Chat.Guid == "chat-late");
    }

    // ---- ApplyChatUpdateAsync (group-name-change / participant-* socket events) ----

    private static Chat UpdatedChat(string guid, string? displayName, List<Handle>? participants) =>
        new(guid, guid, displayName, participants, null, false, false, false, "iMessage",
            null, null, null, null, null, 43, false, false, null);

    private static async Task SeedGroupChat(
        TestDbContextFactory factory, string guid, string? displayName, params string[] participants)
    {
        using var db = factory.CreateDbContext();
        var chat = new ChatEntity { Guid = guid, DisplayName = displayName, LatestMessageDate = 1000 };
        db.Chats.Add(chat);
        await db.SaveChangesAsync();

        foreach (var address in participants)
        {
            var handle = new HandleEntity { Address = address, Service = "iMessage" };
            db.Handles.Add(handle);
            await db.SaveChangesAsync();
            db.ChatParticipants.Add(new ChatParticipant { ChatId = chat.Id, HandleId = handle.Id });
        }
        await db.SaveChangesAsync();
    }

    private static List<string> ParticipantAddresses(TestDbContextFactory factory, string guid)
    {
        using var db = factory.CreateDbContext();
        var chat = db.Chats.Single(c => c.Guid == guid);
        return db.ChatParticipants
            .Where(cp => cp.ChatId == chat.Id)
            .Select(cp => cp.Handle.Address)
            .OrderBy(a => a)
            .ToList();
    }

    [Fact]
    public async Task ApplyChatUpdateAsync_PayloadWithoutUnreadFlag_DoesNotClearTheUnreadBadge()
    {
        // A rename payload is deserialized straight off the wire. `hasUnreadMessage` is a
        // non-nullable bool, so a payload that omits it yields false -- and ApplyServerOwnedFields
        // copies it. If that lands, renaming a group silently marks it read.
        var (svc, factory) = CreateService();
        await SeedGroupChat(factory, "iMessage;+;chat-unread", "Old Name", "+15551112222");
        using (var seed = factory.CreateDbContext())
        {
            seed.Chats.Single(c => c.Guid == "iMessage;+;chat-unread").HasUnreadMessage = true;
            seed.SaveChanges();
        }

        var fromWire = System.Text.Json.JsonSerializer.Deserialize<Chat>("""
            { "guid": "iMessage;+;chat-unread", "displayName": "New Name", "service": "iMessage" }
            """, BlueBubbles.Core.Utils.JsonDefaults.Options)!;

        await svc.ApplyChatUpdateAsync(fromWire);

        using var db = factory.CreateDbContext();
        var chat = db.Chats.Single(c => c.Guid == "iMessage;+;chat-unread");
        Assert.Equal("New Name", chat.DisplayName);
        Assert.True(chat.HasUnreadMessage, "renaming a group cleared its unread badge");
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public async Task ApplyChatUpdateAsync_PayloadWithUnreadFlag_AppliesIt(string wireValue, bool expected)
    {
        // The guard above must not become "ignore the field": read state IS server-owned when the
        // payload states it, so a mark-read on another device still has to land here.
        var (svc, factory) = CreateService();
        await SeedGroupChat(factory, "iMessage;+;chat-read-state", "Group", "+15551112222");
        using (var seed = factory.CreateDbContext())
        {
            seed.Chats.Single(c => c.Guid == "iMessage;+;chat-read-state").HasUnreadMessage = !expected;
            seed.SaveChanges();
        }

        var fromWire = System.Text.Json.JsonSerializer.Deserialize<Chat>($$"""
            { "guid": "iMessage;+;chat-read-state", "hasUnreadMessage": {{wireValue}} }
            """, BlueBubbles.Core.Utils.JsonDefaults.Options)!;

        await svc.ApplyChatUpdateAsync(fromWire);

        using var db = factory.CreateDbContext();
        Assert.Equal(expected,
            db.Chats.Single(c => c.Guid == "iMessage;+;chat-read-state").HasUnreadMessage);
    }

    [Fact]
    public async Task ApplyChatUpdateAsync_GroupNameChange_PersistsNewDisplayName()
    {
        // The socket event is the only notice of a rename made on another device; without a write the
        // conversation list (which renders from the DB) keeps the old name.
        var (svc, factory) = CreateService();
        await SeedGroupChat(factory, "iMessage;+;chat-group", "Old Name", "+15551112222");

        await svc.ApplyChatUpdateAsync(UpdatedChat("iMessage;+;chat-group", "New Name", null));

        using var db = factory.CreateDbContext();
        Assert.Equal("New Name", db.Chats.Single(c => c.Guid == "iMessage;+;chat-group").DisplayName);
        Assert.Equal("New Name", svc.Chats.Single().Chat.DisplayName);
    }

    [Fact]
    public async Task ApplyChatUpdateAsync_DoesNotClobberClientOnlyFields()
    {
        // The server has no endpoint for pin/mute/archive, so its payload always carries the defaults.
        // Copying them wipes the user's state — the exact regression ChatFieldMerge exists to prevent.
        var (svc, factory) = CreateService();
        using (var db = factory.CreateDbContext())
        {
            db.Chats.Add(new ChatEntity
            {
                Guid = "iMessage;+;chat-pinned",
                DisplayName = "Old Name",
                IsPinned = true,
                PinIndex = 3,
                IsArchived = true,
                MuteType = "mute",
                MuteArgs = "args",
                CustomAvatarPath = "avatar.png",
                OldestSyncedMessageDate = 12345
            });
            await db.SaveChangesAsync();
        }

        await svc.ApplyChatUpdateAsync(UpdatedChat("iMessage;+;chat-pinned", "New Name", null));

        using var db2 = factory.CreateDbContext();
        var chat = db2.Chats.Single(c => c.Guid == "iMessage;+;chat-pinned");
        Assert.Equal("New Name", chat.DisplayName);
        Assert.True(chat.IsPinned);
        Assert.Equal(3, chat.PinIndex);
        Assert.True(chat.IsArchived);
        Assert.Equal("mute", chat.MuteType);
        Assert.Equal("args", chat.MuteArgs);
        Assert.Equal("avatar.png", chat.CustomAvatarPath);
        Assert.Equal(12345, chat.OldestSyncedMessageDate);
    }

    [Fact]
    public async Task ApplyChatUpdateAsync_ParticipantAdded_LinksTheNewHandle()
    {
        var (svc, factory) = CreateService();
        await SeedGroupChat(factory, "iMessage;+;chat-add", "Group", "+15551112222");

        await svc.ApplyChatUpdateAsync(UpdatedChat("iMessage;+;chat-add", "Group",
            [MockHandle("+15551112222", "iMessage"), MockHandle("+15553334444", "iMessage")]));

        Assert.Equal(["+15551112222", "+15553334444"],
            ParticipantAddresses(factory, "iMessage;+;chat-add"));
    }

    [Fact]
    public async Task ApplyChatUpdateAsync_ParticipantRemoved_DropsTheJoinRow()
    {
        // The payload's participant list is the whole membership, so an omitted handle has left.
        // LinkParticipantsAsync only adds, so removal has to be applied explicitly.
        var (svc, factory) = CreateService();
        await SeedGroupChat(factory, "iMessage;+;chat-remove", "Group", "+15551112222", "+15553334444");

        await svc.ApplyChatUpdateAsync(UpdatedChat("iMessage;+;chat-remove", "Group",
            [MockHandle("+15551112222", "iMessage")]));

        Assert.Equal(["+15551112222"], ParticipantAddresses(factory, "iMessage;+;chat-remove"));
        Assert.Single(svc.Chats.Single().Participants);
    }

    [Fact]
    public async Task ApplyChatUpdateAsync_PayloadWithoutParticipants_KeepsExistingOnes()
    {
        // A payload that omits participants entirely says nothing about membership; treating it as
        // "everyone left" would empty the chat and render it as Unknown.
        var (svc, factory) = CreateService();
        await SeedGroupChat(factory, "iMessage;+;chat-keep", "Group", "+15551112222", "+15553334444");

        await svc.ApplyChatUpdateAsync(UpdatedChat("iMessage;+;chat-keep", "Renamed", null));

        Assert.Equal(["+15551112222", "+15553334444"],
            ParticipantAddresses(factory, "iMessage;+;chat-keep"));
    }

    [Fact]
    public async Task ApplyChatUpdateAsync_UnknownChat_DoesNotCreateIt()
    {
        // Update-only: creating chats from this payload is EnsureChatExistsAsync's job (off new-message).
        var (svc, factory) = CreateService();

        await svc.ApplyChatUpdateAsync(UpdatedChat("iMessage;+;chat-absent", "Ghost", null));

        using var db = factory.CreateDbContext();
        Assert.Empty(db.Chats.Where(c => c.Guid == "iMessage;+;chat-absent"));
    }
}
