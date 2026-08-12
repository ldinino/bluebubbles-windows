using System.Text.Json.Serialization;

namespace BlueBubbles.Core.Models;

public record Chat(
    [property: JsonPropertyName("guid")] string Guid,
    [property: JsonPropertyName("chatIdentifier")] string? ChatIdentifier,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("participants")] List<Handle>? Participants,
    [property: JsonPropertyName("lastMessage")] Message? LastMessage,
    [property: JsonPropertyName("isArchived")] bool IsArchived,
    [property: JsonPropertyName("isPinned")] bool IsPinned,
    // Nullable so "the payload said nothing" is distinguishable from "the payload said read":
    // group-event payloads omit it entirely, and false there would clear the unread badge.
    [property: JsonPropertyName("hasUnreadMessage")] bool? HasUnreadMessage,
    [property: JsonPropertyName("service")] string? Service,
    [property: JsonPropertyName("muteType")] string? MuteType,
    [property: JsonPropertyName("muteArgs")] string? MuteArgs,
    [property: JsonPropertyName("autoSendReadReceipts")] bool? AutoSendReadReceipts,
    [property: JsonPropertyName("autoSendTypingIndicators")] bool? AutoSendTypingIndicators,
    [property: JsonPropertyName("dateDeleted")] long? DateDeleted,
    [property: JsonPropertyName("style")] int? Style,
    [property: JsonPropertyName("lockChatName")] bool LockChatName,
    [property: JsonPropertyName("lockChatIcon")] bool LockChatIcon,
    [property: JsonPropertyName("lastReadMessageGuid")] string? LastReadMessageGuid,
    [property: JsonPropertyName("customAvatarPath")] string? CustomAvatarPath = null,
    [property: JsonPropertyName("pinIndex")] int? PinIndex = null
);
