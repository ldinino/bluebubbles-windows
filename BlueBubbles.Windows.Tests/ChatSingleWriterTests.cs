using BlueBubbles.Core.Configuration;
using BlueBubbles.Core.Data.Entities;
using BlueBubbles.Core.Models;
using BlueBubbles.Core.Services;

namespace BlueBubbles.Windows.Tests;

/// <summary>Guards the single-writer invariant for <see cref="ChatEntity"/>. Four entry points can
/// create a chat row — local chat create, live socket new-message, full sync and the incremental
/// delta — and they must agree on the server-owned field list, on the client-owned fields they
/// must never touch, and on the one client decision an insert is allowed to make (a chat created
/// because a message just arrived is unread and live).
///
/// <para>Unlike <c>HandleEntity</c>, <see cref="ChatEntity"/> has a genuine ownership split, so
/// "one writer" here must not become "one flat field copy": collapsing the insert paths into the
/// merge would drop the unread decision, and widening the merge would wipe pins/mutes on sync.
/// Both directions are asserted below.</para></summary>
public class ChatSingleWriterTests
{
    private const string Address = "+15550004444";
    private const string Service = "iMessage";

    private static Handle MakeHandle(string address = Address) =>
        new(0, address, Service, null, null, null, null, null, null);

    private static Message MakeMessage(string guid, Handle? handle, long dateCreated = 1700000000500) =>
        new(null, guid, null, null, "hi", null, null, 0,
            dateCreated, null, null,
            false, false, false, null, 0, null, 0, null, null, null, null, null,
            handle, false, false, null, null, null, null, null, null, null, null,
            null, false, null, false, false, false);

    /// <summary>Every server-owned field at a non-default value, so a writer that forgets one shows
    /// up as a wrong value rather than a default that happens to match. <c>DateDeleted</c> is left
    /// null here and covered separately — it is the one server field an insert may override.</summary>
    private static Chat FullyPopulated(string guid, bool? hasUnread, List<Handle>? participants) =>
        new(Guid: guid,
            ChatIdentifier: "ident-" + guid,
            DisplayName: "Display " + guid,
            Participants: participants,
            LastMessage: null,
            IsArchived: true,
            IsPinned: true,
            HasUnreadMessage: hasUnread,
            Service: Service,
            MuteType: "mute",
            MuteArgs: "args",
            AutoSendReadReceipts: true,
            AutoSendTypingIndicators: true,
            DateDeleted: null,
            Style: 43,
            LockChatName: true,
            LockChatIcon: true,
            LastReadMessageGuid: "last-read-" + guid,
            CustomAvatarPath: "server-avatar.jpg",
            PinIndex: 7);

    private static ChatsService CreateChats(TestDbContextFactory factory) =>
        new(factory, new MockApiService(), new AppSettings());

    private static SyncService CreateSync(
        TestDbContextFactory factory, SyncMockApiService api, AppSettings? settings = null) =>
        new(api, factory, new MockFirebaseService(), settings ?? new AppSettings(),
            new MockSettingsService(), new MockChatsService());

    /// <summary>Runs the incremental delta with one message whose embedded chat is
    /// <paramref name="chat"/>, which is the writer at the bottom of
    /// <c>SyncService.EnsureChatExistsAsync</c>.</summary>
    private static async Task RunDeltaAsync(TestDbContextFactory factory, Chat chat)
    {
        var batch = new List<Message>
        {
            MakeMessage("delta-msg-" + chat.Guid, MakeHandle()) with
            {
                OriginalRowId = 5,
                Chats = [chat]
            }
        };
        var api = new SyncMockApiService([chat], queryMessages: offset => offset == 0 ? batch : []);
        var svc = CreateSync(factory, api, new AppSettings { LastIncrementalSync = 1700000000000 });
        await svc.RunIncrementalSyncAsync();
    }

    private static void AssertServerFieldsWritten(ChatEntity row, string guid)
    {
        Assert.Equal("ident-" + guid, row.ChatIdentifier);
        Assert.Equal("Display " + guid, row.DisplayName);
        Assert.Equal(Service, row.Service);
        Assert.Equal(43, row.Style);
        Assert.True(row.AutoSendReadReceipts);
        Assert.True(row.AutoSendTypingIndicators);
        Assert.True(row.LockChatName);
        Assert.True(row.LockChatIcon);
        Assert.Equal("last-read-" + guid, row.LastReadMessageGuid);
    }

    private static void AssertClientFieldsUntouched(ChatEntity row)
    {
        Assert.False(row.IsArchived);
        Assert.False(row.IsPinned);
        Assert.Null(row.PinIndex);
        Assert.Null(row.MuteType);
        Assert.Null(row.MuteArgs);
        Assert.Null(row.CustomAvatarPath);
        Assert.Null(row.OldestSyncedMessageDate);
    }

    // ---- the field list is one list, on all four insert paths --------------------------------

    /// <summary>The defect this file was written for: two of the four insert paths hand-wrote a
    /// 5-field subset, so a chat first seen via a live message or a delta landed without its
    /// read-receipt/typing preferences, name/icon locks and last-read marker — until some later
    /// sync happened to overwrite it. That is B2's shape: a writer that doesn't write.</summary>
    [Fact]
    public async Task LocalChatCreate_WritesEveryServerField()
    {
        var factory = TestDbContextFactory.Create();
        await CreateChats(factory).EnsureChatInDatabaseAsync(
            FullyPopulated("chat-local", false, [MakeHandle()]), "hi");

        using var db = factory.CreateDbContext();
        AssertServerFieldsWritten(db.Chats.Single(c => c.Guid == "chat-local"), "chat-local");
    }

    [Fact]
    public async Task LiveMessageChatCreate_WritesEveryServerField()
    {
        var factory = TestDbContextFactory.Create();
        await CreateChats(factory).EnsureChatExistsAsync(
            FullyPopulated("chat-live", null, [MakeHandle()]));

        using var db = factory.CreateDbContext();
        AssertServerFieldsWritten(db.Chats.Single(c => c.Guid == "chat-live"), "chat-live");
    }

    [Fact]
    public async Task FullSyncChatCreate_WritesEveryServerField()
    {
        var factory = TestDbContextFactory.Create();
        var chat = FullyPopulated("chat-sync", false, [MakeHandle()]);
        await CreateSync(factory, new SyncMockApiService([chat]))
            .RunFullSyncAsync(skipEmptyChats: false);

        using var db = factory.CreateDbContext();
        AssertServerFieldsWritten(db.Chats.Single(c => c.Guid == "chat-sync"), "chat-sync");
    }

    [Fact]
    public async Task IncrementalDeltaChatCreate_WritesEveryServerField()
    {
        var factory = TestDbContextFactory.Create();
        await RunDeltaAsync(factory, FullyPopulated("chat-delta", null, [MakeHandle()]));

        using var db = factory.CreateDbContext();
        AssertServerFieldsWritten(db.Chats.Single(c => c.Guid == "chat-delta"), "chat-delta");
    }

    // ---- client-owned fields survive by omission, on all four paths ---------------------------

    /// <summary>The server has no pin/mute/archive endpoint, so it always returns the defaults on
    /// these fields. A writer that copies them wipes the user's state; the payload here asserts
    /// them all set, which a flattened field copy would faithfully — and wrongly — persist.</summary>
    [Fact]
    public async Task EveryInsertPath_LeavesClientOwnedFieldsAtTheirDefaults()
    {
        // A fixture per path: the delta ends in a chat reconcile, which soft-deletes any local chat
        // its own server list omits, so sharing one database would prune the other paths' rows.
        var local = TestDbContextFactory.Create();
        await CreateChats(local).EnsureChatInDatabaseAsync(
            FullyPopulated("chat-local", false, [MakeHandle()]), "hi");

        var live = TestDbContextFactory.Create();
        await CreateChats(live).EnsureChatExistsAsync(FullyPopulated("chat-live", null, [MakeHandle()]));

        var sync = TestDbContextFactory.Create();
        var syncChat = FullyPopulated("chat-sync", false, [MakeHandle()]);
        await CreateSync(sync, new SyncMockApiService([syncChat])).RunFullSyncAsync(skipEmptyChats: false);

        var delta = TestDbContextFactory.Create();
        await RunDeltaAsync(delta, FullyPopulated("chat-delta", null, [MakeHandle()]));

        foreach (var (factory, guid) in new[]
                 {
                     (local, "chat-local"), (live, "chat-live"),
                     (sync, "chat-sync"), (delta, "chat-delta")
                 })
        {
            using var db = factory.CreateDbContext();
            AssertClientFieldsUntouched(db.Chats.Single(c => c.Guid == guid));
        }
    }

    // ---- the insert-time client decision, and the merge guard it must not weaken --------------

    /// <summary>A chat that exists because a message just arrived is unread by client decision, and
    /// the payloads on those two paths routinely omit <c>hasUnreadMessage</c> (the socket serializes
    /// the chat without a read-state field). Routing the insert through the merge alone would leave
    /// the column at its <c>false</c> default and new chats would stop showing as unread.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task LiveMessageChatCreate_IsUnread_EvenWhenThePayloadIsSilent(bool? payloadUnread)
    {
        var factory = TestDbContextFactory.Create();
        await CreateChats(factory).EnsureChatExistsAsync(
            FullyPopulated("chat-live", payloadUnread, [MakeHandle()]));

        using var db = factory.CreateDbContext();
        Assert.True(db.Chats.Single(c => c.Guid == "chat-live").HasUnreadMessage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task IncrementalDeltaChatCreate_IsUnread_EvenWhenThePayloadIsSilent(bool? payloadUnread)
    {
        var factory = TestDbContextFactory.Create();
        await RunDeltaAsync(factory, FullyPopulated("chat-delta", payloadUnread, [MakeHandle()]));

        using var db = factory.CreateDbContext();
        Assert.True(db.Chats.Single(c => c.Guid == "chat-delta").HasUnreadMessage);
    }

    /// <summary>The other direction: the sync paths are not message-driven, so they have no unread
    /// opinion of their own and must take the server's. A single "inserts are unread" rule shared
    /// by all four writers would mark every chat in a full sync unread.</summary>
    [Fact]
    public async Task FullSyncChatCreate_TakesUnreadFromTheServer()
    {
        var factory = TestDbContextFactory.Create();
        var read = FullyPopulated("chat-read", false, [MakeHandle()]);
        var unread = FullyPopulated("chat-unread", true, [MakeHandle("+15550005555")]);
        await CreateSync(factory, new SyncMockApiService([read, unread]))
            .RunFullSyncAsync(skipEmptyChats: false);

        using var db = factory.CreateDbContext();
        Assert.False(db.Chats.Single(c => c.Guid == "chat-read").HasUnreadMessage);
        Assert.True(db.Chats.Single(c => c.Guid == "chat-unread").HasUnreadMessage);
    }

    /// <summary>The B6 guard, asserted at the writer: a payload that omits <c>hasUnreadMessage</c>
    /// (every group rename / participant event) means "no opinion", not "read". Making the merge's
    /// nullable guard unconditional to serve the insert paths would clear the badge on a rename.</summary>
    [Fact]
    public async Task ChatUpdate_WithASilentPayload_KeepsTheStoredUnreadFlag()
    {
        var factory = TestDbContextFactory.Create();
        using (var db = factory.CreateDbContext())
        {
            db.Chats.Add(new ChatEntity { Guid = "chat-rename", HasUnreadMessage = true });
            db.SaveChanges();
        }

        await CreateChats(factory).ApplyChatUpdateAsync(
            FullyPopulated("chat-rename", null, [MakeHandle()]));

        using var db2 = factory.CreateDbContext();
        Assert.True(db2.Chats.Single(c => c.Guid == "chat-rename").HasUnreadMessage);
    }

    /// <summary>A message-driven insert is holding a live message for that chat right now, so it
    /// must land live even if the embedded chat record still carries a stale delete stamp — the
    /// same reasoning the existing-row branch already applies when it resurrects a soft-deleted
    /// chat. Copying <c>dateDeleted</c> straight through on insert would file the chat as deleted
    /// and it would never appear in the list.</summary>
    [Fact]
    public async Task MessageDrivenChatCreate_IgnoresAStaleDeleteStampOnThePayload()
    {
        var live = TestDbContextFactory.Create();
        await CreateChats(live).EnsureChatExistsAsync(
            FullyPopulated("chat-live", null, [MakeHandle()]) with { DateDeleted = 12345 });

        var delta = TestDbContextFactory.Create();
        await RunDeltaAsync(delta, FullyPopulated("chat-delta", null, [MakeHandle()])
            with { DateDeleted = 12345 });

        using var liveDb = live.CreateDbContext();
        using var deltaDb = delta.CreateDbContext();
        Assert.Null(liveDb.Chats.Single(c => c.Guid == "chat-live").DateDeleted);
        Assert.Null(deltaDb.Chats.Single(c => c.Guid == "chat-delta").DateDeleted);
    }

    /// <summary>The sync paths have no live message in hand, so they must record the server's
    /// delete stamp rather than override it.</summary>
    [Fact]
    public async Task FullSyncChatCreate_RecordsTheServersDeleteStamp()
    {
        var factory = TestDbContextFactory.Create();
        var deleted = FullyPopulated("chat-sync", false, [MakeHandle()]) with { DateDeleted = 12345 };
        await CreateSync(factory, new SyncMockApiService([deleted]))
            .RunFullSyncAsync(skipEmptyChats: false);

        using var db = factory.CreateDbContext();
        Assert.Equal(12345, db.Chats.Single(c => c.Guid == "chat-sync").DateDeleted);
    }

    // ---- participant reconciliation on the update path ----------------------------------------

    /// <summary>One chat-update payload that both adds and removes a participant. The stale set is
    /// computed from the stored membership, so it must be decided against the rows that were there
    /// before this call — not against a collection EF has already fixed up with the links being
    /// added by the same call.</summary>
    [Fact]
    public async Task ChatUpdate_AddingAndRemovingInOneCall_LandsExactlyThePayloadsMembership()
    {
        var stays = MakeHandle("+15550001111");
        var leaves = MakeHandle("+15550002222");
        var joins = MakeHandle("+15550003333");

        var factory = TestDbContextFactory.Create();
        var chats = CreateChats(factory);
        await chats.EnsureChatExistsAsync(FullyPopulated("chat-members", null, [stays, leaves]));
        await chats.ApplyChatUpdateAsync(FullyPopulated("chat-members", null, [stays, joins]));

        using var db = factory.CreateDbContext();
        var chat = db.Chats.Single(c => c.Guid == "chat-members");
        var addresses = db.ChatParticipants
            .Where(cp => cp.ChatId == chat.Id)
            .Join(db.Handles, cp => cp.HandleId, h => h.Id, (_, h) => h.Address)
            .OrderBy(a => a)
            .ToList();

        Assert.Equal(["+15550001111", "+15550003333"], addresses);
    }
}
