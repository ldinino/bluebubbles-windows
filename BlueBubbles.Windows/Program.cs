using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;

namespace BlueBubbles.Windows;

/// <summary>
/// Custom entry point (replaces the XAML-generated Main via DISABLE_XAML_GENERATED_MAIN)
/// so the app is single-instance: a second launch is redirected to the running instance,
/// which then reveals its window (from the tray if hidden) instead of opening a new one.
/// Follows the Windows App SDK single-instancing pattern.
/// </summary>
public static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        if (DecideRedirection())
            return; // activation was forwarded to the existing instance; exit this one

        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
    }

    /// <returns>true if this launch was redirected to the primary instance and should exit.</returns>
    private static bool DecideRedirection()
    {
        var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
        var keyInstance = AppInstance.FindOrRegisterForKey("BlueBubbles-Main");

        if (keyInstance.IsCurrent)
        {
            keyInstance.Activated += OnActivated;
            return false;
        }

        RedirectActivationTo(activationArgs, keyInstance);
        return true;
    }

    private static void OnActivated(object? sender, AppActivationArguments args)
    {
        if (Application.Current is not App app) return;

        // A toast interaction can spawn a fresh process that gets redirected here. Route it to the
        // notification handler so inline actions (reply / tapback) and body-click deep-links still fire —
        // otherwise the action is lost and we'd only surface the window.
        if (args.Kind == ExtendedActivationKind.AppNotification &&
            args.Data is AppNotificationActivatedEventArgs notificationArgs)
        {
            app.HandleNotificationActivation(notificationArgs);
            return;
        }

        // An ordinary second launch (e.g. re-running the exe) — just bring the existing window forward.
        app.OnRedirectedActivation();
    }

    // RedirectActivationToAsync must complete before this process exits, so block on it
    // using a native event + COM-aware wait (per the Windows App SDK sample).
    private static IntPtr _redirectEventHandle = IntPtr.Zero;

    private static void RedirectActivationTo(AppActivationArguments args, AppInstance keyInstance)
    {
        _redirectEventHandle = CreateEvent(IntPtr.Zero, true, false, null);
        Task.Run(() =>
        {
            keyInstance.RedirectActivationToAsync(args).AsTask().Wait();
            SetEvent(_redirectEventHandle);
        });

        const uint cwmoDefault = 0;
        const uint infinite = 0xFFFFFFFF;
        _ = CoWaitForMultipleObjects(
            cwmoDefault, infinite, 1, [_redirectEventHandle], out _);
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr CreateEvent(
        IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string? lpName);

    [DllImport("kernel32.dll")]
    private static extern bool SetEvent(IntPtr hEvent);

    [DllImport("ole32.dll")]
    private static extern uint CoWaitForMultipleObjects(
        uint dwFlags, uint dwMilliseconds, ulong nHandles, IntPtr[] pHandles, out uint dwIndex);
}
