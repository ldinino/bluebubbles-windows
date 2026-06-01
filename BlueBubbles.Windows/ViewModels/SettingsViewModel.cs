using BlueBubbles.Core.Configuration;
using BlueBubbles.Core.Data;
using BlueBubbles.Core.Models;
using BlueBubbles.Core.Services;
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
    private readonly AppSettings _appSettings;

    [ObservableProperty] public partial string LogText { get; set; } = string.Empty;

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

    public event EventHandler? ResetRequested;

    public SettingsViewModel(
        ISocketService socketService,
        IFirebaseService firebase,
        ISettingsService settings,
        ServerConfiguration config,
        IContactResolverService contacts,
        ICredentialService credentials,
        IDbContextFactory<BlueBubblesDbContext> dbFactory,
        ILocalhostDetectionService localhostDetection,
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

        LogText = string.Join(Environment.NewLine, AppLog.Entries);
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
            AppLog.Error($"vCard import failed: {ex.Message}");
        }
    }

    private void OnLogEntry(string entry)
    {
        RunOnUI(() =>
        {
            LogText = string.IsNullOrEmpty(LogText)
                ? entry
                : LogText + Environment.NewLine + entry;
        });
    }

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
            AppLog.Info("Manual URL fetch requested");

            try { await _firebase.FetchAndStoreConfigAsync(); }
            catch { AppLog.Warn("Server unreachable — using existing FCM data"); }

            if (!_config.HasValidFcmData)
            {
                AppLog.Error("No Firebase config stored — cannot fetch URL");
                return;
            }

            var newUrl = await _firebase.FetchNewServerUrlAsync();
            if (newUrl is not null && newUrl != _config.ServerUrl)
            {
                AppLog.Info($"URL changed: {_config.ServerUrl} -> {newUrl}");
                _config.ServerUrl = newUrl;
                _settings.Save();
                ServerUrl = newUrl;
                await _socketService.RestartSocketAsync();
            }
            else if (newUrl is not null)
            {
                AppLog.Info("URL unchanged — reconnecting");
                await _socketService.RestartSocketAsync();
            }
            else
            {
                AppLog.Error("Could not resolve a server URL from Firebase");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"Fetch URL failed: {ex.Message}");
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

        using (var db = await _dbFactory.CreateDbContextAsync())
            await db.Database.EnsureDeletedAsync();

        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BlueBubbles");

        foreach (var file in new[] { "settings.json", "contacts.vcf" })
        {
            try { File.Delete(Path.Combine(dataDir, file)); } catch { }
        }

        _appSettings.FinishedSetup = false;
        ResetRequested?.Invoke(this, EventArgs.Empty);
    }
}
