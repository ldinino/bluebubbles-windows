using BlueBubbles.Core.Models;

namespace BlueBubbles.Core.Services;

public interface ISyncService
{
    bool IsSyncing { get; }
    event EventHandler<bool>? SyncStateChanged;

    Task RunFullSyncAsync(bool skipEmptyChats = true, IProgress<SyncProgress>? progress = null, CancellationToken ct = default);
    Task RunIncrementalSyncAsync(CancellationToken ct = default);

    /// <summary>One-time full true-up after an upgrade that changed how the cache converges; a no-op
    /// once the cache is current. Returns true if a heal ran.</summary>
    Task<bool> RunHealIfNeededAsync(CancellationToken ct = default);

    /// <summary>Lean foreground reconcile of the chat list against the server (catches conversation
    /// deletes, which the server never pushes over the socket). GUID-diff only, no message refetch;
    /// reloads the list only if a chat was actually removed.</summary>
    Task ReconcileChatsAsync(CancellationToken ct = default);
}
