using System.Net.Http;
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
using Microsoft.Windows.AppNotifications;

namespace BlueBubbles.Windows;

public partial class App : Application
{
    private Window? _window;

    public static IServiceProvider Services { get; private set; } = null!;

    public static MainWindow MainWindow => (MainWindow)((App)Current)._window!;

    public App()
    {
        InitializeComponent();
        Services = ConfigureServices();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Load persisted settings before creating the window
        var settingsService = Services.GetRequiredService<ISettingsService>();
        settingsService.Load();

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
        }
        catch { /* notifications unavailable — non-fatal */ }

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
        var arguments = e.Arguments;
        if (!arguments.TryGetValue("action", out var action)) return;

        if (action == "openChat" && arguments.TryGetValue("chatGuid", out var chatGuid))
        {
            MainWindow.DispatcherQueue.TryEnqueue(() =>
            {
                MainWindow.RestoreFromTray();
                NavigateToChat(chatGuid);
            });
        }
        else if (action == "openApp")
        {
            MainWindow.DispatcherQueue.TryEnqueue(() => MainWindow.RestoreFromTray());
        }
    }

    private static void NavigateToChat(string chatGuid)
    {
        var convListVm = Services.GetRequiredService<ConversationListViewModel>();
        var tile = convListVm.Conversations
            .Concat(convListVm.PinnedConversations)
            .FirstOrDefault(t => t.ChatGuid == chatGuid);

        if (tile is null) return;

        convListVm.SelectedConversation = tile;
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
        services.AddSingleton<INotificationService>(sp =>
            new NotificationService(
                sp.GetRequiredService<AppSettings>(),
                sp.GetRequiredService<IWindowStateService>(),
                sp.GetRequiredService<IContactResolverService>(),
                sp.GetRequiredService<IChatsService>()));
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
