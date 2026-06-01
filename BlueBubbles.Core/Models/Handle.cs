using System.Text.Json.Serialization;

namespace BlueBubbles.Core.Models;

public record Handle(
    [property: JsonPropertyName("originalROWID")] int OriginalRowId,
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("service")] string Service,
    [property: JsonPropertyName("country")] string? Country,
    [property: JsonPropertyName("formattedAddress")] string? FormattedAddress,
    [property: JsonPropertyName("color")] string? Color,
    [property: JsonPropertyName("defaultPhone")] string? DefaultPhone,
    [property: JsonPropertyName("defaultEmail")] string? DefaultEmail,
    [property: JsonPropertyName("uniqueAddrAndService")] string? UniqueAddressAndService
);
