using System.Net.Http;
using System.Text;
using BlueBubbles.Core.Models;
using BlueBubbles.Core.Utils;

namespace BlueBubbles.Core.Services;

/// <summary>
/// Best-effort link preview metadata fetcher. GETs the page (capped, HTML only, desktop UA) and
/// delegates extraction to <see cref="LinkMetadataParser"/>. Always fails soft (returns null) so the
/// UI can fall back to a generic card.
/// </summary>
public sealed class LinkPreviewService : ILinkPreviewService
{
    private const int MaxBytes = 512 * 1024;

    private readonly HttpClient _http;

    public LinkPreviewService(HttpClient http) => _http = http;

    public async Task<LinkMetadata?> FetchAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) BlueBubbles/1.0");
            request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return null;

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is not null && !mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
                return null;

            var html = await ReadCappedAsync(response, ct);
            return LinkMetadataParser.Parse(html, uri);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> ReadCappedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[MaxBytes];
        var total = 0;
        int read;
        while (total < MaxBytes &&
               (read = await stream.ReadAsync(buffer.AsMemory(total, MaxBytes - total), ct)) > 0)
        {
            total += read;
        }
        return total == 0 ? null : Encoding.UTF8.GetString(buffer, 0, total);
    }
}
