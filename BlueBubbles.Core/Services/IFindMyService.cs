using BlueBubbles.Core.Models;

namespace BlueBubbles.Core.Services;

public interface IFindMyService
{
    Task<ApiResponse<List<FindMyDevice>>> GetDevicesAsync(
        CancellationToken ct = default);

    Task<ApiResponse<List<FindMyDevice>>> RefreshDevicesAsync(
        CancellationToken ct = default);

    Task<ApiResponse<List<FindMyFriend>>> GetFriendsAsync(
        CancellationToken ct = default);

    Task<ApiResponse<List<FindMyFriend>>> RefreshFriendsAsync(
        CancellationToken ct = default);
}
