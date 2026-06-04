using System.Runtime.InteropServices;
using BlueBubbles.Core.Configuration;
using BlueBubbles.Core.Services;
using BlueBubbles.Windows.Services;
using BlueBubbles.Windows.Views;
using BlueBubbles.Windows.Views.Setup;
using Microsoft.Extensions.DependencyInjection;
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
    private const int MinHeight = 480;

    private const int WM_GETMINMAXINFO = 0x0024;
    private const int WM_SIZE = 0x0005;
    private const int WM_POWERBROADCAST = 0x0218;
    private const int PBT_APMRESUMESUSPEND = 0x0007;
    private const int PBT_APMRESUMEAUTOMATIC = 0x0012;
    private const int SIZE_MINIMIZED = 1;
    private const int GWLP_WNDPROC = -4;
    private const int SW_HIDE = 0;
    private const int SW_RESTORE = 9;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private readonly WndProcDelegate _wndProcDelegate;
    private readonly IntPtr _oldWndProc;
    internal readonly IntPtr _hWnd;

    private SystemTrayService? _trayService;
    private bool _isClosingForReal;

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

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

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
    }

    /// <summary>Hides the window to the system tray (used for launch-at-startup-minimized).</summary>
    public void HideToTray() => ShowWindow(_hWnd, SW_HIDE);

    public void QuitApplication()
    {
        _isClosingForReal = true;
        _trayService?.Dispose();
        Close();
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_GETMINMAXINFO)
        {
            var dpi = GetDpiForWindow(hWnd);
            var scale = dpi / 96.0;
            var info = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            info.ptMinTrackSize.X = (int)(MinWidth * scale);
            info.ptMinTrackSize.Y = (int)(MinHeight * scale);
            Marshal.StructureToPtr(info, lParam, true);
            return IntPtr.Zero;
        }

        if (msg == (uint)SystemTrayService.WM_TRAYICON)
        {
            _trayService?.HandleTrayMessage(lParam);
            return IntPtr.Zero;
        }

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

    private void RestoreOrSetDefaultPlacement()
    {
        var appSettings = App.Services.GetRequiredService<AppSettings>();

        if (appSettings.WindowWidth > 0 && appSettings.WindowHeight > 0)
        {
            AppWindow.Resize(new SizeInt32(appSettings.WindowWidth, appSettings.WindowHeight));

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
            AppWindow.Resize(new SizeInt32((int)(DefaultWidth * scale), (int)(DefaultHeight * scale)));
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        var appSettings = App.Services.GetRequiredService<AppSettings>();

        if (appSettings.CloseToTray && _trayService is not null && !_isClosingForReal)
        {
            args.Handled = true;
            appSettings.WindowX = AppWindow.Position.X;
            appSettings.WindowY = AppWindow.Position.Y;
            appSettings.WindowWidth = AppWindow.Size.Width;
            appSettings.WindowHeight = AppWindow.Size.Height;
            ShowWindow(_hWnd, SW_HIDE);
            return;
        }

        appSettings.WindowX = AppWindow.Position.X;
        appSettings.WindowY = AppWindow.Position.Y;
        appSettings.WindowWidth = AppWindow.Size.Width;
        appSettings.WindowHeight = AppWindow.Size.Height;

        var settingsService = App.Services.GetRequiredService<ISettingsService>();
        settingsService.Save();

        _trayService?.Dispose();
    }
}
