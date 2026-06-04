namespace BlueBubbles.Core.Services;

/// <summary>
/// Pure notification-display policy, split out of the platform <c>NotificationService</c> so the
/// rules are unit-testable without WinRT.
/// </summary>
public static class NotificationPolicy
{
    /// <summary>
    /// The window-state gate (punchlist N1): should an incoming-message toast be shown, given window
    /// focus and which chat (if any) is currently on screen?
    ///
    /// Rules:
    ///   - Window not focused (minimized, hidden in the tray, or another app in front) → always show.
    ///     This is the heart of N1: the active-chat suppression must NOT apply when the window isn't
    ///     the one being looked at.
    ///   - Window focused and the originating chat is the one on screen → suppress (you're reading it).
    ///   - Window focused on the chat list (no chat open) → show only if the user opted in via
    ///     <paramref name="notifyOnChatList"/>.
    ///   - Window focused on a different chat → show.
    /// </summary>
    public static bool ShouldShowForWindowState(
        bool isWindowFocused, string? activeChatGuid, string chatGuid, bool notifyOnChatList)
    {
        if (!isWindowFocused) return true;
        if (activeChatGuid == chatGuid) return false;
        if (activeChatGuid is null && !notifyOnChatList) return false;
        return true;
    }
}
