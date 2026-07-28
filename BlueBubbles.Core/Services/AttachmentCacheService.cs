using System.Collections.Concurrent;

namespace BlueBubbles.Core.Services;

public class AttachmentCacheService : IAttachmentCacheService
{
    private readonly IBlueBubblesApiService _api;
    private readonly string _cacheRoot;
    private readonly SemaphoreSlim _downloadGate = new(2, 2);

    // In-flight downloads keyed by attachment GUID. Coalescing here means N bubbles asking for
    // the same attachment share ONE request, and — unlike the per-GUID lock objects this
    // replaced — there's no window where one caller removes the lock another is still holding.
    private readonly ConcurrentDictionary<string, Task<string>> _inFlight = new();

    // Partial downloads land here first and are renamed into place only once complete, so a
    // crash or a dropped connection can never leave a truncated file that GetCachedPath would
    // then serve forever.
    private const string PartialSuffix = ".partial";

    public AttachmentCacheService(IBlueBubblesApiService api, string cacheRoot)
    {
        _api = api;
        _cacheRoot = cacheRoot;
        Directory.CreateDirectory(_cacheRoot);
    }

    public bool IsCached(string attachmentGuid) => GetCachedPath(attachmentGuid) is not null;

    public string? GetCachedPath(string attachmentGuid)
    {
        var dir = GetAttachmentDir(attachmentGuid);
        if (!Directory.Exists(dir)) return null;

        // Skip in-progress temp files and zero-byte leftovers: an empty file is never a valid
        // attachment, and returning one presents as a permanently blank image with no way back.
        foreach (var path in Directory.EnumerateFiles(dir))
        {
            if (path.EndsWith(PartialSuffix, StringComparison.Ordinal)) continue;
            long length;
            try { length = new FileInfo(path).Length; }
            catch { continue; }
            if (length > 0) return path;
        }
        return null;
    }

    public Task<string> DownloadAsync(string attachmentGuid, string? transferName,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var existing = GetCachedPath(attachmentGuid);
        if (existing is not null) return Task.FromResult(existing);
        return AwaitSharedDownloadAsync(attachmentGuid, transferName, progress, ct);
    }

    private async Task<string> AwaitSharedDownloadAsync(string attachmentGuid, string? transferName,
        IProgress<double>? progress, CancellationToken ct)
    {
        var shared = GetOrStartDownload(attachmentGuid, transferName, progress);

        // WaitAsync gives each caller its own cancellation without tearing down a download other
        // bubbles are still waiting on (a merged conversation can show the same attachment twice).
        return await shared.WaitAsync(ct);
    }

    private Task<string> GetOrStartDownload(string attachmentGuid, string? transferName,
        IProgress<double>? progress)
    {
        if (_inFlight.TryGetValue(attachmentGuid, out var running)) return running;

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shared = _inFlight.GetOrAdd(attachmentGuid, tcs.Task);

        // Lost the race — someone else owns this download; just await theirs.
        if (!ReferenceEquals(shared, tcs.Task)) return shared;

        _ = RunOwnedDownloadAsync(attachmentGuid, transferName, progress, tcs);
        return tcs.Task;
    }

    private async Task RunOwnedDownloadAsync(string attachmentGuid, string? transferName,
        IProgress<double>? progress, TaskCompletionSource<string> tcs)
    {
        // Every waiter may have walked away (each awaits through WaitAsync with its own token), so
        // make sure a failure is always observed rather than surfacing as an unobserved exception.
        _ = tcs.Task.ContinueWith(static t => _ = t.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

        try
        {
            tcs.SetResult(await DownloadCoreAsync(attachmentGuid, transferName, progress));
        }
        catch (Exception ex)
        {
            tcs.SetException(ex);
        }
        finally
        {
            _inFlight.TryRemove(attachmentGuid, out _);
        }
    }

    private async Task<string> DownloadCoreAsync(string attachmentGuid, string? transferName,
        IProgress<double>? progress)
    {
        var existing = GetCachedPath(attachmentGuid);
        if (existing is not null) return existing;

        await _downloadGate.WaitAsync();
        try
        {
            var bytes = await _api.DownloadAttachmentAsync(attachmentGuid, progress: progress);

            // An empty body is a server-side failure, not a zero-length attachment. Failing loudly
            // keeps it out of the cache so a retry can actually succeed.
            if (bytes is null || bytes.Length == 0)
                throw new InvalidOperationException(
                    $"The server returned no data for attachment {attachmentGuid}.");

            var dir = GetAttachmentDir(attachmentGuid);
            Directory.CreateDirectory(dir);

            var fileName = SanitizeFileName(transferName) ?? "attachment";
            var filePath = Path.Combine(dir, fileName);
            await WriteAtomicAsync(filePath, bytes);
            return filePath;
        }
        finally
        {
            _downloadGate.Release();
        }
    }

    /// <summary>Writes to a uniquely-named sibling temp file and renames it into place, so readers
    /// only ever see a complete file and two writers can never collide on the temp path.</summary>
    private static async Task WriteAtomicAsync(string filePath, byte[] bytes)
    {
        var tempPath = $"{filePath}.{Guid.NewGuid():N}{PartialSuffix}";
        try
        {
            await File.WriteAllBytesAsync(tempPath, bytes);
            File.Move(tempPath, filePath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
    }

    public async Task<string?> SeedFromLocalFileAsync(string attachmentGuid, string sourceFilePath,
        CancellationToken ct = default)
    {
        var existing = GetCachedPath(attachmentGuid);
        if (existing is not null) return existing;

        // The source can be a transient file (e.g. a clipboard-paste temp) — tolerate it
        // disappearing; the attachment is still downloadable from the server later.
        if (!File.Exists(sourceFilePath)) return null;

        var dir = GetAttachmentDir(attachmentGuid);
        Directory.CreateDirectory(dir);

        var fileName = SanitizeFileName(Path.GetFileName(sourceFilePath)) ?? "attachment";
        var filePath = Path.Combine(dir, fileName);
        var tempPath = $"{filePath}.{Guid.NewGuid():N}{PartialSuffix}";
        try
        {
            await Task.Run(() =>
            {
                File.Copy(sourceFilePath, tempPath, overwrite: true);
                File.Move(tempPath, filePath, overwrite: true);
            }, ct);
            return filePath;
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            return null;
        }
    }

    public async Task InvalidateAsync(string attachmentGuid, CancellationToken ct = default)
    {
        var dir = GetAttachmentDir(attachmentGuid);
        await Task.Run(() =>
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch { }
        }, ct);
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
        var sanitized = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();

        // "." / ".." survive the invalid-char filter but resolve to directories, so a
        // server-supplied transferName could otherwise steer the write outside the cache folder.
        if (sanitized.Length == 0 || sanitized == "." || sanitized == "..") return null;
        return sanitized;
    }
}
