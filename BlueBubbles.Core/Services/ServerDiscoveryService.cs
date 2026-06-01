using System.Net.Http.Headers;
using System.Text.Json;
using BlueBubbles.Core.Models;
using BlueBubbles.Core.Utils;

namespace BlueBubbles.Core.Services;

public class ServerDiscoveryService : IServerDiscoveryService
{
    private readonly IBlueBubblesApiService _api;
    private readonly HttpClient _httpClient;

    private const string DesktopClientId =
        "500464701389-18rfq995s6dqo3e5d3n2e7i3ljr0uc9i.apps.googleusercontent.com";

    private const string RedirectUri = "http://localhost:8641/oauth/callback";

    private static readonly string[] Scopes =
    [
        "https://www.googleapis.com/auth/cloudplatformprojects",
        "https://www.googleapis.com/auth/firebase",
        "https://www.googleapis.com/auth/datastore"
    ];

    public ServerDiscoveryService(IBlueBubblesApiService api, HttpClient httpClient)
    {
        _api = api;
        _httpClient = httpClient;
    }

    public string BuildGoogleOAuthUrl()
    {
        var scope = Uri.EscapeDataString(string.Join(" ", Scopes));
        return "https://accounts.google.com/o/oauth2/v2/auth" +
               $"?client_id={DesktopClientId}" +
               $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
               "&response_type=token" +
               $"&scope={scope}";
    }

    public async Task<List<DiscoveredServer>> DiscoverServersAsync(
        string accessToken, CancellationToken ct = default)
    {
        var projects = await FetchFirebaseProjectsAsync(accessToken, ct);
        var results = new List<DiscoveredServer>();

        foreach (var project in projects)
        {
            ct.ThrowIfCancellationRequested();

            var serverUrl = await ResolveServerUrlAsync(project, accessToken, ct);
            if (serverUrl is null) continue;

            var reachable = await CheckReachabilityAsync(serverUrl, ct);
            results.Add(new DiscoveredServer(
                project.ProjectId,
                project.DisplayName,
                serverUrl,
                reachable));
        }

        return results;
    }

    private async Task<List<FirebaseProject>> FetchFirebaseProjectsAsync(
        string accessToken, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://firebase.googleapis.com/v1beta1/projects");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var list = JsonSerializer.Deserialize<FirebaseProjectList>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return list?.Results ?? [];
        }
        catch { return []; }
    }

    private async Task<string?> ResolveServerUrlAsync(
        FirebaseProject project, string accessToken, CancellationToken ct)
    {
        // Try RTDB first
        var rtdb = project.Resources?.RealtimeDatabaseInstance;
        if (!string.IsNullOrEmpty(rtdb))
        {
            var url = await FetchUrlFromRtdbAsync(rtdb, accessToken, ct);
            if (url is not null) return url;
        }

        // Fall back to Firestore
        var url2 = await FetchUrlFromFirestoreAsync(project.ProjectId, accessToken, ct);
        return url2;
    }

    private async Task<string?> FetchUrlFromRtdbAsync(
        string rtdbInstance, string accessToken, CancellationToken ct)
    {
        try
        {
            var url = $"https://{rtdbInstance}.firebaseio.com/config/serverUrl.json" +
                      $"?access_token={accessToken}";
            var response = await _httpClient.GetStringAsync(url, ct);
            var raw = response.Trim('"');
            return string.IsNullOrEmpty(raw) || raw == "null"
                ? null
                : AddressHelpers.SanitizeServerAddress(raw);
        }
        catch { return null; }
    }

    private async Task<string?> FetchUrlFromFirestoreAsync(
        string projectId, string accessToken, CancellationToken ct)
    {
        try
        {
            var url = $"https://firestore.googleapis.com/v1/projects/{projectId}" +
                      "/databases/(default)/documents/server/config";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(ct);
            var doc = JsonSerializer.Deserialize<JsonElement>(json);

            if (doc.TryGetProperty("fields", out var fields) &&
                fields.TryGetProperty("serverUrl", out var field) &&
                field.TryGetProperty("stringValue", out var value))
            {
                return AddressHelpers.SanitizeServerAddress(value.GetString());
            }
        }
        catch { /* Firestore read failed */ }

        return null;
    }

    private async Task<bool> CheckReachabilityAsync(string serverUrl, CancellationToken ct)
    {
        try
        {
            _api.OriginOverride = serverUrl;
            var response = await _api.PingAsync(ct);
            return response.Status == 200;
        }
        catch { return false; }
        finally { _api.OriginOverride = null; }
    }
}
