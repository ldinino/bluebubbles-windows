using BlueBubbles.Core.Utils;
using Xunit;

namespace BlueBubbles.Windows.Tests;

public class UrlDetectorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("just some plain text with no link")]
    [InlineData("email me at someone@example.com")] // not a URL we link
    public void Find_ReturnsEmpty_WhenNoUrl(string? text)
    {
        Assert.Empty(UrlDetector.Find(text));
        Assert.False(UrlDetector.ContainsUrl(text));
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://example.com/path?q=1")]
    [InlineData("www.example.com")]
    public void Find_DetectsSingleUrl(string text)
    {
        var tokens = UrlDetector.Find(text);
        var token = Assert.Single(tokens);
        Assert.Equal(0, token.Start);
        Assert.Equal(text.Length, token.Length);
    }

    [Fact]
    public void Find_NormalizesBareWwwToHttps()
    {
        var token = Assert.Single(UrlDetector.Find("www.example.com"));
        Assert.Equal("www.example.com", token.Text);
        Assert.Equal("https://www.example.com", token.Url);
    }

    [Fact]
    public void Find_TrimsTrailingSentencePunctuation()
    {
        var token = Assert.Single(UrlDetector.Find("Check this out: https://example.com."));
        Assert.Equal("https://example.com", token.Text);
        Assert.Equal("https://example.com", token.Url);
    }

    [Fact]
    public void Find_KeepsBalancedTrailingParenthesis()
    {
        var token = Assert.Single(UrlDetector.Find("see https://en.wikipedia.org/wiki/Foo_(bar)"));
        Assert.Equal("https://en.wikipedia.org/wiki/Foo_(bar)", token.Text);
    }

    [Fact]
    public void Find_DetectsMultipleUrlsInOrder()
    {
        var tokens = UrlDetector.Find("first https://a.com then https://b.com end");
        Assert.Equal(2, tokens.Count);
        Assert.Equal("https://a.com", tokens[0].Text);
        Assert.Equal("https://b.com", tokens[1].Text);
        Assert.True(tokens[0].Start < tokens[1].Start);
    }

    [Fact]
    public void FirstUrl_ReturnsNormalizedFirstMatch()
    {
        Assert.Equal("https://www.example.com", UrlDetector.FirstUrl("go to www.example.com now"));
        Assert.Null(UrlDetector.FirstUrl("no links here"));
    }

    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("  https://example.com  ", true)]
    [InlineData("hey https://example.com", false)]
    [InlineData("https://example.com and more", false)]
    [InlineData("https://example.com.", false)] // trailing period leaves trailing text
    public void IsSingleUrl_DistinguishesPureUrlMessages(string text, bool expected)
    {
        Assert.Equal(expected, UrlDetector.IsSingleUrl(text));
    }
}
