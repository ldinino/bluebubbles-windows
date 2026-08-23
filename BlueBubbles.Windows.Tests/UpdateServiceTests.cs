using System.Net;
using System.Security.Cryptography;
using System.Text;
using BlueBubbles.Core.Services;
using BlueBubbles.Core.Utils;

namespace BlueBubbles.Windows.Tests;

/// <summary>Records whether the installer was executed, so "refuses to run" is directly assertable.</summary>
public sealed class FakeInstallerLauncher : IInstallerLauncher
{
    public List<string> Launched { get; } = new();

    public void Launch(string installerPath) => Launched.Add(installerPath);
}

public class SemanticVersionTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("V0.23.0", 0, 23, 0)]
    [InlineData("  v10.0.1  ", 10, 0, 1)]
    public void TryParse_AcceptsTaggedAndPlainVersions(string text, int major, int minor, int patch)
    {
        Assert.True(SemanticVersion.TryParse(text, out var version));
        Assert.Equal(new SemanticVersion(major, minor, patch), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("1.2.x")]
    [InlineData("nightly")]
    [InlineData("v")]
    [InlineData("-1.2.3")]
    [InlineData("1.2.-3")]
    [InlineData("release-2024")]
    public void TryParse_RejectsMalformed(string? text)
    {
        Assert.False(SemanticVersion.TryParse(text, out _));
    }

    [Fact]
    public void Compare_IsNumericNotLexicographic()
    {
        Assert.True(SemanticVersion.TryParse("0.10.0", out var ten));
        Assert.True(SemanticVersion.TryParse("0.9.0", out var nine));

        // The whole reason this type exists: an ordinal string compare puts "0.9.0" first.
        Assert.True(ten > nine);
        Assert.True(string.CompareOrdinal("0.10.0", "0.9.0") < 0);
    }

    [Fact]
    public void Compare_OrdersMajorThenMinorThenPatch()
    {
        Func<string, SemanticVersion> v = s =>
        {
            SemanticVersion.TryParse(s, out var r);
            return r;
        };

        Assert.True(v("2.0.0") > v("1.99.99"));
        Assert.True(v("1.3.0") > v("1.2.99"));
        Assert.True(v("1.2.4") > v("1.2.3"));
        Assert.True(v("1.2.3") == v("1.2.3"));
        Assert.False(v("1.2.3") > v("1.2.3"));
    }
}

public class UpdateServiceTests : IDisposable
{
    private const string DownloadUrl =
        "https://github.com/ldinino/bluebubbles-windows/releases/download/v9.9.9/BlueBubbles-Setup-9.9.9-x64.exe";

    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), "bb-update-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; a locked temp file must not fail the suite.
        }
    }

    // ---- helpers ------------------------------------------------------------------------

    private static string Sha256Hex(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>Shape-accurate excerpt of a real GitHub "get latest release" payload.</summary>
    private static string ReleaseJson(
        string tag,
        string assetName = "BlueBubbles-Setup-9.9.9-x64.exe",
        string? digest = "sha256:0000000000000000000000000000000000000000000000000000000000000000",
        string downloadUrl = DownloadUrl,
        string? extraAssets = null)
    {
        var digestField = digest is null ? "" : $"\"digest\": \"{digest}\",";
        return $$"""
        {
          "url": "https://api.github.com/repos/ldinino/bluebubbles-windows/releases/1",
          "html_url": "https://github.com/ldinino/bluebubbles-windows/releases/tag/{{tag}}",
          "tag_name": "{{tag}}",
          "name": "{{tag}}",
          "draft": false,
          "prerelease": false,
          "published_at": "2026-08-01T12:00:00Z",
          "body": "Release notes here.",
          "assets": [
            {{extraAssets}}
            {
              "name": "{{assetName}}",
              "content_type": "application/x-msdownload",
              "size": 4,
              {{digestField}}
              "browser_download_url": "{{downloadUrl}}"
            }
          ]
        }
        """;
    }

    private static MockHandler JsonMock(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(_ => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        }));

    private UpdateService CreateService(
        MockHandler mock, IInstallerLauncher launcher, string currentVersion = "0.23.0") =>
        new(mock.CreateClient(), launcher, _tempDir, () => currentVersion);

    // ---- check: version comparison -----------------------------------------------------

    [Fact]
    public async Task Check_NewerRemote_ReportsUpdateAvailable()
    {
        var mock = JsonMock(ReleaseJson("v9.9.9"));
        var service = CreateService(mock, new FakeInstallerLauncher());

        var result = await service.CheckForUpdateAsync();

        Assert.True(result.UpdateAvailable);
        Assert.Equal("9.9.9", result.LatestVersion);
        Assert.Equal("0.23.0", result.CurrentVersion);
        Assert.Equal("BlueBubbles-Setup-9.9.9-x64.exe", result.Asset?.Name);
    }

    [Fact]
    public async Task Check_UsesLatestEndpoint_WhichExcludesDraftsAndPrereleases()
    {
        var mock = JsonMock(ReleaseJson("v9.9.9"));
        var service = CreateService(mock, new FakeInstallerLauncher());

        await service.CheckForUpdateAsync();

        var uri = Assert.Single(mock.Requests).RequestUri!;
        Assert.Equal(
            "https://api.github.com/repos/ldinino/bluebubbles-windows/releases/latest",
            uri.ToString());
    }

    [Fact]
    public async Task Check_EqualVersion_ReportsNoUpdate()
    {
        var mock = JsonMock(ReleaseJson("v0.23.0"));
        var service = CreateService(mock, new FakeInstallerLauncher());

        var result = await service.CheckForUpdateAsync();

        Assert.False(result.UpdateAvailable);
        Assert.Null(result.LatestVersion);
    }

    [Fact]
    public async Task Check_LocalNewerThanRemote_OffersNoDowngrade()
    {
        var mock = JsonMock(ReleaseJson("v0.22.0"));
        var service = CreateService(mock, new FakeInstallerLauncher(), currentVersion: "0.23.0");

        var result = await service.CheckForUpdateAsync();

        Assert.False(result.UpdateAvailable);
    }

    [Fact]
    public async Task Check_ComparesNumerically_Not_Lexicographically()
    {
        // String compare would rank "0.9.0" above "0.10.0" and miss this update.
        var mock = JsonMock(ReleaseJson("v0.10.0"));
        var service = CreateService(mock, new FakeInstallerLauncher(), currentVersion: "0.9.0");

        var result = await service.CheckForUpdateAsync();

        Assert.True(result.UpdateAvailable);
        Assert.Equal("0.10.0", result.LatestVersion);
    }

    [Theory]
    [InlineData("nightly")]
    [InlineData("v1.2")]
    [InlineData("")]
    public async Task Check_MalformedTag_IsIgnoredWithoutThrowing(string tag)
    {
        var mock = JsonMock(ReleaseJson(tag));
        var service = CreateService(mock, new FakeInstallerLauncher());

        var result = await service.CheckForUpdateAsync();

        Assert.False(result.UpdateAvailable);
    }

    // ---- check: failure must never throw into the launch path ---------------------------

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]        // unauthenticated rate limit
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Check_NonSuccessResponse_ReturnsNoUpdateWithoutThrowing(HttpStatusCode status)
    {
        var mock = JsonMock("""{"message":"Not Found"}""", status);
        var service = CreateService(mock, new FakeInstallerLauncher());

        var result = await service.CheckForUpdateAsync();

        Assert.False(result.UpdateAvailable);
        Assert.Equal("0.23.0", result.CurrentVersion);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"tag_name\":")]
    [InlineData("null")]
    [InlineData("[]")]
    public async Task Check_MalformedJson_ReturnsNoUpdateWithoutThrowing(string body)
    {
        var mock = JsonMock(body);
        var service = CreateService(mock, new FakeInstallerLauncher());

        var result = await service.CheckForUpdateAsync();

        Assert.False(result.UpdateAvailable);
    }

    [Fact]
    public async Task Check_NetworkFailure_ReturnsNoUpdateWithoutThrowing()
    {
        var mock = new MockHandler(_ =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("No such host is known.")));
        var service = CreateService(mock, new FakeInstallerLauncher());

        var result = await service.CheckForUpdateAsync();

        Assert.False(result.UpdateAvailable);
    }

    [Fact]
    public async Task Check_MalformedLocalVersion_DoesNotThrow()
    {
        var mock = JsonMock(ReleaseJson("v9.9.9"));
        var service = CreateService(mock, new FakeInstallerLauncher(), currentVersion: "not-a-version");

        var result = await service.CheckForUpdateAsync();

        Assert.False(result.UpdateAvailable);
    }

    // ---- check: asset selection ---------------------------------------------------------

    [Fact]
    public async Task Check_SelectsX64SetupAsset_IgnoringArm64AndOtherFiles()
    {
        const string others = """
            {
              "name": "BlueBubbles-Setup-9.9.9-arm64.exe",
              "digest": "sha256:1111111111111111111111111111111111111111111111111111111111111111",
              "browser_download_url": "https://github.com/x/y/arm64.exe"
            },
            {
              "name": "SHA256SUMS.txt",
              "digest": "sha256:2222222222222222222222222222222222222222222222222222222222222222",
              "browser_download_url": "https://github.com/x/y/SHA256SUMS.txt"
            },
            """;
        var mock = JsonMock(ReleaseJson("v9.9.9", extraAssets: others));
        var service = CreateService(mock, new FakeInstallerLauncher());

        var result = await service.CheckForUpdateAsync();

        Assert.True(result.UpdateAvailable);
        Assert.Equal("BlueBubbles-Setup-9.9.9-x64.exe", result.Asset?.Name);
    }

    [Fact]
    public async Task Check_NoMatchingAsset_ReportsNoUpdate()
    {
        var mock = JsonMock(ReleaseJson("v9.9.9", assetName: "BlueBubbles-Setup-9.9.9-arm64.exe"));
        var service = CreateService(mock, new FakeInstallerLauncher());

        var result = await service.CheckForUpdateAsync();

        Assert.False(result.UpdateAvailable);
    }

    // ---- download: SHA-256 verification gates execution ---------------------------------

    private async Task<(UpdateDownloadResult result, FakeInstallerLauncher launcher)> DownloadAsync(
        byte[] payload, string? digest, string downloadUrl = DownloadUrl, Uri? finalUri = null,
        HttpStatusCode status = HttpStatusCode.OK)
    {
        var launcher = new FakeInstallerLauncher();
        var mock = new MockHandler(request =>
        {
            HttpResponseMessage response;
            if (request.RequestUri!.Host == "api.github.com")
            {
                response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        ReleaseJson("v9.9.9", digest: digest, downloadUrl: downloadUrl),
                        Encoding.UTF8, "application/json")
                };
            }
            else
            {
                response = new HttpResponseMessage(status)
                {
                    Content = new ByteArrayContent(payload),
                    // Mirrors HttpClient, which reports the *final* URI after any redirects.
                    RequestMessage = new HttpRequestMessage(
                        HttpMethod.Get, finalUri ?? request.RequestUri)
                };
            }
            return Task.FromResult(response);
        });

        var service = CreateService(mock, launcher);
        var check = await service.CheckForUpdateAsync();
        Assert.True(check.UpdateAvailable, "precondition: the check must offer an update");

        return (await service.DownloadAndLaunchAsync(check), launcher);
    }

    [Fact]
    public async Task Download_DigestMatches_VerifiesThenLaunches()
    {
        var payload = Encoding.UTF8.GetBytes("MZfake-installer-bytes");

        var (result, launcher) = await DownloadAsync(payload, "sha256:" + Sha256Hex(payload));

        Assert.Equal(UpdateDownloadStatus.Launched, result.Status);
        Assert.True(result.Success);
        var launched = Assert.Single(launcher.Launched);
        Assert.Equal(payload, await File.ReadAllBytesAsync(launched));
    }

    [Fact]
    public async Task Download_DigestMismatch_RefusesToExecuteAndDeletesFile()
    {
        var payload = Encoding.UTF8.GetBytes("MZtampered-installer");
        var wrongDigest = "sha256:" + Sha256Hex(Encoding.UTF8.GetBytes("the-legitimate-installer"));

        var (result, launcher) = await DownloadAsync(payload, wrongDigest);

        Assert.Equal(UpdateDownloadStatus.DigestMismatch, result.Status);
        Assert.False(result.Success);
        Assert.Empty(launcher.Launched);
        Assert.Contains("FAILED", result.Message);

        // The unverified bytes must not be left on disk for anything else to pick up.
        Assert.False(Directory.Exists(_tempDir) && Directory.GetFiles(_tempDir).Length > 0);
    }

    [Theory]
    [InlineData(null)]                                   // field absent entirely
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("md5:0000000000000000000000000000000f")]  // wrong algorithm
    [InlineData("sha256:")]
    [InlineData("sha256:abcd")]                          // truncated hex
    [InlineData("sha256:zz00000000000000000000000000000000000000000000000000000000000000")] // non-hex
    public async Task Download_UnusableDigest_RefusesToExecute(string? digest)
    {
        var payload = Encoding.UTF8.GetBytes("MZfake-installer-bytes");

        var (result, launcher) = await DownloadAsync(payload, digest);

        Assert.Equal(UpdateDownloadStatus.DigestMissing, result.Status);
        Assert.Empty(launcher.Launched);
        Assert.Contains("Refusing", result.Message);
    }

    [Fact]
    public async Task Download_MissingDigest_DoesNotEvenFetchTheInstaller()
    {
        var launcher = new FakeInstallerLauncher();
        var mock = new MockHandler(request => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    ReleaseJson("v9.9.9", digest: null), Encoding.UTF8, "application/json")
            }));

        var service = CreateService(mock, launcher);
        var check = await service.CheckForUpdateAsync();
        var result = await service.DownloadAndLaunchAsync(check);

        Assert.Equal(UpdateDownloadStatus.DigestMissing, result.Status);
        Assert.Empty(launcher.Launched);
        // Only the release-metadata call; the binary was never requested.
        Assert.Single(mock.Requests);
    }

    // ---- download: transport trust -------------------------------------------------------

    [Theory]
    [InlineData("http://github.com/ldinino/bluebubbles-windows/releases/download/v9.9.9/BlueBubbles-Setup-9.9.9-x64.exe")]
    [InlineData("https://evil.example.com/BlueBubbles-Setup-9.9.9-x64.exe")]
    [InlineData("https://evilgithubusercontent.com/BlueBubbles-Setup-9.9.9-x64.exe")]
    [InlineData("https://github.com.evil.example.com/BlueBubbles-Setup-9.9.9-x64.exe")]
    public async Task Download_UntrustedOrPlaintextUrl_RefusesToExecute(string url)
    {
        var payload = Encoding.UTF8.GetBytes("MZfake-installer-bytes");

        var (result, launcher) = await DownloadAsync(
            payload, "sha256:" + Sha256Hex(payload), downloadUrl: url);

        Assert.Equal(UpdateDownloadStatus.UntrustedHost, result.Status);
        Assert.Empty(launcher.Launched);
    }

    [Fact]
    public async Task Download_RedirectedOffGitHub_RefusesToExecuteEvenIfDigestWouldMatch()
    {
        var payload = Encoding.UTF8.GetBytes("MZfake-installer-bytes");

        var (result, launcher) = await DownloadAsync(
            payload, "sha256:" + Sha256Hex(payload),
            finalUri: new Uri("https://evil.example.com/payload.exe"));

        Assert.Equal(UpdateDownloadStatus.UntrustedHost, result.Status);
        Assert.Empty(launcher.Launched);
    }

    [Fact]
    public async Task Download_AcceptsGitHubReleaseAssetCdnHost()
    {
        var payload = Encoding.UTF8.GetBytes("MZfake-installer-bytes");

        var (result, launcher) = await DownloadAsync(
            payload, "sha256:" + Sha256Hex(payload),
            finalUri: new Uri("https://release-assets.githubusercontent.com/github-production-release-asset/1/2"));

        Assert.Equal(UpdateDownloadStatus.Launched, result.Status);
        Assert.Single(launcher.Launched);
    }

    [Fact]
    public async Task Download_NonSuccessResponse_RefusesToExecute()
    {
        var payload = Encoding.UTF8.GetBytes("MZfake-installer-bytes");

        var (result, launcher) = await DownloadAsync(
            payload, "sha256:" + Sha256Hex(payload), status: HttpStatusCode.NotFound);

        Assert.Equal(UpdateDownloadStatus.DownloadFailed, result.Status);
        Assert.Empty(launcher.Launched);
    }

    [Fact]
    public async Task Download_WithoutAsset_RefusesToExecute()
    {
        var launcher = new FakeInstallerLauncher();
        var service = CreateService(JsonMock("{}"), launcher);

        var result = await service.DownloadAndLaunchAsync(UpdateCheckResult.None("0.23.0"));

        Assert.Equal(UpdateDownloadStatus.NoAsset, result.Status);
        Assert.Empty(launcher.Launched);
    }

    [Fact]
    public async Task Download_WritesIntoTheConfiguredPerUserDirectory()
    {
        var payload = Encoding.UTF8.GetBytes("MZfake-installer-bytes");

        var (result, _) = await DownloadAsync(payload, "sha256:" + Sha256Hex(payload));

        Assert.True(result.Success);
        Assert.StartsWith(
            Path.GetFullPath(_tempDir) + Path.DirectorySeparatorChar,
            Path.GetFullPath(result.FilePath!));
    }

    [Fact]
    public async Task Download_AssetNameWithTraversal_StaysInsideUpdateDirectory()
    {
        var payload = Encoding.UTF8.GetBytes("MZfake-installer-bytes");
        var launcher = new FakeInstallerLauncher();

        var mock = new MockHandler(request =>
        {
            if (request.RequestUri!.Host == "api.github.com")
            {
                // The asset name is remote input and must not steer the write location.
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        ReleaseJson(
                            "v9.9.9",
                            assetName: "BlueBubbles-Setup-../../../evil-x64.exe",
                            digest: "sha256:" + Sha256Hex(payload)),
                        Encoding.UTF8, "application/json")
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, request.RequestUri)
            });
        });

        var service = CreateService(mock, launcher);
        var check = await service.CheckForUpdateAsync();
        var result = await service.DownloadAndLaunchAsync(check);

        Assert.True(result.Success);
        Assert.StartsWith(
            Path.GetFullPath(_tempDir) + Path.DirectorySeparatorChar,
            Path.GetFullPath(result.FilePath!));
        Assert.Equal("evil-x64.exe", Path.GetFileName(result.FilePath!));
    }
}
