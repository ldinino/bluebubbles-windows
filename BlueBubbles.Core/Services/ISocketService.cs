using System.ComponentModel;
using System.Text.Json;
using BlueBubbles.Core.Models;

namespace BlueBubbles.Core.Services;

// INotifyPropertyChanged is part of the contract so consumers can observe State without
// downcasting the implementation to a concrete MVVM base type.
public interface ISocketService : INotifyPropertyChanged
{
    SocketState State { get; }
    string LastError { get; }

    Task ConnectAsync();
    void Disconnect();
    Task ReconnectAsync();
    Task RestartSocketAsync();

    /// <summary>Verifies the connection is actually alive and recovers it if not. Detects a half-open
    /// socket (reports Connected but the server is gone — common after sleep) via a lightweight HTTP
    /// ping, refreshes the server URL, and restarts. Safe to call repeatedly; concurrent calls coalesce.</summary>
    Task EnsureHealthyAsync();
    Task<JsonElement> SendMessageAsync(string eventName, Dictionary<string, object?> data, CancellationToken ct = default);
}
