using System.Collections.Concurrent;

namespace BlueBubbles.Core.Services;

public class AttachmentCacheService : IAttachmentCacheService
{
    private readonly IBlueBubblesApiService _api;
    private readonly string _cacheRoot;
    private readonly SemaphoreSlim _downloadGate = new(2, 2);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _perGuidLocks = new();

    public AttachmentCacheService(IBlueBubblesApiService api, string cacheRoot)
    {
        _api = api;
        _cacheRoot = cacheRoot;
        Directory.CreateDirectory(_cacheRoot);
    }

    public bool IsCached(string attachmentGuid)
    {
        var dir = GetAttachmentDir(attachmentGuid);
        return Directory.Exists(dir) && Directory.EnumerateFiles(dir).Any();
    }

    public string? GetCachedPath(string attachmentGuid)
    {
        var dir = GetAttachmentDir(attachmentGuid);
        if (!Directory.Exists(dir)) return null;
        return Directory.EnumerateFiles(dir).FirstOrDefault();
    }

    public async Task<string> DownloadAsync(string attachmentGuid, string? transferName,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var existing = GetCachedPath(attachmentGuid);
        if (existing is not null) return existing;

        var guidLock = _perGuidLocks.GetOrAdd(attachmentGuid, _ => new SemaphoreSlim(1, 1));
        await guidLock.WaitAsync(ct);
        try
        {
            existing = GetCachedPath(attachmentGuid);
            if (existing is not null) return existing;

            await _downloadGate.WaitAsync(ct);
            try
            {
                var bytes = await _api.DownloadAttachmentAsync(attachmentGuid, progress: progress, ct: ct);
                var dir = GetAttachmentDir(attachmentGuid);
                Directory.CreateDirectory(dir);

                var fileName = SanitizeFileName(transferName) ?? "attachment";
                var filePath = Path.Combine(dir, fileName);
                await File.WriteAllBytesAsync(filePath, bytes, ct);
                return filePath;
            }
            finally
            {
                _downloadGate.Release();
            }
        }
        finally
        {
            guidLock.Release();
            _perGuidLocks.TryRemove(attachmentGuid, out _);
        }
    }

    public async Task<string?> SeedFromLocalFileAsync(string attachmentGuid, string sourceFilePath,
        CancellationToken ct = default)
    {
        var existing = GetCachedPath(attachmentGuid);
        if (existing is not null) return existing;

        var guidLock = _perGuidLocks.GetOrAdd(attachmentGuid, _ => new SemaphoreSlim(1, 1));
        await guidLock.WaitAsync(ct);
        try
        {
            existing = GetCachedPath(attachmentGuid);
            if (existing is not null) return existing;

            // The source can be a transient file (e.g. a clipboard-paste temp) — tolerate it
            // disappearing; the attachment is still downloadable from the server later.
            if (!File.Exists(sourceFilePath)) return null;

            var dir = GetAttachmentDir(attachmentGuid);
            Directory.CreateDirectory(dir);

            var fileName = SanitizeFileName(Path.GetFileName(sourceFilePath)) ?? "attachment";
            var filePath = Path.Combine(dir, fileName);
            await Task.Run(() => File.Copy(sourceFilePath, filePath, overwrite: true), ct);
            return filePath;
        }
        finally
        {
            guidLock.Release();
            _perGuidLocks.TryRemove(attachmentGuid, out _);
        }
    }

    public async Task PurgeCacheAsync(CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            if (Directory.Exists(_cacheRoot))
                Directory.Delete(_cacheRoot, recursive: true);
            Directory.CreateDirectory(_cacheRoot);
        }, ct);
    }

    public long GetCacheSizeBytes()
    {
        if (!Directory.Exists(_cacheRoot)) return 0;
        return new DirectoryInfo(_cacheRoot)
            .EnumerateFiles("*", SearchOption.AllDirectories)
            .Sum(f => f.Length);
    }

    private string GetAttachmentDir(string attachmentGuid)
    {
        var safe = attachmentGuid.Replace('/', '_').Replace('\\', '_');
        return Path.Combine(_cacheRoot, safe);
    }

    private static string? SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return sanitized.Length > 0 ? sanitized : null;
    }
}
