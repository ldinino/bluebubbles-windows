using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using BlueBubbles.Core.Models;
using BlueBubbles.Core.Utils;

namespace BlueBubbles.Core.Services;

/// <summary>Outcome of a version check. Never null; "no update" is a populated instance.</summary>
public sealed class UpdateCheckResult
{
    public bool UpdateAvailable { get; init; }

    public string CurrentVersion { get; init; } = string.Empty;

    public string? LatestVersion { get; init; }

    public string? ReleaseNotes { get; init; }

    public string? ReleaseUrl { get; init; }

    public GitHubReleaseAsset? Asset { get; init; }

    public static UpdateCheckResult None(string currentVersion) =>
        new() { CurrentVersion = currentVersion };
}

/// <summary>Why a download/launch attempt ended the way it did.</summary>
public enum UpdateDownloadStatus
{
    Launched,
    NoAsset,
    DigestMissing,
    DigestMismatch,
    UntrustedHost,
    DownloadFailed
}

public sealed record UpdateDownloadResult(
    UpdateDownloadStatus Status,
    string Message,
    string? FilePath = null)
{
    public bool Success => Status == UpdateDownloadStatus.Launched;
}

/// <summary>Starts the verified installer. Abstracted so tests can assert it is *not* called.</summary>
public interface IInstallerLauncher
{
    void Launch(string installerPath);
}

/// <summary>
/// Launches the installer as the current user. Deliberately does not set <c>Verb = "runas"</c> —
/// the Inno Setup installer raises its own elevation prompt only if it actually needs one.
/// </summary>
public sealed class ProcessInstallerLauncher : IInstallerLauncher
{
    public void Launch(string installerPath) =>
        System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo(installerPath) { UseShellExecute = true });
}

public interface IUpdateService
{
    /// <summary>Result of the most recent successful check, or null if none has completed.</summary>
    UpdateCheckResult? LastResult { get; }

    /// <summary>Raised only when a strictly newer release with a usable x64 asset was found.</summary>
    event Action<UpdateCheckResult>? UpdateAvailable;

    Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken ct = default);

    Task<UpdateDownloadResult> DownloadAndLaunchAsync(
        UpdateCheckResult update,
        IProgress<double>? progress = null,
        CancellationToken ct = default);
}

/// <summary>
/// Checks GitHub Releases for a newer build and, on explicit user action, downloads the x64
/// installer, verifies its SHA-256 against the release API's per-asset <c>digest</c>, and runs it.
///
/// Two rules make this safe to have at all:
/// (1) the download is executed only after the hash matches — no digest means no execution, and
/// (2) checking never throws into the caller, so a launch-time check cannot break app start.
/// </summary>
public sealed class UpdateService : IUpdateService
{
    // /releases/latest excludes drafts and prereleases; enumerating /releases and taking [0] does
    // not, and this project cuts drafts routinely.
    private const string LatestReleaseUrl =
        "https://api.github.com/repos/ldinino/bluebubbles-windows/releases/latest";

    private const string AssetPrefix = "BlueBubbles-Setup-";

    // x64 only: arm64 is blocked on a vendored x64 runtime DLL (punchlist S1).
    private const string AssetSuffix = "-x64.exe";

    private const string DigestPrefix = "sha256:";
    private const int Sha256HexLength = 64;

    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(15);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly IInstallerLauncher _launcher;
    private readonly Func<string> _currentVersion;
    private readonly string _downloadDirectory;

    public UpdateService(
        HttpClient httpClient,
        IInstallerLauncher? launcher = null,
        string? downloadDirectory = null,
        Func<string>? currentVersion = null)
    {
        _httpClient = httpClient;
        _launcher = launcher ?? new ProcessInstallerLauncher();
        _currentVersion = currentVersion ?? (() => AppInfo.Version);

        // Per-user, not a shared temp root: %LocalAppData% is ACL'd to the current user.
        _downloadDirectory = downloadDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BlueBubbles", "updates");
    }

    public UpdateCheckResult? LastResult { get; private set; }

    public event Action<UpdateCheckResult>? UpdateAvailable;

    public async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken ct = default)
    {
        var current = _currentVersion();
        var none = UpdateCheckResult.None(current);

        try
        {
            if (!SemanticVersion.TryParse(current, out var local))
            {
                AppLog.Warn(LogCategory.App,
                    $"Update check skipped: local version '{current}' is not a 3-part version.");
                return none;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(CheckTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.UserAgent.ParseAdd($"BlueBubbles-Windows/{current}");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            using var response = await _httpClient.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                // 403/429 here is the unauthenticated 60-req/hr/IP limit. Not an error worth surfacing.
                AppLog.Warn(LogCategory.App,
                    $"Update check failed: GitHub returned {(int)response.StatusCode}.");
                return none;
            }

            var release = await response.Content
                .ReadFromJsonAsync<GitHubRelease>(JsonOptions, cts.Token);
            if (release is null)
            {
                AppLog.Warn(LogCategory.App, "Update check failed: empty release payload.");
                return none;
            }

            if (!SemanticVersion.TryParse(release.TagName, out var remote))
            {
                AppLog.Warn(LogCategory.App,
                    $"Update check ignored: release tag '{release.TagName}' is not a version.");
                return none;
            }

            if (remote <= local)
            {
                // Equal, or a local dev build ahead of the release. Never offer a downgrade.
                AppLog.Info(LogCategory.App,
                    $"Update check: running {local}, latest release is {remote}. No update offered.");
                return none;
            }

            var asset = SelectAsset(release);
            if (asset is null)
            {
                AppLog.Warn(LogCategory.App,
                    $"Update {remote} found but it has no {AssetPrefix}*{AssetSuffix} asset.");
                return none;
            }

            var result = new UpdateCheckResult
            {
                UpdateAvailable = true,
                CurrentVersion = current,
                LatestVersion = remote.ToString(),
                ReleaseNotes = release.Body,
                ReleaseUrl = release.HtmlUrl,
                Asset = asset
            };

            LastResult = result;
            AppLog.Info(LogCategory.App, $"Update available: {local} -> {remote} ({asset.Name}).");
            UpdateAvailable?.Invoke(result);
            return result;
        }
        catch (Exception ex)
        {
            // No network, DNS failure, timeout, malformed JSON — all non-fatal by design.
            AppLog.Warn(LogCategory.App, $"Update check failed: {ex.Message}");
            return none;
        }
    }

    public async Task<UpdateDownloadResult> DownloadAndLaunchAsync(
        UpdateCheckResult update,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var asset = update?.Asset;
        if (asset is null || string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
            return Fail(UpdateDownloadStatus.NoAsset, "No x64 installer was attached to that release.");

        // Checked before the request: an unverifiable download is refused, never run unverified.
        var expectedHash = ParseSha256Digest(asset.Digest);
        if (expectedHash is null)
        {
            return Fail(UpdateDownloadStatus.DigestMissing,
                $"Refusing to run '{asset.Name}': the release did not publish a SHA-256 digest, " +
                "so the download cannot be verified. Please download it manually from GitHub.");
        }

        if (!IsTrustedUrl(asset.BrowserDownloadUrl, out var uri))
        {
            return Fail(UpdateDownloadStatus.UntrustedHost,
                $"Refusing to download from '{asset.BrowserDownloadUrl}': not an HTTPS GitHub URL.");
        }

        string? path = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd($"BlueBubbles-Windows/{_currentVersion()}");

            using var response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                return Fail(UpdateDownloadStatus.DownloadFailed,
                    $"Download failed: GitHub returned {(int)response.StatusCode}.");
            }

            // Defence in depth: a redirect chain must not have walked us off GitHub over plain HTTP.
            var finalUri = response.RequestMessage?.RequestUri;
            if (finalUri is not null && !IsTrustedUrl(finalUri.ToString(), out _))
            {
                return Fail(UpdateDownloadStatus.UntrustedHost,
                    $"Refusing the download: it redirected to '{finalUri.Host}', which is not GitHub.");
            }

            path = BuildDownloadPath(asset.Name);
            var actualHash = await DownloadAndHashAsync(response, path, asset.Size, progress, ct);

            if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
            {
                TryDelete(path);
                var message =
                    $"SHA-256 verification FAILED for '{asset.Name}'. Expected " +
                    $"{Convert.ToHexString(expectedHash).ToLowerInvariant()}, got " +
                    $"{Convert.ToHexString(actualHash).ToLowerInvariant()}. The file was deleted and " +
                    "NOT run. Do not install this update; report it.";
                AppLog.Error(LogCategory.App, message);
                return new UpdateDownloadResult(UpdateDownloadStatus.DigestMismatch, message);
            }

            AppLog.Info(LogCategory.App,
                $"Update installer verified (sha256 {Convert.ToHexString(actualHash).ToLowerInvariant()}); launching.");
            _launcher.Launch(path);

            return new UpdateDownloadResult(
                UpdateDownloadStatus.Launched, "Installer verified and started.", path);
        }
        catch (Exception ex)
        {
            TryDelete(path);
            return Fail(UpdateDownloadStatus.DownloadFailed, $"Download failed: {ex.Message}");
        }
    }

    /// <summary>Picks the x64 setup asset. An arm64 asset ends "-arm64.exe" and is excluded.</summary>
    private static GitHubReleaseAsset? SelectAsset(GitHubRelease release) =>
        release.Assets.FirstOrDefault(a =>
            a.Name is not null &&
            a.Name.StartsWith(AssetPrefix, StringComparison.OrdinalIgnoreCase) &&
            a.Name.EndsWith(AssetSuffix, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(a.BrowserDownloadUrl));

    /// <summary>Returns the 32 raw bytes of a well-formed <c>sha256:&lt;hex&gt;</c>, else null.</summary>
    private static byte[]? ParseSha256Digest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
            return null;

        var trimmed = digest.Trim();
        if (!trimmed.StartsWith(DigestPrefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var hex = trimmed[DigestPrefix.Length..].Trim();
        if (hex.Length != Sha256HexLength)
            return null;

        try
        {
            return Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static bool IsTrustedUrl(string? url, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
            return false;

        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;

        // Leading dots matter: they stop "evilgithubusercontent.com" from matching.
        var host = parsed.Host;
        var trusted =
            host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);

        if (!trusted)
            return false;

        uri = parsed;
        return true;
    }

    private string BuildDownloadPath(string? assetName)
    {
        var safe = Path.GetFileName(assetName ?? string.Empty);
        foreach (var invalid in Path.GetInvalidFileNameChars())
            safe = safe.Replace(invalid, '_');

        if (string.IsNullOrWhiteSpace(safe))
            safe = "BlueBubbles-Setup-x64.exe";

        Directory.CreateDirectory(_downloadDirectory);

        var root = Path.GetFullPath(_downloadDirectory);
        var full = Path.GetFullPath(Path.Combine(root, safe));

        // The asset name is remote input; never let it escape the per-user update directory.
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Installer path escaped the update directory.");

        return full;
    }

    private static async Task<byte[]> DownloadAndHashAsync(
        HttpResponseMessage response,
        string path,
        long expectedSize,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var destination = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[81920];
        long total = 0;
        int read;

        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);

            total += read;
            if (expectedSize > 0)
                progress?.Report(Math.Clamp((double)total / expectedSize, 0d, 1d));
        }

        return hash.GetHashAndReset();
    }

    private static UpdateDownloadResult Fail(UpdateDownloadStatus status, string message)
    {
        AppLog.Warn(LogCategory.App, message);
        return new UpdateDownloadResult(status, message);
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;

        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogCategory.App, $"Could not delete '{path}': {ex.Message}");
        }
    }
}
