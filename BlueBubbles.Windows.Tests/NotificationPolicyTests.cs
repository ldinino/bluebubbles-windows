using BlueBubbles.Core.Services;

namespace BlueBubbles.Windows.Tests;

public class NotificationPolicyTests
{
    private const string ChatA = "iMessage;-;+15551234567";
    private const string ChatB = "iMessage;-;+15559876543";

    // ── N1: when the window isn't focused, suppression never applies ──

    [Fact]
    public void NotFocused_ShowsEvenWhenOriginatingChatIsActive()
    {
        // The window is minimized/in the tray while chat A is the last-opened chat: a new message in
        // A must still notify. This is the bug N1 fixes.
        Assert.True(NotificationPolicy.ShouldShowForWindowState(
            isWindowFocused: false, activeChatGuid: ChatA, chatGuid: ChatA, notifyOnChatList: false));
    }

    [Fact]
    public void NotFocused_ShowsWhenOnList()
    {
        Assert.True(NotificationPolicy.ShouldShowForWindowState(
            isWindowFocused: false, activeChatGuid: null, chatGuid: ChatA, notifyOnChatList: false));
    }

    // ── Focused: suppress only the chat actually on screen ──

    [Fact]
    public void Focused_OnTheOriginatingChat_Suppresses()
    {
        Assert.False(NotificationPolicy.ShouldShowForWindowState(
            isWindowFocused: true, activeChatGuid: ChatA, chatGuid: ChatA, notifyOnChatList: true));
    }

    [Fact]
    public void Focused_OnADifferentChat_Shows()
    {
        Assert.True(NotificationPolicy.ShouldShowForWindowState(
            isWindowFocused: true, activeChatGuid: ChatB, chatGuid: ChatA, notifyOnChatList: false));
    }

    // ── Focused on the chat list (no chat open): honor the preference ──

    [Fact]
    public void Focused_OnList_NotifyOff_Suppresses()
    {
        Assert.False(NotificationPolicy.ShouldShowForWindowState(
            isWindowFocused: true, activeChatGuid: null, chatGuid: ChatA, notifyOnChatList: false));
    }

    [Fact]
    public void Focused_OnList_NotifyOn_Shows()
    {
        Assert.True(NotificationPolicy.ShouldShowForWindowState(
            isWindowFocused: true, activeChatGuid: null, chatGuid: ChatA, notifyOnChatList: true));
    }
}
