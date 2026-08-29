using BlueBubbles.Core.Configuration;
using BlueBubbles.Core.Data.Entities;
using BlueBubbles.Core.Models;
using BlueBubbles.Core.Services;
using BlueBubbles.Core.Utils;

namespace BlueBubbles.Windows.Tests;

/// <summary>B10: the chat-details pane and the persistence path both hang off one
/// <c>ChatUpdated</c> event and are not ordered, so the pane's cached read is stale.</summary>
public class DetailsParticipantsTests
{
    private static Handle MockHandle(string address) =>
        new(0, address, "iMessage", null, null, null, null, null, null);

    private static HandleEntity Cached(string address) =>
        new() { Id = address.GetHashCode() & 0x7fffffff, Address = address };

    private static Chat MockChat(string guid, List<Handle>? participants) =>
        new(guid, guid, null, participants, null, false, false, false, "iMessage",
            null, null, null, null, null, 43, false, false, null);

    [Fact]
    public void Resolve_PrefersPayloadParticipants_OverStaleCache()
    {
        var cached = new List<HandleEntity> { Cached("+15550001"), Cached("+15550002") };
        var payload = new List<Handle> { MockHandle("+15550001"), MockHandle("+15550002"), MockHandle("+15550003") };

        var resolved = DetailsParticipants.Resolve(payload, cached);

        Assert.Equal(["+15550001", "+15550002", "+15550003"], resolved.Select(h => h.Address));
    }

    [Fact]
    public void Resolve_PrefersPayloadParticipants_OnRemoval()
    {
        var cached = new List<HandleEntity> { Cached("+15550001"), Cached("+15550002"), Cached("+15550003") };
        var payload = new List<Handle> { MockHandle("+15550001"), MockHandle("+15550003") };

        var resolved = DetailsParticipants.Resolve(payload, cached);

        Assert.Equal(["+15550001", "+15550003"], resolved.Select(h => h.Address));
    }

    [Fact]
    public void Resolve_ReusesCachedEntity_ForKnownAddress()
    {
        var known = Cached("+15550001");
        var resolved = DetailsParticipants.Resolve([MockHandle("+15550001")], [known]);

        Assert.Same(known, Assert.Single(resolved));
    }

    // The fallback is load-bearing: ChatsService.ResolveParticipantsAsync documents that a
    // chat-update payload can carry no participants at all. Blanking a populated pane is worse
    // than rendering a set one beat stale.
    [Fact]
    public void Resolve_PayloadWithoutParticipants_DoesNotBlankPopulatedPane()
    {
        var cached = new List<HandleEntity> { Cached("+15550001"), Cached("+15550002") };

        Assert.Equal(["+15550001", "+15550002"],
            DetailsParticipants.Resolve(null, cached).Select(h => h.Address));
        Assert.Equal(["+15550001", "+15550002"],
            DetailsParticipants.Resolve([], cached).Select(h => h.Address));
    }

    [Fact]
    public void Resolve_NoPayloadAndNoCache_ReturnsEmpty()
    {
        Assert.Empty(DetailsParticipants.Resolve(null, null));
    }

    /// <summary>The persistence path refreshes <see cref="IChatsService.Chats"/> only at the very
    /// end of <c>ApplyChatUpdateAsync</c>, and the details pane's refresh is not ordered against it
    /// (the pane reads on the UI dispatcher; persistence runs off a background channel drain). This
    /// pins the two ends: the cache as the pane can still see it carries the pre-update set, while
    /// the payload delivered with the very same event already carries the new one.</summary>
    [Fact]
    public async Task PayloadCarriesNewSet_WhileTheCacheThePaneReadsIsStillPreUpdate()
    {
        var factory = TestDbContextFactory.Create();
        var svc = new ChatsService(factory, new MockApiService(), new AppSettings());

        await using (var db = factory.CreateDbContext())
        {
            var chat = new ChatEntity { Guid = "grp", DisplayName = "Group", LatestMessageDate = 1000 };
            db.Chats.Add(chat);
            await db.SaveChangesAsync();
            foreach (var address in new[] { "+15550001", "+15550002" })
            {
                var handle = new HandleEntity { Address = address };
                db.Handles.Add(handle);
                await db.SaveChangesAsync();
                db.ChatParticipants.Add(new ChatParticipant { ChatId = chat.Id, HandleId = handle.Id });
            }
            await db.SaveChangesAsync();
        }

        await svc.LoadChatsAsync();

        var payload = MockChat("grp",
            [MockHandle("+15550001"), MockHandle("+15550002"), MockHandle("+15550003")]);

        // The snapshot is what a details pane holds when the event arrives: the persistence path has
        // not reloaded the cache yet, so this is deliberately taken before ApplyChatUpdateAsync.
        var cachedNow = svc.Chats.First(c => c.Chat.Guid == "grp").Participants;
        Assert.Equal(2, cachedNow.Count);

        Assert.Equal(3, DetailsParticipants.Resolve(payload.Participants, cachedNow).Count);

        await svc.ApplyChatUpdateAsync(payload);
        Assert.Equal(3, svc.Chats.First(c => c.Chat.Guid == "grp").Participants.Count);
    }
}
