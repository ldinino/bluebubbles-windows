using System.Text.Json;
using BlueBubbles.Core.Configuration;
using BlueBubbles.Core.Models;
using BlueBubbles.Core.Utils;
using CommunityToolkit.Mvvm.ComponentModel;
using SocketIOClient;
using SocketIOClient.Transport;

namespace BlueBubbles.Core.Services;

public partial class SocketService : ObservableObject, ISocketService
{
    private readonly ServerConfiguration _config;
    private readonly IActionHandler _actionHandler;
    private readonly IFirebaseService _firebase;
    private readonly ISettingsService _settings;
    private readonly ISyncService _syncService;
    private readonly ILocalhostDetectionService _localhostDetection;
    private readonly IBlueBubblesApiService _api;
    private readonly AppSettings _appSettings;
    private SocketIOClient.SocketIO? _socket;
    private Timer? _reconnectTimer;
    private Timer? _heartbeatTimer;
    private readonly object _stateLock = new();
    private SocketState _lastState = SocketState.Disconnected;
    private int _reconnectAttempt;
    private int _restarting;

    // How often, while connected, to confirm the link is actually alive. A websocket can sit in a
    // half-open "Connected" state after the machine sleeps; an HTTP ping flushes that out.
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    [ObservableProperty] public partial SocketState State { get; set; }
    [ObservableProperty] public partial string LastError { get; set; }

    public SocketService(
        ServerConfiguration config,
        IActionHandler actionHandler,
        IFirebaseService firebase,
        ISettingsService settings,
        ISyncService syncService,
        ILocalhostDetectionService localhostDetection,
        IBlueBubblesApiService api,
        AppSettings appSettings)
    {
        _config = config;
        _actionHandler = actionHandler;
        _firebase = firebase;
        _settings = settings;
        _syncService = syncService;
        _localhostDetection = localhostDetection;
        _api = api;
        _appSettings = appSettings;
        LastError = string.Empty;
    }

    public async Task ConnectAsync()
    {
        if (string.IsNullOrEmpty(_config.ServerUrl)) return;

        if (_socket is not null)
        {
            _socket.Dispose();
            _socket = null;
        }

        // We own reconnection ourselves (ScheduleReconnect → RefreshUrlAndRestartAsync) so we can
        // refresh the proxy URL (ngrok/zrok rotate after the Mac sleeps) between attempts —
        // something the library's built-in loop can't do. Leaving the library's reconnection on
        // would race our restarts, so it's disabled here.
        _socket = new SocketIOClient.SocketIO(_config.ServerUrl, new SocketIOOptions
        {
            Query = new Dictionary<string, string> { ["guid"] = _config.Password },
            Transport = TransportProtocol.WebSocket,
            Reconnection = false,
            ConnectionTimeout = TimeSpan.FromSeconds(15),
            ExtraHeaders = BuildHeaders()
        });

        _socket.OnConnected += (_, _) => UpdateState(SocketState.Connected);
        _socket.OnReconnected += (_, _) => UpdateState(SocketState.Connected);
        _socket.OnReconnectAttempt += (_, _) => UpdateState(SocketState.Connecting);
        _socket.OnDisconnected += (_, _) => UpdateState(SocketState.Disconnected);
        _socket.OnError += (_, e) => UpdateState(SocketState.Error, e);
        _socket.OnReconnectError += (_, e) => UpdateState(SocketState.Error, e?.Message);
        _socket.OnReconnectFailed += (_, _) => UpdateState(SocketState.Error, "Reconnection failed");

        RegisterEvent(SocketEvents.NewMessage);
        RegisterEvent(SocketEvents.UpdatedMessage);
        RegisterEvent(SocketEvents.TypingIndicator);
        RegisterEvent(SocketEvents.ChatReadStatusChanged);
        RegisterEvent(SocketEvents.GroupNameChange);
        RegisterEvent(SocketEvents.ParticipantAdded);
        RegisterEvent(SocketEvents.ParticipantRemoved);
        RegisterEvent(SocketEvents.ParticipantLeft);
        RegisterEvent(SocketEvents.ScheduledMessageCreated);
        RegisterEvent(SocketEvents.ScheduledMessageUpdated);
        RegisterEvent(SocketEvents.ScheduledMessageDeleted);
        RegisterEvent(SocketEvents.ScheduledMessageSent);
        RegisterEvent(SocketEvents.ScheduledMessageError);

        UpdateState(SocketState.Connecting);
        await _socket.ConnectAsync();
    }

    public void Disconnect()
    {
        _reconnectTimer?.Dispose();
        _reconnectTimer = null;
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;

        if (_socket is null) return;
        _ = _socket.DisconnectAsync();
        lock (_stateLock) { _lastState = SocketState.Disconnected; }
        State = SocketState.Disconnected;
    }

    public async Task ReconnectAsync()
    {
        if (State == SocketState.Connected || string.IsNullOrEmpty(_config.ServerUrl)) return;
        State = SocketState.Connecting;
        if (_socket is not null)
            await _socket.ConnectAsync();
    }

    public async Task RestartSocketAsync()
    {
        _reconnectTimer?.Dispose();
        _reconnectTimer = null;

        if (_socket is not null)
        {
            try { await _socket.DisconnectAsync(); } catch { }
            _socket.Dispose();
            _socket = null;
        }

        lock (_stateLock) { _lastState = SocketState.Disconnected; }
        await ConnectAsync();
    }

    public async Task<JsonElement> SendMessageAsync(string eventName, Dictionary<string, object?> data, CancellationToken ct = default)
    {
        if (_socket is null)
            throw new InvalidOperationException("Socket is not connected");

        var tcs = new TaskCompletionSource<JsonElement>();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
        await using var reg = timeoutCts.Token.Register(() =>
            tcs.TrySetCanceled(timeoutCts.Token));

        await _socket.EmitAsync(eventName, response =>
        {
            try
            {
                var result = response.GetValue<JsonElement>();
                result = DecryptIfNeeded(result);
                tcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }, data);

        return await tcs.Task;
    }

    private void RegisterEvent(string eventName)
    {
        _socket!.On(eventName, response =>
        {
            try
            {
                var data = response.GetValue<JsonElement>();
                data = DecryptIfNeeded(data);
                _actionHandler.HandleEvent(eventName, data, "Socket");
            }
            catch (Exception ex)
            {
                AppLog.Error(LogCategory.Socket, $"Error handling '{eventName}': {ex}");
            }
        });
    }

    private JsonElement DecryptIfNeeded(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("encrypted", out var enc) &&
            enc.ValueKind == JsonValueKind.True &&
            data.TryGetProperty("data", out var encData) &&
            encData.ValueKind == JsonValueKind.String)
        {
            var decrypted = CryptoUtils.DecryptAESCryptoJS(encData.GetString()!, _config.Password);
            return JsonSerializer.Deserialize<JsonElement>(decrypted);
        }
        return data;
    }

    private void UpdateState(SocketState newState, string? errorMessage = null)
    {
        bool stateChanged;
        lock (_stateLock)
        {
            stateChanged = _lastState != newState;
            _lastState = newState;
        }

        if (errorMessage is not null)
            LastError = errorMessage;

        if (stateChanged)
            State = newState;

        switch (newState)
        {
            case SocketState.Connected:
                _reconnectTimer?.Dispose();
                _reconnectTimer = null;
                _reconnectAttempt = 0;
                StartHeartbeat();
                AppLog.Info(LogCategory.Socket, "Connected");
                if (_appSettings.FinishedSetup)
                    _ = OnConnectedAsync();
                break;
            case SocketState.Error:
            case SocketState.Disconnected:
                _heartbeatTimer?.Dispose();
                _heartbeatTimer = null;
                ScheduleReconnect();
                break;
        }
    }

    private void StartHeartbeat()
    {
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = new Timer(
            _ => _ = HeartbeatTickAsync(), null, HeartbeatInterval, HeartbeatInterval);
    }

    private async Task HeartbeatTickAsync()
    {
        if (State != SocketState.Connected) return;
        try
        {
            var resp = await _api.PingAsync();
            if (resp.Status != 200)
            {
                AppLog.Warn(LogCategory.Socket, $"Heartbeat ping returned {resp.Status}; restarting");
                await EnsureHealthyAsync();
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogCategory.Socket, $"Heartbeat ping failed ({ex.Message}); restarting");
            await EnsureHealthyAsync();
        }
    }

    public async Task EnsureHealthyAsync()
    {
        if (string.IsNullOrEmpty(_config.ServerUrl)) return;

        // Not connected → just (re)connect. Connected → verify it's not half-open with a ping.
        if (State == SocketState.Connected)
        {
            try
            {
                var resp = await _api.PingAsync();
                if (resp.Status == 200) return;
            }
            catch (Exception ex)
            {
                // Fall through to restart, but keep the cause visible in diagnostics.
                AppLog.Debug(LogCategory.Socket, $"Health ping failed ({ex.Message}); restarting");
            }
        }

        AppLog.Info(LogCategory.Socket, "EnsureHealthy: connection unhealthy, restarting");
        await RefreshUrlAndRestartAsync();
    }

    private async Task OnConnectedAsync()
    {
        try { await _syncService.RunIncrementalSyncAsync(); }
        catch (Exception ex) { AppLog.Warn(LogCategory.Socket, $"Incremental sync on connect failed: {ex.Message}"); }

        if (_appSettings.UseLocalConnection)
        {
            try { await _localhostDetection.TryActivateAsync(); }
            catch (Exception ex) { AppLog.Warn(LogCategory.Socket, $"Localhost detection on connect failed: {ex.Message}"); }
        }
    }

    private void ScheduleReconnect()
    {
        _reconnectTimer?.Dispose();
        var delaySec = Math.Min(5 * (1 << Math.Min(_reconnectAttempt, 5)), 60);
        _reconnectAttempt++;
        AppLog.Info(LogCategory.Socket, $"Scheduling reconnect attempt {_reconnectAttempt} in {delaySec}s");
        _reconnectTimer = new Timer(_ =>
        {
            if (State == SocketState.Connected) return;
            _ = RefreshUrlAndRestartAsync();
        }, null, TimeSpan.FromSeconds(delaySec), Timeout.InfiniteTimeSpan);
    }

    private async Task RefreshUrlAndRestartAsync()
    {
        // Coalesce concurrent restart requests (reconnect timer + heartbeat + resume/network events).
        if (Interlocked.Exchange(ref _restarting, 1) == 1) return;
        try
        {
            await RefreshUrlAndRestartCoreAsync();
        }
        finally
        {
            Interlocked.Exchange(ref _restarting, 0);
        }
    }

    private async Task RefreshUrlAndRestartCoreAsync()
    {
        try
        {
            var newUrl = await _firebase.FetchNewServerUrlAsync();
            if (newUrl is not null && newUrl != _config.ServerUrl)
            {
                AppLog.Info(LogCategory.Socket, $"Server URL changed: {_config.ServerUrl} -> {newUrl}");
                _config.ServerUrl = newUrl;
                _settings.Save();
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogCategory.Socket, $"Firebase URL refresh failed: {ex.Message}");
        }

        try
        {
            await RestartSocketAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error(LogCategory.Socket, $"Restart failed: {ex.Message}");
            UpdateState(SocketState.Error, ex.Message);
        }
    }

    private Dictionary<string, string> BuildHeaders()
    {
        var headers = new Dictionary<string, string>();

        if (Uri.TryCreate(_config.ServerUrl, UriKind.Absolute, out var uri))
        {
            var host = uri.Host;
            if (host.Contains("ngrok", StringComparison.OrdinalIgnoreCase))
                headers["ngrok-skip-browser-warning"] = "true";
            else if (host.Contains("zrok", StringComparison.OrdinalIgnoreCase))
                headers["skip_zrok_interstitial"] = "true";
        }

        foreach (var kv in _config.CustomHeaders)
            headers[kv.Key] = kv.Value;

        return headers;
    }
}
