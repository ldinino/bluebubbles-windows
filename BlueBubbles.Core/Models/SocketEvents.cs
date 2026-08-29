using System.Text.Json;

namespace BlueBubbles.Core.Models;

public static class SocketEvents
{
    public const string NewMessage = "new-message";
    public const string UpdatedMessage = "updated-message";
    public const string TypingIndicator = "typing-indicator";
    public const string ChatReadStatusChanged = "chat-read-status-changed";
    public const string GroupNameChange = "group-name-change";
    public const string ParticipantAdded = "participant-added";
    public const string ParticipantRemoved = "participant-removed";
    public const string ParticipantLeft = "participant-left";
    public const string IncomingFacetime = "incoming-facetime";
    public const string FtCallStatusChanged = "ft-call-status-changed";
    public const string IMessageAliasesRemoved = "imessage-aliases-removed";
    public const string ScheduledMessageCreated = "scheduled-message-created";
    public const string ScheduledMessageUpdated = "scheduled-message-updated";
    public const string ScheduledMessageDeleted = "scheduled-message-deleted";
    public const string ScheduledMessageSent = "scheduled-message-sent";
    public const string ScheduledMessageError = "scheduled-message-error";
}

public enum SocketState
{
    Disconnected,
    Connecting,
    Connected,
    Error
}

public record TypingIndicatorPayload(bool Display, string Guid);

public record ChatReadStatusPayload(string ChatGuid, bool Read);

public record MessageEventArgs(Message Message, string? TempGuid);

/// <summary>A tapback (reaction) message together with its resolved (prefix-stripped) parent GUID.</summary>
public record ReactionEventArgs(Message Reaction, string ParentGuid);

/// <summary>Chat is the chat carried by the payload (the server emits these events as a serialized
/// message whose <c>chats</c> array holds the freshly-loaded chat, participants included), or null
/// when it couldn't be parsed.</summary>
public record ChatUpdatedEventArgs(string EventType, JsonElement Data, Chat? Chat = null)
{
    /// <summary>Transport-neutral classification of <see cref="EventType"/>, for UI consumers.</summary>
    public ChatUpdateKind Kind => ChatUpdateKinds.FromEventName(EventType);
}

/// <summary>
/// EventType is the raw scheduled-message socket event name; Messages is normalized to a list
/// (the deleted event arrives as an array, the rest as a single object).
/// </summary>
public record ScheduledMessagesEventArgs(string EventType, List<ScheduledMessage> Messages);
