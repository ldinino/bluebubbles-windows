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

public record ChatUpdatedEventArgs(string EventType, JsonElement Data);
