using System.Text.Json;
using BlueBubbles.Core.Models;
using BlueBubbles.Core.Services;

namespace BlueBubbles.Windows.Tests;

public class ActionHandlerTests
{
    private static JsonElement Parse(string json) =>
        JsonSerializer.Deserialize<JsonElement>(json);

    [Fact]
    public void NewMessage_FiresEvent_WithDeserializedMessage()
    {
        var handler = new ActionHandler();
        MessageEventArgs? received = null;
        handler.NewMessageReceived += (_, e) => received = e;

        var json = Parse("""
        {
            "data": {
                "originalROWID": 1,
                "guid": "msg-001",
                "text": "Hello!",
                "isFromMe": false,
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
                "isBookmarked": false
            },
            "tempGuid": "temp-001"
        }
        """);

        handler.HandleEvent(SocketEvents.NewMessage, json, "Test");

        Assert.NotNull(received);
        Assert.Equal("msg-001", received!.Message.Guid);
        Assert.Equal("Hello!", received.Message.Text);
        Assert.Equal("temp-001", received.TempGuid);
    }

    [Fact]
    public void UpdatedMessage_FiresEvent()
    {
        var handler = new ActionHandler();
        MessageEventArgs? received = null;
        handler.MessageUpdated += (_, e) => received = e;

        var json = Parse("""
        {
            "data": {
                "originalROWID": 2,
                "guid": "msg-002",
                "text": "Edited text",
                "isFromMe": true,
                "isDelivered": true,
                "error": 0,
                "hasDdResults": false,
                "itemType": 0,
                "groupActionType": 0,
                "hasAttachments": false,
                "hasReactions": false,
                "hasApplePayloadData": false,
                "wasDeliveredQuietly": false,
                "didNotifyRecipient": false,
                "isBookmarked": false
            }
        }
        """);

        handler.HandleEvent(SocketEvents.UpdatedMessage, json, "Test");

        Assert.NotNull(received);
        Assert.Equal("msg-002", received!.Message.Guid);
        Assert.Null(received.TempGuid);
    }

    [Fact]
    public void TypingIndicator_FiresEvent()
    {
        var handler = new ActionHandler();
        TypingIndicatorPayload? received = null;
        handler.TypingIndicatorChanged += (_, e) => received = e;

        var json = Parse("""{"display": true, "guid": "chat-abc"}""");

        handler.HandleEvent(SocketEvents.TypingIndicator, json, "Test");

        Assert.NotNull(received);
        Assert.True(received!.Display);
        Assert.Equal("chat-abc", received.Guid);
    }

    [Fact]
    public void ChatReadStatusChanged_FiresEvent()
    {
        var handler = new ActionHandler();
        ChatReadStatusPayload? received = null;
        handler.ChatReadStatusChanged += (_, e) => received = e;

        var json = Parse("""{"chatGuid": "chat-123", "read": true}""");

        handler.HandleEvent(SocketEvents.ChatReadStatusChanged, json, "Test");

        Assert.NotNull(received);
        Assert.Equal("chat-123", received!.ChatGuid);
        Assert.True(received.Read);
    }

    [Fact]
    public void GroupNameChange_FiresChatUpdated()
    {
        var handler = new ActionHandler();
        ChatUpdatedEventArgs? received = null;
        handler.ChatUpdated += (_, e) => received = e;

        var json = Parse("""{"guid": "chat-group", "displayName": "New Name"}""");

        handler.HandleEvent(SocketEvents.GroupNameChange, json, "Test");

        Assert.NotNull(received);
        Assert.Equal(SocketEvents.GroupNameChange, received!.EventType);
    }

    [Fact]
    public void GroupNameChange_ExposesChatFromMessagePayload()
    {
        // The server emits these events as a serialized message whose chats[0] is the freshly loaded
        // chat (participants included) — the top-level "guid" is the message's, not the chat's.
        var handler = new ActionHandler();
        ChatUpdatedEventArgs? received = null;
        handler.ChatUpdated += (_, e) => received = e;

        var json = Parse("""
        {
            "guid": "msg-500",
            "itemType": 2,
            "groupTitle": "New Name",
            "chats": [{
                "guid": "iMessage;+;chat-group",
                "displayName": "New Name",
                "participants": [{ "address": "+15551112222", "service": "iMessage" }]
            }]
        }
        """);

        handler.HandleEvent(SocketEvents.GroupNameChange, json, "Test");

        Assert.NotNull(received?.Chat);
        Assert.Equal("iMessage;+;chat-group", received!.Chat!.Guid);
        Assert.Equal("New Name", received.Chat.DisplayName);
        Assert.Equal("+15551112222", Assert.Single(received.Chat.Participants!).Address);
    }

    [Theory]
    [InlineData(SocketEvents.ParticipantAdded)]
    [InlineData(SocketEvents.ParticipantRemoved)]
    [InlineData(SocketEvents.ParticipantLeft)]
    public void ParticipantEvents_FireChatUpdated(string eventName)
    {
        var handler = new ActionHandler();
        ChatUpdatedEventArgs? received = null;
        handler.ChatUpdated += (_, e) => received = e;

        var json = Parse("""{"guid": "chat-group"}""");

        handler.HandleEvent(eventName, json, "Test");

        Assert.NotNull(received);
        Assert.Equal(eventName, received!.EventType);
    }

    [Fact]
    public void FtCallStatusChanged_FiresEvent()
    {
        var handler = new ActionHandler();
        JsonElement? received = null;
        handler.FaceTimeStatusChanged += (_, e) => received = e;

        var json = Parse("""{"status_id": 4, "uuid": "call-123"}""");

        handler.HandleEvent(SocketEvents.FtCallStatusChanged, json, "Test");

        Assert.NotNull(received);
    }

    [Fact]
    public void AliasesRemoved_FiresEventWithList()
    {
        var handler = new ActionHandler();
        List<string>? received = null;
        handler.AliasesRemoved += (_, e) => received = e;

        var json = Parse("""{"aliases": ["alias1@icloud.com", "alias2@icloud.com"]}""");

        handler.HandleEvent(SocketEvents.IMessageAliasesRemoved, json, "Test");

        Assert.NotNull(received);
        Assert.Equal(2, received!.Count);
        Assert.Contains("alias1@icloud.com", received);
    }

    [Fact]
    public void ShouldNotifyForNewMessageGuid_FirstCall_ReturnsTrue()
    {
        var handler = new ActionHandler();
        Assert.True(handler.ShouldNotifyForNewMessageGuid("msg-001"));
    }

    [Fact]
    public void ShouldNotifyForNewMessageGuid_DuplicateCall_ReturnsFalse()
    {
        var handler = new ActionHandler();
        handler.ShouldNotifyForNewMessageGuid("msg-001");
        Assert.False(handler.ShouldNotifyForNewMessageGuid("msg-001"));
    }

    [Fact]
    public void ShouldNotifyForNewMessageGuid_TrimsToLast100()
    {
        var handler = new ActionHandler();

        for (int i = 0; i < 110; i++)
            handler.ShouldNotifyForNewMessageGuid($"msg-{i}");

        // First 10 should have been trimmed, so they should return true again
        Assert.True(handler.ShouldNotifyForNewMessageGuid("msg-0"));
        Assert.True(handler.ShouldNotifyForNewMessageGuid("msg-9"));
        // Recent ones should still be tracked
        Assert.False(handler.ShouldNotifyForNewMessageGuid("msg-109"));
    }

    [Fact]
    public async Task NewMessage_FromMe_NoTempGuid_DelaysBeforeFiring()
    {
        var handler = new ActionHandler();
        MessageEventArgs? received = null;
        handler.NewMessageReceived += (_, e) => received = e;

        var json = Parse("""
        {
            "data": {
                "originalROWID": 5,
                "guid": "msg-mine",
                "text": "My message",
                "isFromMe": true,
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
                "isBookmarked": false
            }
        }
        """);

        handler.HandleEvent(SocketEvents.NewMessage, json, "Test");

        // Should not fire immediately (500ms delay for out-of-order handling)
        Assert.Null(received);
        Assert.True(handler.ContainsOutOfOrderGuid("msg-mine"));

        // Wait for the delay
        await Task.Delay(700);
        Assert.NotNull(received);
        Assert.Equal("msg-mine", received!.Message.Guid);
    }

    [Fact]
    public void NewMessage_FromMe_WithTempGuid_FiresImmediately()
    {
        var handler = new ActionHandler();
        // Pre-populate out-of-order list to simulate the race condition
        handler.AddOutOfOrderGuid("msg-matched");

        MessageEventArgs? received = null;
        handler.NewMessageReceived += (_, e) => received = e;

        var json = Parse("""
        {
            "data": {
                "originalROWID": 6,
                "guid": "msg-matched",
                "text": "Matched message",
                "isFromMe": true,
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
                "isBookmarked": false
            },
            "tempGuid": "temp-matched"
        }
        """);

        handler.HandleEvent(SocketEvents.NewMessage, json, "Test");

        Assert.NotNull(received);
        Assert.Equal("msg-matched", received!.Message.Guid);
        Assert.False(handler.ContainsOutOfOrderGuid("msg-matched"));
    }

    [Fact]
    public void UnknownEvent_DoesNotThrow()
    {
        var handler = new ActionHandler();
        var json = Parse("""{"some": "data"}""");

        var exception = Record.Exception(() =>
            handler.HandleEvent("unknown-event", json, "Test"));

        Assert.Null(exception);
    }

    private const string ScheduledMessageJson = """
    {
        "id": 12,
        "type": "send-message",
        "payload": {
            "chatGuid": "iMessage;-;+11234567890",
            "message": "Happy birthday!",
            "method": "private-api"
        },
        "scheduledFor": "2026-06-11T15:30:00.000Z",
        "schedule": { "type": "once" },
        "status": "pending",
        "created": "2026-06-10T15:30:00.000Z"
    }
    """;

    [Fact]
    public void ScheduledMessageCreated_FiresEvent_WithSingleMessage()
    {
        var handler = new ActionHandler();
        ScheduledMessagesEventArgs? received = null;
        handler.ScheduledMessagesChanged += (_, e) => received = e;

        handler.HandleEvent(SocketEvents.ScheduledMessageCreated,
            Parse(ScheduledMessageJson), "Test");

        Assert.NotNull(received);
        Assert.Equal(SocketEvents.ScheduledMessageCreated, received!.EventType);
        Assert.Single(received.Messages);
        Assert.Equal(12, received.Messages[0].Id);
        Assert.Equal("iMessage;-;+11234567890", received.Messages[0].Payload!.ChatGuid);
    }

    [Fact]
    public void ScheduledMessageSent_FiresEvent_WhenWrappedInData()
    {
        var handler = new ActionHandler();
        ScheduledMessagesEventArgs? received = null;
        handler.ScheduledMessagesChanged += (_, e) => received = e;

        handler.HandleEvent(SocketEvents.ScheduledMessageSent,
            Parse($$"""{ "data": {{ScheduledMessageJson}} }"""), "Test");

        Assert.NotNull(received);
        Assert.Equal(SocketEvents.ScheduledMessageSent, received!.EventType);
        Assert.Single(received.Messages);
        Assert.Equal(12, received.Messages[0].Id);
    }

    [Fact]
    public void ScheduledMessageDeleted_FiresEvent_WithList()
    {
        var handler = new ActionHandler();
        ScheduledMessagesEventArgs? received = null;
        handler.ScheduledMessagesChanged += (_, e) => received = e;

        handler.HandleEvent(SocketEvents.ScheduledMessageDeleted,
            Parse($$"""[ {{ScheduledMessageJson}}, {{ScheduledMessageJson}} ]"""), "Test");

        Assert.NotNull(received);
        Assert.Equal(SocketEvents.ScheduledMessageDeleted, received!.EventType);
        Assert.Equal(2, received.Messages.Count);
    }
}
