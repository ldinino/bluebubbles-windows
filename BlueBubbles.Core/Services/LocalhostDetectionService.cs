using BlueBubbles.Core.Configuration;

namespace BlueBubbles.Core.Services;

public class LocalhostDetectionService : ILocalhostDetectionService
{
    private readonly IBlueBubblesApiService _api;
    private readonly AppSettings _appSettings;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(3);

    public string? ResolvedLocalUrl { get; private set; }

    public LocalhostDetectionService(IBlueBubblesApiService api, AppSettings appSettings)
    {
        _api = api;
        _appSettings = appSettings;
    }

    public async Task<bool> TryActivateAsync(CancellationToken ct = default)
    {
        var port = _appSettings.LocalhostPort;
        if (string.IsNullOrEmpty(port))
        {
            Deactivate();
            return false;
        }

        if (!await _lock.WaitAsync(0, ct))
            return ResolvedLocalUrl is not null;

        try
        {
            var savedOverride = _api.OriginOverride;
            _api.OriginOverride = null;

            List<string> ipv4s;
            List<string> ipv6s;
            try
            {
                var info = await _api.GetServerInfoAsync(ct);
                if (info.Status != 200 || info.Data is null)
                {
                    _api.OriginOverride = savedOverride;
                    return false;
                }
                ipv4s = info.Data.LocalIpv4s ?? [];
                ipv6s = info.Data.LocalIpv6s ?? [];
            }
            catch
            {
                _api.OriginOverride = savedOverride;
                return false;
            }

            string? found = null;

            if (_appSettings.UseLocalIpv6 && ipv6s.Count > 0)
                found = await ProbeAddressesAsync(ipv6s, port, isIpv6: true, ct);

            if (found is null && ipv4s.Count > 0)
                found = await ProbeAddressesAsync(ipv4s, port, isIpv6: false, ct);

            ResolvedLocalUrl = found;
            _api.OriginOverride = found;

            if (found is not null)
                AppLog.Info(LogCategory.Socket, $"Local connection active: {found}");

            return found is not null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Deactivate()
    {
        ResolvedLocalUrl = null;
        _api.OriginOverride = null;
    }

    private async Task<string?> ProbeAddressesAsync(
        List<string> ips, string port, bool isIpv6, CancellationToken ct)
    {
        string[] schemes = ["https", "http"];

        foreach (var ip in ips)
        {
            foreach (var scheme in schemes)
            {
                ct.ThrowIfCancellationRequested();

                var host = isIpv6 ? $"[{ip}]" : ip;
                var address = $"{scheme}://{host}:{port}";

                if (await PingAddressAsync(address, ct))
                    return address;
            }
        }
        return null;
    }

    private async Task<bool> PingAddressAsync(string address, CancellationToken ct)
    {
        try
        {
            _api.OriginOverride = address;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(PingTimeout);
            var response = await _api.PingAsync(cts.Token);
            return response.Status == 200;
        }
        catch
        {
            return false;
        }
    }
}
