using System.Text.Json;
using BlueBubbles.Core.Models;

namespace BlueBubbles.Core.Services;

public interface ISocketService
{
    SocketState State { get; }
    string LastError { get; }

    Task ConnectAsync();
    void Disconnect();
    Task ReconnectAsync();
    Task RestartSocketAsync();
    Task<JsonElement> SendMessageAsync(string eventName, Dictionary<string, object?> data, CancellationToken ct = default);
}
