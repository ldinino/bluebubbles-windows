using System.Text.Json.Serialization;

namespace BlueBubbles.Core.Models;

public record ScheduledMessage(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("payload")] ScheduledMessagePayload Payload,
    [property: JsonPropertyName("scheduledFor")] string ScheduledFor,
    [property: JsonPropertyName("schedule")] ScheduledMessageSchedule Schedule,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("sentAt")] string? SentAt,
    [property: JsonPropertyName("created")] string Created
);

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
