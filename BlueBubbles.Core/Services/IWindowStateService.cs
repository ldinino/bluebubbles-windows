namespace BlueBubbles.Core.Services;

public interface IWindowStateService
{
    bool IsWindowFocused { get; }
    string? ActiveChatGuid { get; }

    void SetActiveChatGuid(string? chatGuid);
}
