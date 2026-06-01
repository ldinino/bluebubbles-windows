namespace BlueBubbles.Core.Services;

public interface IAttachmentCacheService
{
    bool IsCached(string attachmentGuid);
    string? GetCachedPath(string attachmentGuid);
    Task<string> DownloadAsync(string attachmentGuid, string? transferName,
        IProgress<double>? progress = null, CancellationToken ct = default);
    Task PurgeCacheAsync(CancellationToken ct = default);
    long GetCacheSizeBytes();
}
