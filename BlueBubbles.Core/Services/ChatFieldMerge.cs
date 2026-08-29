using BlueBubbles.Core.Data;
using BlueBubbles.Core.Data.Entities;
using BlueBubbles.Core.Models;

namespace BlueBubbles.Core.Services;

/// <summary>
/// The single writer of <see cref="ChatEntity"/> columns, and the single authority on chat field
/// ownership for the server-authoritative sync model.
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
///
/// <para><b>A third category exists: server-owned but only when present.</b> <c>HasUnreadMessage</c>
/// is nullable on <see cref="Chat"/> and is copied only when the payload actually carries it. Not
/// every payload is a full chat record — the <c>group-name-change</c> / <c>participant-*</c> socket
/// events serialize the chat without a read-state field, and a non-nullable bool would deserialize
/// that silence to <c>false</c> and clear the user's unread badge on every rename. Silence means
/// "no opinion", not "read". Any future field the server sometimes omits belongs here too, guarded
/// the same way — never preserved by an inline exception at a call site.</para>
///
/// <para><b>Inserts go through <see cref="InsertFromServer"/> or
/// <see cref="InsertForLiveMessage"/>, never a hand-written object initializer.</b> Two of the four
/// insert paths used to construct the row inline with a five-field subset, so a chat first seen via
/// a live message or an incremental delta landed without its read-receipt/typing preferences, its
/// name/icon locks or its last-read marker. The two entry points differ only in the one decision an
/// insert is entitled to make, which is why the split is in the signature rather than in a
/// boolean the callers each interpret.</para>
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
        if (server.HasUnreadMessage is { } hasUnread) target.HasUnreadMessage = hasUnread;
        target.Service = server.Service;
        target.AutoSendReadReceipts = server.AutoSendReadReceipts;
        target.AutoSendTypingIndicators = server.AutoSendTypingIndicators;
        target.DateDeleted = server.DateDeleted;
        target.Style = server.Style;
        target.LockChatName = server.LockChatName;
        target.LockChatIcon = server.LockChatIcon;
        target.LastReadMessageGuid = server.LastReadMessageGuid;
    }

    /// <summary>Inserts a chat the server told us about, with no client opinion of its own: read
    /// state and delete stamp are the server's. Used by the sync paths and by local chat creation,
    /// which have a server record in hand and no live message. Caller owns the save.</summary>
    public static ChatEntity InsertFromServer(BlueBubblesDbContext db, Chat server)
    {
        var entity = new ChatEntity { Guid = server.Guid };
        ApplyServerOwnedFields(entity, server);
        db.Chats.Add(entity);
        return entity;
    }

    /// <summary>Inserts a chat that exists because a message for it just arrived. The server fields
    /// still come from <paramref name="server"/> when the payload carries one — the socket's
    /// <c>new-message</c> event and the delta's embedded chat can both be absent or sparse — but two
    /// columns are the client's call here and are applied last, on purpose:
    /// <list type="bullet">
    /// <item><c>HasUnreadMessage</c> is <c>true</c>. These payloads routinely omit
    /// <c>hasUnreadMessage</c>, and the merge (correctly) treats that silence as "no opinion", so
    /// leaving it to the merge would leave the column at its <c>false</c> default and new chats
    /// would stop showing as unread. That decision belongs to the insert, not to a weakened
    /// guard in the merge — the nullable guard is the B6 fix and must stay.</item>
    /// <item><c>DateDeleted</c> is cleared. We are holding a live message for this chat right now,
    /// so it is not deleted however stale the embedded record is — the same reasoning the
    /// existing-row branches already apply when they resurrect a soft-deleted chat.</item>
    /// </list>
    /// Caller owns the save.</summary>
    public static ChatEntity InsertForLiveMessage(BlueBubblesDbContext db, string guid, Chat? server)
    {
        var entity = new ChatEntity { Guid = guid };
        if (server is not null) ApplyServerOwnedFields(entity, server);
        entity.HasUnreadMessage = true;
        entity.DateDeleted = null;
        db.Chats.Add(entity);
        return entity;
    }
}
