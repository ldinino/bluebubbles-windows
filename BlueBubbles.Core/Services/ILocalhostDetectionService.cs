namespace BlueBubbles.Core.Services;

public interface ILocalhostDetectionService
{
    string? ResolvedLocalUrl { get; }
    Task<bool> TryActivateAsync(CancellationToken ct = default);
    void Deactivate();
}
