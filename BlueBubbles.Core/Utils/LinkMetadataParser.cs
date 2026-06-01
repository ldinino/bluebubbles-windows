using System.Net;
using System.Text.RegularExpressions;
using BlueBubbles.Core.Models;

namespace BlueBubbles.Core.Utils;

/// <summary>
/// Extracts Open-Graph / Twitter-card / &lt;title&gt; metadata from an HTML document with tolerant
/// regexes (no HTML-parser dependency). Pure and testable; <see cref="Services.LinkPreviewService"/>
/// does the HTTP and delegates parsing here.
/// </summary>
public static class LinkMetadataParser
{
    public static LinkMetadata? Parse(string? html, Uri baseUri)
    {
        if (string.IsNullOrEmpty(html)) return null;

        var title = Clean(Meta(html, "og:title") ?? Meta(html, "twitter:title") ?? TitleTag(html));
        var description = Clean(Meta(html, "og:description") ?? Meta(html, "twitter:description") ?? Meta(html, "description"));
        var image = Meta(html, "og:image") ?? Meta(html, "og:image:url") ?? Meta(html, "twitter:image") ?? Meta(html, "twitter:image:src");
        var site = Clean(Meta(html, "og:site_name")) ?? baseUri.Host;

        // Resolve a relative og:image against the page URL.
        if (!string.IsNullOrWhiteSpace(image) && Uri.TryCreate(baseUri, image.Trim(), out var absImage))
            image = absImage.ToString();

        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(image) && string.IsNullOrWhiteSpace(description))
            return null;

        return new LinkMetadata(title, description, string.IsNullOrWhiteSpace(image) ? null : image, site);
    }

    // Matches <meta property|name="key" ... content="value"> in either attribute order.
    private static string? Meta(string html, string key)
    {
        var k = Regex.Escape(key);
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.Singleline;

        var m = Regex.Match(html,
            $"<meta[^>]+?(?:property|name)\\s*=\\s*[\"']{k}[\"'][^>]+?content\\s*=\\s*[\"'](?<v>[^\"']*)[\"']",
            options);
        if (m.Success) return m.Groups["v"].Value;

        m = Regex.Match(html,
            $"<meta[^>]+?content\\s*=\\s*[\"'](?<v>[^\"']*)[\"'][^>]+?(?:property|name)\\s*=\\s*[\"']{k}[\"']",
            options);
        return m.Success ? m.Groups["v"].Value : null;
    }

    private static string? TitleTag(string html)
    {
        var m = Regex.Match(html, "<title[^>]*>(?<v>.*?)</title>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return m.Success ? m.Groups["v"].Value : null;
    }

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : WebUtility.HtmlDecode(value).Trim();
}
