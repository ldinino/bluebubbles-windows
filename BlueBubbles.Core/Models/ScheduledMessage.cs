using System.Globalization;
using System.Text.Json.Serialization;

namespace BlueBubbles.Core.Models;

public record ScheduledMessage(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("payload")] ScheduledMessagePayload? Payload,
    [property: JsonPropertyName("scheduledFor")] string ScheduledFor,
    [property: JsonPropertyName("schedule")] ScheduledMessageSchedule? Schedule,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("sentAt")] string? SentAt,
    [property: JsonPropertyName("created")] string Created
)
{
    /// <summary>
    /// <see cref="ScheduledFor"/> parsed into local time, or null when the server sent an
    /// unparseable value. Responses carry ISO 8601 strings (requests use ms epoch — the
    /// asymmetry is the server's contract).
    /// </summary>
    [JsonIgnore]
    public DateTimeOffset? ScheduledForLocal =>
        DateTimeOffset.TryParse(ScheduledFor, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto)
            ? dto.ToLocalTime()
            : null;
}

public record ScheduledMessagePayload(
    [property: JsonPropertyName("chatGuid")] string ChatGuid,
    [property: JsonPropertyName("message")] string MessageText,
    [property: JsonPropertyName("method")] string Method
);

public record ScheduledMessageSchedule(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("interval")] int? Interval,
    [property: JsonPropertyName("intervalType")] string? IntervalType
);

/// <summary>Wire values of <see cref="ScheduledMessage.Status"/>.</summary>
public static class ScheduledMessageStatus
{
    public const string Pending = "pending";
    public const string InProgress = "in-progress";
    public const string Complete = "complete";
    public const string Error = "error";
}
