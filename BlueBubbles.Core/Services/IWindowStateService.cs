namespace BlueBubbles.Core.Services;

public interface IWindowStateService
{
    bool IsWindowFocused { get; }

    /// <summary>One of the chats currently on screen, or null. A merged conversation spans several
    /// underlying chats — see <see cref="ActiveChatGuids"/> for the full set.</summary>
    string? ActiveChatGuid { get; }

    /// <summary>Every chat GUID currently on screen. For a normal chat this is a single GUID; for a
    /// merged conversation it's all underlying chats, so notifications for any of them are suppressed.</summary>
    IReadOnlyCollection<string> ActiveChatGuids { get; }

    /// <summary>True when the given chat GUID is one of the chats currently on screen.</summary>
    bool IsChatActive(string? chatGuid);

    void SetActiveChatGuid(string? chatGuid);

    /// <summary>Sets the full set of chats currently on screen (a merged conversation's constituents).
    /// Null/empty clears it (e.g. on returning to the list).</summary>
    void SetActiveChats(IEnumerable<string>? chatGuids);
}
