namespace BlueBubbles.Core.Models;

/// <summary>Transport-neutral connection state the UI binds to, so views and view models never
/// name a socket. Mapped from the transport's own state by <see cref="ConnectionStatusPolicy"/>.</summary>
public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Error
}

/// <summary>What the conversation-list connection banner should show.</summary>
public enum ConnectionBanner
{
    Hidden,
    Connecting,
    Syncing,
    Disconnected
}

public static class ConnectionStatusPolicy
{
    public static ConnectionState FromSocketState(SocketState state) => state switch
    {
        SocketState.Connecting => ConnectionState.Connecting,
        SocketState.Connected => ConnectionState.Connected,
        SocketState.Error => ConnectionState.Error,
        _ => ConnectionState.Disconnected
    };

    public static ConnectionBanner ResolveBanner(ConnectionState state, bool isSyncing) => state switch
    {
        ConnectionState.Connected => isSyncing ? ConnectionBanner.Syncing : ConnectionBanner.Hidden,
        ConnectionState.Connecting => ConnectionBanner.Connecting,
        _ => ConnectionBanner.Disconnected
    };

    /// <summary>Short status label for the connection settings page.</summary>
    public static string DescribeStatus(ConnectionState state) => state switch
    {
        ConnectionState.Connected => "Connected",
        ConnectionState.Connecting => "Connecting...",
        _ => "Disconnected"
    };
}
