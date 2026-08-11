using System.Text.Json;
using BlueBubbles.Core.Data.Entities;
using BlueBubbles.Core.Models;
using BlueBubbles.Core.Services;
using BlueBubbles.Core.Utils;

namespace BlueBubbles.Windows.Tests;

public class MessageEditsTests
{
    [Fact]
    public void IsPartRetracted_Model_DetectsRetractedPart()
    {
        var summary = new List<MessageSummaryInfo>
        {
            new(RetractedParts: [0], EditedContent: null, OriginalTextRange: null, EditedParts: null)
        };

        Assert.True(MessageEdits.IsPartRetracted(summary, 0));
        Assert.False(MessageEdits.IsPartRetracted(summary, 1));
    }

    [Fact]
    public void IsPartRetracted_Model_NullOrEmpty_ReturnsFalse()
    {
        Assert.False(MessageEdits.IsPartRetracted((IReadOnlyList<MessageSummaryInfo>?)null, 0));
        Assert.False(MessageEdits.IsPartRetracted(new List<MessageSummaryInfo>(), 0));
    }

    [Fact]
    public void IsPartRetracted_Json_DetectsRetractedPart()
    {
        const string json =
            """[{"retractedParts":[0],"editedContent":{},"originalTextRange":{},"editedParts":[]}]""";

        Assert.True(MessageEdits.IsPartRetracted(json, 0));
        Assert.False(MessageEdits.IsPartRetracted(json, 2));
    }

    [Fact]
    public void IsPartRetracted_Json_NullEmptyOrGarbage_ReturnsFalse()
    {
        Assert.False(MessageEdits.IsPartRetracted((string?)null, 0));
        Assert.False(MessageEdits.IsPartRetracted("", 0));
        Assert.False(MessageEdits.IsPartRetracted("{not valid json", 0));
    }

    [Fact]
    public void BuildBackwardsCompatText_WrapsInCurlyQuotes()
    {
        // Mirrors the Flutter client's "Edited to: “{text}”" backwards-compatibility string.
        Assert.Equal("Edited to: “hello”", MessageEdits.BuildBackwardsCompatText("hello"));
    }
}

public class MessagesServiceEditTests
{
    private static MessagesService CreateService(TestDbContextFactory factory)
        => new(factory, new SyncMockApiService([]));

    private static ChatEntity SeedChat(TestDbContextFactory factory, string guid = "chat;+1")
    {
        using var db = factory.CreateDbContext();
        var chat = new ChatEntity { Guid = guid };
        db.Chats.Add(chat);
        db.SaveChanges();
        return chat;
    }

    private static Message Deserialize(string json)
        => JsonSerializer.Deserialize<Message>(json, JsonDefaults.Options)!;

    private static Message BaseMessage(string guid, string? text, bool isFromMe, long date)
    {
        var fromMe = isFromMe ? "true" : "false";
        var textJson = text is null ? "null" : $"\"{text}\"";
        return Deserialize($$"""
        {
            "guid": "{{guid}}",
            "text": {{textJson}},
            "isFromMe": {{fromMe}},
            "error": 0,
            "isDelivered": true,
            "hasDdResults": false,
            "itemType": 0,
            "groupActionType": 0,
            "hasAttachments": false,
            "hasReactions": false,
            "hasApplePayloadData": false,
            "wasDeliveredQuietly": false,
            "didNotifyRecipient": false,
            "isBookmarked": false,
            "dateCreated": {{date}}
        }
        """);
    }

    [Fact]
    public async Task UpdateMessage_Edit_PersistsNewTextAndDateEdited()
    {
        var factory = TestDbContextFactory.Create();
        var svc = CreateService(factory);
        var chat = SeedChat(factory);

        await svc.SaveIncomingMessageAsync(chat.Guid, BaseMessage("m1", "original", true, 1000));

        var edited = Deserialize("""
        {
            "guid": "m1", "text": "edited!", "isFromMe": true, "error": 0,
            "isDelivered": true, "hasDdResults": false, "itemType": 0, "groupActionType": 0,
            "hasAttachments": false, "hasReactions": false, "hasApplePayloadData": false,
            "wasDeliveredQuietly": false, "didNotifyRecipient": false, "isBookmarked": false,
            "dateEdited": 5000
        }
        """);
        await svc.UpdateMessageAsync(edited);

        using var db = factory.CreateDbContext();
        var entity = db.Messages.First(m => m.Guid == "m1");
        Assert.Equal("edited!", entity.Text);
        Assert.Equal(5000, entity.DateEdited);
    }

    [Fact]
    public async Task UpdateMessage_Unsend_PersistsRetractedPart()
    {
        var factory = TestDbContextFactory.Create();
        var svc = CreateService(factory);
        var chat = SeedChat(factory);

        await svc.SaveIncomingMessageAsync(chat.Guid, BaseMessage("m2", "secret", true, 1000));

        var unsent = Deserialize("""
        {
            "guid": "m2", "text": "secret", "isFromMe": true, "error": 0,
            "isDelivered": true, "hasDdResults": false, "itemType": 0, "groupActionType": 0,
            "hasAttachments": false, "hasReactions": false, "hasApplePayloadData": false,
            "wasDeliveredQuietly": false, "didNotifyRecipient": false, "isBookmarked": false,
            "messageSummaryInfo": [{"retractedParts":[0],"editedContent":{},"originalTextRange":{},"editedParts":[]}]
        }
        """);
        await svc.UpdateMessageAsync(unsent);

        using var db = factory.CreateDbContext();
        var entity = db.Messages.First(m => m.Guid == "m2");
        Assert.True(MessageEdits.IsPartRetracted(entity.MessageSummaryInfoJson, 0));
    }

    [Fact]
    public async Task UpdateMessage_NullText_DoesNotWipeExistingText()
    {
        var factory = TestDbContextFactory.Create();
        var svc = CreateService(factory);
        var chat = SeedChat(factory);

        await svc.SaveIncomingMessageAsync(chat.Guid, BaseMessage("m3", "keep me", true, 1000));

        // A later delivery-only update (e.g. a read receipt) carrying null text must not erase the text.
        var deliveryUpdate = BaseMessage("m3", null, true, 1000) with { DateRead = 8000 };
        await svc.UpdateMessageAsync(deliveryUpdate);

        using var db = factory.CreateDbContext();
        var entity = db.Messages.First(m => m.Guid == "m3");
        Assert.Equal("keep me", entity.Text);
        Assert.Equal(8000, entity.DateRead);
    }

    [Fact]
    public async Task UpdateMessage_ReturnsOwningChatGuid_NotJustTheFirstChat()
    {
        // The returned GUID is what IncomingMessageProcessor announces to the conversation list, so a
        // wrong-but-plausible chat (the first row, an off-by-one) is silently as bad as returning null.
        var factory = TestDbContextFactory.Create();
        var svc = CreateService(factory);
        SeedChat(factory, "chat;+decoy");
        var owner = SeedChat(factory, "chat;+owner");

        await svc.SaveIncomingMessageAsync(owner.Guid, BaseMessage("m4", "hi", false, 1000));

        var guid = await svc.UpdateMessageAsync(BaseMessage("m4", null, false, 1000) with { DateRead = 9000 });

        Assert.Equal("chat;+owner", guid);
        Assert.Null(await svc.UpdateMessageAsync(BaseMessage("not-cached", "x", false, 1000)));
    }

    [Fact]
    public async Task Delete_CallsServer_SetsDateDeleted_AndHidesFromLoad()
    {
        var factory = TestDbContextFactory.Create();
        var api = new MockApiService();
        (string chatGuid, string messageGuid)? deleted = null;
        api.DeleteMessageFunc = (chatGuid, messageGuid) =>
        {
            deleted = (chatGuid, messageGuid);
            return Task.FromResult(new ApiResponse<JsonElement>(200, "OK", default, null));
        };
        var svc = new MessagesService(factory, api);
        var chat = SeedChat(factory);

        await svc.SaveIncomingMessageAsync(chat.Guid, BaseMessage("m4", "delete me", true, 1000));
        Assert.True(await svc.DeleteMessageAsync(chat.Guid, "m4"));

        Assert.Equal((chat.Guid, "m4"), deleted);
        Assert.Empty(await svc.LoadMessagesAsync(chat.Id));

        using var db = factory.CreateDbContext();
        Assert.NotNull(db.Messages.First(m => m.Guid == "m4").DateDeleted);
    }

    [Fact]
    public async Task Delete_ServerFailure_LeavesMessageUntouched()
    {
        // A local-only soft delete is overwritten by the next sync, so a failed server call must
        // leave the row alone and report failure.
        var factory = TestDbContextFactory.Create();
        var api = new MockApiService
        {
            DeleteMessageFunc = (_, _) =>
                Task.FromResult(new ApiResponse<JsonElement>(500, "error", default, null))
        };
        var svc = new MessagesService(factory, api);
        var chat = SeedChat(factory);

        await svc.SaveIncomingMessageAsync(chat.Guid, BaseMessage("m4", "keep me", true, 1000));
        Assert.False(await svc.DeleteMessageAsync(chat.Guid, "m4"));

        Assert.Single(await svc.LoadMessagesAsync(chat.Id));

        using var db = factory.CreateDbContext();
        Assert.Null(db.Messages.First(m => m.Guid == "m4").DateDeleted);
    }

    [Fact]
    public async Task Delete_UnknownGuid_StillSucceedsWithoutLocalRow()
    {
        var factory = TestDbContextFactory.Create();
        var api = new MockApiService
        {
            DeleteMessageFunc = (_, _) =>
                Task.FromResult(new ApiResponse<JsonElement>(200, "OK", default, null))
        };
        var svc = new MessagesService(factory, api);
        var chat = SeedChat(factory);

        // Server says deleted, no matching local row — should not throw.
        Assert.True(await svc.DeleteMessageAsync(chat.Guid, "does-not-exist"));
    }
}
