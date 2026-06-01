using BlueBubbles.Core.Models;

namespace BlueBubbles.Core.Services;

/// <summary>Fetches Open-Graph/HTML metadata for a URL so the client can build a link preview card
/// on demand when iMessage didn't attach rich <c>PayloadData</c>.</summary>
public interface ILinkPreviewService
{
    /// <summary>Best-effort fetch of a page's title/description/image/site. Returns null on any
    /// failure (network, non-HTML, timeout) so callers fall back to a generic card.</summary>
    Task<LinkMetadata?> FetchAsync(string url, CancellationToken ct = default);
}
