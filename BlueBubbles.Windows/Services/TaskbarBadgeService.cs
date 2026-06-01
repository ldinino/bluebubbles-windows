using System.Runtime.InteropServices;
using BlueBubbles.Core.Services;
using BlueBubbles.Windows.Helpers;

namespace BlueBubbles.Windows.Services;

internal sealed class TaskbarBadgeService : IDisposable
{
    [ComImport]
    [Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        // ITaskbarList
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        // ITaskbarList2
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);
        // ITaskbarList3
        void SetProgressValue(IntPtr hwnd, ulong ullCompleted, ulong ullTotal);
        void SetProgressState(IntPtr hwnd, int tbpFlags);
        void RegisterTab(IntPtr hwndTab, IntPtr hwndMDI);
        void UnregisterTab(IntPtr hwndTab);
        void SetTabOrder(IntPtr hwndTab, IntPtr hwndInsertBefore);
        void SetTabActive(IntPtr hwndTab, IntPtr hwndMDI, int dwReserved);
        void ThumbBarAddButtons(IntPtr hwnd, uint cButtons, IntPtr pButton);
        void ThumbBarUpdateButtons(IntPtr hwnd, uint cButtons, IntPtr pButton);
        void ThumbBarSetImageList(IntPtr hwnd, IntPtr himl);
        void SetOverlayIcon(IntPtr hwnd, IntPtr hIcon, [MarshalAs(UnmanagedType.LPWStr)] string? pszDescription);
        void SetThumbnailTooltip(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string? pszTip);
        void SetThumbnailClip(IntPtr hwnd, IntPtr prcClip);
    }

    [ComImport]
    [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    [ClassInterface(ClassInterfaceType.None)]
    private class TaskbarListClass { }

    private readonly IChatsService _chats;
    private ITaskbarList3? _taskbar;
    private IntPtr _hWnd;
    private int _lastCount;

    public TaskbarBadgeService(IChatsService chats)
    {
        _chats = chats;
        _chats.ChatsChanged += (_, _) => UpdateBadge();
        _chats.ChatUpdated += (_, _) => UpdateBadge();
    }

    public void Initialize(IntPtr hWnd)
    {
        _hWnd = hWnd;
        try
        {
            _taskbar = (ITaskbarList3)new TaskbarListClass();
            _taskbar.HrInit();
        }
        catch
        {
            _taskbar = null;
        }

        UpdateBadge();
    }

    private void UpdateBadge()
    {
        if (_taskbar is null || _hWnd == IntPtr.Zero) return;

        var count = _chats.Chats.Count(c => c.Chat.HasUnreadMessage);
        if (count == _lastCount) return;
        _lastCount = count;

        try
        {
            if (count == 0)
            {
                _taskbar.SetOverlayIcon(_hWnd, IntPtr.Zero, null);
            }
            else
            {
                var hIcon = BadgeIconRenderer.GetBadgeIcon(count);
                _taskbar.SetOverlayIcon(_hWnd, hIcon, $"{count} unread");
            }
        }
        catch { }
    }

    public void Dispose()
    {
        BadgeIconRenderer.ClearCache();
        if (_taskbar is not null && _hWnd != IntPtr.Zero)
        {
            try { _taskbar.SetOverlayIcon(_hWnd, IntPtr.Zero, null); }
            catch { }
        }
    }
}
