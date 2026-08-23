using System.Text.Json.Serialization;

namespace BlueBubbles.Core.Models;

/// <summary>
/// Subset of the GitHub "get latest release" payload we consume. Field names are the wire format
/// (snake_case) and are pinned with <see cref="JsonPropertyNameAttribute"/> rather than a naming
/// policy so they cannot drift with serializer settings.
/// </summary>
public sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")] public string? TagName { get; set; }

    [JsonPropertyName("name")] public string? Name { get; set; }

    [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }

    [JsonPropertyName("body")] public string? Body { get; set; }

    [JsonPropertyName("draft")] public bool Draft { get; set; }

    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }

    [JsonPropertyName("published_at")] public DateTimeOffset? PublishedAt { get; set; }

    [JsonPropertyName("assets")] public List<GitHubReleaseAsset> Assets { get; set; } = new();
}

/// <summary>A downloadable file attached to a release.</summary>
public sealed class GitHubReleaseAsset
{
    [JsonPropertyName("name")] public string? Name { get; set; }

    [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }

    /// <summary>
    /// Server-supplied content hash in the form <c>sha256:&lt;hex&gt;</c>. This is the only thing
    /// that makes downloading and executing an installer safe; absence means "cannot verify".
    /// </summary>
    [JsonPropertyName("digest")] public string? Digest { get; set; }

    [JsonPropertyName("size")] public long Size { get; set; }

    [JsonPropertyName("content_type")] public string? ContentType { get; set; }
}
