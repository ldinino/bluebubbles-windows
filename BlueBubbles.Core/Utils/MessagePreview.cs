namespace BlueBubbles.Core.Utils;

/// <summary>
/// Derives the one-line conversation-list preview for a message. An attachment-only message has
/// no usable text (null, empty, or just U+FFFC placeholders), which previously left the tile
/// subtitle blank (B14) — fall back to a label derived from the attachment mime types instead,
/// matching how iMessage clients show "Image" for a photo-only message.
/// </summary>
public static class MessagePreview
{
    // U+FFFC object replacement character — iMessage embeds it in the text at each position
    // where an attachment belongs, so attachment messages often have text that is *only* these.
    private const string ObjectReplacementChar = "￼";

    public static string? Derive(string? text, IEnumerable<string?>? attachmentMimeTypes)
    {
        var stripped = text?.Replace(ObjectReplacementChar, string.Empty).Trim();
        if (!string.IsNullOrEmpty(stripped)) return stripped;

        var mimes = attachmentMimeTypes as IReadOnlyCollection<string?> ?? attachmentMimeTypes?.ToList();
        if (mimes is null or { Count: 0 }) return text;

        return DescribeAttachments(mimes);
    }

    private static string DescribeAttachments(IReadOnlyCollection<string?> mimeTypes)
    {
        var kinds = mimeTypes.Select(KindFromMime).Distinct().ToList();
        var label = kinds.Count == 1 ? kinds[0] : "Attachment";
        return mimeTypes.Count == 1 ? label : $"{mimeTypes.Count} {label}s";
    }

    private static string KindFromMime(string? mimeType)
    {
        if (string.IsNullOrEmpty(mimeType)) return "Attachment";
        if (mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return "Image";
        if (mimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) return "Video";
        if (mimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) return "Audio Message";
        return "Attachment";
    }
}
