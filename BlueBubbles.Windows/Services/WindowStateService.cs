using System.Runtime.InteropServices;
using BlueBubbles.Core.Services;

namespace BlueBubbles.Windows.Services;

internal sealed class WindowStateService : IWindowStateService
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private IntPtr _hWnd;
    private volatile HashSet<string> _activeChats = new(StringComparer.OrdinalIgnoreCase);

    public bool IsWindowFocused => _hWnd != IntPtr.Zero && GetForegroundWindow() == _hWnd;

    public string? ActiveChatGuid => _activeChats.FirstOrDefault();

    public IReadOnlyCollection<string> ActiveChatGuids => _activeChats;

    public bool IsChatActive(string? chatGuid) => chatGuid is not null && _activeChats.Contains(chatGuid);

    public void SetWindowHandle(IntPtr hWnd) => _hWnd = hWnd;

    public void SetActiveChatGuid(string? chatGuid)
        => SetActiveChats(chatGuid is null ? null : new[] { chatGuid });

    public void SetActiveChats(IEnumerable<string>? chatGuids)
        => _activeChats = chatGuids is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(chatGuids, StringComparer.OrdinalIgnoreCase);
}
