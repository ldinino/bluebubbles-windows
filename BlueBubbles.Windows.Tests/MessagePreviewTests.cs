using BlueBubbles.Core.Utils;

namespace BlueBubbles.Windows.Tests;

public class MessagePreviewTests
{
    [Fact]
    public void Text_PassesThrough()
        => Assert.Equal("Hello", MessagePreview.Derive("Hello", null));

    [Fact]
    public void TextWithAttachmentPlaceholder_StripsPlaceholder()
        => Assert.Equal("Look at this", MessagePreview.Derive("￼Look at this",
            ["image/jpeg"]));

    [Fact]
    public void NullText_NoAttachments_StaysNull()
        => Assert.Null(MessagePreview.Derive(null, null));

    [Theory]
    [InlineData("image/jpeg", "Image")]
    [InlineData("video/mp4", "Video")]
    [InlineData("audio/x-caf", "Audio Message")]
    [InlineData("application/pdf", "Attachment")]
    [InlineData(null, "Attachment")]
    public void AttachmentOnly_SingleAttachment_DescribesKind(string? mime, string expected)
        => Assert.Equal(expected, MessagePreview.Derive(null, [mime]));

    [Fact]
    public void PlaceholderOnlyText_FallsBackToAttachmentKind()
        => Assert.Equal("Image", MessagePreview.Derive("￼", ["image/png"]));

    [Fact]
    public void WhitespaceText_FallsBackToAttachmentKind()
        => Assert.Equal("Video", MessagePreview.Derive("  ", ["video/quicktime"]));

    [Fact]
    public void MultipleSameKind_Pluralizes()
        => Assert.Equal("3 Images", MessagePreview.Derive(null,
            ["image/jpeg", "image/png", "image/gif"]));

    [Fact]
    public void MixedKinds_FallsBackToAttachments()
        => Assert.Equal("2 Attachments", MessagePreview.Derive(null,
            ["image/jpeg", "video/mp4"]));
}
