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

    Task LoadChatsAsync();
    Task LoadArchivedChatsAsync();
    Task HandleNewMessageAsync(string chatGuid, string? messageText, long dateCreated, bool isFromMe, string? senderAddress = null);
    Task MarkChatReadAsync(string chatGuid, bool read, bool notifyServer = true);
    Task TogglePinAsync(string chatGuid);
    Task ReorderPinsAsync(List<string> chatGuids);
    Task ArchiveChatAsync(string chatGuid);
    Task UnarchiveChatAsync(string chatGuid);
    Task DeleteChatAsync(string chatGuid);

    string? FindExistingChatGuid(IEnumerable<string> addresses);
    Task EnsureChatInDatabaseAsync(Chat chat, string? messageText);

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
