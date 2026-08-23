using System.Net.Http;
using System.Net.NetworkInformation;
using BlueBubbles.Core.Configuration;
using BlueBubbles.Core.Data;
using BlueBubbles.Core.Services;
using BlueBubbles.Core.Services.Http;
using BlueBubbles.Windows.Services;
using BlueBubbles.Windows.ViewModels;
using BlueBubbles.Windows.Views;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;

namespace BlueBubbles.Windows;

public partial class App : Application
{
    private Window? _window;

    // Debounces connectivity-recovery bursts (NetworkAddressChanged can fire many times for a single
    // network switch) so we don't fire a flurry of pings/restarts.
    private Timer? _recoverDebounce;

    // Gentle always-on poll that catches conversation deletes the server never pushes (it emits no
    // socket event for a chat delete). Each tick reconciles only when GetForegroundWindow says we're
    // in the foreground, so it's idle (a cheap focus check, no network) while backgrounded/in tray.
    private Timer? _foregroundReconcile;
    private static readonly TimeSpan ForegroundReconcileInterval = TimeSpan.FromSeconds(60);
    private static long _lastChatReconcileTicks;
    // Collapse rapid triggers (poll tick + focus-regain + tray-restore) into at most one reconcile.
    private const long ChatReconcileThrottleMs = 15_000;

    public static IServiceProvider Services { get; private set; } = null!;

    public static MainWindow MainWindow => (MainWindow)((App)Current)._window!;

    public App()
    {
        InitializeComponent();
        Services = ConfigureServices();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Set up file logging first so the session banner precedes all other startup work and any
        // failures below are captured to disk.
        AppLog.Initialize();

        // Load persisted settings before creating the window
        var settingsService = Services.GetRequiredService<ISettingsService>();
        settingsService.Load();

        var appSettings = Services.GetRequiredService<AppSettings>();
        AppLog.MinLevel = appSettings.VerboseLogging ? LogLevel.Debug : LogLevel.Info;

        // Restore the saved server password (DPAPI-encrypted file via CredentialService — NOT
        // PasswordVault, which needs package identity) into ServerConfiguration.
        var credentials = Services.GetRequiredService<ICredentialService>();
        var serverConfig = Services.GetRequiredService<ServerConfiguration>();
        var savedPassword = credentials.GetPassword();
        if (savedPassword is not null)
            serverConfig.Password = savedPassword;

        // Register toast notification activation before window creation. Wrapped because
        // registration can fail on some unpackaged configurations; toasts then simply no-op
        // (every Show call in NotificationService is already guarded) rather than crashing launch.
        try
        {
            AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;
            AppNotificationManager.Default.Register();
            AppLog.Info(LogCategory.Ui, "AppNotificationManager registered.");
        }
        catch (Exception ex)
        {
            // Surface the failure rather than swallowing it: a failed Register means toasts may still
            // display but their click/reply/react activations can never route back, which is invisible
            // otherwise. Every Show call in NotificationService is guarded, so this stays non-fatal.
            AppLog.Error(LogCategory.Ui, $"AppNotificationManager registration failed: {ex.Message}");
        }

        // Start the incoming message processor (serializes socket event DB writes)
        Services.GetRequiredService<IIncomingMessageProcessor>().Start();

        _window = new MainWindow();
        _window.Activate();

        // Initialize window-dependent services
        var mainWindow = (MainWindow)_window;

        var windowState = (WindowStateService)Services.GetRequiredService<IWindowStateService>();
        windowState.SetWindowHandle(mainWindow._hWnd);

        var trayService = Services.GetRequiredService<SystemTrayService>();
        mainWindow.InitializeTrayService(trayService);

        var badgeService = Services.GetRequiredService<TaskbarBadgeService>();
        badgeService.Initialize(mainWindow._hWnd);

        // Reconcile the launch-at-startup registry entry with the persisted preference and, if
        // launched via that entry with the minimized flag, start hidden in the tray.
        ReconcileStartupState(mainWindow);

        // Cold start FROM a toast: the click launched this process, so the action rides in on the
        // activation args rather than the in-process NotificationInvoked event (which only covers an
        // already-running instance). Route it through the same handler so a body-click deep-links and
        // an inline reply/tapback still fires when the app was fully closed.
        TryHandleColdStartActivation();

        // Self-healing sync: recover the connection + run a catch-up delta whenever the machine wakes
        // from sleep or the network changes (Wi-Fi switch, VPN toggle). A socket can sit half-open
        // after sleep, so we can't rely on socket events alone.
        mainWindow.SystemResumed += (_, _) => ScheduleConnectivityRecovery();
        NetworkChange.NetworkAddressChanged += (_, _) => ScheduleConnectivityRecovery();

        // Launch-time catch-up: kick a delta immediately (independent of the socket's OnConnected),
        // so a relaunch backfills anything missed even before the socket settles. After an upgrade
        // that changed how the cache converges, run a one-time full heal first (it applies server-side
        // deletes/edits an older build never reconciled) and skip the redundant delta.
        if (appSettings.FinishedSetup)
        {
            var sync = Services.GetRequiredService<ISyncService>();
            _ = Task.Run(async () =>
            {
                try
                {
                    if (!await sync.RunHealIfNeededAsync())
                        await sync.RunIncrementalSyncAsync();
                }
                catch (Exception ex)
                {
                    AppLog.Warn(LogCategory.Sync, $"Launch catch-up failed: {ex.Message}");
                }
            });

            // Conversation deletes are never pushed over the socket (a server limitation), so the only
            // way to catch one while the app is open is to diff the chat list. An always-on poll gated
            // by GetForegroundWindow reconciles only when the window is actually in the foreground —
            // Window.Activated is unreliable here (missed at launch, and tray hide/show via Win32
            // bypasses it). RestoreFromTray and focus-regain also trigger it for immediacy.
            _foregroundReconcile = new Timer(_ =>
            {
                if (Services.GetRequiredService<IWindowStateService>().IsWindowFocused)
                    RequestChatReconcile();
            }, null, ForegroundReconcileInterval, ForegroundReconcileInterval);
        }
    }

    /// <summary>Throttled, fire-and-forget lean chat reconcile (catches server-side conversation
    /// deletes). Triggered by the foreground poll, focus-regain, and tray-restore; the throttle and the
    /// SyncService's own guard keep overlapping triggers from issuing redundant requests.</summary>
    internal static void RequestChatReconcile()
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastChatReconcileTicks) < ChatReconcileThrottleMs) return;
        Interlocked.Exchange(ref _lastChatReconcileTicks, now);

        _ = Task.Run(async () =>
        {
            try { await Services.GetRequiredService<ISyncService>().ReconcileChatsAsync(); }
            catch (Exception ex) { AppLog.Warn(LogCategory.Sync, $"Foreground chat reconcile failed: {ex.Message}"); }
        });
    }

    /// <summary>Coalesces resume/network-change signals and, after a short settle delay, verifies the
    /// socket is healthy then runs a safety-net delta sync.</summary>
    private void ScheduleConnectivityRecovery()
    {
        _recoverDebounce?.Dispose();
        _recoverDebounce = new Timer(_ => _ = RecoverConnectivityAsync(), null,
            TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
    }

    private static async Task RecoverConnectivityAsync()
    {
        var settings = Services.GetRequiredService<AppSettings>();
        if (!settings.FinishedSetup) return;

        try { await Services.GetRequiredService<ISocketService>().EnsureHealthyAsync(); }
        catch (Exception ex) { AppLog.Warn(LogCategory.Socket, $"Connectivity recovery (socket) failed: {ex.Message}"); }

        try { await Services.GetRequiredService<ISyncService>().RunIncrementalSyncAsync(); }
        catch (Exception ex) { AppLog.Warn(LogCategory.Sync, $"Connectivity recovery (delta) failed: {ex.Message}"); }
    }

    private static void ReconcileStartupState(MainWindow mainWindow)
    {
        var settings = Services.GetRequiredService<AppSettings>();
        var startupTask = Services.GetRequiredService<StartupTaskService>();

        // The user could remove the Run entry by other means; trust the registry as the source of
        // truth and reflect it back into the setting so the toggle never lies.
        var actual = startupTask.IsEnabled();
        if (actual != settings.LaunchAtStartup)
        {
            settings.LaunchAtStartup = actual;
            Services.GetRequiredService<ISettingsService>().Save();
        }

        // Re-register when enabled so the command stays correct (exe path / minimized preference)
        // even if the install moved or the preference changed.
        if (actual)
            startupTask.SetEnabled(true, settings.LaunchAtStartupMinimized);

        if (Environment.GetCommandLineArgs().Contains(StartupTaskService.MinimizedArg))
            mainWindow.HideToTray();
    }

    /// <summary>Invoked (from <see cref="Program"/>) when a second launch is redirected here.
    /// Reveals the existing window — restoring it from the tray if hidden — rather than
    /// starting another instance.</summary>
    public void OnRedirectedActivation()
    {
        _window?.DispatcherQueue.TryEnqueue(() =>
        {
            if (_window is MainWindow mainWindow)
                mainWindow.RestoreFromTray();
        });
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs e)
    {
        AppLog.Info(LogCategory.Ui, "NotificationInvoked (in-process) received.");
        HandleNotificationActivation(e);
    }

    /// <summary>Routes a toast interaction (body click or inline button) to the right action. Reached two
    /// ways: the in-process <see cref="AppNotificationManager.NotificationInvoked"/> event when the app is
    /// already running, and — because this app is single-instanced — the redirected activation path
    /// (<see cref="Program"/>'s <c>OnActivated</c>) when the toast spawned a fresh process that forwarded
    /// to us. Without the second route, clicking a toast button just brought the window forward and the
    /// action (e.g. a tapback) was dropped.</summary>
    internal void HandleNotificationActivation(AppNotificationActivatedEventArgs e)
    {
        var activation = ToastActivationRouter.Resolve(e.Arguments, e.UserInput);
        if (activation.Kind == ToastActionKind.None)
        {
            AppLog.Warn(LogCategory.Ui, "Toast activation had no usable action argument; ignoring.");
            return;
        }

        AppLog.Info(LogCategory.Ui, $"Toast activation: action={activation.Kind}");

        switch (activation.Kind)
        {
            // Inline actions: act in the background — don't yank the window to the foreground.
            case ToastActionKind.Reply:
                Services.GetRequiredService<IOutgoingMessageService>()
                    .EnqueueText(activation.ChatGuid, activation.ReplyText);
                MarkChatActedOn(activation.ChatGuid);
                break;

            case ToastActionKind.React:
                // partIndex 0 mirrors the in-app reaction path; the private-API message/react endpoint
                // expects a concrete part index (a null index silently no-ops the tapback server-side).
                _ = Services.GetRequiredService<IOutgoingMessageService>().SendTapbackAsync(
                    activation.ChatGuid, activation.SelectedText, activation.MessageGuid,
                    activation.Reaction, partIndex: 0);
                MarkChatActedOn(activation.ChatGuid);
                break;

            case ToastActionKind.MarkRead:
                MarkChatActedOn(activation.ChatGuid);
                break;

            // Body click: surface the window and deep-link to the chat.
            case ToastActionKind.OpenChat:
                MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    MainWindow.RestoreFromTray();
                    NavigateToChat(activation.ChatGuid);
                });
                break;

            case ToastActionKind.OpenApp:
                MainWindow.DispatcherQueue.TryEnqueue(() => MainWindow.RestoreFromTray());
                break;
        }
    }

    /// <summary>Acting on a toast implies the message was seen: clear that chat's toasts and mark it read.</summary>
    private static void MarkChatActedOn(string chatGuid)
    {
        Services.GetRequiredService<INotificationService>().ClearNotificationsForChat(chatGuid);
        _ = Services.GetRequiredService<IChatsService>().MarkChatReadAsync(chatGuid, true);
    }

    /// <summary>Surfaces the requested chat in the shell. Setting the view-model selection alone does
    /// NOT navigate the chat frame (only the list's ConversationSelected path does), so this routes
    /// through the shell's OpenChat, which reuses that path and waits for the list on a cold start.</summary>
    private static void NavigateToChat(string chatGuid)
    {
        if (!Services.GetRequiredService<AppSettings>().FinishedSetup) return;

        // A notification deep-link should land on the chat even from Settings, or mid-launch before the
        // shell is the active page. ShellPage is cached (NavigationCacheMode=Required), so navigating to
        // it restores the existing instance with its loaded list rather than building a fresh one.
        var root = MainWindow.RootNavigationFrame;
        if (root.Content is not Views.ShellPage)
            root.Navigate(typeof(Views.ShellPage));

        if (root.Content is Views.ShellPage shell)
            shell.OpenChat(chatGuid);
    }

    /// <summary>If this process was launched by a toast interaction, the action arrives on the
    /// activation args (the in-process event never fires for a cold start). Pull it and route it.</summary>
    private void TryHandleColdStartActivation()
    {
        try
        {
            var activated = AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activated.Kind == ExtendedActivationKind.AppNotification &&
                activated.Data is AppNotificationActivatedEventArgs notif)
            {
                AppLog.Info(LogCategory.Ui, "Cold-start activation is a notification; routing it.");
                HandleNotificationActivation(notif);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogCategory.Ui, $"Cold-start notification routing failed: {ex.Message}");
        }
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // Database
        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BlueBubbles", "bluebubbles.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        services.AddDbContextFactory<BlueBubblesDbContext>(options =>
            options.UseSqlite($"Data Source={dbPath}"));

        // Configuration (singletons — shared app-wide state)
        services.AddSingleton<AppSettings>();
        services.AddSingleton<ServerConfiguration>();

        // Core services (singletons — long-lived, stateful)
        services.AddSingleton<IBlueBubblesApiService>(sp =>
        {
            var config = sp.GetRequiredService<ServerConfiguration>();
            var settings = sp.GetRequiredService<AppSettings>();
            var handler = new ProxyHeaderHandler(config)
            {
                InnerHandler = new CloudflareRetryHandler
                {
                    InnerHandler = new SocketsHttpHandler
                    {
                        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
                    }
                }
            };
            var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            return new BlueBubblesApiService(client, config, settings);
        });
        services.AddSingleton<IActionHandler, ActionHandler>();
        services.AddSingleton<ICredentialService, CredentialService>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IFirebaseService>(sp =>
            new FirebaseService(
                sp.GetRequiredService<IBlueBubblesApiService>(),
                sp.GetRequiredService<ServerConfiguration>(),
                new HttpClient()));
        services.AddSingleton<ILocalhostDetectionService>(sp =>
            new LocalhostDetectionService(
                sp.GetRequiredService<IBlueBubblesApiService>(),
                sp.GetRequiredService<AppSettings>()));
        services.AddSingleton<ISocketService>(sp =>
            new SocketService(
                sp.GetRequiredService<ServerConfiguration>(),
                sp.GetRequiredService<IActionHandler>(),
                sp.GetRequiredService<IFirebaseService>(),
                sp.GetRequiredService<ISettingsService>(),
                sp.GetRequiredService<ISyncService>(),
                sp.GetRequiredService<ILocalhostDetectionService>(),
                sp.GetRequiredService<IBlueBubblesApiService>(),
                sp.GetRequiredService<AppSettings>()));
        services.AddSingleton<IServerDiscoveryService>(sp =>
            new ServerDiscoveryService(
                sp.GetRequiredService<IBlueBubblesApiService>(),
                new HttpClient()));
        services.AddSingleton<ISyncService, SyncService>();
        services.AddSingleton<IContactResolverService, ContactResolverService>();

        services.AddSingleton<IWindowStateService, WindowStateService>();
        services.AddSingleton<IChatsService, ChatsService>();
        services.AddSingleton<IMessagesService, MessagesService>();
        services.AddSingleton<IOutgoingMessageService, OutgoingMessageService>();
        services.AddSingleton<IScheduledMessageService, ScheduledMessageService>();
        services.AddSingleton<INotificationSoundService, NotificationSoundService>();
        services.AddSingleton<INotificationService>(sp =>
            new NotificationService(
                sp.GetRequiredService<AppSettings>(),
                sp.GetRequiredService<IWindowStateService>(),
                sp.GetRequiredService<IContactResolverService>(),
                sp.GetRequiredService<IChatsService>(),
                sp.GetRequiredService<INotificationSoundService>()));
        services.AddSingleton<IIncomingMessageProcessor, IncomingMessageProcessor>();
        services.AddSingleton<IAttachmentCacheService>(sp =>
        {
            var api = sp.GetRequiredService<IBlueBubblesApiService>();
            var cacheRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BlueBubbles", "attachments");
            return new AttachmentCacheService(api, cacheRoot);
        });

        // Link preview metadata fetcher (on-demand "Show preview") — a plain client to the open web,
        // separate from the proxied BlueBubbles-server client.
        services.AddSingleton<ILinkPreviewService>(
            new LinkPreviewService(new HttpClient { Timeout = TimeSpan.FromSeconds(10) }));

        // Tray + badge services
        services.AddSingleton<SystemTrayService>();
        services.AddSingleton<TaskbarBadgeService>();
        services.AddSingleton<StartupTaskService>();

        // ViewModels
        services.AddSingleton<SetupViewModel>();
        services.AddSingleton<ConversationListViewModel>();
        services.AddSingleton<ChatViewModel>();
        services.AddSingleton<ChatDetailsViewModel>();
        services.AddSingleton<NewChatViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddTransient<ShellViewModel>();

        return services.BuildServiceProvider();
    }
}
