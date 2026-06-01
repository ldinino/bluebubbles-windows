using BlueBubbles.Core.Models;

namespace BlueBubbles.Core.Services;

public interface IOutgoingMessageService
{
    event EventHandler<OutgoingMessageEvent>? MessageStateChanged;

    string EnqueueText(string chatGuid, string text, string? subject = null,
        string? effectId = null, string? selectedMessageGuid = null,
        int? partIndex = null, bool? ddScan = null);

    string EnqueueAttachment(string chatGuid, string filePath,
        string? subject = null, string? effectId = null,
        string? selectedMessageGuid = null, int? partIndex = null,
        bool? isAudioMessage = null);

    string EnqueueMultipart(string chatGuid,
        List<Dictionary<string, object?>> parts,
        string? effectId = null, string? subject = null,
        string? selectedMessageGuid = null, int? partIndex = null,
        bool? ddScan = null);

    Task<ApiResponse<Message>> SendTapbackAsync(string chatGuid,
        string selectedMessageText, string selectedMessageGuid,
        string reaction, int? partIndex = null);

    Task<ApiResponse<Message>> SendEditAsync(string messageGuid,
        string editedMessage, string backwardsCompatMessage,
        int partIndex = 0);

    Task<ApiResponse<Message>> SendUnsendAsync(string messageGuid,
        int partIndex = 0);

    void CancelPending(string tempGuid);
}

public record OutgoingMessageEvent(
    string TempGuid,
    string ChatGuid,
    OutgoingMessageState State,
    Message? ServerMessage = null,
    string? ErrorMessage = null);

public enum OutgoingMessageState
{
    Queued,
    Sending,
    Sent,
    Failed,
    Cancelled
}
