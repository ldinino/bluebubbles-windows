using System.Text.Json.Serialization;

namespace BlueBubbles.Core.Models;

public record ApiResponse<T>(
    [property: JsonPropertyName("status")] int Status,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("data")] T? Data,
    [property: JsonPropertyName("error")] ApiError? Error
);

public record ApiError(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("error")] string ErrorMessage
);
