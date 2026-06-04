using BlueBubbles.Core.Data.Entities;
using BlueBubbles.Core.Models;

namespace BlueBubbles.Core.Services;

public interface IMessagesService
{
    Task<List<MessageEntity>> LoadMessagesAsync(int chatId, int limit = 50, long? beforeDate = null);
    Task<List<MessageEntity>> FetchOlderMessagesFromServerAsync(
        int chatId, string chatGuid, int limit = 25, CancellationToken ct = default);

    /// <summary>Safety net for chats that ended up empty locally (a missed/incomplete sync). When the
    /// chat has no local messages, fetches its newest page from the server and persists it so opening
    /// a chat never shows a permanently-blank thread. No-op when the chat already has messages.
    /// Returns true if any messages were fetched and saved.</summary>
    Task<bool> EnsureChatHydratedAsync(int chatId, string chatGuid, int limit = 50, CancellationToken ct = default);
    Task SaveIncomingMessageAsync(string chatGuid, Message message);
    Task UpdateMessageAsync(Message message);

    /// <summary>Marks a message as locally deleted (sets <c>DateDeleted</c>) so it is hidden from the
    /// chat view. This is a client-side delete only — it does not unsend the message on Apple's side.</summary>
    Task SoftDeleteMessageAsync(string messageGuid);
    Task<List<AttachmentEntity>> LoadMediaAttachmentsAsync(int chatId, int limit = 50, int offset = 0);

    /// <summary>Loads all stored reactions (associated messages with a type) targeting any of the
    /// given parent message GUIDs, oldest first. Includes the reacting handle.</summary>
    Task<List<MessageEntity>> LoadReactionsAsync(IReadOnlyCollection<string> parentGuids);

    /// <summary>Loads messages by GUID (with their handle), e.g. to resolve reply-thread originals.</summary>
    Task<List<MessageEntity>> GetMessagesByGuidsAsync(IReadOnlyCollection<string> guids);

    /// <summary>Persists an incoming reaction message and flags its parent as having reactions.</summary>
    Task SaveReactionAsync(string chatGuid, Message reaction);
}
