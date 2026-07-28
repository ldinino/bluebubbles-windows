using System.Net;
using BlueBubbles.Core.Data.Entities;
using BlueBubbles.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BlueBubbles.Windows.ViewModels;

public enum AttachmentState
{
    NotDownloaded,
    Downloading,
    Cached,
    Error
}

public enum AttachmentCategory
{
    Image,
    Video,
    Audio,
    Other
}

public partial class AttachmentViewModel : ObservableObject
{
    private readonly IAttachmentCacheService _cache;
    private readonly string _attachmentGuid;
    private CancellationTokenSource? _downloadCts;
    private long _generationCounter;

    public string AttachmentGuid => _attachmentGuid;
    public string? TransferName { get; }
    public string? MimeType { get; }
    public long TotalBytes { get; }
    public int? Width { get; }
    public int? Height { get; }
    public bool HasLivePhoto { get; }
    public AttachmentCategory Category { get; }

    public string FormattedSize => FormatBytes(TotalBytes);
    public string DisplayName => TransferName ?? "Attachment";

    [ObservableProperty] public partial AttachmentState State { get; set; }
    [ObservableProperty] public partial double Progress { get; set; }
    [ObservableProperty] public partial string? LocalPath { get; set; }
    [ObservableProperty] public partial string? ErrorMessage { get; set; }

    public AttachmentViewModel(AttachmentEntity entity, IAttachmentCacheService cache)
    {
        _cache = cache;
        _attachmentGuid = entity.Guid;
        TransferName = entity.TransferName;
        MimeType = entity.MimeType;
        TotalBytes = entity.TotalBytes;
        Width = entity.Width;
        Height = entity.Height;
        HasLivePhoto = entity.HasLivePhoto;
        Category = CategorizeFromMime(entity.MimeType);

        var existing = cache.GetCachedPath(entity.Guid);
        if (existing is not null)
        {
            LocalPath = existing;
            State = AttachmentState.Cached;
        }
        else
        {
            State = AttachmentState.NotDownloaded;
        }
    }

    /// <summary>Wraps a freshly-picked local file as an already-"cached" attachment so an
    /// optimistic outgoing bubble can render the real image/thumbnail instead of a filename.
    /// No server GUID exists yet; download is a no-op (state is Cached).</summary>
    private AttachmentViewModel(string localPath)
    {
        _cache = null!; // never used: Cached state short-circuits DownloadAsync
        _attachmentGuid = $"local-{Guid.NewGuid():N}";
        TransferName = Path.GetFileName(localPath);
        MimeType = GuessMimeFromPath(localPath);
        Category = CategorizeFromMime(MimeType);
        try { TotalBytes = new FileInfo(localPath).Length; } catch { TotalBytes = 0; }
        LocalPath = localPath;
        State = AttachmentState.Cached;
    }

    public static AttachmentViewModel CreateLocal(string localPath) => new(localPath);

    private static string GuessMimeFromPath(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".heic" or ".heif" => "image/heic",
        ".bmp" => "image/bmp",
        ".tif" or ".tiff" => "image/tiff",
        ".mp4" or ".m4v" => "video/mp4",
        ".mov" => "video/quicktime",
        ".webm" => "video/webm",
        ".avi" => "video/x-msvideo",
        ".mp3" => "audio/mpeg",
        ".m4a" => "audio/mp4",
        ".wav" => "audio/wav",
        ".caf" => "audio/x-caf",
        _ => "application/octet-stream"
    };

    public async Task DownloadAsync()
    {
        if (State is AttachmentState.Downloading or AttachmentState.Cached) return;
        await DownloadInternalAsync(force: false);
    }

    private async Task DownloadInternalAsync(bool force)
    {
        State = AttachmentState.Downloading;
        Progress = 0;
        ErrorMessage = null;
        _downloadCts?.Cancel();
        _downloadCts = new CancellationTokenSource();
        var generation = Interlocked.Increment(ref _generationCounter);

        try
        {
            var progressReporter = new Progress<double>(p =>
            {
                if (Interlocked.Read(ref _generationCounter) == generation)
                    Progress = p;
            });

            var path = await _cache.DownloadAsync(
                _attachmentGuid, TransferName, progressReporter, force, _downloadCts.Token);

            if (Interlocked.Read(ref _generationCounter) != generation) return;

            LocalPath = path;
            State = AttachmentState.Cached;
        }
        catch (OperationCanceledException)
        {
            if (Interlocked.Read(ref _generationCounter) == generation)
                State = AttachmentState.NotDownloaded;
        }
        catch (Exception ex)
        {
            if (Interlocked.Read(ref _generationCounter) == generation)
            {
                State = AttachmentState.Error;
                ErrorMessage = Describe(ex, force);
            }
        }
    }

    /// <summary>Turns a download failure into something a person can act on. The raw text
    /// ("Response status code does not indicate success: 500") says nothing useful, and a 500 from
    /// this endpoint almost always means the Mac hasn't got the file on disk.</summary>
    private static string Describe(Exception ex, bool wasForced) => ex switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.InternalServerError } => wasForced
            ? "The Mac still couldn't produce this attachment. It may no longer be in iCloud."
            : "This attachment isn't on the Mac yet. Retry to pull it down from iCloud.",
        HttpRequestException { StatusCode: HttpStatusCode.NotFound } =>
            "The server no longer has this attachment.",
        HttpRequestException { StatusCode: { } code } =>
            $"The server refused the download ({(int)code}).",
        HttpRequestException => "Couldn't reach the server.",
        TaskCanceledException => "The download timed out.",
        _ => "Couldn't download this attachment."
    };

    public void CancelDownload()
    {
        _downloadCts?.Cancel();
    }

    /// <summary>Reports that the cached file is present but nothing on this machine could decode it
    /// (a truncated download, or a codec Windows lacks — HEIC without the HEIF extension is the
    /// common one). Moves the attachment to <see cref="AttachmentState.Error"/> so the UI shows a
    /// retry affordance instead of a silently blank frame, which is what used to happen.</summary>
    public void MarkUnreadable(string? reason = null)
    {
        // A local (not-yet-sent) attachment has no server copy to refetch, so retrying is pointless.
        if (_cache is null) return;
        if (State == AttachmentState.Error) return;

        Interlocked.Increment(ref _generationCounter);
        State = AttachmentState.Error;
        ErrorMessage = reason ?? "This file couldn't be displayed.";
    }

    /// <summary>Re-fetches the attachment from scratch, dropping the cached copy first. Plain
    /// <see cref="DownloadAsync"/> would short-circuit on the existing (bad) file. Goes through the
    /// server's force endpoint so an iCloud-purged file is pulled onto the Mac first — that is the
    /// case a plain download reports as a 500.</summary>
    public async Task RetryAsync()
    {
        if (_cache is null) return;

        Interlocked.Increment(ref _generationCounter);
        _downloadCts?.Cancel();

        try { await _cache.InvalidateAsync(_attachmentGuid); }
        catch { /* best effort — the download below is what matters */ }

        LocalPath = null;
        ErrorMessage = null;
        await DownloadInternalAsync(force: true);
    }

    private static AttachmentCategory CategorizeFromMime(string? mimeType)
    {
        if (string.IsNullOrEmpty(mimeType)) return AttachmentCategory.Other;
        if (mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return AttachmentCategory.Image;
        if (mimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) return AttachmentCategory.Video;
        if (mimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) return AttachmentCategory.Audio;
        return AttachmentCategory.Other;
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):F1} MB",
        _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB"
    };
}
