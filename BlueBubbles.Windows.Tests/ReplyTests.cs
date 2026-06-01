using System.Text.Json;
using BlueBubbles.Core.Data.Entities;
using BlueBubbles.Core.Models;
using BlueBubbles.Core.Services;
using BlueBubbles.Core.Utils;

namespace BlueBubbles.Windows.Tests;

public class MessagesServiceReplyTests
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

    private static Message MakeMessage(string guid, string? text, bool isFromMe,
        long date, string? threadOriginatorGuid = null, string? address = null)
    {
        var handleJson = address is null
            ? "null"
            : $$"""{ "originalROWID": 1, "address": "{{address}}", "service": "iMessage" }""";
        var textJson = text is null ? "null" : $"\"{text}\"";
        var threadJson = threadOriginatorGuid is null ? "null" : $"\"{threadOriginatorGuid}\"";

        var json = $$"""
        {
            "guid": "{{guid}}",
            "text": {{textJson}},
            "isFromMe": {{(isFromMe ? "true" : "false")}},
            "threadOriginatorGuid": {{threadJson}},
            "threadOriginatorPart": "0:0:0",
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
            "dateCreated": {{date}},
            "handle": {{handleJson}}
        }
        """;
        return JsonSerializer.Deserialize<Message>(json, JsonDefaults.Options)!;
    }

    [Fact]
    public async Task GetMessagesByGuids_ReturnsMatching_WithHandle()
    {
        var factory = TestDbContextFactory.Create();
        var svc = CreateService(factory);
        var chat = SeedChat(factory);

        await svc.SaveIncomingMessageAsync(chat.Guid, MakeMessage("m1", "first", false, 1_000, address: "+15550001111"));
        await svc.SaveIncomingMessageAsync(chat.Guid, MakeMessage("m2", "second", true, 2_000));
        await svc.SaveIncomingMessageAsync(chat.Guid, MakeMessage("m3", "third", false, 3_000, address: "+15550002222"));

        var found = await svc.GetMessagesByGuidsAsync(["m1", "m3"]);

        Assert.Equal(2, found.Count);
        Assert.Contains(found, m => m.Guid == "m1" && m.Handle?.Address == "+15550001111");
        Assert.Contains(found, m => m.Guid == "m3" && m.Handle?.Address == "+15550002222");
        Assert.DoesNotContain(found, m => m.Guid == "m2");
    }

    [Fact]
    public async Task GetMessagesByGuids_EmptyInput_ReturnsEmpty()
    {
        var factory = TestDbContextFactory.Create();
        var svc = CreateService(factory);

        Assert.Empty(await svc.GetMessagesByGuidsAsync([]));
    }

    [Fact]
    public async Task ReplyMessage_PersistsThreadLink_AndLoadsAsNormalBubble()
    {
        var factory = TestDbContextFactory.Create();
        var svc = CreateService(factory);
        var chat = SeedChat(factory);

        await svc.SaveIncomingMessageAsync(chat.Guid, MakeMessage("original", "the original", false, 1_000, address: "+1"));
        await svc.SaveIncomingMessageAsync(chat.Guid,
            MakeMessage("the-reply", "a reply", true, 2_000, threadOriginatorGuid: "original"));

        // Replies are real messages — unlike reactions they appear in the main stream.
        var loaded = await svc.LoadMessagesAsync(chat.Id);

        Assert.Equal(2, loaded.Count);
        var reply = Assert.Single(loaded, m => m.Guid == "the-reply");
        Assert.Equal("original", reply.ThreadOriginatorGuid);
    }
}
