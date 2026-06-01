using System.Text.RegularExpressions;

namespace BlueBubbles.Core.Utils;

/// <summary>
/// Finds URLs inside message text so the chat bubble can render them as clickable links,
/// and helps decide whether a message body is "just a URL" (a rich-link preview shows only
/// its card in that case). Kept in Core so it's unit-testable without a UI host.
/// </summary>
public static partial class UrlDetector
{
    // http(s):// links and bare "www." links. Greedy up to the first whitespace or angle bracket;
    // trailing sentence punctuation is trimmed in Find so "see https://x.com." doesn't swallow the period.
    [GeneratedRegex(@"(?:https?://|www\.)[^\s<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    /// <summary>A URL span within a piece of text: its position, the displayed text, and a
    /// navigable absolute URL (bare "www." links are upgraded to https).</summary>
    public readonly record struct UrlToken(int Start, int Length, string Text, string Url);

    /// <summary>Returns the URL spans in <paramref name="text"/> in order, with trailing
    /// punctuation trimmed and an absolute URL resolved for each.</summary>
    public static IReadOnlyList<UrlToken> Find(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];

        List<UrlToken>? tokens = null;
        foreach (Match m in UrlRegex().Matches(text))
        {
            var display = TrimTrailingPunctuation(m.Value);
            if (display.Length == 0) continue;

            var url = display.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                ? "https://" + display
                : display;

            (tokens ??= []).Add(new UrlToken(m.Index, display.Length, display, url));
        }
        return (IReadOnlyList<UrlToken>?)tokens ?? [];
    }

    public static bool ContainsUrl(string? text) => Find(text).Count > 0;

    /// <summary>The first URL in the text (a click target / preview fallback), or null.</summary>
    public static string? FirstUrl(string? text)
        => Find(text) is [var first, ..] ? first.Url : null;

    /// <summary>True when the trimmed text is a single URL and nothing else — iMessage shows only
    /// the preview card for these, hiding the redundant raw URL.</summary>
    public static bool IsSingleUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.Trim();
        return Find(trimmed) is [var only] && only.Start == 0 && only.Length == trimmed.Length;
    }

    // Strips punctuation that is almost always sentence punctuation rather than part of the URL.
    // A trailing ")" is only stripped when the URL has no balancing "(" (so wiki-style links survive).
    private static string TrimTrailingPunctuation(string url)
    {
        var end = url.Length;
        while (end > 0)
        {
            var c = url[end - 1];
            var isClosingParen = c is ')';
            if (isClosingParen && url.AsSpan(0, end).Contains('(')) break;
            if (c is '.' or ',' or ';' or ':' or '!' or '?' or '\'' or '"' or ')' or ']' or '}' or '>')
                end--;
            else
                break;
        }
        return url[..end];
    }
}
