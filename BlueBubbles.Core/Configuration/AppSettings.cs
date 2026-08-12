using CommunityToolkit.Mvvm.ComponentModel;

namespace BlueBubbles.Core.Configuration;

public partial class AppSettings : ObservableObject
{
    // Bumped when a settings migration is required. New installs start at the current
    // version; older files (which predate a migration) load with a lower value and are
    // upgraded in SettingsService.Load.
    public const int CurrentSettingsVersion = 1;

    [ObservableProperty] public partial int SettingsVersion { get; set; }

    // Setup state
    [ObservableProperty] public partial bool FinishedSetup { get; set; }

    // Connection
    [ObservableProperty] public partial string ServerAddress { get; set; }
    [ObservableProperty] public partial bool UseLocalConnection { get; set; }
    [ObservableProperty] public partial string LocalhostPort { get; set; }
    [ObservableProperty] public partial bool UseLocalIpv6 { get; set; }
    [ObservableProperty] public partial int ApiTimeout { get; set; }

    // Sync
    [ObservableProperty] public partial long LastIncrementalSync { get; set; }
    [ObservableProperty] public partial long LastIncrementalSyncRowId { get; set; }
    // Watermark for the "updated-since" sweep: edits/unsends to already-synced messages don't bump
    // ROWID, so the ROWID delta misses them. This tracks the last time we trued-up in-place changes.
    [ObservableProperty] public partial long LastUpdatedSync { get; set; }
    // Bumped to force a one-time full heal on upgrade (e.g. to apply server deletes the old cache
    // never reconciled). Compared against SyncService.CurrentSyncModelVersion at launch.
    [ObservableProperty] public partial int SyncModelVersion { get; set; }

    // Appearance
    [ObservableProperty] public partial int Theme { get; set; }
    [ObservableProperty] public partial bool ColorfulAvatars { get; set; }
    [ObservableProperty] public partial bool Use24HrFormat { get; set; }

    // Messaging
    [ObservableProperty] public partial bool AutoDownload { get; set; }
    [ObservableProperty] public partial bool SendWithReturn { get; set; } = true;
    [ObservableProperty] public partial bool ShowDeliveryTimestamps { get; set; }
    [ObservableProperty] public partial int SendDelay { get; set; }

    // Notifications
    [ObservableProperty] public partial bool NotifyOnChatList { get; set; }
    [ObservableProperty] public partial bool NotifyReactions { get; set; }
    [ObservableProperty] public partial string NotificationSound { get; set; }
    // Absolute path to a user-picked custom sound; used only when NotificationSound == "custom".
    [ObservableProperty] public partial string NotificationSoundCustomPath { get; set; }
    [ObservableProperty] public partial bool FilterUnknownSenders { get; set; }

    // Private API
    [ObservableProperty] public partial bool? ServerPrivateAPI { get; set; }
    [ObservableProperty] public partial bool PrivateSendTypingIndicators { get; set; }
    [ObservableProperty] public partial bool PrivateMarkChatAsRead { get; set; }
    [ObservableProperty] public partial bool PrivateManualMarkAsRead { get; set; }

    // Contacts
    [ObservableProperty] public partial string VCardFilePath { get; set; }

    // Desktop
    [ObservableProperty] public partial bool LaunchAtStartup { get; set; }
    [ObservableProperty] public partial bool LaunchAtStartupMinimized { get; set; }
    [ObservableProperty] public partial bool MinimizeToTray { get; set; }
    [ObservableProperty] public partial bool CloseToTray { get; set; }

    // Window placement (physical pixels, persisted across sessions). X/Y/Width/Height are the
    // RESTORE bounds — the size the window returns to when un-maximized — never the maximized
    // bounds, or reopening a maximized window would produce a normal window that merely looks
    // maximized.
    [ObservableProperty] public partial int WindowX { get; set; }
    [ObservableProperty] public partial int WindowY { get; set; }
    [ObservableProperty] public partial int WindowWidth { get; set; }
    [ObservableProperty] public partial int WindowHeight { get; set; }
    [ObservableProperty] public partial bool WindowMaximized { get; set; }

    // Session restore — the conversation that was open when the app last closed.
    [ObservableProperty] public partial string LastSelectedChatGuid { get; set; }

    // Diagnostics
    // When on, AppLog captures verbose Debug-level tracing (e.g. avatar load/recycle) in addition to
    // the default Info+ output. Off by default to keep the log readable; applied to AppLog.MinLevel
    // at startup and live when toggled in About > Diagnostics.
    [ObservableProperty] public partial bool VerboseLogging { get; set; }

    public AppSettings()
    {
        SettingsVersion = CurrentSettingsVersion;
        ServerAddress = string.Empty;
        LocalhostPort = "1234";
        VCardFilePath = string.Empty;
        NotificationSoundCustomPath = string.Empty;
        LastSelectedChatGuid = string.Empty;
        ApiTimeout = 30000;
        AutoDownload = true;
        // Default-on so the established look is preserved now that these toggles are wired up.
        ColorfulAvatars = true;
        ShowDeliveryTimestamps = true;
        NotifyReactions = true;
        NotificationSound = "default";
        PrivateSendTypingIndicators = true;
        PrivateMarkChatAsRead = true;
        CloseToTray = true;
    }
}
