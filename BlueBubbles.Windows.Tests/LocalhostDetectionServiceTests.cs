using System.Net;
using System.Text.Json;
using BlueBubbles.Core.Configuration;
using BlueBubbles.Core.Models;
using BlueBubbles.Core.Services;

namespace BlueBubbles.Windows.Tests;

public class LocalhostDetectionServiceTests
{
    private static readonly string ServerInfoWithIps = """
        {"status":200,"message":"OK","data":{"os_version":"14.0","server_version":"1.9.0","private_api":true,"helper_connected":true,"proxy_service":"ngrok","detected_icloud":null,"local_ipv4s":["192.168.1.50","192.168.1.51"],"local_ipv6s":["fe80::1"],"platform":"darwin"}}
        """;

    private static readonly string PingResponse = """{"status":200,"message":"pong"}""";

    private static (LocalhostDetectionService service, AppSettings settings, BlueBubblesApiService api) CreateService(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler,
        string serverUrl = "https://remote.example.com")
    {
        var mock = new MockHandler(handler);
        var config = new ServerConfiguration { ServerUrl = serverUrl, Password = "pw" };
        var settings = new AppSettings { LocalhostPort = "1234" };
        var api = new BlueBubblesApiService(new HttpClient(mock), config, settings);
        var service = new LocalhostDetectionService(api, settings);
        return (service, settings, api);
    }

    [Fact]
    public async Task TryActivateAsync_NoPort_ReturnsFalse()
    {
        var (service, settings, api) = CreateService(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        settings.LocalhostPort = string.Empty;

        var result = await service.TryActivateAsync();

        Assert.False(result);
        Assert.Null(service.ResolvedLocalUrl);
        Assert.Null(api.OriginOverride);
    }

    [Fact]
    public async Task TryActivateAsync_ServerInfoFails_ReturnsFalse()
    {
        var (service, _, api) = CreateService(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("""{"status":500,"message":"error"}""")
            }));

        var result = await service.TryActivateAsync();

        Assert.False(result);
        Assert.Null(service.ResolvedLocalUrl);
        Assert.Null(api.OriginOverride);
    }

    [Fact]
    public async Task TryActivateAsync_Ipv4Reachable_SetsOverride()
    {
        var (service, _, api) = CreateService(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("server/info"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(ServerInfoWithIps)
                });
            if (url.Contains("192.168.1.50:1234"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(PingResponse)
                });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        });

        var result = await service.TryActivateAsync();

        Assert.True(result);
        Assert.Equal("https://192.168.1.50:1234", service.ResolvedLocalUrl);
        Assert.Equal("https://192.168.1.50:1234", api.OriginOverride);
    }

    [Fact]
    public async Task TryActivateAsync_HttpsFails_FallsBackToHttp()
    {
        var (service, _, api) = CreateService(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("server/info"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(ServerInfoWithIps)
                });
            if (url.StartsWith("http://192.168.1.50:1234"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(PingResponse)
                });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        });

        var result = await service.TryActivateAsync();

        Assert.True(result);
        Assert.Equal("http://192.168.1.50:1234", service.ResolvedLocalUrl);
        Assert.Equal("http://192.168.1.50:1234", api.OriginOverride);
    }

    [Fact]
    public async Task TryActivateAsync_AllPingsFail_ReturnsFlase()
    {
        var (service, _, api) = CreateService(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("server/info"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(ServerInfoWithIps)
                });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        });

        var result = await service.TryActivateAsync();

        Assert.False(result);
        Assert.Null(service.ResolvedLocalUrl);
        Assert.Null(api.OriginOverride);
    }

    [Fact]
    public async Task TryActivateAsync_Ipv6Preferred_WhenEnabled()
    {
        var (service, settings, api) = CreateService(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("server/info"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(ServerInfoWithIps)
                });
            if (url.Contains("[fe80::1]:1234"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(PingResponse)
                });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        });
        settings.UseLocalIpv6 = true;

        var result = await service.TryActivateAsync();

        Assert.True(result);
        Assert.Contains("[fe80::1]:1234", service.ResolvedLocalUrl!);
    }

    [Fact]
    public void Deactivate_ClearsState()
    {
        var (service, _, api) = CreateService(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        api.OriginOverride = "https://192.168.1.50:1234";

        service.Deactivate();

        Assert.Null(service.ResolvedLocalUrl);
        Assert.Null(api.OriginOverride);
    }

    [Fact]
    public async Task TryActivateAsync_ConcurrentCalls_DoesNotCrash()
    {
        var (service, _, _) = CreateService(async req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("server/info"))
            {
                await Task.Delay(50);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(ServerInfoWithIps)
                };
            }
            if (url.Contains("192.168.1.50:1234"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(PingResponse)
                };
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });

        var tasks = Enumerable.Range(0, 5)
            .Select(_ => service.TryActivateAsync())
            .ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.Contains(true, results);
    }
}
