using System.Text.Json;
using System.Text.Json.Serialization;
using BlueBubbles.Core.Configuration;

namespace BlueBubbles.Core.Services;

public class SettingsService : ISettingsService
{
    private readonly AppSettings _appSettings;
    private readonly ServerConfiguration _serverConfig;
    private readonly ICredentialService? _credentials;
    private readonly string _filePath;

    private static readonly string DefaultFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BlueBubbles", "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public SettingsService(AppSettings appSettings, ServerConfiguration serverConfig,
        string? filePath = null, ICredentialService? credentials = null)
    {
        _appSettings = appSettings;
        _serverConfig = serverConfig;
        _credentials = credentials;
        _filePath = filePath ?? DefaultFilePath;
    }

    public void Save()
    {
        var data = new PersistedSettings
        {
            SettingsVersion = _appSettings.SettingsVersion,
            FinishedSetup = _appSettings.FinishedSetup,
            ServerAddress = _appSettings.ServerAddress,
            ApiTimeout = _appSettings.ApiTimeout,
            LastIncrementalSync = _appSettings.LastIncrementalSync,
            LastIncrementalSyncRowId = _appSettings.LastIncrementalSyncRowId,
            ServerUrl = _serverConfig.ServerUrl,
            // Password is deliberately absent: it lives only in the DPAPI-encrypted credential
            // store (ICredentialService). See PersistedSettings.Password for the legacy read path.
            ProxyService = _serverConfig.ProxyService,
            CustomHeaders = _serverConfig.CustomHeaders.Count > 0 ? _serverConfig.CustomHeaders : null,
            UseLocalConnection = _appSettings.UseLocalConnection,
            LocalhostPort = _appSettings.LocalhostPort,
            FcmProjectId = _serverConfig.FcmProjectId,
            FcmStorageBucket = _serverConfig.FcmStorageBucket,
            FcmApiKey = _serverConfig.FcmApiKey,
            FcmFirebaseUrl = _serverConfig.FcmFirebaseUrl,
            FcmClientId = _serverConfig.FcmClientId,
            FcmApplicationId = _serverConfig.FcmApplicationId,
            VCardFilePath = _appSettings.VCardFilePath,
            UseLocalIpv6 = _appSettings.UseLocalIpv6,
            WindowX = _appSettings.WindowX,
            WindowY = _appSettings.WindowY,
            WindowWidth = _appSettings.WindowWidth,
            WindowHeight = _appSettings.WindowHeight,
            WindowMaximized = _appSettings.WindowMaximized,
            LastSelectedChatGuid = _appSettings.LastSelectedChatGuid,
            // Appearance
            Theme = _appSettings.Theme,
            ColorfulAvatars = _appSettings.ColorfulAvatars,
            ColorfulBubbles = _appSettings.ColorfulBubbles,
            HideDividers = _appSettings.HideDividers,
            DenseChatTiles = _appSettings.DenseChatTiles,
            AvatarScale = _appSettings.AvatarScale,
            Use24HrFormat = _appSettings.Use24HrFormat,
            // Messaging
            AutoDownload = _appSettings.AutoDownload,
            SendWithReturn = _appSettings.SendWithReturn,
            ShowDeliveryTimestamps = _appSettings.ShowDeliveryTimestamps,
            StatusIndicatorsOnChats = _appSettings.StatusIndicatorsOnChats,
            SendDelay = _appSettings.SendDelay,
            ScrollToLastUnread = _appSettings.ScrollToLastUnread,
            // Notifications
            NotifyOnChatList = _appSettings.NotifyOnChatList,
            NotifyReactions = _appSettings.NotifyReactions,
            NotificationSound = _appSettings.NotificationSound,
            NotificationSoundCustomPath = _appSettings.NotificationSoundCustomPath,
            FilterUnknownSenders = _appSettings.FilterUnknownSenders,
            // Private API
            ServerPrivateAPI = _appSettings.ServerPrivateAPI,
            PrivateSendTypingIndicators = _appSettings.PrivateSendTypingIndicators,
            PrivateMarkChatAsRead = _appSettings.PrivateMarkChatAsRead,
            PrivateManualMarkAsRead = _appSettings.PrivateManualMarkAsRead,
            // Desktop
            MinimizeToTray = _appSettings.MinimizeToTray,
            CloseToTray = _appSettings.CloseToTray,
            LaunchAtStartup = _appSettings.LaunchAtStartup,
            LaunchAtStartupMinimized = _appSettings.LaunchAtStartupMinimized,
            // Diagnostics
            VerboseLogging = _appSettings.VerboseLogging
        };

        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        File.WriteAllText(_filePath, JsonSerializer.Serialize(data, JsonOpts));
    }

    public void Load()
    {
        if (!File.Exists(_filePath)) return;

        string? legacyPassword = null;

        try
        {
            var json = File.ReadAllText(_filePath);
            var data = JsonSerializer.Deserialize<PersistedSettings>(json, JsonOpts);
            if (data is null) return;

            legacyPassword = data.Password;

            _appSettings.FinishedSetup = data.FinishedSetup;
            _appSettings.ServerAddress = data.ServerAddress ?? string.Empty;
            if (data.ApiTimeout > 0) _appSettings.ApiTimeout = data.ApiTimeout;
            _appSettings.LastIncrementalSync = data.LastIncrementalSync;
            _appSettings.LastIncrementalSyncRowId = data.LastIncrementalSyncRowId;

            _serverConfig.ServerUrl = data.ServerUrl ?? string.Empty;
            // Seed from the legacy cleartext field only when it is actually present, so a normal
            // (already migrated) file never blanks a password restored from the credential store.
            if (!string.IsNullOrEmpty(data.Password))
                _serverConfig.Password = data.Password;
            _serverConfig.ProxyService = data.ProxyService ?? string.Empty;
            _serverConfig.CustomHeaders = data.CustomHeaders ?? new();
            _appSettings.UseLocalConnection = data.UseLocalConnection;
            if (!string.IsNullOrEmpty(data.LocalhostPort))
                _appSettings.LocalhostPort = data.LocalhostPort;
            _serverConfig.FcmProjectId = data.FcmProjectId;
            _serverConfig.FcmStorageBucket = data.FcmStorageBucket;
            _serverConfig.FcmApiKey = data.FcmApiKey;
            _serverConfig.FcmFirebaseUrl = data.FcmFirebaseUrl;
            _serverConfig.FcmClientId = data.FcmClientId;
            _serverConfig.FcmApplicationId = data.FcmApplicationId;
            _appSettings.VCardFilePath = data.VCardFilePath ?? string.Empty;
            _appSettings.UseLocalIpv6 = data.UseLocalIpv6;
            _appSettings.WindowX = data.WindowX;
            _appSettings.WindowY = data.WindowY;
            _appSettings.WindowWidth = data.WindowWidth;
            _appSettings.WindowHeight = data.WindowHeight;
            _appSettings.WindowMaximized = data.WindowMaximized;
            _appSettings.LastSelectedChatGuid = data.LastSelectedChatGuid ?? string.Empty;
            // Appearance
            _appSettings.Theme = data.Theme;
            _appSettings.ColorfulAvatars = data.ColorfulAvatars;
            _appSettings.ColorfulBubbles = data.ColorfulBubbles;
            _appSettings.HideDividers = data.HideDividers;
            _appSettings.DenseChatTiles = data.DenseChatTiles;
            if (data.AvatarScale > 0) _appSettings.AvatarScale = data.AvatarScale;
            _appSettings.Use24HrFormat = data.Use24HrFormat;
            // Messaging
            _appSettings.AutoDownload = data.AutoDownload;
            _appSettings.SendWithReturn = data.SendWithReturn;
            _appSettings.ShowDeliveryTimestamps = data.ShowDeliveryTimestamps;
            _appSettings.StatusIndicatorsOnChats = data.StatusIndicatorsOnChats;
            _appSettings.SendDelay = data.SendDelay;
            _appSettings.ScrollToLastUnread = data.ScrollToLastUnread;
            // Notifications
            _appSettings.NotifyOnChatList = data.NotifyOnChatList;
            _appSettings.NotifyReactions = data.NotifyReactions;
            _appSettings.NotificationSound = data.NotificationSound ?? "default";
            _appSettings.NotificationSoundCustomPath = data.NotificationSoundCustomPath ?? string.Empty;
            _appSettings.FilterUnknownSenders = data.FilterUnknownSenders;
            // Private API
            _appSettings.ServerPrivateAPI = data.ServerPrivateAPI;
            _appSettings.PrivateSendTypingIndicators = data.PrivateSendTypingIndicators;
            _appSettings.PrivateMarkChatAsRead = data.PrivateMarkChatAsRead;
            _appSettings.PrivateManualMarkAsRead = data.PrivateManualMarkAsRead;
            // Desktop
            _appSettings.MinimizeToTray = data.MinimizeToTray;
            _appSettings.CloseToTray = data.CloseToTray;
            _appSettings.LaunchAtStartup = data.LaunchAtStartup;
            _appSettings.LaunchAtStartupMinimized = data.LaunchAtStartupMinimized;
            // Diagnostics
            _appSettings.VerboseLogging = data.VerboseLogging;

            // --- Migrations ---
            // v0 → v1: "Colorful avatars" and "Show delivery timestamps" used to be unused toggles
            // that persisted as `false`. Now that they're wired up, coerce pre-v1 files to the
            // intended defaults (on) so the established look is preserved.
            if (data.SettingsVersion < 1)
            {
                _appSettings.ColorfulAvatars = true;
                _appSettings.ShowDeliveryTimestamps = true;
            }
            _appSettings.SettingsVersion = AppSettings.CurrentSettingsVersion;
        }
        catch { /* corrupt settings — start fresh */ }

        if (string.IsNullOrEmpty(legacyPassword)) return;

        // Older builds persisted the server password here in cleartext. Hand it to the
        // DPAPI-encrypted credential store (an existing entry there wins, since it is the
        // newer of the two) and rewrite the file so the cleartext copy stops existing on disk.
        try
        {
            if (_credentials is not null && string.IsNullOrEmpty(_credentials.GetPassword()))
                _credentials.SavePassword(legacyPassword);
            Save();
        }
        catch { /* best-effort cleanup — never block startup on it */ }
    }

    private sealed record PersistedSettings
    {
        // 0 = file predates settings versioning (triggers v0→v1 migration in Load).
        public int SettingsVersion { get; init; }
        public bool FinishedSetup { get; init; }
        public string? ServerAddress { get; init; }
        public int ApiTimeout { get; init; } = 30000;
        public long LastIncrementalSync { get; init; }
        public long LastIncrementalSyncRowId { get; init; }
        public string? ServerUrl { get; init; }
        // Legacy only. Never written any more (it is always null on save, and the attribute keeps
        // the key out of the file); read so that installs upgrading from a build that stored it in
        // cleartext can be migrated to the credential store and the field dropped.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Password { get; init; }
        public string? ProxyService { get; init; }
        public Dictionary<string, string>? CustomHeaders { get; init; }
        public bool UseLocalConnection { get; init; }
        public string? LocalhostPort { get; init; }
        public string? FcmProjectId { get; init; }
        public string? FcmStorageBucket { get; init; }
        public string? FcmApiKey { get; init; }
        public string? FcmFirebaseUrl { get; init; }
        public string? FcmClientId { get; init; }
        public string? FcmApplicationId { get; init; }
        public string? VCardFilePath { get; init; }
        public bool UseLocalIpv6 { get; init; }
        public int WindowX { get; init; }
        public int WindowY { get; init; }
        public int WindowWidth { get; init; }
        public int WindowHeight { get; init; }
        public bool WindowMaximized { get; init; }
        public string? LastSelectedChatGuid { get; init; }
        // Appearance
        public int Theme { get; init; }
        public bool ColorfulAvatars { get; init; }
        public bool ColorfulBubbles { get; init; }
        public bool HideDividers { get; init; }
        public bool DenseChatTiles { get; init; }
        public double AvatarScale { get; init; } = 1.0;
        public bool Use24HrFormat { get; init; }
        // Messaging
        public bool AutoDownload { get; init; } = true;
        public bool SendWithReturn { get; init; } = true;
        public bool ShowDeliveryTimestamps { get; init; }
        public bool StatusIndicatorsOnChats { get; init; }
        public int SendDelay { get; init; }
        public bool ScrollToLastUnread { get; init; }
        // Notifications
        public bool NotifyOnChatList { get; init; }
        public bool NotifyReactions { get; init; } = true;
        public string? NotificationSound { get; init; } = "default";
        public string? NotificationSoundCustomPath { get; init; }
        public bool FilterUnknownSenders { get; init; }
        // Private API
        public bool? ServerPrivateAPI { get; init; }
        public bool PrivateSendTypingIndicators { get; init; } = true;
        public bool PrivateMarkChatAsRead { get; init; } = true;
        public bool PrivateManualMarkAsRead { get; init; }
        // Desktop
        public bool MinimizeToTray { get; init; }
        public bool CloseToTray { get; init; } = true;
        public bool LaunchAtStartup { get; init; }
        public bool LaunchAtStartupMinimized { get; init; }
        // Diagnostics
        public bool VerboseLogging { get; init; }
    }
}
