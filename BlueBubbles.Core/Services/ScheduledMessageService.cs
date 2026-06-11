using System.Text.Json;
using BlueBubbles.Core.Models;

namespace BlueBubbles.Core.Services;

/// <summary>
/// Thin pass-through over the scheduled-message REST endpoints. The server owns all timing and
/// state (it persists scheduled messages and fires them through the normal private-api send
/// path); the client never runs a timer. Live updates arrive via
/// <see cref="IActionHandler.ScheduledMessagesChanged"/>, not through this service.
/// </summary>
public class ScheduledMessageService(IBlueBubblesApiService api) : IScheduledMessageService
{
    public Task<ApiResponse<List<ScheduledMessage>>> GetAllAsync(CancellationToken ct = default)
        => api.GetScheduledMessagesAsync(ct);

    public Task<ApiResponse<ScheduledMessage>> CreateAsync(string chatGuid,
        string message, long scheduledForMs, string? effectId = null,
        string? subject = null, string? selectedMessageGuid = null,
        int? partIndex = null, Dictionary<string, object?>? schedule = null,
        CancellationToken ct = default)
    {
        if (Validate(message, scheduledForMs) is { } error)
            return Task.FromResult(error);

        return api.CreateScheduledMessageAsync(chatGuid, message.Trim(), scheduledForMs,
            effectId: effectId, subject: subject, selectedMessageGuid: selectedMessageGuid,
            partIndex: partIndex, schedule: schedule ?? OnceSchedule(), ct: ct);
    }

    public Task<ApiResponse<ScheduledMessage>> UpdateAsync(int id, string chatGuid,
        string message, long scheduledForMs, string? effectId = null,
        string? subject = null, string? selectedMessageGuid = null,
        int? partIndex = null, Dictionary<string, object?>? schedule = null,
        CancellationToken ct = default)
    {
        if (Validate(message, scheduledForMs) is { } error)
            return Task.FromResult(error);

        return api.UpdateScheduledMessageAsync(id, chatGuid, message.Trim(), scheduledForMs,
            effectId: effectId, subject: subject, selectedMessageGuid: selectedMessageGuid,
            partIndex: partIndex, schedule: schedule ?? OnceSchedule(), ct: ct);
    }

    public Task<ApiResponse<JsonElement>> DeleteAsync(int id, CancellationToken ct = default)
        => api.DeleteScheduledMessageAsync(id, ct);

    private static Dictionary<string, object?> OnceSchedule()
        => new() { ["type"] = "once" };

    /// <summary>Returns a synthesized 400 response (no API call) when the input is invalid.</summary>
    private static ApiResponse<ScheduledMessage>? Validate(string message, long scheduledForMs)
    {
        if (string.IsNullOrWhiteSpace(message))
            return BadRequest("Message text cannot be empty.");
        if (scheduledForMs <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            return BadRequest("Scheduled time must be in the future.");
        return null;
    }

    private static ApiResponse<ScheduledMessage> BadRequest(string message)
        => new(400, "Bad Request", null, new ApiError("ValidationError", message));
}
