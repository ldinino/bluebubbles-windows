using BlueBubbles.Core.Configuration;

namespace BlueBubbles.Core.Services.Http;

public class ProxyHeaderHandler : DelegatingHandler
{
    private readonly ServerConfiguration _config;

    public ProxyHeaderHandler(ServerConfiguration config) => _config = config;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var host = request.RequestUri?.Host ?? string.Empty;

        if (host.Contains("ngrok", StringComparison.OrdinalIgnoreCase))
            request.Headers.TryAddWithoutValidation("ngrok-skip-browser-warning", "true");
        else if (host.Contains("zrok", StringComparison.OrdinalIgnoreCase))
            request.Headers.TryAddWithoutValidation("skip_zrok_interstitial", "true");

        foreach (var kv in _config.CustomHeaders)
            request.Headers.TryAddWithoutValidation(kv.Key, kv.Value);

        return base.SendAsync(request, cancellationToken);
    }
}
