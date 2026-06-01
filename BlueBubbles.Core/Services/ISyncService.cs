using BlueBubbles.Core.Models;

namespace BlueBubbles.Core.Services;

public interface ISyncService
{
    bool IsSyncing { get; }
    event EventHandler<bool>? SyncStateChanged;

    Task RunFullSyncAsync(bool skipEmptyChats = true, IProgress<SyncProgress>? progress = null, CancellationToken ct = default);
    Task RunIncrementalSyncAsync(CancellationToken ct = default);
}
