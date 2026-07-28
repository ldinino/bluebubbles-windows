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
    /// <summary>Drops the cached copy of <paramref name="attachmentGuid"/> so the next
    /// <see cref="DownloadAsync"/> refetches it. Used when a file is present but unusable (a
    /// truncated download, or bytes no codec on this machine can decode) — without this the
    /// bad file is returned forever and the attachment is permanently broken.</summary>
    Task InvalidateAsync(string attachmentGuid, CancellationToken ct = default);
    Task PurgeCacheAsync(CancellationToken ct = default);
    long GetCacheSizeBytes();
}
