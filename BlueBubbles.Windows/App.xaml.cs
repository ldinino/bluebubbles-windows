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

        // Verbose ("Debug") logging is dormant: the About > Diagnostics toggle is hidden and the log
        // stays at its Info default, so the avatar flicker tracing stays off. To re-light it while
        // debugging, un-hide VerboseLoggingToggle in AboutSettingsPage.xaml (flipping it sets
        // AppLog.MinLevel live) and, for it to persist across launches, restore the line that reads
        // AppSettings.VerboseLogging into AppLog.MinLevel here.
        var appSettings = Services.GetRequiredService<AppSettings>();

        // Restore password from PasswordVault into ServerConfiguration
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
        // so a relaunch backfills anything missed even before the socket settles.
        if (appSettings.FinishedSetup)
            _ = Services.GetRequiredService<ISyncService>().RunIncrementalSyncAsync();
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
        var args = e.Arguments;
        if (!args.TryGetValue(NotificationService.ActionKey, out var action))
        {
            AppLog.Warn(LogCategory.Ui, "Toast activation had no action argument; ignoring.");
            return;
        }

        AppLog.Info(LogCategory.Ui, $"Toast activation: action={action}");

        switch (action)
        {
            // Inline actions: send in the background — don't yank the window to the foreground.
            case NotificationService.ActionReply:
                HandleReply(args, e.UserInput);
                break;
            case NotificationService.ActionReact:
                HandleReaction(args);
                break;

            // Body click: surface the window and deep-link to the chat.
            case NotificationService.ActionOpenChat
                when args.TryGetValue(NotificationService.ChatGuidKey, out var chatGuid):
                MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    MainWindow.RestoreFromTray();
                    NavigateToChat(chatGuid);
                });
                break;
            case NotificationService.ActionOpenApp:
                MainWindow.DispatcherQueue.TryEnqueue(() => MainWindow.RestoreFromTray());
                break;
        }
    }

    /// <summary>Inline quick-reply: send straight through the outgoing (private-API) queue. The
    /// server echo persists the message via the normal incoming path, so the chat need not be open.</summary>
    private static void HandleReply(IDictionary<string, string> args, IDictionary<string, string> userInput)
    {
        if (!args.TryGetValue(NotificationService.ChatGuidKey, out var chatGuid)) return;
        if (!userInput.TryGetValue(NotificationService.ReplyInputId, out var text) ||
            string.IsNullOrWhiteSpace(text))
            return;

        Services.GetRequiredService<IOutgoingMessageService>().EnqueueText(chatGuid, text.Trim());
        MarkChatActedOn(chatGuid);
    }

    /// <summary>Inline tapback: react to the originating message through the private-API send path.</summary>
    private static void HandleReaction(IDictionary<string, string> args)
    {
        if (!args.TryGetValue(NotificationService.ChatGuidKey, out var chatGuid)) return;
        if (!args.TryGetValue(NotificationService.MessageGuidKey, out var messageGuid)) return;
        if (!args.TryGetValue(NotificationService.ReactionKey, out var reaction)) return;
        args.TryGetValue(NotificationService.SelectedTextKey, out var selectedText);

        // partIndex 0 mirrors the in-app reaction path; the private-API message/react endpoint expects
        // a concrete part index (a null index silently no-ops the tapback on the server).
        _ = Services.GetRequiredService<IOutgoingMessageService>()
            .SendTapbackAsync(chatGuid, selectedText ?? string.Empty, messageGuid, reaction, partIndex: 0);
        MarkChatActedOn(chatGuid);
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
