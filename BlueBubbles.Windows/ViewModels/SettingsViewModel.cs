using BlueBubbles.Core.Configuration;
using BlueBubbles.Core.Data;
using BlueBubbles.Core.Models;
using BlueBubbles.Core.Services;
using BlueBubbles.Windows.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;

namespace BlueBubbles.Windows.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ISocketService _socketService;
    private readonly IFirebaseService _firebase;
    private readonly ISettingsService _settings;
    private readonly ServerConfiguration _config;
    private readonly IContactResolverService _contacts;
    private readonly ICredentialService _credentials;
    private readonly IDbContextFactory<BlueBubblesDbContext> _dbFactory;
    private readonly ILocalhostDetectionService _localhostDetection;
    private readonly StartupTaskService _startupTask;
    private readonly AppSettings _appSettings;

    /// <summary>Sentinel filter value meaning "show every category".</summary>
    public const string AllCategories = "All";

    [ObservableProperty] public partial string LogText { get; set; } = string.Empty;
    [ObservableProperty] public partial string LogCategoryFilter { get; set; } = AllCategories;

    [ObservableProperty] public partial SocketState ConnectionState { get; set; }
    [ObservableProperty] public partial string ServerUrl { get; set; }
    [ObservableProperty] public partial bool IsFetchingUrl { get; set; }

    [ObservableProperty] public partial bool UseLocalConnection { get; set; }
    [ObservableProperty] public partial string LocalhostPort { get; set; }
    [ObservableProperty] public partial string LocalConnectionStatus { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsTestingLocal { get; set; }

    [ObservableProperty] public partial string VCardStatus { get; set; }
    [ObservableProperty] public partial int ContactCount { get; set; }
    [ObservableProperty] public partial bool HasVCard { get; set; }

    public SettingsViewModel(
        ISocketService socketService,
        IFirebaseService firebase,
        ISettingsService settings,
        ServerConfiguration config,
        IContactResolverService contacts,
        ICredentialService credentials,
        IDbContextFactory<BlueBubblesDbContext> dbFactory,
        ILocalhostDetectionService localhostDetection,
        StartupTaskService startupTask,
        AppSettings appSettings)
    {
        _socketService = socketService;
        _firebase = firebase;
        _settings = settings;
        _config = config;
        _contacts = contacts;
        _credentials = credentials;
        _dbFactory = dbFactory;
        _localhostDetection = localhostDetection;
        _startupTask = startupTask;
        _appSettings = appSettings;

        ServerUrl = _config.ServerUrl;
        ConnectionState = _socketService.State;
        UseLocalConnection = _appSettings.UseLocalConnection;
        LocalhostPort = _appSettings.LocalhostPort;
        if (_localhostDetection.ResolvedLocalUrl is not null)
            LocalConnectionStatus = $"Connected: {_localhostDetection.ResolvedLocalUrl}";

        if (_socketService is ObservableObject observable)
        {
            observable.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ISocketService.State))
                {
                    RunOnUI(() =>
                    {
                        ConnectionState = _socketService.State;
                        ServerUrl = _config.ServerUrl;
                    });
                }
            };
        }

        RebuildLogText();
        AppLog.EntryAdded += OnLogEntry;

        RefreshVCardStatus();
    }

    private void RefreshVCardStatus()
    {
        ContactCount = _contacts.ContactCount;
        HasVCard = !string.IsNullOrEmpty(_contacts.LoadedFilePath);
        VCardStatus = HasVCard
            ? $"{_contacts.ContactCount} contacts loaded"
            : "No contacts imported";
    }

    [RelayCommand]
    private async Task ImportVCardAsync(string sourcePath)
    {
        try
        {
            var localDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BlueBubbles");
            Directory.CreateDirectory(localDir);
            var destPath = Path.Combine(localDir, "contacts.vcf");

            File.Copy(sourcePath, destPath, overwrite: true);

            await _contacts.LoadFromVCardAsync(destPath);
            _appSettings.VCardFilePath = destPath;
            _settings.Save();

            RefreshVCardStatus();
        }
        catch (Exception ex)
        {
            AppLog.Error(LogCategory.Contacts, $"vCard import failed: {ex.Message}");
        }
    }

    /// <summary>Clears all imported contacts and deletes the cached vCard, so conversations fall back to
    /// raw addresses (and any merged threads split apart) until the next import. Backs the Reset button —
    /// it isn't otherwise obvious that re-importing replaces what's loaded.</summary>
    [RelayCommand]
    private void ResetContacts()
    {
        _contacts.ClearContacts();
        _appSettings.VCardFilePath = string.Empty;

        try
        {
            var destPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BlueBubbles", "contacts.vcf");
            if (File.Exists(destPath))
                File.Delete(destPath);
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogCategory.Contacts, $"Failed to delete cached vCard: {ex.Message}");
        }

        _settings.Save();
        RefreshVCardStatus();
    }

    private void OnLogEntry(string entry)
    {
        if (!MatchesFilter(entry)) return;
        RunOnUI(() =>
        {
            LogText = string.IsNullOrEmpty(LogText)
                ? entry
                : LogText + Environment.NewLine + entry;
        });
    }

    partial void OnLogCategoryFilterChanged(string value) => RebuildLogText();

    private void RebuildLogText() =>
        LogText = string.Join(Environment.NewLine, AppLog.Entries.Where(MatchesFilter));

    private bool MatchesFilter(string entry) =>
        LogCategoryFilter == AllCategories || entry.Contains($"[{LogCategoryFilter}]");

    private static void RunOnUI(Action action)
    {
        var dispatcher = App.MainWindow?.DispatcherQueue;
        if (dispatcher is not null)
            dispatcher.TryEnqueue(() => action());
        else
            action();
    }

    [RelayCommand]
    private async Task FetchUrlAsync()
    {
        IsFetchingUrl = true;
        try
        {
            AppLog.Info(LogCategory.Firebase, "Manual URL fetch requested");

            try { await _firebase.FetchAndStoreConfigAsync(); }
            catch { AppLog.Warn(LogCategory.Firebase, "Server unreachable — using existing FCM data"); }

            if (!_config.HasValidFcmData)
            {
                AppLog.Error(LogCategory.Firebase, "No Firebase config stored — cannot fetch URL");
                return;
            }

            var newUrl = await _firebase.FetchNewServerUrlAsync();
            if (newUrl is not null && newUrl != _config.ServerUrl)
            {
                AppLog.Info(LogCategory.Firebase, $"URL changed: {_config.ServerUrl} -> {newUrl}");
                _config.ServerUrl = newUrl;
                _settings.Save();
                ServerUrl = newUrl;
                await _socketService.RestartSocketAsync();
            }
            else if (newUrl is not null)
            {
                AppLog.Info(LogCategory.Firebase, "URL unchanged — reconnecting");
                await _socketService.RestartSocketAsync();
            }
            else
            {
                AppLog.Error(LogCategory.Firebase, "Could not resolve a server URL from Firebase");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error(LogCategory.Firebase, $"Fetch URL failed: {ex.Message}");
        }
        finally
        {
            IsFetchingUrl = false;
            ServerUrl = _config.ServerUrl;
        }
    }

    [RelayCommand]
    private async Task ToggleLocalConnectionAsync(bool isEnabled)
    {
        _appSettings.UseLocalConnection = isEnabled;
        UseLocalConnection = isEnabled;

        if (isEnabled)
        {
            _appSettings.LocalhostPort = LocalhostPort;
            await TestLocalConnectionAsync();
        }
        else
        {
            _localhostDetection.Deactivate();
            LocalConnectionStatus = string.Empty;
        }

        _settings.Save();
    }

    [RelayCommand]
    private async Task TestLocalConnectionAsync()
    {
        IsTestingLocal = true;
        LocalConnectionStatus = "Probing local network...";

        try
        {
            _appSettings.LocalhostPort = LocalhostPort;
            var success = await _localhostDetection.TryActivateAsync();
            RunOnUI(() =>
            {
                if (success)
                    LocalConnectionStatus = $"Connected: {_localhostDetection.ResolvedLocalUrl}";
                else
                    LocalConnectionStatus = "No local server found on this network";
                IsTestingLocal = false;
            });
        }
        catch (Exception ex)
        {
            RunOnUI(() =>
            {
                LocalConnectionStatus = $"Error: {ex.Message}";
                IsTestingLocal = false;
            });
        }
    }

    [RelayCommand]
    private void ClearLog() => LogText = string.Empty;

    [RelayCommand]
    private async Task ResetAppAsync()
    {
        _socketService.Disconnect();
        _credentials.DeletePassword();

        // Drop the DB, then release SQLite's pooled connections so the -wal/-shm sidecars aren't
        // left locked when we wipe the directory below.
        using (var db = await _dbFactory.CreateDbContextAsync())
            await db.Database.EnsureDeletedAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        // Remove the run-on-login entry so a stale autostart can't point at a reset/wiped install.
        _startupTask.SetEnabled(false, startMinimized: false);

        // Whole-directory wipe (replaces the old file-by-file deletes that missed attachments\,
        // logs\, and the Run key). Covers db + sidecars, attachments\, logs\, settings.json,
        // contacts.vcf, credential.bin, and any future data file — a true return to first-run.
        await ClearDataDirectoryAsync();

        _appSettings.FinishedSetup = false;
    }

    /// <summary>
    /// Recursively delete <c>%LocalAppData%\BlueBubbles</c>, then recreate the empty base + logs
    /// dirs so the still-running app keeps writing without first-run path errors. Best-effort with
    /// a short retry, since a just-released SQLite/attachment handle can briefly hold a lock.
    /// </summary>
    private static async Task ClearDataDirectoryAsync()
    {
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BlueBubbles");

        if (Directory.Exists(dataDir))
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try { Directory.Delete(dataDir, recursive: true); break; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AppLog.Warn(LogCategory.App, $"Reset: data dir delete attempt {attempt + 1} failed: {ex.Message}");
                    await Task.Delay(150);
                }
            }
        }

        // Recreates both BlueBubbles\ (DB on next setup, settings) and BlueBubbles\logs\ (so file
        // logging resumes immediately rather than only after the next daily roll).
        try { Directory.CreateDirectory(AppLog.LogDirectory); } catch { }
    }
}
