using System.Text.Json;
using BlueBubbles.Core.Models;

namespace BlueBubbles.Core.Services;

public interface IFaceTimeService
{
    Task<ApiResponse<JsonElement>> AnswerAsync(string callUuid,
        CancellationToken ct = default);

    Task<ApiResponse<JsonElement>> LeaveAsync(string callUuid,
        CancellationToken ct = default);
}
