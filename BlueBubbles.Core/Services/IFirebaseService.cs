namespace BlueBubbles.Core.Services;

public interface IFirebaseService
{
    Task FetchAndStoreConfigAsync(CancellationToken ct = default);
    Task<string?> FetchNewServerUrlAsync(CancellationToken ct = default);
}
