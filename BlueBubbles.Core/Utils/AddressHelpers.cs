namespace BlueBubbles.Core.Utils;

public static class AddressHelpers
{
    private static readonly string[] HttpsTunnelHosts =
        ["ngrok", "trycloudflare.com", "zrok"];

    public static string? SanitizeServerAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;

        var sanitized = address.Replace("\"", "").Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(sanitized)) return null;

        if (!sanitized.Contains("://"))
        {
            var forceHttps = HttpsTunnelHosts.Any(h =>
                sanitized.Contains(h, StringComparison.OrdinalIgnoreCase));
            sanitized = (forceHttps ? "https://" : "http://") + sanitized;
        }

        return Uri.TryCreate(sanitized, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Authority)
            : null;
    }
}
