using System.Net;
using System.Text.Json;
using BlueBubbles.Core.Configuration;
using BlueBubbles.Core.Services;
using BlueBubbles.Core.Services.Http;

namespace BlueBubbles.Windows.Tests;

public class MockHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;
    public List<HttpRequestMessage> Requests { get; } = new();

    public MockHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        => _handler = handler;

    public MockHandler(HttpResponseMessage response)
        : this(_ => Task.FromResult(response)) { }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        return _handler(request);
    }

    public HttpClient CreateClient() => new(this);
}

public class ProxyHeaderHandlerTests
{
    [Fact]
    public async Task Adds_NgrokHeader_ForNgrokUrls()
    {
        var config = new ServerConfiguration();
        var mock = new MockHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var handler = new ProxyHeaderHandler(config) { InnerHandler = mock };
        var client = new HttpClient(handler);

        await client.GetAsync("https://abc123.ngrok-free.app/api/v1/ping?guid=test");

        var sent = mock.Requests.Single();
        Assert.True(sent.Headers.Contains("ngrok-skip-browser-warning"));
        Assert.Equal("true", sent.Headers.GetValues("ngrok-skip-browser-warning").Single());
    }

    [Fact]
    public async Task Adds_ZrokHeader_ForZrokUrls()
    {
        var config = new ServerConfiguration();
        var mock = new MockHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var handler = new ProxyHeaderHandler(config) { InnerHandler = mock };
        var client = new HttpClient(handler);

        await client.GetAsync("https://myserver.share.zrok.io/api/v1/ping?guid=test");

        var sent = mock.Requests.Single();
        Assert.True(sent.Headers.Contains("skip_zrok_interstitial"));
        Assert.Equal("true", sent.Headers.GetValues("skip_zrok_interstitial").Single());
    }

    [Fact]
    public async Task Adds_CustomHeaders()
    {
        var config = new ServerConfiguration();
        config.CustomHeaders["X-Custom"] = "value123";
        var mock = new MockHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var handler = new ProxyHeaderHandler(config) { InnerHandler = mock };
        var client = new HttpClient(handler);

        await client.GetAsync("https://example.com/api/v1/ping");

        var sent = mock.Requests.Single();
        Assert.Equal("value123", sent.Headers.GetValues("X-Custom").Single());
    }

    [Fact]
    public async Task NoProxyHeaders_ForPlainUrls()
    {
        var config = new ServerConfiguration();
        var mock = new MockHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var handler = new ProxyHeaderHandler(config) { InnerHandler = mock };
        var client = new HttpClient(handler);

        await client.GetAsync("https://my-server.example.com/api/v1/ping");

        var sent = mock.Requests.Single();
        Assert.False(sent.Headers.Contains("ngrok-skip-browser-warning"));
        Assert.False(sent.Headers.Contains("skip_zrok_interstitial"));
    }
}

public class CloudflareRetryHandlerTests
{
    [Fact]
    public async Task Retries_On502_ForTrycloudflare()
    {
        int callCount = 0;
        var mock = new MockHandler(_ =>
        {
            callCount++;
            var status = callCount == 1 ? HttpStatusCode.BadGateway : HttpStatusCode.OK;
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(
                    """{"status":200,"message":"pong"}""",
                    System.Text.Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        });
        var handler = new CloudflareRetryHandler { InnerHandler = mock };
        var client = new HttpClient(handler);

        var result = await client.GetAsync(
            "https://abc.trycloudflare.com/api/v1/ping?guid=test");

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task NoRetry_On502_ForNonCloudflare()
    {
        int callCount = 0;
        var mock = new MockHandler(_ =>
        {
            callCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
        });
        var handler = new CloudflareRetryHandler { InnerHandler = mock };
        var client = new HttpClient(handler);

        var result = await client.GetAsync(
            "https://my-server.example.com/api/v1/ping?guid=test");

        Assert.Equal(HttpStatusCode.BadGateway, result.StatusCode);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task NoRetry_On200_ForTrycloudflare()
    {
        int callCount = 0;
        var mock = new MockHandler(_ =>
        {
            callCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"status":200,"message":"pong"}""",
                    System.Text.Encoding.UTF8, "application/json")
            });
        });
        var handler = new CloudflareRetryHandler { InnerHandler = mock };
        var client = new HttpClient(handler);

        var result = await client.GetAsync(
            "https://abc.trycloudflare.com/api/v1/ping?guid=test");

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task Retries_PostWithBody_PreservesContent()
    {
        int callCount = 0;
        string? receivedBody = null;
        var mock = new MockHandler(async req =>
        {
            callCount++;
            if (req.Content is not null)
                receivedBody = await req.Content.ReadAsStringAsync();
            var status = callCount == 1 ? HttpStatusCode.BadGateway : HttpStatusCode.OK;
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(
                    """{"status":200,"message":"Success"}""",
                    System.Text.Encoding.UTF8, "application/json")
            };
        });
        var handler = new CloudflareRetryHandler { InnerHandler = mock };
        var client = new HttpClient(handler);

        var request = new HttpRequestMessage(HttpMethod.Post,
            "https://abc.trycloudflare.com/api/v1/chat/query?guid=test")
        {
            Content = new StringContent(
                """{"offset":0,"limit":100}""",
                System.Text.Encoding.UTF8, "application/json")
        };
        await client.SendAsync(request);

        Assert.Equal(2, callCount);
        Assert.Equal("""{"offset":0,"limit":100}""", receivedBody);
    }
}

public class BlueBubblesApiServiceTests
{
    private static (BlueBubblesApiService service, MockHandler mock) CreateService(
        MockHandler mock,
        string serverUrl = "https://test.example.com",
        string password = "test-password")
    {
        var config = new ServerConfiguration
        {
            ServerUrl = serverUrl,
            Password = password
        };
        var settings = new AppSettings { ApiTimeout = 30000 };
        var client = new HttpClient(mock);
        var service = new BlueBubblesApiService(client, config, settings);
        return (service, mock);
    }

    private static MockHandler JsonMock(string json) => new(_ =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        }));

    [Fact]
    public async Task BuildUrl_AppendsGuidAuth()
    {
        var mock = JsonMock("""{"status":200,"message":"pong"}""");
        var (service, _) = CreateService(mock);

        await service.PingAsync();

        var url = mock.Requests.Single().RequestUri!.ToString();
        Assert.Contains("guid=test-password", url);
        Assert.Contains("/api/v1/ping", url);
    }

    [Fact]
    public async Task Ping_ReturnsApiResponse()
    {
        var mock = JsonMock("""{"status":200,"message":"pong"}""");
        var (service, _) = CreateService(mock);

        var result = await service.PingAsync();

        Assert.Equal(200, result.Status);
        Assert.Equal("pong", result.Message);
    }

    [Fact]
    public async Task GetServerInfo_DeserializesCorrectly()
    {
        var json = """
        {
            "status": 200,
            "message": "Success",
            "data": {
                "os_version": "14.0",
                "server_version": "1.9.7",
                "private_api": true,
                "helper_connected": true,
                "proxy_service": "Cloudflare",
                "detected_icloud": "test@icloud.com",
                "platform": "macOS"
            }
        }
        """;
        var (service, _) = CreateService(JsonMock(json));

        var result = await service.GetServerInfoAsync();

        Assert.Equal(200, result.Status);
        Assert.NotNull(result.Data);
        Assert.Equal("14.0", result.Data!.OsVersion);
        Assert.Equal("1.9.7", result.Data.ServerVersion);
        Assert.True(result.Data.PrivateApi);
        Assert.Equal("Cloudflare", result.Data.ProxyService);
    }

    [Fact]
    public async Task GetServerInfo_CachesForOneMinute()
    {
        int callCount = 0;
        var mock = new MockHandler(_ =>
        {
            callCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"status":200,"message":"Success","data":{"os_version":"14.0","server_version":"1.9.7"}}""",
                    System.Text.Encoding.UTF8, "application/json")
            });
        });
        var (service, _) = CreateService(mock);

        await service.GetServerInfoAsync();
        await service.GetServerInfoAsync();
        await service.GetServerInfoAsync();

        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task QueryChats_PostsCorrectBody()
    {
        string? requestBody = null;
        var mock = new MockHandler(async req =>
        {
            if (req.Content is not null)
                requestBody = await req.Content.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"status":200,"message":"Success","data":[]}""",
                    System.Text.Encoding.UTF8, "application/json")
            };
        });
        var (service, _) = CreateService(mock);

        await service.QueryChatsAsync(
            withQuery: new List<string> { "participants", "lastmessage" },
            offset: 10, limit: 25, sort: "lastmessage");

        Assert.NotNull(requestBody);
        var body = JsonDocument.Parse(requestBody!);
        Assert.Equal(10, body.RootElement.GetProperty("offset").GetInt32());
        Assert.Equal(25, body.RootElement.GetProperty("limit").GetInt32());
        Assert.Equal("lastmessage", body.RootElement.GetProperty("sort").GetString());
        var withArr = body.RootElement.GetProperty("with");
        Assert.Equal(2, withArr.GetArrayLength());
    }

    [Fact]
    public async Task SendText_EmptyMessageWithSubject_SendsSpace()
    {
        string? requestBody = null;
        var mock = new MockHandler(async req =>
        {
            if (req.Content is not null)
                requestBody = await req.Content.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"status":200,"message":"Success","data":{"guid":"msg-123"}}""",
                    System.Text.Encoding.UTF8, "application/json")
            };
        });
        var (service, _) = CreateService(mock);

        await service.SendTextAsync("chat-guid", "temp-guid", "",
            subject: "Hello Subject");

        Assert.NotNull(requestBody);
        var body = JsonDocument.Parse(requestBody!);
        Assert.Equal(" ", body.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task OriginOverride_ChangesBaseUrl()
    {
        var mock = JsonMock("""{"status":200,"message":"pong"}""");
        var (service, _) = CreateService(mock,
            serverUrl: "https://original.example.com");

        service.OriginOverride = "https://override.local:1234";
        await service.PingAsync();

        var url = mock.Requests.Single().RequestUri!.ToString();
        Assert.Contains("override.local:1234", url);
        Assert.DoesNotContain("original.example.com", url);
    }

    [Fact]
    public async Task GetChatMessages_IncludesQueryParams()
    {
        var mock = JsonMock("""{"status":200,"message":"Success","data":[]}""");
        var (service, _) = CreateService(mock);

        await service.GetChatMessagesAsync("chat-guid-123",
            withQuery: "attachment,handle", sort: "ASC",
            before: 1700000000000, after: 1600000000000,
            offset: 5, limit: 50);

        var url = mock.Requests.Single().RequestUri!.ToString();
        Assert.Contains("chat/chat-guid-123/message", url);
        Assert.Contains("sort=ASC", url);
        Assert.Contains("before=1700000000000", url);
        Assert.Contains("after=1600000000000", url);
        Assert.Contains("offset=5", url);
        Assert.Contains("limit=50", url);
    }

    [Fact]
    public async Task DeleteChat_UsesDeleteMethod()
    {
        var mock = JsonMock("""{"status":200,"message":"Success"}""");
        var (service, _) = CreateService(mock);

        await service.DeleteChatAsync("chat-guid-abc");

        var request = mock.Requests.Single();
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Contains("chat/chat-guid-abc", request.RequestUri!.ToString());
    }

    [Fact]
    public async Task UpdateChat_UsesPutMethod()
    {
        var mock = JsonMock("""{"status":200,"message":"Success","data":{"guid":"chat-1"}}""");
        var (service, _) = CreateService(mock);

        await service.UpdateChatAsync("chat-1", "New Group Name");

        var request = mock.Requests.Single();
        Assert.Equal(HttpMethod.Put, request.Method);
    }
}
