using System.Runtime.InteropServices;

namespace BlueBubbles.Windows.Services;

internal sealed class SystemTrayService : IDisposable
{
    private const int WM_APP = 0x8000;
    internal const int WM_TRAYICON = WM_APP + 1;

    private const int NIM_ADD = 0x00;
    private const int NIM_MODIFY = 0x01;
    private const int NIM_DELETE = 0x02;

    private const int NIF_MESSAGE = 0x01;
    private const int NIF_ICON = 0x02;
    private const int NIF_TIP = 0x04;
    private const int NIF_SHOWTIP = 0x10;

    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;

    private const int MF_STRING = 0x00;
    private const int MF_SEPARATOR = 0x0800;
    private const int TPM_RIGHTBUTTON = 0x0002;
    private const int TPM_RETURNCMD = 0x0100;

    private const int IDM_SHOW = 1001;
    private const int IDM_SETTINGS = 1002;
    private const int IDM_QUIT = 1003;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool InsertMenu(IntPtr hMenu, uint uPosition, uint uFlags, nuint uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadImage(IntPtr hInst, string name, uint uType, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x0010;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private NOTIFYICONDATA _nid;
    private IntPtr _hWnd;
    private IntPtr _hIcon;
    private bool _created;

    public event EventHandler? ShowRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? QuitRequested;

    public void Initialize(IntPtr hWnd, string iconPath)
    {
        _hWnd = hWnd;
        _hIcon = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 16, 16, LR_LOADFROMFILE);

        _nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = hWnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP,
            uCallbackMessage = (uint)WM_TRAYICON,
            hIcon = _hIcon,
            szTip = "BlueBubbles"
        };

        Shell_NotifyIcon(NIM_ADD, ref _nid);
        _created = true;
    }

    public void UpdateTooltip(string tooltip)
    {
        if (!_created) return;
        _nid.szTip = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        Shell_NotifyIcon(NIM_MODIFY, ref _nid);
    }

    public bool HandleTrayMessage(IntPtr lParam)
    {
        var msg = (int)(lParam & 0xFFFF);

        switch (msg)
        {
            case WM_LBUTTONDBLCLK:
                ShowRequested?.Invoke(this, EventArgs.Empty);
                return true;

            case WM_RBUTTONUP:
                ShowContextMenu();
                return true;
        }

        return false;
    }

    private void ShowContextMenu()
    {
        var hMenu = CreatePopupMenu();
        InsertMenu(hMenu, 0, MF_STRING, IDM_SHOW, "Show");
        InsertMenu(hMenu, 1, MF_STRING, IDM_SETTINGS, "Settings");
        InsertMenu(hMenu, 2, MF_SEPARATOR, 0, string.Empty);
        InsertMenu(hMenu, 3, MF_STRING, IDM_QUIT, "Quit");

        SetForegroundWindow(_hWnd);
        GetCursorPos(out var pt);
        var cmd = TrackPopupMenu(hMenu, TPM_RIGHTBUTTON | TPM_RETURNCMD, pt.X, pt.Y, 0, _hWnd, IntPtr.Zero);
        DestroyMenu(hMenu);

        switch (cmd)
        {
            case IDM_SHOW:
                ShowRequested?.Invoke(this, EventArgs.Empty);
                break;
            case IDM_SETTINGS:
                SettingsRequested?.Invoke(this, EventArgs.Empty);
                break;
            case IDM_QUIT:
                QuitRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    public void Dispose()
    {
        if (_created)
        {
            Shell_NotifyIcon(NIM_DELETE, ref _nid);
            _created = false;
        }

        if (_hIcon != IntPtr.Zero)
        {
            DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }
    }
}
