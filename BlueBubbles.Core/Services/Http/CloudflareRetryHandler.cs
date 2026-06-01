using System.Net;
using System.Net.Http.Headers;

namespace BlueBubbles.Core.Services.Http;

public class CloudflareRetryHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var isCloudflare = request.RequestUri?.Host
            .Contains("trycloudflare", StringComparison.OrdinalIgnoreCase) == true;

        byte[]? bodyBytes = null;
        MediaTypeHeaderValue? contentType = null;
        if (isCloudflare && request.Content is not null)
        {
            bodyBytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            contentType = request.Content.Headers.ContentType;
        }

        var response = await base.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.BadGateway && isCloudflare)
        {
            response.Dispose();

            using var retry = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers)
                retry.Headers.TryAddWithoutValidation(header.Key, header.Value);

            if (bodyBytes is not null)
            {
                retry.Content = new ByteArrayContent(bodyBytes);
                if (contentType is not null)
                    retry.Content.Headers.ContentType = contentType;
            }

            response = await base.SendAsync(retry, cancellationToken);
        }

        return response;
    }
}
