using System.Text.Json.Serialization;

namespace BlueBubbles.Core.Models;

public record Attachment(
    [property: JsonPropertyName("originalROWID")] int? OriginalRowId,
    [property: JsonPropertyName("guid")] string Guid,
    [property: JsonPropertyName("uti")] string? Uti,
    [property: JsonPropertyName("mimeType")] string? MimeType,
    [property: JsonPropertyName("isOutgoing")] bool IsOutgoing,
    [property: JsonPropertyName("transferName")] string? TransferName,
    [property: JsonPropertyName("totalBytes")] long TotalBytes,
    [property: JsonPropertyName("height")] int? Height,
    [property: JsonPropertyName("width")] int? Width,
    [property: JsonPropertyName("hasLivePhoto")] bool HasLivePhoto,
    [property: JsonPropertyName("metadata")] Dictionary<string, object?>? Metadata
);
