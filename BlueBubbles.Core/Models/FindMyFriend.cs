using System.Text.Json.Serialization;

namespace BlueBubbles.Core.Models;

public record FindMyFriend(
    [property: JsonPropertyName("coordinates")] List<double?>? Coordinates,
    [property: JsonPropertyName("long_address")] string? LongAddress,
    [property: JsonPropertyName("short_address")] string? ShortAddress,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("subtitle")] string? Subtitle,
    [property: JsonPropertyName("handle")] string? HandleAddress,
    [property: JsonPropertyName("last_updated")] long? LastUpdated,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("is_locating_in_progress")] bool LocatingInProgress
);
