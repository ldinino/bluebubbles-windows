using System.Runtime.InteropServices;
using BlueBubbles.Core.Services;

namespace BlueBubbles.Windows.Services;

internal sealed class WindowStateService : IWindowStateService
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private IntPtr _hWnd;

    public bool IsWindowFocused => _hWnd != IntPtr.Zero && GetForegroundWindow() == _hWnd;

    public string? ActiveChatGuid { get; private set; }

    public void SetWindowHandle(IntPtr hWnd) => _hWnd = hWnd;

    public void SetActiveChatGuid(string? chatGuid) => ActiveChatGuid = chatGuid;
}
