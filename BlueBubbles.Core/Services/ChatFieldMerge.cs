using BlueBubbles.Core.Data.Entities;
using BlueBubbles.Core.Models;

namespace BlueBubbles.Core.Services;

/// <summary>
/// The single authority on chat field ownership for the server-authoritative sync model.
///
/// <para>The server is the source of truth for everything it actually stores, so every chat
/// upsert (full sync, new-chat insert, delta) must funnel through <see cref="ApplyServerOwnedFields"/>
/// rather than copying fields inline — that is how a rename/read-state change made on another device
/// reaches this client, and how the rule stays greppable instead of drifting across call sites.</para>
///
/// <para><b>Client-owned fields are deliberately NOT written here</b> and must never be set from a
/// server payload: <c>IsArchived</c>, <c>IsPinned</c>, <c>PinIndex</c>, <c>MuteType</c>,
/// <c>MuteArgs</c>, <c>CustomAvatarPath</c>, and <c>OldestSyncedMessageDate</c>. The BlueBubbles
/// server has no endpoint for pin/mute/archive (only read/unread), so it always returns the
/// defaults (false/null); blindly copying them — as the old inline upserts did — wiped the user's
/// pins/mutes/archive on every sync. These are local UI preferences the server has no opinion on,
/// so they survive purely by omission from this method. <c>OldestSyncedMessageDate</c> is a local
/// pagination watermark, not server state.</para>
///
/// <para><c>LatestMessageDate</c> is intentionally left to the caller: it is derived from the
/// chat's last message (with a caller-specific fallback for brand-new local chats), not a direct
/// server field copy.</para>
/// </summary>
internal static class ChatFieldMerge
{
    /// <summary>Copies the server-owned fields from <paramref name="server"/> onto
    /// <paramref name="target"/>, leaving every client-owned field untouched. See the type doc for
    /// the ownership split.</summary>
    public static void ApplyServerOwnedFields(ChatEntity target, Chat server)
    {
        target.ChatIdentifier = server.ChatIdentifier;
        target.DisplayName = server.DisplayName;
        target.HasUnreadMessage = server.HasUnreadMessage;
        target.Service = server.Service;
        target.AutoSendReadReceipts = server.AutoSendReadReceipts;
        target.AutoSendTypingIndicators = server.AutoSendTypingIndicators;
        target.DateDeleted = server.DateDeleted;
        target.Style = server.Style;
        target.LockChatName = server.LockChatName;
        target.LockChatIcon = server.LockChatIcon;
        target.LastReadMessageGuid = server.LastReadMessageGuid;
    }
}
