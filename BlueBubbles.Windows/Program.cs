using System.Runtime.InteropServices;
using BlueBubbles.Core.Services;
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

    // Keeps the registered single-instance AppInstance alive for the whole process lifetime.
    // CRITICAL: this must NOT be a local. The WinRT `Activated` event subscription below lives on
    // this RCW; if the RCW is garbage-collected (which happens within seconds of startup), the
    // subscription is silently torn down and every redirected activation is dropped on the floor.
    // Toast-click activations arrive this way — the shell spawns a short-lived process that forwards
    // the activation here via RedirectActivationToAsync — so a collected RCW means toast taps, inline
    // replies, and tapbacks all stop working in every window state, with no error. (Matches the
    // Windows App SDK single-instancing sample, which likewise holds a static reference.)
    private static AppInstance? _keyInstance;

    /// <returns>true if this launch was redirected to the primary instance and should exit.</returns>
    private static bool DecideRedirection()
    {
        var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
        _keyInstance = AppInstance.FindOrRegisterForKey("BlueBubbles-Main");

        if (_keyInstance.IsCurrent)
        {
            _keyInstance.Activated += OnActivated;
            return false;
        }

        AppLog.Info(LogCategory.Ui, $"Redirecting activation (kind={activationArgs.Kind}) to primary instance.");
        RedirectActivationTo(activationArgs, _keyInstance);
        return true;
    }

    private static void OnActivated(object? sender, AppActivationArguments args)
    {
        AppLog.Info(LogCategory.Ui, $"AppInstance.Activated received: kind={args.Kind}");
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
