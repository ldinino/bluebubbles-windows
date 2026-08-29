namespace BlueBubbles.Core.Models;

/// <summary>Transport-neutral classification of a chat-update event, so the UI branches on intent
/// instead of on a wire event name.</summary>
public enum ChatUpdateKind
{
    Unknown,
    GroupNameChanged,
    ParticipantAdded,
    ParticipantRemoved,
    ParticipantLeft
}

public static class ChatUpdateKinds
{
    public static ChatUpdateKind FromEventName(string? eventName) => eventName switch
    {
        SocketEvents.GroupNameChange => ChatUpdateKind.GroupNameChanged,
        SocketEvents.ParticipantAdded => ChatUpdateKind.ParticipantAdded,
        SocketEvents.ParticipantRemoved => ChatUpdateKind.ParticipantRemoved,
        SocketEvents.ParticipantLeft => ChatUpdateKind.ParticipantLeft,
        _ => ChatUpdateKind.Unknown
    };

    /// <summary>True when the update changes who is in the chat, so participants must be re-read.</summary>
    public static bool IsParticipantChange(this ChatUpdateKind kind) =>
        kind is ChatUpdateKind.ParticipantAdded
             or ChatUpdateKind.ParticipantRemoved
             or ChatUpdateKind.ParticipantLeft;
}
