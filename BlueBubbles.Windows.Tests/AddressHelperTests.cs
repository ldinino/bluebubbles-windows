using BlueBubbles.Core.Utils;

namespace BlueBubbles.Windows.Tests;

public class AddressHelperTests
{
    [Fact]
    public void Null_ReturnsNull()
        => Assert.Null(AddressHelpers.SanitizeServerAddress(null));

    [Fact]
    public void Empty_ReturnsNull()
        => Assert.Null(AddressHelpers.SanitizeServerAddress(""));

    [Fact]
    public void Whitespace_ReturnsNull()
        => Assert.Null(AddressHelpers.SanitizeServerAddress("   "));

    [Theory]
    [InlineData("http://192.168.1.100:1234", "http://192.168.1.100:1234")]
    [InlineData("http://192.168.1.100:1234/", "http://192.168.1.100:1234")]
    [InlineData("http://192.168.1.100:1234///", "http://192.168.1.100:1234")]
    public void PreservesExistingScheme(string input, string expected)
        => Assert.Equal(expected, AddressHelpers.SanitizeServerAddress(input));

    [Fact]
    public void NoScheme_DefaultsToHttp()
        => Assert.Equal("http://192.168.1.100:1234",
            AddressHelpers.SanitizeServerAddress("192.168.1.100:1234"));

    [Theory]
    [InlineData("abc123.ngrok.io")]
    [InlineData("abc123.ngrok-free.app")]
    [InlineData("my-tunnel.trycloudflare.com")]
    [InlineData("my-share.zrok.io")]
    public void TunnelHosts_ForceHttps(string host)
    {
        var result = AddressHelpers.SanitizeServerAddress(host);
        Assert.NotNull(result);
        Assert.StartsWith("https://", result);
    }

    [Fact]
    public void RemovesQuotes()
        => Assert.Equal("http://192.168.1.100:1234",
            AddressHelpers.SanitizeServerAddress("\"192.168.1.100:1234\""));

    [Fact]
    public void TrimsWhitespace()
        => Assert.Equal("http://192.168.1.100:1234",
            AddressHelpers.SanitizeServerAddress("  192.168.1.100:1234  "));

    [Fact]
    public void HttpsPreserved()
        => Assert.Equal("https://myserver.example.com",
            AddressHelpers.SanitizeServerAddress("https://myserver.example.com"));

    [Fact]
    public void ReturnsAuthorityOnly_StripsPath()
        => Assert.Equal("http://192.168.1.100:1234",
            AddressHelpers.SanitizeServerAddress("http://192.168.1.100:1234/api/v1"));

    [Fact]
    public void InvalidUrl_ReturnsNull()
        => Assert.Null(AddressHelpers.SanitizeServerAddress("not a valid url at all !@#$"));
}
