using System.Text.Json.Serialization;

namespace BlueBubbles.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PayloadType
{
    Url = 0,
    App = 1
}

public record PayloadData(
    [property: JsonPropertyName("type")] PayloadType Type,
    [property: JsonPropertyName("urlData")] List<UrlPreviewData>? UrlData,
    [property: JsonPropertyName("appData")] List<IMessageAppData>? AppData
);

public record UrlPreviewData(
    [property: JsonPropertyName("itemType")] string? ItemType,
    [property: JsonPropertyName("originalURL")] Dictionary<string, string?>? OriginalUrl,
    [property: JsonPropertyName("URL")] Dictionary<string, string?>? Url,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("summary")] string? Summary,
    [property: JsonPropertyName("siteName")] string? SiteName,
    [property: JsonPropertyName("imageMetadata")] MediaMetadata? ImageMetadata,
    [property: JsonPropertyName("videoMetadata")] MediaMetadata? VideoMetadata,
    [property: JsonPropertyName("iconMetadata")] MediaMetadata? IconMetadata
);

public record MediaMetadata(
    [property: JsonPropertyName("size")] object? Size,
    [property: JsonPropertyName("URL")] Dictionary<string, string?>? Url
);

public record IMessageAppData(
    [property: JsonPropertyName("an")] string? AppName,
    [property: JsonPropertyName("ldtext")] string? LdText,
    [property: JsonPropertyName("URL")] Dictionary<string, string?>? Url,
    [property: JsonPropertyName("userInfo")] IMessageAppUserInfo? UserInfo
);

public record IMessageAppUserInfo(
    [property: JsonPropertyName("image-subtitle")] string? ImageSubtitle,
    [property: JsonPropertyName("image-title")] string? ImageTitle,
    [property: JsonPropertyName("caption")] string? Caption,
    [property: JsonPropertyName("secondary-subcaption")] string? SecondarySubcaption,
    [property: JsonPropertyName("tertiary-subcaption")] string? TertiarySubcaption,
    [property: JsonPropertyName("subcaption")] string? Subcaption
);
