using System.Collections.Concurrent;
using System.Text.Json;
using BlueBubbles.Core.Models;
using BlueBubbles.Core.Utils;

namespace BlueBubbles.Core.Services;

public class ActionHandler : IActionHandler
{
    private const int MaxHandledMessages = 100;

    private readonly ConcurrentDictionary<string, byte> _handledNewMessages = new();
    private readonly ConcurrentQueue<string> _handledNewMessagesOrder = new();

    private readonly ConcurrentDictionary<string, byte> _outOfOrderTempGuids = new();

    public event EventHandler<MessageEventArgs>? NewMessageReceived;
    public event EventHandler<MessageEventArgs>? MessageUpdated;
    public event EventHandler<ReactionEventArgs>? ReactionReceived;
    public event EventHandler<TypingIndicatorPayload>? TypingIndicatorChanged;
    public event EventHandler<ChatReadStatusPayload>? ChatReadStatusChanged;
    public event EventHandler<ChatUpdatedEventArgs>? ChatUpdated;
    public event EventHandler<JsonElement>? IncomingFaceTime;
    public event EventHandler<JsonElement>? FaceTimeStatusChanged;
    public event EventHandler<List<string>>? AliasesRemoved;
    public event EventHandler<ScheduledMessagesEventArgs>? ScheduledMessagesChanged;

    public bool ShouldNotifyForNewMessageGuid(string guid)
    {
        if (!_handledNewMessages.TryAdd(guid, 0)) return false;

        _handledNewMessagesOrder.Enqueue(guid);
        while (_handledNewMessages.Count > MaxHandledMessages &&
               _handledNewMessagesOrder.TryDequeue(out var oldest))
            _handledNewMessages.TryRemove(oldest, out _);

        return true;
    }

    public void AddOutOfOrderGuid(string guid) => _outOfOrderTempGuids.TryAdd(guid, 0);
    public bool RemoveOutOfOrderGuid(string guid) => _outOfOrderTempGuids.TryRemove(guid, out _);
    public bool ContainsOutOfOrderGuid(string guid) => _outOfOrderTempGuids.ContainsKey(guid);

    public void HandleEvent(string eventName, JsonElement data, string source)
    {
        switch (eventName)
        {
            case SocketEvents.NewMessage:
                HandleNewMessage(data);
                break;
            case SocketEvents.UpdatedMessage:
                HandleUpdatedMessage(data);
                break;
            case SocketEvents.TypingIndicator:
                var typing = data.Deserialize<TypingIndicatorPayload>(JsonDefaults.Options);
                if (typing is not null)
                    TypingIndicatorChanged?.Invoke(this, typing);
                break;
            case SocketEvents.ChatReadStatusChanged:
                HandleChatReadStatus(data);
                break;
            case SocketEvents.GroupNameChange:
            case SocketEvents.ParticipantAdded:
            case SocketEvents.ParticipantRemoved:
            case SocketEvents.ParticipantLeft:
                ChatUpdated?.Invoke(this, new ChatUpdatedEventArgs(eventName, data, ExtractChat(data)));
                break;
            case SocketEvents.IncomingFacetime:
                IncomingFaceTime?.Invoke(this, data);
                break;
            case SocketEvents.FtCallStatusChanged:
                FaceTimeStatusChanged?.Invoke(this, data);
                break;
            case SocketEvents.IMessageAliasesRemoved:
                HandleAliasesRemoved(data);
                break;
            case SocketEvents.ScheduledMessageCreated:
            case SocketEvents.ScheduledMessageUpdated:
            case SocketEvents.ScheduledMessageDeleted:
            case SocketEvents.ScheduledMessageSent:
            case SocketEvents.ScheduledMessageError:
                HandleScheduledMessage(eventName, data);
                break;
        }
    }

    /// <summary>Pulls the chat out of a group-event payload. The server emits these as a serialized
    /// message (<c>chats[0]</c>, participants loaded), matching the Flutter client's
    /// <c>Chat.fromMap(data['chats'].first)</c>; a bare chat object is accepted as a fallback.</summary>
    private static Chat? ExtractChat(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;
        if (data.TryGetProperty("data", out var inner) && inner.ValueKind == JsonValueKind.Object)
            data = inner;

        JsonElement chatElement;
        if (data.TryGetProperty("chats", out var chats)
            && chats.ValueKind == JsonValueKind.Array && chats.GetArrayLength() > 0)
        {
            chatElement = chats[0];
        }
        else if (data.TryGetProperty("guid", out _))
        {
            chatElement = data;
        }
        else return null;

        try { return chatElement.Deserialize<Chat>(JsonDefaults.Options); }
        catch (JsonException ex)
        {
            AppLog.Warn(LogCategory.Socket, $"Could not parse chat from chat-updated payload: {ex.Message}");
            return null;
        }
    }

    private void HandleScheduledMessage(string eventName, JsonElement data)
    {
        // The deleted event carries an array, the others a single object; either form may be
        // wrapped in { "data": ... } depending on the server's emit path.
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("data", out var inner))
            data = inner;

        List<ScheduledMessage>? messages = data.ValueKind switch
        {
            JsonValueKind.Array => data.Deserialize<List<ScheduledMessage>>(JsonDefaults.Options),
            JsonValueKind.Object when data.Deserialize<ScheduledMessage>(JsonDefaults.Options) is { } single
                => [single],
            _ => null
        };

        if (messages is null or { Count: 0 }) return;
        ScheduledMessagesChanged?.Invoke(this, new ScheduledMessagesEventArgs(eventName, messages));
    }

    private void HandleNewMessage(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return;

        string? tempGuid = null;
        JsonElement messageData = data;

        if (data.TryGetProperty("data", out var innerData))
        {
            messageData = innerData;
            if (data.TryGetProperty("tempGuid", out var tg))
                tempGuid = tg.GetString();
        }

        var message = messageData.Deserialize<Message>(JsonDefaults.Options);
        if (message is null) return;

        // Tapbacks arrive as new-message events carrying an associated GUID + reaction type.
        // They route to their own channel rather than the message stream (no temp-guid/echo
        // dance — a from-me reaction echo is reconciled by GUID downstream).
        if (message.AssociatedMessageGuid is not null && ReactionTypes.IsReaction(message.AssociatedMessageType))
        {
            var parentGuid = ReactionTypes.NormalizeAssociatedGuid(message.AssociatedMessageGuid);
            if (parentGuid is not null)
                ReactionReceived?.Invoke(this, new ReactionEventArgs(message, parentGuid));
            return;
        }

        if (message.IsFromMe)
        {
            if (tempGuid is null)
            {
                AddOutOfOrderGuid(message.Guid);
                _ = DelayAndEmitNewMessage(message, tempGuid);
                return;
            }
            else
            {
                RemoveOutOfOrderGuid(message.Guid);
            }
        }

        NewMessageReceived?.Invoke(this, new MessageEventArgs(message, tempGuid));
    }

    private async Task DelayAndEmitNewMessage(Message message, string? tempGuid)
    {
        await Task.Delay(500);
        if (!ContainsOutOfOrderGuid(message.Guid)) return;
        NewMessageReceived?.Invoke(this, new MessageEventArgs(message, tempGuid));
    }

    private void HandleUpdatedMessage(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return;

        string? tempGuid = null;
        JsonElement messageData = data;

        if (data.TryGetProperty("data", out var innerData))
        {
            messageData = innerData;
            if (data.TryGetProperty("tempGuid", out var tg))
                tempGuid = tg.GetString();
        }

        var message = messageData.Deserialize<Message>(JsonDefaults.Options);
        if (message is null) return;

        MessageUpdated?.Invoke(this, new MessageEventArgs(message, tempGuid));
    }

    private void HandleChatReadStatus(JsonElement data)
    {
        if (!data.TryGetProperty("chatGuid", out var chatGuidEl)) return;
        if (!data.TryGetProperty("read", out var readEl)) return;

        var chatGuid = chatGuidEl.GetString();
        if (chatGuid is null) return;

        ChatReadStatusChanged?.Invoke(this, new ChatReadStatusPayload(chatGuid, readEl.GetBoolean()));
    }

    private void HandleAliasesRemoved(JsonElement data)
    {
        if (!data.TryGetProperty("aliases", out var aliasesEl)) return;

        var aliases = aliasesEl.Deserialize<List<string>>();
        if (aliases is not null)
            AliasesRemoved?.Invoke(this, aliases);
    }
}
