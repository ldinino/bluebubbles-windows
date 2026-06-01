using System.Text.Json;
using BlueBubbles.Core.Models;

namespace BlueBubbles.Core.Services;

public interface IScheduledMessageService
{
    Task<ApiResponse<List<ScheduledMessage>>> GetAllAsync(
        CancellationToken ct = default);

    Task<ApiResponse<ScheduledMessage>> CreateAsync(string chatGuid,
        string message, long scheduledForMs, string? effectId = null,
        string? subject = null, string? selectedMessageGuid = null,
        int? partIndex = null, Dictionary<string, object?>? schedule = null,
        CancellationToken ct = default);

    Task<ApiResponse<ScheduledMessage>> UpdateAsync(int id, string chatGuid,
        string message, long scheduledForMs, string? effectId = null,
        string? subject = null, string? selectedMessageGuid = null,
        int? partIndex = null, Dictionary<string, object?>? schedule = null,
        CancellationToken ct = default);

    Task<ApiResponse<JsonElement>> DeleteAsync(int id,
        CancellationToken ct = default);
}
