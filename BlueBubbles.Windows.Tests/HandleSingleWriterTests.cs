using System.Reflection;
using BlueBubbles.Core.Configuration;
using BlueBubbles.Core.Data.Entities;
using BlueBubbles.Core.Models;
using BlueBubbles.Core.Services;

namespace BlueBubbles.Windows.Tests;

/// <summary>Guards the single-writer invariant for <see cref="HandleEntity"/> and
/// <see cref="ChatParticipant"/>: every entry point that can create a handle or a chat link
/// (full sync, incremental sync, chat-create, chat-update, live message save, window upsert)
/// must agree on identity — (Address, Service) for a handle, (ChatId, HandleId) for a link —
/// and must not clobber metadata another path already stored. Multiple writers for one entity
/// is the shape shared by B1, B2, B6 and B7.</summary>
public class HandleSingleWriterTests
{
    private const string Address = "+15550002222";
    private const string Service = "iMessage";

    /// <summary>Every server-owned handle field at a non-default value, so a dropped assignment
    /// in the shared writer shows up as a wrong value rather than a default that happens to match.</summary>
    private static readonly Handle FullyPopulated = new(
        OriginalRowId: 4711,
        Address: Address,
        Service: Service,
        Country: "us",
        FormattedAddress: "(555) 000-2222",
        Color: "#ff0055",
        DefaultPhone: "+15550002222",
        DefaultEmail: "someone@example.com",
        UniqueAddressAndService: Address + "/" + Service);

    /// <summary>The same participant as the server sends it on a sparse payload: identity only.
    /// Chat payloads routinely carry this shape, which is why "last writer wins" is dangerous.</summary>
    private static readonly Handle Sparse =
        new(0, Address, Service, null, null, null, null, null, null);

    private static Chat MakeChat(string guid, List<Handle>? participants) =>
        new(guid, guid, null, participants, null, false, false, false, Service,
            null, null, null, null, null, null, false, false, null);

    private static Message MakeMessage(string guid, Handle? handle, long dateCreated = 1700000000000) =>
        new(null, guid, null, null, "hi", null, null, 0,
            dateCreated, null, null,
            false, false, false, null, 0, null, 0, null, null, null, null, null,
            handle, false, false, null, null, null, null, null, null, null, null,
            null, false, null, false, false, false);

    private static (ChatsService Svc, TestDbContextFactory Factory) CreateChatsService(
        TestDbContextFactory? factory = null)
    {
        factory ??= TestDbContextFactory.Create();
        return (new ChatsService(factory, new MockApiService(), new AppSettings()), factory);
    }

    private static (SyncService Svc, TestDbContextFactory Factory) CreateSyncService(
        SyncMockApiService api, TestDbContextFactory? factory = null)
    {
        factory ??= TestDbContextFactory.Create();
        return (new SyncService(api, factory, new MockFirebaseService(), new AppSettings(),
            new MockSettingsService(), new MockChatsService()), factory);
    }

    // ---- characterization: identity and de-duplication ----------------------------------

    /// <summary>One handle, reached through five different writers. Identity is (Address, Service)
    /// on every one of them, so the cache holds a single row no matter which path saw it first.</summary>
    [Fact]
    public async Task SameAddressAndService_ThroughEveryWriter_IsOneHandleRow()
    {
        var factory = TestDbContextFactory.Create();

        var syncApi = new SyncMockApiService(
            [MakeChat("chat-sync", [FullyPopulated])],
            new Dictionary<string, List<Message>>
            {
                ["chat-sync"] = [MakeMessage("msg-sync", Sparse)]
            });
        var (sync, _) = CreateSyncService(syncApi, factory);
        await sync.RunFullSyncAsync(skipEmptyChats: false);

        var (chats, _) = CreateChatsService(factory);
        await chats.EnsureChatInDatabaseAsync(MakeChat("chat-ensure-db", [Sparse]), "hi");
        await chats.EnsureChatExistsAsync(MakeChat("chat-ensure-exists", [Sparse]));
        await chats.ApplyChatUpdateAsync(MakeChat("chat-ensure-exists", [Sparse]));

        var messages = new MessagesService(factory, syncApi);
        await messages.SaveIncomingMessageAsync("chat-ensure-db", MakeMessage("msg-live", Sparse));

        using var db = factory.CreateDbContext();
        var rows = db.Handles.Where(h => h.Address == Address && h.Service == Service).ToList();
        Assert.Single(rows);
    }

    /// <summary>Linking the same participant repeatedly, across writers, is idempotent. A second
    /// row for the same (ChatId, HandleId) is a duplicate participant in the UI at best and a
    /// primary-key violation at worst.</summary>
    [Fact]
    public async Task RepeatedParticipantLinks_AcrossWriters_ProduceOneRow()
    {
        var factory = TestDbContextFactory.Create();
        var (chats, _) = CreateChatsService(factory);

        await chats.EnsureChatExistsAsync(MakeChat("chat-link", [FullyPopulated]));
        await chats.EnsureChatInDatabaseAsync(MakeChat("chat-link", [FullyPopulated]), "hi");
        await chats.ApplyChatUpdateAsync(MakeChat("chat-link", [FullyPopulated]));
        await chats.ApplyChatUpdateAsync(MakeChat("chat-link", [Sparse]));

        using var db = factory.CreateDbContext();
        var chat = db.Chats.Single(c => c.Guid == "chat-link");
        Assert.Single(db.ChatParticipants.Where(cp => cp.ChatId == chat.Id));
    }

    /// <summary>A chat update carries the whole membership, so a handle it omits has left the
    /// chat and its link must go. Removal lives with the writer, not scattered beside it.</summary>
    [Fact]
    public async Task ChatUpdate_OmittingAParticipant_RemovesTheLink()
    {
        var other = new Handle(0, "+15550003333", Service, null, null, null, null, null, null);
        var factory = TestDbContextFactory.Create();
        var (chats, _) = CreateChatsService(factory);

        await chats.EnsureChatExistsAsync(MakeChat("chat-remove", [FullyPopulated, other]));
        await chats.ApplyChatUpdateAsync(MakeChat("chat-remove", [FullyPopulated]));

        using var db = factory.CreateDbContext();
        var chat = db.Chats.Single(c => c.Guid == "chat-remove");
        var kept = db.ChatParticipants.Where(cp => cp.ChatId == chat.Id).ToList();
        Assert.Single(kept);
        Assert.Equal(Address, db.Handles.Single(h => h.Id == kept[0].HandleId).Address);
        // The handle row itself survives; only the membership was revoked.
        Assert.Equal(2, db.Handles.Count());
    }

    // ---- characterization: the field list and the no-clobber rule ------------------------

    /// <summary>The sync writer stores every server-owned field. The reflection sweep is the point:
    /// a column added later, or an assignment dropped from the shared field list, fails here by
    /// default instead of silently going unwritten.</summary>
    [Fact]
    public async Task SyncWriter_StoresEveryServerOwnedHandleField()
    {
        var api = new SyncMockApiService([MakeChat("chat-fields", [FullyPopulated])]);
        var (sync, factory) = CreateSyncService(api);

        await sync.RunFullSyncAsync(skipEmptyChats: false);

        using var db = factory.CreateDbContext();
        var row = db.Handles.Single();

        Assert.Equal(FullyPopulated.OriginalRowId, row.OriginalRowId);
        Assert.Equal(FullyPopulated.Address, row.Address);
        Assert.Equal(FullyPopulated.Service, row.Service);
        Assert.Equal(FullyPopulated.Country, row.Country);
        Assert.Equal(FullyPopulated.FormattedAddress, row.FormattedAddress);
        Assert.Equal(FullyPopulated.Color, row.Color);
        Assert.Equal(FullyPopulated.DefaultPhone, row.DefaultPhone);
        Assert.Equal(FullyPopulated.DefaultEmail, row.DefaultEmail);
        Assert.Equal(FullyPopulated.UniqueAddressAndService, row.UniqueAddressAndService);

        // Every scalar column except the generated key came from the payload. Nothing on
        // HandleEntity is client-owned, so an unwritten column here is an unwritten column, period.
        var unwritten = typeof(HandleEntity)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType.IsValueType || p.PropertyType == typeof(string))
            .Where(p => p.Name != nameof(HandleEntity.Id))
            .Where(p => p.GetValue(row) is null or 0 or "")
            .Select(p => p.Name)
            .ToList();
        Assert.Empty(unwritten);
    }

    /// <summary>A later sparse payload must not blank metadata an earlier full payload stored.
    /// This is the B6 shape — a writer copying defaults off a payload that never carried the
    /// field — and it is why the chat/message paths deliberately do not refresh a known handle.</summary>
    [Fact]
    public async Task SparsePayloadAfterFullSync_DoesNotBlankStoredHandleMetadata()
    {
        var factory = TestDbContextFactory.Create();
        var api = new SyncMockApiService([MakeChat("chat-seed", [FullyPopulated])]);
        var (sync, _) = CreateSyncService(api, factory);
        await sync.RunFullSyncAsync(skipEmptyChats: false);

        var (chats, _) = CreateChatsService(factory);
        await chats.EnsureChatInDatabaseAsync(MakeChat("chat-sparse", [Sparse]), "hi");
        await chats.EnsureChatExistsAsync(MakeChat("chat-sparse-2", [Sparse]));
        await chats.ApplyChatUpdateAsync(MakeChat("chat-sparse", [Sparse]));

        var messages = new MessagesService(factory, api);
        await messages.SaveIncomingMessageAsync("chat-sparse", MakeMessage("msg-sparse", Sparse));

        using var db = factory.CreateDbContext();
        var row = db.Handles.Single(h => h.Address == Address);
        Assert.Equal("us", row.Country);
        Assert.Equal("(555) 000-2222", row.FormattedAddress);
        Assert.Equal("#ff0055", row.Color);
        Assert.Equal("someone@example.com", row.DefaultEmail);
        Assert.Equal(4711, row.OriginalRowId);
    }

    /// <summary>The message writers resolve an existing handle rather than inserting a second one,
    /// and the message row points at it.</summary>
    [Fact]
    public async Task MessageWriters_ReuseTheExistingHandleRow()
    {
        var factory = TestDbContextFactory.Create();
        var api = new SyncMockApiService(
            [MakeChat("chat-msg", [FullyPopulated])],
            new Dictionary<string, List<Message>>
            {
                ["chat-msg"] = [MakeMessage("msg-upsert", FullyPopulated, 1700000001000)]
            });
        var (sync, _) = CreateSyncService(api, factory);
        await sync.RunFullSyncAsync(skipEmptyChats: false);

        var messages = new MessagesService(factory, api);
        await messages.SaveIncomingMessageAsync("chat-msg", MakeMessage("msg-live", FullyPopulated, 1700000002000));

        using var db = factory.CreateDbContext();
        var handle = db.Handles.Single(h => h.Address == Address);
        var live = db.Messages.Single(m => m.Guid == "msg-live");
        var upserted = db.Messages.Single(m => m.Guid == "msg-upsert");
        Assert.Equal(handle.Id, live.HandleId);
        Assert.Equal(handle.Id, upserted.HandleId);
    }
}
