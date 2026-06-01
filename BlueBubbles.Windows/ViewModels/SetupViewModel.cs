using BlueBubbles.Core.Configuration;
using BlueBubbles.Core.Models;
using BlueBubbles.Core.Services;
using BlueBubbles.Core.Utils;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlueBubbles.Windows.ViewModels;

public enum SetupStep { Welcome, ServerConnect, GoogleSignIn, Syncing, Complete }

public partial class SetupViewModel : ObservableObject
{
    private readonly AppSettings _appSettings;
    private readonly ServerConfiguration _serverConfig;
    private readonly IBlueBubblesApiService _api;
    private readonly ICredentialService _credentials;
    private readonly ISettingsService _settingsService;
    private readonly ISyncService _syncService;
    private readonly ISocketService _socketService;
    private readonly IServerDiscoveryService _discovery;

    [ObservableProperty] public partial SetupStep CurrentStep { get; set; }
    [ObservableProperty] public partial string ServerUrl { get; set; }
    [ObservableProperty] public partial string Password { get; set; }
    [ObservableProperty] public partial string StatusMessage { get; set; }
    [ObservableProperty] public partial double SyncProgressValue { get; set; }
    [ObservableProperty] public partial bool IsConnecting { get; set; }
    [ObservableProperty] public partial string? ErrorMessage { get; set; }
    [ObservableProperty] public partial bool IsSyncing { get; set; }
    [ObservableProperty] public partial List<DiscoveredServer> DiscoveredServers { get; set; }
    [ObservableProperty] public partial bool IsDiscovering { get; set; }
    [ObservableProperty] public partial bool IsBrowserAuthInProgress { get; set; }
    [ObservableProperty] public partial string? GoogleAccessToken { get; set; }

    public string GoogleOAuthUrl => _discovery.BuildGoogleOAuthUrl();

    public SetupViewModel(
        AppSettings appSettings,
        ServerConfiguration serverConfig,
        IBlueBubblesApiService api,
        ICredentialService credentials,
        ISettingsService settingsService,
        ISyncService syncService,
        ISocketService socketService,
        IServerDiscoveryService discovery)
    {
        _appSettings = appSettings;
        _serverConfig = serverConfig;
        _api = api;
        _credentials = credentials;
        _settingsService = settingsService;
        _syncService = syncService;
        _socketService = socketService;
        _discovery = discovery;

        ServerUrl = string.Empty;
        Password = string.Empty;
        StatusMessage = string.Empty;
        DiscoveredServers = [];
    }

    [RelayCommand]
    private void GetStarted() => CurrentStep = SetupStep.ServerConnect;

    [RelayCommand]
    private void GoToGoogleSignIn() => CurrentStep = SetupStep.GoogleSignIn;

    private CancellationTokenSource? _browserAuthCts;

    [RelayCommand]
    private async Task SignInViaBrowserAsync()
    {
        if (IsBrowserAuthInProgress) return;

        IsBrowserAuthInProgress = true;
        ErrorMessage = null;
        StatusMessage = "Waiting for browser sign-in...";

        _browserAuthCts?.Cancel();
        _browserAuthCts?.Dispose();
        _browserAuthCts = new CancellationTokenSource();

        try
        {
            var token = await OAuthLoopbackListener.ListenForTokenAsync(
                GoogleOAuthUrl, _browserAuthCts.Token);

            if (token is not null)
            {
                OnGoogleTokenReceived(token);
            }
            else
            {
                ErrorMessage = "Browser sign-in was cancelled.";
                StatusMessage = string.Empty;
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Browser sign-in failed: {ex.Message}";
            StatusMessage = string.Empty;
        }
        finally
        {
            IsBrowserAuthInProgress = false;
        }
    }

    /// <summary>Tears down any in-flight browser sign-in (stops the loopback listener and clears
    /// the busy flag). Safe to call when nothing is pending. Called when leaving the connect step
    /// so a started-but-abandoned attempt can't leave port 8641 bound or the flag wedged —
    /// which otherwise breaks the next attempt (e.g. after a reset, since this VM is a singleton).</summary>
    public void CancelBrowserAuth()
    {
        _browserAuthCts?.Cancel();
        if (IsBrowserAuthInProgress)
        {
            IsBrowserAuthInProgress = false;
            StatusMessage = string.Empty;
        }
    }

    [RelayCommand]
    private async Task ConnectManualAsync()
    {
        ErrorMessage = null;

        var sanitized = AddressHelpers.SanitizeServerAddress(ServerUrl);
        if (sanitized is null)
        {
            ErrorMessage = "Please enter a valid server URL.";
            return;
        }

        if (string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "Please enter the server password.";
            return;
        }

        IsConnecting = true;
        StatusMessage = "Testing connection...";

        try
        {
            _serverConfig.ServerUrl = sanitized;
            _serverConfig.Password = Password;

            var response = await _api.GetServerInfoAsync();
            if (response.Status != 200)
            {
                ErrorMessage = response.Error?.ErrorMessage ?? "Connection failed.";
                return;
            }

            _appSettings.ServerPrivateAPI = response.Data?.PrivateApi;
            _appSettings.ServerAddress = sanitized;
            _credentials.SavePassword(Password);

            StatusMessage = "Connected! Starting sync...";
            CurrentStep = SetupStep.Syncing;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            ErrorMessage = "Authentication failed. Check your password.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Connection failed: {ex.Message}";
        }
        finally
        {
            IsConnecting = false;
        }
    }

    public void OnGoogleTokenReceived(string accessToken)
    {
        GoogleAccessToken = accessToken;
        CurrentStep = SetupStep.ServerConnect;
        _ = DiscoverServersInternalAsync();
    }

    private async Task DiscoverServersInternalAsync()
    {
        if (GoogleAccessToken is null) return;

        IsDiscovering = true;
        ErrorMessage = null;
        StatusMessage = "Discovering BlueBubbles servers...";

        try
        {
            DiscoveredServers = await _discovery.DiscoverServersAsync(GoogleAccessToken);
            if (DiscoveredServers.Count == 0)
                ErrorMessage = "No BlueBubbles servers found in your Firebase projects.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Discovery failed: {ex.Message}";
        }
        finally
        {
            IsDiscovering = false;
            StatusMessage = string.Empty;
        }
    }

    [RelayCommand]
    private async Task ConnectDiscoveredAsync(DiscoveredServer server)
    {
        if (string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "Please enter the server password.";
            return;
        }

        ServerUrl = server.ServerUrl;
        await ConnectManualAsync();
    }

    [RelayCommand]
    private async Task RunSyncAsync()
    {
        IsSyncing = true;
        ErrorMessage = null;
        SyncProgressValue = 0;
        StatusMessage = "Starting sync...";

        var progress = new Progress<SyncProgress>(p =>
        {
            StatusMessage = p.Description ?? string.Empty;
            if (p.Total > 0)
                SyncProgressValue = (double)p.Current / p.Total * 100;

            if (p.Phase == SyncPhase.Complete)
            {
                IsSyncing = false;
                CurrentStep = SetupStep.Complete;
            }
            else if (p.Phase == SyncPhase.Error)
            {
                IsSyncing = false;
                ErrorMessage = p.Description;
            }
        });

        try
        {
            await _syncService.RunFullSyncAsync(skipEmptyChats: true, progress);
        }
        catch (Exception ex)
        {
            IsSyncing = false;
            var inner = ex.InnerException?.Message;
            ErrorMessage = inner is not null
                ? $"Sync failed: {inner}"
                : $"Sync failed: {ex.Message}";
            AppLog.Error($"Sync exception: {ex}");
        }
    }

    [RelayCommand]
    private async Task FinishSetupAsync()
    {
        _appSettings.FinishedSetup = true;
        _settingsService.Save();

        try { await _socketService.ConnectAsync(); }
        catch { /* socket will retry */ }
    }
}
