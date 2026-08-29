using BlueBubbles.Core.Data.Entities;
using BlueBubbles.Core.Models;

namespace BlueBubbles.Core.Services;

public interface IChatsService
{
    IReadOnlyList<ChatWithParticipants> Chats { get; }
    IReadOnlyList<ChatWithParticipants> ArchivedChats { get; }

    event EventHandler? ChatsChanged;
    event EventHandler<string>? ChatUpdated;
    event EventHandler? ArchivedChatsChanged;

    /// <summary>Raised whenever messages have just been persisted for a chat, by *every* path that
    /// writes them — live socket, delta sync, history backfill, window reconcile, soft delete. The
    /// open thread listens to this to append from the DB, so the view stays in step with the database
    /// regardless of which path wrote the rows. Subscribers must filter on
    /// <see cref="MessagesPersistedEventArgs.Kind"/>: a backfill of old history is not a new message.</summary>
    event EventHandler<MessagesPersistedEventArgs>? MessagesPersisted;

    Task LoadChatsAsync();
    Task LoadArchivedChatsAsync();
    Task HandleNewMessageAsync(string chatGuid, string? messageText, long dateCreated, bool isFromMe, string? senderAddress = null);
    Task MarkChatReadAsync(string chatGuid, bool read, bool notifyServer = true);
    Task TogglePinAsync(string chatGuid);
    Task ReorderPinsAsync(List<string> chatGuids);
    Task ArchiveChatAsync(string chatGuid);
    Task UnarchiveChatAsync(string chatGuid);

    /// <summary>Deletes the chat on the server (which removes it from Messages on the Mac), then
    /// removes it from the local cache. Returns false — leaving local state untouched — when the
    /// server call fails, since a local-only delete would just be re-pulled by the next sync.</summary>
    Task<bool> DeleteChatAsync(string chatGuid);

    string? FindExistingChatGuid(IEnumerable<string> addresses);
    Task EnsureChatInDatabaseAsync(Chat chat, string? messageText);

    /// <summary>Creates a chat row from incoming socket payload data when it doesn't exist yet, and
    /// backfills participants if the stored set is empty. Create-only: never overwrites the metadata
    /// (pin/archive/mute/etc.) of an existing chat, since the live <c>new-message</c> payload carries
    /// only a sparse chat object. Without this, a brand-new chat someone else starts is silently
    /// dropped until a full incremental sync runs.</summary>
    Task EnsureChatExistsAsync(Chat chatData);

    /// <summary>Persists the chat carried by a <c>group-name-change</c> / <c>participant-*</c> socket
    /// event: server-owned fields via ChatFieldMerge, plus a full reconcile of the participant join
    /// rows (the payload's participant list is authoritative, so omissions are removals). Update-only —
    /// an unknown chat is ignored. Without this the events change nothing on disk and the conversation
    /// list keeps showing the old name/participants.</summary>
    Task ApplyChatUpdateAsync(Chat chatData);

    /// <summary>Announces that messages were just persisted for <paramref name="chatGuid"/>. Call it
    /// from the component that owns the transaction, once it has committed — a subscriber reads the
    /// database back, so announcing mid-write hands it the pre-change state.</summary>
    void NotifyMessagesPersisted(string chatGuid, MessagePersistKind kind);

    // Group management
    Task<bool> RenameChatAsync(string chatGuid, string newName);
    Task ToggleMuteAsync(string chatGuid);
    Task<bool> AddParticipantAsync(string chatGuid, string address);
    Task<bool> RemoveParticipantAsync(string chatGuid, string address);
    Task<bool> LeaveChatAsync(string chatGuid);
    Task<bool> SetChatIconAsync(string chatGuid, Stream iconStream, string fileName);
    Task<bool> DeleteChatIconAsync(string chatGuid);
}

public record ChatWithParticipants(
    ChatEntity Chat,
    List<HandleEntity> Participants,
    string? LastMessageText,
    List<HandleEntity>? RecentSenders = null,
    // Last-message delivery info, used by the optional "Send/receive indicators on chat list" tile badge.
    bool LastMessageIsFromMe = false,
    long? LastMessageDateDelivered = null,
    long? LastMessageDateRead = null);
