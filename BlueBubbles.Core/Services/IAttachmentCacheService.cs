namespace BlueBubbles.Core.Services;

public interface IAttachmentCacheService
{
    bool IsCached(string attachmentGuid);
    string? GetCachedPath(string attachmentGuid);
    Task<string> DownloadAsync(string attachmentGuid, string? transferName,
        IProgress<double>? progress = null, CancellationToken ct = default);
    /// <summary>Seeds the cache for <paramref name="attachmentGuid"/> by copying an
    /// already-local file (e.g. one we just sent) instead of downloading from the server.
    /// Returns the cached path, or null when the source file no longer exists.</summary>
    Task<string?> SeedFromLocalFileAsync(string attachmentGuid, string sourceFilePath,
        CancellationToken ct = default);
    Task PurgeCacheAsync(CancellationToken ct = default);
    long GetCacheSizeBytes();
}
