using BlueBubbles.Core.Data.Entities;
using BlueBubbles.Core.Models;

namespace BlueBubbles.Core.Services;

public interface IMessagesService
{
    Task<List<MessageEntity>> LoadMessagesAsync(int chatId, int limit = 50, long? beforeDate = null);

    /// <summary>Loads messages across several chats unioned and interleaved by date (newest page first,
    /// returned ascending). Backs a merged conversation, whose history spans the underlying chats.</summary>
    Task<List<MessageEntity>> LoadMessagesAsync(IReadOnlyList<int> chatIds, int limit = 50, long? beforeDate = null);

    /// <summary>Loads a chat's messages newer than <paramref name="afterDate"/> (ascending). Used to
    /// catch an already-open thread up after a background delta sync persists messages the socket
    /// never pushed (it's silent while the app was asleep/disconnected). Excludes deleted rows and
    /// reactions (associated messages).</summary>
    Task<List<MessageEntity>> LoadMessagesAfterAsync(int chatId, long afterDate);

    /// <summary>Union of <see cref="LoadMessagesAfterAsync(int, long)"/> across several chats, for a
    /// merged conversation's post-sync catch-up.</summary>
    Task<List<MessageEntity>> LoadMessagesAfterAsync(IReadOnlyList<int> chatIds, long afterDate);
    Task<List<MessageEntity>> FetchOlderMessagesFromServerAsync(
        int chatId, string chatGuid, int limit = 25, CancellationToken ct = default);

    /// <summary>Safety net for chats that ended up empty locally (a missed/incomplete sync). When the
    /// chat has no local messages, fetches its newest page from the server and persists it so opening
    /// a chat never shows a permanently-blank thread. No-op when the chat already has messages.
    /// Returns true if any messages were fetched and saved.</summary>
    Task<bool> EnsureChatHydratedAsync(int chatId, string chatGuid, int limit = 50, CancellationToken ct = default);

    /// <summary>Re-fetches a chat's newest page from the server and upserts it. Recovers in-place
    /// mutations the ROWID-watermark delta sync structurally can't see — edits, unsends, and
    /// read/delivery receipts all update an existing row without changing its ROWID — so an open chat
    /// can be made fully correct after an offline window. Returns true if the server returned messages.</summary>
    Task<bool> RefreshLatestFromServerAsync(int chatId, string chatGuid, int limit = 50, CancellationToken ct = default);
    Task SaveIncomingMessageAsync(string chatGuid, Message message);

    /// <summary>Applies an in-place server update (edit, unsend, delivery/read receipt) to the cached
    /// row. Returns the GUID of the chat that owns the message, or null when the message isn't cached,
    /// so the caller can announce the persist for that chat.</summary>
    Task<string?> UpdateMessageAsync(Message message);

    /// <summary>Deletes the message on the server (which removes it from Messages on the Mac — note
    /// this is a delete, not an unsend; the recipient's copy is unaffected), then soft-deletes it
    /// locally (sets <c>DateDeleted</c>) so it is hidden from the chat view. Returns false — leaving
    /// local state untouched — when the server call fails, since a local-only delete would be
    /// overwritten by the next sync.</summary>
    Task<bool> DeleteMessageAsync(string chatGuid, string messageGuid);
    Task<List<AttachmentEntity>> LoadMediaAttachmentsAsync(int chatId, int limit = 50, int offset = 0);

    /// <summary>Union of <see cref="LoadMediaAttachmentsAsync(int, int, int)"/> across several chats, so
    /// a merged conversation's details shows media from every underlying chat.</summary>
    Task<List<AttachmentEntity>> LoadMediaAttachmentsAsync(IReadOnlyList<int> chatIds, int limit = 50, int offset = 0);

    /// <summary>Loads all stored reactions (associated messages with a type) targeting any of the
    /// given parent message GUIDs, oldest first. Includes the reacting handle.</summary>
    Task<List<MessageEntity>> LoadReactionsAsync(IReadOnlyCollection<string> parentGuids);

    /// <summary>Loads messages by GUID (with their handle), e.g. to resolve reply-thread originals.</summary>
    Task<List<MessageEntity>> GetMessagesByGuidsAsync(IReadOnlyCollection<string> guids);

    /// <summary>Persists an incoming reaction message and flags its parent as having reactions.</summary>
    Task SaveReactionAsync(string chatGuid, Message reaction);
}
