using BlueBubbles.Core.Utils;
using Xunit;

namespace BlueBubbles.Windows.Tests;

public class LinkMetadataParserTests
{
    private static readonly Uri Base = new("https://example.com/page");

    [Fact]
    public void Parse_ExtractsOpenGraphTags()
    {
        const string html = """
            <html><head>
            <meta property="og:title" content="Great Article">
            <meta property="og:description" content="A summary of the article.">
            <meta property="og:image" content="https://cdn.example.com/img.jpg">
            <meta property="og:site_name" content="Example News">
            </head></html>
            """;

        var meta = LinkMetadataParser.Parse(html, Base);

        Assert.NotNull(meta);
        Assert.Equal("Great Article", meta!.Title);
        Assert.Equal("A summary of the article.", meta.Description);
        Assert.Equal("https://cdn.example.com/img.jpg", meta.ImageUrl);
        Assert.Equal("Example News", meta.SiteName);
    }

    [Fact]
    public void Parse_HandlesContentBeforeProperty()
    {
        const string html = """<meta content="Reversed Order" property="og:title">""";
        var meta = LinkMetadataParser.Parse(html, Base);
        Assert.Equal("Reversed Order", meta!.Title);
    }

    [Fact]
    public void Parse_FallsBackToTitleTagAndNameDescription()
    {
        const string html = """
            <html><head>
            <title>Plain Title</title>
            <meta name="description" content="Name-based description">
            </head></html>
            """;

        var meta = LinkMetadataParser.Parse(html, Base);

        Assert.NotNull(meta);
        Assert.Equal("Plain Title", meta!.Title);
        Assert.Equal("Name-based description", meta.Description);
        Assert.Equal("example.com", meta.SiteName); // host fallback
        Assert.Null(meta.ImageUrl);
    }

    [Fact]
    public void Parse_ResolvesRelativeImageAgainstBase()
    {
        const string html = """<meta property="og:image" content="/assets/cover.png">""";
        var meta = LinkMetadataParser.Parse(html, Base);
        Assert.Equal("https://example.com/assets/cover.png", meta!.ImageUrl);
    }

    [Fact]
    public void Parse_DecodesHtmlEntities()
    {
        const string html = """<meta property="og:title" content="Tom &amp; Jerry &#39;s">""";
        var meta = LinkMetadataParser.Parse(html, Base);
        Assert.Equal("Tom & Jerry 's", meta!.Title);
    }

    [Fact]
    public void Parse_UsesTwitterCardWhenNoOpenGraph()
    {
        const string html = """<meta name="twitter:title" content="Tweet Title">""";
        var meta = LinkMetadataParser.Parse(html, Base);
        Assert.Equal("Tweet Title", meta!.Title);
    }

    [Fact]
    public void Parse_ReturnsNull_WhenNoUsableMetadata()
    {
        Assert.Null(LinkMetadataParser.Parse("<html><body>no metadata here</body></html>", Base));
        Assert.Null(LinkMetadataParser.Parse("", Base));
        Assert.Null(LinkMetadataParser.Parse(null, Base));
    }
}
