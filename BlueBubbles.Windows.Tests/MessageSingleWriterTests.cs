using System.Reflection;
using BlueBubbles.Core.Data.Entities;
using BlueBubbles.Core.Models;
using BlueBubbles.Core.Services;

namespace BlueBubbles.Windows.Tests;

/// <summary>Guards the single-writer invariant for <see cref="MessageEntity"/>: the live socket
/// insert and the server upsert must write the same field set from the same payload, and the same
/// message arriving on both must still be one row. B2 was a field (attachments) written on one
/// path and forgotten on the other; these tests fail if that shape comes back.</summary>
public class MessageSingleWriterTests
{
    private const string ParentGuid = "p:0/BFB1E1AE-0000-0000-0000-000000000001";

    private static (MessagesService Service, TestDbContextFactory Factory) CreateService(
        SyncMockApiService api)
    {
        var factory = TestDbContextFactory.Create();
        return (new MessagesService(factory, api, new MockChatsService()), factory);
    }

    private static ChatEntity SeedChat(TestDbContextFactory factory, string guid)
    {
        using var db = factory.CreateDbContext();
        var chat = new ChatEntity { Guid = guid };
        db.Chats.Add(chat);
        db.SaveChanges();
        return chat;
    }

    private static readonly Handle SharedHandle =
        new(0, "+15550001111", "iMessage", "US", "(555) 000-1111", null, null, null, null);

    /// <summary>A payload with every server-owned field populated to a non-default value, so a
    /// dropped assignment in the shared writer shows up as a wrong value rather than as a default
    /// that happens to match.</summary>
    private static Message FullyPopulated(string guid) => new(
        OriginalRowId: 9033,
        Guid: guid,
        HandleId: 7,
        OtherHandle: 11,
        Text: "the body text",
        Subject: "the subject",
        Country: "US",
        Error: 22,
        DateCreated: 1700000000000,
        DateRead: 1700000005000,
        DateDelivered: 1700000002000,
        IsDelivered: true,
        IsFromMe: true,
        HasDdResults: true,
        DatePlayed: 1700000009000,
        ItemType: 2,
        GroupTitle: "the group name",
        GroupActionType: 3,
        BalloonBundleId: "com.apple.Handwriting.HandwritingProvider",
        AssociatedMessageGuid: ParentGuid,
        AssociatedMessagePart: null,
        AssociatedMessageType: "love",
        ExpressiveSendStyleId: "com.apple.MobileSMS.expressivesend.gentle",
        Handle: SharedHandle,
        HasAttachments: true,
        HasReactions: true,
        DateDeleted: null,
        Metadata: new Dictionary<string, object?> { ["site_name"] = "example.com" },
        ThreadOriginatorGuid: "AAAA1111-2222-3333-4444-555566667777",
        ThreadOriginatorPart: "0:0:0",
        Attachments: null,
        Chats: null,
        AttributedBody: [new AttributedBody("the body text", null)],
        MessageSummaryInfo: [new MessageSummaryInfo([1], null, null, [0])],
        PayloadData: null,
        HasApplePayloadData: true,
        DateEdited: 1700000007000,
        WasDeliveredQuietly: true,
        DidNotifyRecipient: true,
        IsBookmarked: true);

    private static void AssertMatchesPayload(MessageEntity row, Message payload)
    {
        Assert.Equal(payload.OriginalRowId, row.OriginalRowId);
        Assert.Equal(payload.OtherHandle, row.OtherHandle);
        Assert.Equal(payload.Text, row.Text);
        Assert.Equal(payload.Subject, row.Subject);
        Assert.Equal(payload.Country, row.Country);
        Assert.Equal(payload.Error, row.Error);
        Assert.Equal(payload.DateCreated, row.DateCreated);
        Assert.Equal(payload.DateRead, row.DateRead);
        Assert.Equal(payload.DateDelivered, row.DateDelivered);
        Assert.Equal(payload.IsDelivered, row.IsDelivered);
        Assert.Equal(payload.IsFromMe, row.IsFromMe);
        Assert.Equal(payload.HasDdResults, row.HasDdResults);
        Assert.Equal(payload.DatePlayed, row.DatePlayed);
        Assert.Equal(payload.ItemType, row.ItemType);
        Assert.Equal(payload.GroupTitle, row.GroupTitle);
        Assert.Equal(payload.GroupActionType, row.GroupActionType);
        Assert.Equal(payload.BalloonBundleId, row.BalloonBundleId);
        Assert.Equal(payload.AssociatedMessageType, row.AssociatedMessageType);
        Assert.Equal(payload.ExpressiveSendStyleId, row.ExpressiveSendStyleId);
        Assert.Equal(payload.HasAttachments, row.HasAttachments);
        Assert.Equal(payload.HasReactions, row.HasReactions);
        Assert.Equal(payload.DateDeleted, row.DateDeleted);
        Assert.Equal(payload.ThreadOriginatorGuid, row.ThreadOriginatorGuid);
        Assert.Equal(payload.ThreadOriginatorPart, row.ThreadOriginatorPart);
        Assert.Equal(payload.HasApplePayloadData, row.HasApplePayloadData);
        Assert.Equal(payload.DateEdited, row.DateEdited);
        Assert.Equal(payload.WasDeliveredQuietly, row.WasDeliveredQuietly);
        Assert.Equal(payload.DidNotifyRecipient, row.DidNotifyRecipient);
        Assert.Equal(payload.IsBookmarked, row.IsBookmarked);

        // The reaction prefix is stripped on persist so a tapback matches its parent in the DB;
        // the part is parsed out of the same prefix rather than taken from the (null) field.
        Assert.Equal("BFB1E1AE-0000-0000-0000-000000000001", row.AssociatedMessageGuid);
        Assert.Equal(0, row.AssociatedMessagePart);

        Assert.NotNull(row.MetadataJson);
        Assert.Contains("example.com", row.MetadataJson);
        Assert.NotNull(row.AttributedBodyJson);
        Assert.Contains("the body text", row.AttributedBodyJson);
        Assert.NotNull(row.MessageSummaryInfoJson);
        Assert.Contains("retractedParts", row.MessageSummaryInfoJson);
        Assert.Null(row.PayloadDataJson);
        Assert.NotNull(row.HandleId);
    }

    // The two entry points must produce the same row from the same payload. Asserting against the
    // payload (not just row-vs-row) is what makes a dropped assignment in the shared writer fail:
    // two identically-wrong rows would still be equal to each other.
    [Fact]
    public async Task LiveInsertAndServerUpsert_WriteTheSameFieldsFromTheSamePayload()
    {
        var upserted = FullyPopulated("msg-upsert");
        var api = new SyncMockApiService([], new Dictionary<string, List<Message>>
        {
            ["chat-upsert"] = [upserted]
        });
        var (svc, factory) = CreateService(api);
        SeedChat(factory, "chat-live");
        var upsertChat = SeedChat(factory, "chat-upsert");

        var inserted = FullyPopulated("msg-live");
        await svc.SaveIncomingMessageAsync("chat-live", inserted);
        await svc.RefreshLatestFromServerAsync(upsertChat.Id, "chat-upsert");

        using var db = factory.CreateDbContext();
        var liveRow = db.Messages.Single(m => m.Guid == "msg-live");
        var syncRow = db.Messages.Single(m => m.Guid == "msg-upsert");

        AssertMatchesPayload(liveRow, inserted);
        AssertMatchesPayload(syncRow, upserted);

        // Catches a column added later and written on only one path: every scalar the two rows can
        // legitimately differ on is named here, so anything new is compared by default.
        string[] rowIdentity = [nameof(MessageEntity.Id), nameof(MessageEntity.Guid),
            nameof(MessageEntity.ChatId)];
        var scalars = typeof(MessageEntity).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType.IsValueType || p.PropertyType == typeof(string))
            .Where(p => !rowIdentity.Contains(p.Name))
            .ToList();
        Assert.NotEmpty(scalars);
        foreach (var p in scalars)
            Assert.Equal($"{p.Name}={p.GetValue(liveRow)}", $"{p.Name}={p.GetValue(syncRow)}");
    }

    // Identity is the server GUID on every message write path. The same message seen live and then
    // again in a re-fetched window is one row, and the live insert never overwrites it.
    [Fact]
    public async Task SameMessageThroughBothEntryPoints_PersistsExactlyOneRow()
    {
        var serverCopy = FullyPopulated("msg-both") with { Text = "edited on the server" };
        var api = new SyncMockApiService([], new Dictionary<string, List<Message>>
        {
            ["chat-both"] = [serverCopy]
        });
        var (svc, factory) = CreateService(api);
        var chat = SeedChat(factory, "chat-both");

        await svc.SaveIncomingMessageAsync("chat-both", FullyPopulated("msg-both"));
        await svc.RefreshLatestFromServerAsync(chat.Id, "chat-both");
        // A socket replay of the original payload must not revert the applied server text.
        await svc.SaveIncomingMessageAsync("chat-both", FullyPopulated("msg-both"));

        using var db = factory.CreateDbContext();
        var rows = db.Messages.Where(m => m.Guid == "msg-both").ToList();
        Assert.Single(rows);
        Assert.Equal("edited on the server", rows[0].Text);
    }
}
