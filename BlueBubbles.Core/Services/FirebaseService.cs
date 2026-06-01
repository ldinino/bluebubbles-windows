using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BlueBubbles.Core.Configuration;
using BlueBubbles.Core.Models;
using BlueBubbles.Core.Utils;

namespace BlueBubbles.Core.Services;

public class FirebaseService : IFirebaseService
{
    private readonly IBlueBubblesApiService _api;
    private readonly ServerConfiguration _config;
    private readonly HttpClient _httpClient;

    public FirebaseService(
        IBlueBubblesApiService api,
        ServerConfiguration config,
        HttpClient httpClient)
    {
        _api = api;
        _config = config;
        _httpClient = httpClient;
    }

    private async Task<string?> GetFirebaseIdTokenAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_config.FcmApiKey))
        {
            AppLog.Error("No FCM API key available for Firebase auth");
            return null;
        }

        try
        {
            AppLog.Info("Requesting Firebase anonymous ID token...");
            var url = $"https://identitytoolkit.googleapis.com/v1/accounts:signUp?key={_config.FcmApiKey}";
            var content = new StringContent("{\"returnSecureToken\":true}", Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(url, content, ct);

            if (!response.IsSuccessStatusCode)
            {
                AppLog.Info("Firebase anonymous auth not available — proceeding without token");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var doc = JsonSerializer.Deserialize<JsonElement>(json);
            if (doc.TryGetProperty("idToken", out var token))
            {
                AppLog.Info("Firebase ID token obtained");
                return token.GetString();
            }

            AppLog.Error("Firebase auth response missing idToken field");
            return null;
        }
        catch
        {
            AppLog.Info("Firebase anonymous auth not available — proceeding without token");
            return null;
        }
    }

    public async Task FetchAndStoreConfigAsync(CancellationToken ct = default)
    {
        AppLog.Info("Fetching FCM config from server...");
        var response = await _api.GetFcmClientAsync(ct);
        if (response.Status != 200)
        {
            AppLog.Warn($"FCM config fetch returned status {response.Status}");
            return;
        }

        var json = response.Data;
        if (json.ValueKind != JsonValueKind.Object) return;

        var fcmData = json.Deserialize<FcmData>(new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        if (fcmData is null) return;

        _config.FcmProjectId = fcmData.ProjectInfo?.ProjectId;
        _config.FcmStorageBucket = fcmData.ProjectInfo?.StorageBucket;
        _config.FcmFirebaseUrl = fcmData.ProjectInfo?.FirebaseUrl;

        var client = fcmData.Client?.FirstOrDefault();
        _config.FcmApplicationId = client?.ClientInfo?.MobileSdkAppId;
        _config.FcmApiKey = client?.ApiKey?.FirstOrDefault()?.CurrentKey;
        _config.FcmClientId = client?.OAuthClient?.FirstOrDefault()?.ClientId;
        AppLog.Info($"FCM config stored: project={_config.FcmProjectId}, rtdb={_config.FcmFirebaseUrl ?? "none"}");
    }

    public async Task<string?> FetchNewServerUrlAsync(CancellationToken ct = default)
    {
        if (!_config.HasValidFcmData)
        {
            AppLog.Error("No valid FCM data — cannot fetch server URL");
            return null;
        }

        AppLog.Info($"Fetching server URL (project={_config.FcmProjectId})");
        var idToken = await GetFirebaseIdTokenAsync(ct);

        if (!string.IsNullOrEmpty(_config.FcmFirebaseUrl))
        {
            AppLog.Info($"Trying RTDB: {_config.FcmFirebaseUrl}");
            var url = await FetchFromRtdbAsync(_config.FcmFirebaseUrl, idToken, ct);
            if (url is not null)
            {
                AppLog.Info($"RTDB returned: {url}");
                return url;
            }
            AppLog.Warn("RTDB returned no URL");
        }

        if (!string.IsNullOrEmpty(_config.FcmProjectId))
        {
            AppLog.Info($"Trying Firestore: {_config.FcmProjectId}");
            var url = await FetchFromFirestoreAsync(_config.FcmProjectId, idToken, ct);
            if (url is not null)
            {
                AppLog.Info($"Firestore returned: {url}");
                return url;
            }
            AppLog.Warn("Firestore returned no URL");
        }

        AppLog.Error("All Firebase sources failed to return a URL");
        return null;
    }

    private async Task<string?> FetchFromRtdbAsync(string firebaseUrl, string? idToken, CancellationToken ct)
    {
        try
        {
            var rtdbUrl = firebaseUrl.TrimEnd('/');
            var requestUrl = $"{rtdbUrl}/config/serverUrl.json";
            if (idToken is not null)
                requestUrl += $"?auth={idToken}";

            var response = await _httpClient.GetStringAsync(requestUrl, ct);
            var raw = response.Trim('"');
            return string.IsNullOrEmpty(raw) || raw == "null"
                ? null
                : AddressHelpers.SanitizeServerAddress(raw);
        }
        catch (Exception ex)
        {
            AppLog.Error($"RTDB fetch failed: {ex.Message}");
            return null;
        }
    }

    private async Task<string?> FetchFromFirestoreAsync(string projectId, string? idToken, CancellationToken ct)
    {
        try
        {
            var url = $"https://firestore.googleapis.com/v1/projects/{projectId}" +
                      "/databases/(default)/documents/server/config";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (idToken is not null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", idToken);

            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                AppLog.Error($"Firestore fetch failed: {response.StatusCode} — {body}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var doc = JsonSerializer.Deserialize<JsonElement>(json);

            if (doc.TryGetProperty("fields", out var fields) &&
                fields.TryGetProperty("serverUrl", out var serverUrlField) &&
                serverUrlField.TryGetProperty("stringValue", out var value))
            {
                return AddressHelpers.SanitizeServerAddress(value.GetString());
            }

            AppLog.Warn($"Firestore doc missing serverUrl field. Fields: {doc}");
        }
        catch (Exception ex)
        {
            AppLog.Error($"Firestore fetch exception: {ex.Message}");
        }

        return null;
    }
}
