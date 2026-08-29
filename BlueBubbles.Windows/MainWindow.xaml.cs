using System.Runtime.InteropServices;
using BlueBubbles.Core.Configuration;
using BlueBubbles.Core.Diagnostics;
using BlueBubbles.Core.Services;
using BlueBubbles.Windows.Services;
using BlueBubbles.Windows.Views;
using BlueBubbles.Windows.Views.Setup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinRT.Interop;

namespace BlueBubbles.Windows;

public sealed partial class MainWindow : Window
{
    private const int DefaultWidth = 1024;
    private const int DefaultHeight = 768;
    private const int MinWidth = 640;

    // Deliberately low: a 10" tablet is only ~800 DIPs tall, and the docked touch keyboard eats
    // ~320 of them. The old 480 floor left the window taller than the space above the keyboard,
    // which clipped the composer. 360 still fits the title bar, chat header, composer, and a
    // readable slice of the thread. (Tunable.)
    private const int MinHeight = 360;

    private const int WM_GETMINMAXINFO = 0x0024;
    private const int WM_SIZE = 0x0005;
    private const int WM_POWERBROADCAST = 0x0218;
    private const int PBT_APMRESUMESUSPEND = 0x0007;
    private const int PBT_APMRESUMEAUTOMATIC = 0x0012;
    private const int SIZE_MINIMIZED = 1;
    private const int GWLP_WNDPROC = -4;
    private const int SW_HIDE = 0;
    private const int SW_RESTORE = 9;
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private readonly WndProcDelegate _wndProcDelegate;
    private readonly IntPtr _oldWndProc;
    internal readonly IntPtr _hWnd;

    private SystemTrayService? _trayService;
    private bool _isClosingForReal;

    // Last known non-minimized maximized state, so closing from the taskbar while minimized still
    // remembers that the window was maximized.
    private bool _wasMaximized;
    private DispatcherQueueTimer? _placementSaveTimer;

    /// <summary>Raised when the machine wakes from sleep/hibernate (WM_POWERBROADCAST resume). Used to
    /// kick connection-health + delta-sync recovery, since a socket can sit half-open after sleep.</summary>
    public event EventHandler? SystemResumed;

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");

        _hWnd = WindowNative.GetWindowHandle(this);

        _wndProcDelegate = WndProc;
        _oldWndProc = SetWindowLongPtr(
            _hWnd, GWLP_WNDPROC,
            Marshal.GetFunctionPointerForDelegate(_wndProcDelegate));

        RestoreOrSetDefaultPlacement();

        var settings = App.Services.GetRequiredService<AppSettings>();
        if (Content is FrameworkElement root)
            root.RequestedTheme = ThemeHelper.ToElementTheme(settings.Theme);
        RootFrame.Navigate(settings.FinishedSetup ? typeof(ShellPage) : typeof(SetupPage));

        Closed += OnClosed;
        Activated += OnActivated;
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated) return;

        // Focus regain is a good moment to catch a conversation deleted elsewhere (the server pushes
        // no socket event for it). Best-effort/immediate; the foreground poll is the reliable backstop.
        App.RequestChatReconcile();

        // Regaining focus while a chat is on screen means you're now reading it — mark it read (which
        // also clears its toasts). While the window was unfocused we deliberately leave the on-screen
        // chat unread so its notification can fire (punchlist N1), so we reconcile the read here on
        // focus rather than on message arrival. Guarded on the unread flag so we don't ping the server's
        // read endpoint on every focus change.
        var activeChats = App.Services.GetRequiredService<IWindowStateService>().ActiveChatGuids;
        if (activeChats.Count == 0) return;

        // A merged conversation has several underlying chats on screen — reconcile the read on each.
        var chats = App.Services.GetRequiredService<IChatsService>();
        foreach (var guid in activeChats)
        {
            var chat = chats.Chats.FirstOrDefault(c => c.Chat.Guid == guid);
            if (chat?.Chat.HasUnreadMessage == true)
                _ = chats.MarkChatReadAsync(guid, true);
        }
    }

    public Frame RootNavigationFrame => RootFrame;

    internal void InitializeTrayService(SystemTrayService trayService)
    {
        _trayService = trayService;

        var iconPath = Path.Combine(
            AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        _trayService.Initialize(_hWnd, iconPath);

        _trayService.ShowRequested += (_, _) => RestoreFromTray();
        _trayService.SettingsRequested += (_, _) =>
        {
            RestoreFromTray();
            if (RootFrame.Content is ShellPage shell)
                shell.NavigateToSettings();
        };
        _trayService.QuitRequested += (_, _) => QuitApplication();
    }

    public void RestoreFromTray()
    {
        ShowWindow(_hWnd, SW_RESTORE);
        SetForegroundWindow(_hWnd);
        Activate();

        // Win32 hide/show bypasses Window.Activated, so reconcile the chat list explicitly here to
        // catch a conversation deleted on the server while we were in the tray.
        App.RequestChatReconcile();
    }

    /// <summary>Hides the window to the system tray (used for launch-at-startup-minimized).</summary>
    public void HideToTray()
    {
        SavePlacement();
        ShowWindow(_hWnd, SW_HIDE);
    }

    public void QuitApplication()
    {
        _isClosingForReal = true;
        _trayService?.Dispose();
        Close();
    }

    /// <summary>Removes the tray icon without closing the window. Used before
    /// <c>AppInstance.Restart</c>, which terminates the process without running Closed handlers
    /// and would otherwise leave a ghost icon beside the restarted instance's.</summary>
    internal void RemoveTrayIcon()
    {
        // Same reason: no Closed handler runs, so persist placement before we lose the chance.
        SavePlacement();
        _trayService?.Dispose();
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_GETMINMAXINFO)
        {
            var dpi = GetDpiForWindow(hWnd);
            var scale = dpi / 96.0;
            var info = Marshal.PtrToStructure<MINMAXINFO>(lParam);

            var minWidth = (int)(MinWidth * scale);
            var minHeight = (int)(MinHeight * scale);

            // Never demand more room than the display actually offers. The docked touch keyboard
            // reserves screen space, so the work area shrinks while it's up and Windows resizes the
            // window to fit above it. On a small high-DPI tablet (Surface Go: 1800x1200 @150%, i.e.
            // 1200x800 DIPs) our nominal 480-DIP minimum is taller than what's left, so the clamp
            // blocked the shrink and the bottom of the app -- the composer -- stayed under the
            // keyboard. Clamping to the work area lets the window get out of the keyboard's way.
            if (TryGetWorkAreaSize(hWnd, out var workWidth, out var workHeight))
            {
                minWidth = Math.Min(minWidth, workWidth);
                minHeight = Math.Min(minHeight, workHeight);
            }

            info.ptMinTrackSize.X = minWidth;
            info.ptMinTrackSize.Y = minHeight;
            Marshal.StructureToPtr(info, lParam, true);
            return IntPtr.Zero;
        }

        if (msg == (uint)SystemTrayService.WM_TRAYICON)
        {
            _trayService?.HandleTrayMessage(lParam);
            return IntPtr.Zero;
        }

        // Explorer (re)started: its new taskbar has no notification icons, so re-add ours or it's
        // gone until the app restarts. Fall through to the default proc like other broadcasts.
        if (msg == SystemTrayService.WM_TASKBARCREATED)
            _trayService?.HandleTaskbarCreated();

        if (msg == WM_POWERBROADCAST &&
            ((int)wParam == PBT_APMRESUMESUSPEND || (int)wParam == PBT_APMRESUMEAUTOMATIC))
        {
            SystemResumed?.Invoke(this, EventArgs.Empty);
            // Don't return — let the default proc see it too.
        }

        if (msg == WM_SIZE && (int)wParam == SIZE_MINIMIZED)
        {
            var settings = App.Services.GetRequiredService<AppSettings>();
            if (settings.MinimizeToTray && _trayService is not null)
            {
                ShowWindow(hWnd, SW_HIDE);
                return IntPtr.Zero;
            }
        }

        return CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
    }

    /// <summary>Work area of the monitor the window sits on, in physical pixels. Shrinks while the
    /// docked touch keyboard is up. Raw P/Invoke rather than <see cref="DisplayArea"/> because this
    /// is also called from WM_GETMINMAXINFO, which arrives before AppWindow exists.</summary>
    private static bool TryGetWorkAreaSize(IntPtr hWnd, out int width, out int height)
    {
        width = height = 0;

        var monitor = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return false;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return false;

        width = info.rcWork.Right - info.rcWork.Left;
        height = info.rcWork.Bottom - info.rcWork.Top;
        return width > 0 && height > 0;
    }

    /// <summary>Caps a requested window size to the current work area so we never open larger than
    /// the screen. The 1024x768 default is 1536x1152 physical at 150% scale, which overflows a
    /// 1800x1200 tablet display vertically.</summary>
    private SizeInt32 ClampToWorkArea(int width, int height) =>
        TryGetWorkAreaSize(_hWnd, out var workWidth, out var workHeight)
            ? new SizeInt32(Math.Min(width, workWidth), Math.Min(height, workHeight))
            : new SizeInt32(width, height);

    private void RestoreOrSetDefaultPlacement()
    {
        var appSettings = App.Services.GetRequiredService<AppSettings>();

        if (appSettings.WindowWidth > 0 && appSettings.WindowHeight > 0)
        {
            AppWindow.Resize(ClampToWorkArea(appSettings.WindowWidth, appSettings.WindowHeight));

            var checkPoint = new PointInt32(appSettings.WindowX + 50, appSettings.WindowY + 50);
            var display = DisplayArea.GetFromPoint(checkPoint, DisplayAreaFallback.None);
            if (display is not null)
            {
                AppWindow.Move(new PointInt32(appSettings.WindowX, appSettings.WindowY));
            }
        }
        else
        {
            var dpi = GetDpiForWindow(_hWnd);
            var scale = dpi / 96.0;
            AppWindow.Resize(ClampToWorkArea((int)(DefaultWidth * scale), (int)(DefaultHeight * scale)));
        }

        // Restore bounds are applied first so un-maximizing returns to the right size and place.
        if (appSettings.WindowMaximized && AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
            _wasMaximized = true;
        }

        // Placement is now known-good; from here on, changes are what we persist.
        AppWindow.Changed += OnAppWindowChanged;
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidPositionChange && !args.DidSizeChange && !args.DidPresenterChange) return;

        CapturePlacement();

        // Persist shortly after things settle rather than only on close. A tablet can have the app
        // terminated without a clean shutdown, and everything since the last save would be lost —
        // which is how the window "forgot" its size. Debounced so a drag-resize writes once.
        _placementSaveTimer ??= CreatePlacementSaveTimer();
        _placementSaveTimer.Stop();
        _placementSaveTimer.Start();
    }

    private DispatcherQueueTimer CreatePlacementSaveTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            _placementSaveTimer?.Stop();
            App.Services.GetRequiredService<ISettingsService>().Save();
        };
        return timer;
    }

    /// <summary>Copies the window's placement into settings, in memory.
    /// <para>Only records bounds while the window is in its normal state: <see cref="AppWindow.Size"/>
    /// reports the MAXIMIZED bounds when maximized, and saving those as the restore bounds is what
    /// made a maximized-then-closed window reopen as a normal window sized to look maximized. A
    /// maximized window keeps whatever restore bounds it had, which is what it will return to.</para></summary>
    private void CapturePlacement()
    {
        var appSettings = App.Services.GetRequiredService<AppSettings>();

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            switch (presenter.State)
            {
                // Nothing reliable to read while minimized, and it says nothing about what the
                // window will restore to — keep the last known state.
                case OverlappedPresenterState.Minimized:
                    appSettings.WindowMaximized = _wasMaximized;
                    return;
                case OverlappedPresenterState.Maximized:
                    _wasMaximized = true;
                    appSettings.WindowMaximized = true;
                    return;
                default:
                    _wasMaximized = false;
                    break;
            }
        }

        appSettings.WindowMaximized = false;

        var size = AppWindow.Size;
        if (size.Width <= 0 || size.Height <= 0) return;
        appSettings.WindowX = AppWindow.Position.X;
        appSettings.WindowY = AppWindow.Position.Y;
        appSettings.WindowWidth = size.Width;
        appSettings.WindowHeight = size.Height;
    }

    private void SavePlacement()
    {
        CapturePlacement();
        _placementSaveTimer?.Stop();
        App.Services.GetRequiredService<ISettingsService>().Save();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        var appSettings = App.Services.GetRequiredService<AppSettings>();

        // Draw-timing rollup (B2b) — written on the way out so a session that was never dumped by
        // hand still leaves its numbers in the log. Inert when verbose logging is off.
        if (PerfStats.IsEnabled) PerfStats.Dump("Perf summary (session, at close)");

        if (appSettings.CloseToTray && _trayService is not null && !_isClosingForReal)
        {
            args.Handled = true;
            // Save here too: closing to the tray previously only updated settings in memory, so a
            // process that never got a clean shutdown lost its placement entirely.
            SavePlacement();
            ShowWindow(_hWnd, SW_HIDE);
            return;
        }

        SavePlacement();
        _trayService?.Dispose();
    }
}
