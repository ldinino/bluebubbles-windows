using BlueBubbles.Core.Models;

namespace BlueBubbles.Core.Services;

public interface IServerDiscoveryService
{
    string BuildGoogleOAuthUrl();
    Task<List<DiscoveredServer>> DiscoverServersAsync(string accessToken, CancellationToken ct = default);
}
