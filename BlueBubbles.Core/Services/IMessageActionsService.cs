using BlueBubbles.Core.Models;

namespace BlueBubbles.Core.Services;

public interface IMessageActionsService
{
    Task<ApiResponse<Message>> ReactAsync(string chatGuid,
        string selectedMessageText, string selectedMessageGuid,
        string reaction, int? partIndex = null);

    Task<ApiResponse<Message>> EditAsync(string messageGuid,
        string editedMessage, string backwardsCompatMessage,
        int partIndex = 0);

    Task<ApiResponse<Message>> UnsendAsync(string messageGuid,
        int partIndex = 0);

    Task ForwardAsync(string fromChatGuid, string messageGuid,
        string toChatGuid);
}
