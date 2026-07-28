using BlueBubbles.Core.Services;

namespace BlueBubbles.Windows.Tests;

/// <summary>
/// Cache-integrity regression tests. The cache is the reason a broken image used to stay broken
/// forever: a truncated or empty file was written straight to the final path and then handed back
/// by <see cref="IAttachmentCacheService.GetCachedPath"/> on every subsequent load.
/// </summary>
public class AttachmentCacheServiceTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "bb-cache-tests-" + Guid.NewGuid().ToString("N"));

    private (AttachmentCacheService Cache, MockApiService Api) Create()
    {
        var api = new MockApiService();
        return (new AttachmentCacheService(api, _root), api);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task DownloadAsync_WritesBytesAndReturnsPath()
    {
        var (cache, api) = Create();
        api.DownloadAttachmentFunc = _ => Task.FromResult(new byte[] { 1, 2, 3, 4 });

        var path = await cache.DownloadAsync("guid-1", "photo.jpg");

        Assert.True(File.Exists(path));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(path));
        Assert.Equal("photo.jpg", Path.GetFileName(path));
    }

    [Fact]
    public async Task DownloadAsync_EmptyResponse_ThrowsAndCachesNothing()
    {
        var (cache, api) = Create();
        api.DownloadAttachmentFunc = _ => Task.FromResult(Array.Empty<byte>());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => cache.DownloadAsync("guid-empty", "photo.jpg"));

        // Nothing may be left behind, or the empty file becomes a permanently blank image.
        Assert.Null(cache.GetCachedPath("guid-empty"));
        Assert.False(cache.IsCached("guid-empty"));
    }

    [Fact]
    public async Task DownloadAsync_FailureMidWrite_LeavesNoPartialFile()
    {
        var (cache, api) = Create();
        api.DownloadAttachmentFunc = _ => throw new HttpRequestException("connection reset");

        await Assert.ThrowsAsync<HttpRequestException>(
            () => cache.DownloadAsync("guid-fail", "photo.jpg"));

        Assert.Null(cache.GetCachedPath("guid-fail"));
    }

    [Fact]
    public void GetCachedPath_IgnoresZeroByteFiles()
    {
        var (cache, _) = Create();
        var dir = Path.Combine(_root, "guid-zero");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "photo.jpg"), Array.Empty<byte>());

        // A zero-byte file is never a valid attachment; treating it as cached is what made a
        // failed download unrecoverable.
        Assert.Null(cache.GetCachedPath("guid-zero"));
        Assert.False(cache.IsCached("guid-zero"));
    }

    [Fact]
    public void GetCachedPath_IgnoresPartialFilesButFindsTheRealOne()
    {
        var (cache, _) = Create();
        var dir = Path.Combine(_root, "guid-partial");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "photo.jpg.abc123.partial"), new byte[] { 9 });
        File.WriteAllBytes(Path.Combine(dir, "photo.jpg"), new byte[] { 1, 2, 3 });

        var path = cache.GetCachedPath("guid-partial");

        Assert.NotNull(path);
        Assert.Equal("photo.jpg", Path.GetFileName(path));
    }

    [Fact]
    public async Task DownloadAsync_ConcurrentCallsForSameGuid_DownloadOnce()
    {
        var (cache, api) = Create();
        var release = new TaskCompletionSource();
        api.DownloadAttachmentFunc = async _ =>
        {
            await release.Task;
            return new byte[] { 7, 7, 7 };
        };

        var calls = Enumerable.Range(0, 5)
            .Select(_ => cache.DownloadAsync("guid-race", "photo.jpg"))
            .ToArray();
        release.SetResult();
        var paths = await Task.WhenAll(calls);

        Assert.Equal(1, api.DownloadAttachmentCalls);
        Assert.All(paths, p => Assert.Equal(paths[0], p));
    }

    [Fact]
    public async Task DownloadAsync_CancellingOneCaller_DoesNotBreakTheOther()
    {
        var (cache, api) = Create();
        var release = new TaskCompletionSource();
        api.DownloadAttachmentFunc = async _ =>
        {
            await release.Task;
            return new byte[] { 5 };
        };

        using var cts = new CancellationTokenSource();
        var cancelled = cache.DownloadAsync("guid-shared", "photo.jpg", ct: cts.Token);
        var survivor = cache.DownloadAsync("guid-shared", "photo.jpg");

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        release.SetResult();
        Assert.True(File.Exists(await survivor));
    }

    [Fact]
    public async Task DownloadAsync_Force_UsesTheForceEndpoint()
    {
        var (cache, api) = Create();
        api.DownloadAttachmentFunc = _ => throw new HttpRequestException(
            "500", null, System.Net.HttpStatusCode.InternalServerError);
        api.ForceDownloadAttachmentFunc = _ => Task.FromResult(new byte[] { 8, 8 });

        var path = await cache.DownloadAsync("guid-purged", "shot.png", force: true);

        Assert.Equal(0, api.DownloadAttachmentCalls);
        Assert.Equal(1, api.ForceDownloadAttachmentCalls);
        Assert.Equal(new byte[] { 8, 8 }, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task DownloadAsync_ForcedRetry_IsNotAnsweredByAnInFlightPlainDownload()
    {
        var (cache, api) = Create();
        var stuck = new TaskCompletionSource<byte[]>();
        api.DownloadAttachmentFunc = _ => stuck.Task;
        api.ForceDownloadAttachmentFunc = _ => Task.FromResult(new byte[] { 3 });

        // A plain download is already in flight (this is the one that is failing).
        var plain = cache.DownloadAsync("guid-both", "shot.png");
        var forced = await cache.DownloadAsync("guid-both", "shot.png", force: true);

        Assert.True(File.Exists(forced));
        Assert.Equal(1, api.ForceDownloadAttachmentCalls);

        stuck.SetException(new HttpRequestException("500"));
        await Assert.ThrowsAsync<HttpRequestException>(() => plain);
    }

    [Fact]
    public async Task InvalidateAsync_RemovesCachedFileSoTheNextDownloadRefetches()
    {
        var (cache, api) = Create();
        api.DownloadAttachmentFunc = _ => Task.FromResult(new byte[] { 1 });
        var first = await cache.DownloadAsync("guid-bad", "photo.jpg");
        Assert.True(File.Exists(first));

        await cache.InvalidateAsync("guid-bad");
        Assert.Null(cache.GetCachedPath("guid-bad"));

        api.DownloadAttachmentFunc = _ => Task.FromResult(new byte[] { 2, 2 });
        var second = await cache.DownloadAsync("guid-bad", "photo.jpg");

        Assert.Equal(new byte[] { 2, 2 }, await File.ReadAllBytesAsync(second));
        Assert.Equal(2, api.DownloadAttachmentCalls);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("   ")]
    public async Task DownloadAsync_HostileTransferName_StaysInsideTheCacheFolder(string transferName)
    {
        var (cache, api) = Create();
        api.DownloadAttachmentFunc = _ => Task.FromResult(new byte[] { 1 });

        var path = await cache.DownloadAsync("guid-hostile", transferName);

        var expectedDir = Path.Combine(_root, "guid-hostile");
        Assert.Equal(expectedDir, Path.GetDirectoryName(path));
        Assert.Equal("attachment", Path.GetFileName(path));
    }

    [Fact]
    public async Task DownloadAsync_GuidWithSeparators_DoesNotEscapeTheCacheRoot()
    {
        var (cache, api) = Create();
        api.DownloadAttachmentFunc = _ => Task.FromResult(new byte[] { 1 });

        var path = await cache.DownloadAsync(@"../../evil", "photo.jpg");

        Assert.StartsWith(_root, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
    }
}
