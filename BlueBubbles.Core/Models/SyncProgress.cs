namespace BlueBubbles.Core.Models;

public enum SyncPhase
{
    Starting,
    SyncingChats,
    SyncingMessages,
    FetchingFcmConfig,
    Complete,
    Error
}

public record SyncProgress(SyncPhase Phase, int Current, int Total, string? Description);

public record DiscoveredServer(string ProjectId, string? DisplayName, string ServerUrl, bool IsReachable);
