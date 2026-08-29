using System.Text.Json.Serialization;

namespace BlueBubbles.Core.Models;

public record ApiResponse<T>(
    [property: JsonPropertyName("status")] int Status,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("data")] T? Data,
    [property: JsonPropertyName("error")] ApiError? Error
)
{
    [JsonIgnore] public bool IsSuccess => Status is >= 200 and < 300;

    /// <summary>Human-readable reason the call failed; empty when it succeeded.</summary>
    [JsonIgnore] public string FailureMessage => IsSuccess ? string.Empty : Error?.ErrorMessage ?? Message;
}

public record ApiError(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("error")] string ErrorMessage
);
