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

    /// <summary>Raised with a chat GUID whenever messages have just been persisted for that chat —
    /// from the live socket or a delta sync. The open thread listens to this to append from the DB,
    /// so the view stays in step with the database regardless of which path wrote the rows.</summary>
    event EventHandler<string>? MessagesPersisted;

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

    /// <summary>Announces that messages were just persisted for <paramref name="chatGuid"/> so the
    /// open thread can append them from the DB. Used by the sync path, which writes straight to the
    /// database rather than through the live socket event.</summary>
    void NotifyMessagesPersisted(string chatGuid);

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
