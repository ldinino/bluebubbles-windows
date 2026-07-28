using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlueBubbles.Core.Configuration;
using BlueBubbles.Core.Models;

namespace BlueBubbles.Core.Services;

public class BlueBubblesApiService : IBlueBubblesApiService
{
    private readonly HttpClient _httpClient;
    private readonly ServerConfiguration _config;
    private readonly AppSettings _settings;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private ApiResponse<ServerInfo>? _serverInfoCache;
    private DateTime _serverInfoCacheTime;

    public string? OriginOverride { get; set; }

    private string Origin
    {
        get
        {
            var url = OriginOverride ?? _config.ServerUrl;
            if (string.IsNullOrEmpty(url)) return string.Empty;
            return Uri.TryCreate(url, UriKind.Absolute, out var uri)
                ? uri.GetLeftPart(UriPartial.Authority)
                : string.Empty;
        }
    }

    private string ApiRoot => $"{Origin}/api/v1";

    public BlueBubblesApiService(
        HttpClient httpClient, ServerConfiguration config, AppSettings settings)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
        _config = config;
        _settings = settings;
    }

    // ── Helpers ──

    private string BuildUrl(string path, Dictionary<string, string?>? extraParams = null)
    {
        var baseUrl = $"{ApiRoot}/{path}";
        var allParams = new Dictionary<string, string?> { ["guid"] = _config.Password };
        if (extraParams is not null)
            foreach (var kv in extraParams)
                allParams[kv.Key] = kv.Value;

        var pairs = allParams
            .Where(kv => kv.Value is not null)
            .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value!)}");
        var query = string.Join("&", pairs);
        return string.IsNullOrEmpty(query) ? baseUrl : $"{baseUrl}?{query}";
    }

    private TimeSpan DefaultTimeout => TimeSpan.FromMilliseconds(
        _settings.ApiTimeout > 0 ? _settings.ApiTimeout : 30000);

    private TimeSpan LongTimeout => DefaultTimeout * 12;

    private CancellationTokenSource CreateTimeoutCts(
        TimeSpan? timeout, CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? DefaultTimeout);
        return cts;
    }

    private async Task<ApiResponse<T>> SendJsonAsync<T>(
        HttpRequestMessage request, TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(timeout, ct);
        using var response = await _httpClient.SendAsync(request, cts.Token);
        var result = await response.Content
            .ReadFromJsonAsync<ApiResponse<T>>(JsonOptions, cts.Token);
        return result ?? throw new InvalidOperationException(
            "Server returned null response body.");
    }

    private async Task<ApiResponse<T>> GetAsync<T>(
        string path, Dictionary<string, string?>? query = null,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUrl(path, query));
        return await SendJsonAsync<T>(request, timeout, ct);
    }

    private async Task<ApiResponse<T>> PostAsync<T>(
        string path, object? body = null,
        Dictionary<string, string?>? query = null,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl(path, query));
        if (body is not null)
            request.Content = JsonContent.Create(body, options: JsonOptions);
        return await SendJsonAsync<T>(request, timeout, ct);
    }

    private async Task<ApiResponse<T>> PutAsync<T>(
        string path, object? body = null,
        Dictionary<string, string?>? query = null,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, BuildUrl(path, query));
        if (body is not null)
            request.Content = JsonContent.Create(body, options: JsonOptions);
        return await SendJsonAsync<T>(request, ct: ct);
    }

    private async Task<ApiResponse<T>> DeleteAsync<T>(
        string path, object? body = null,
        Dictionary<string, string?>? query = null,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, BuildUrl(path, query));
        if (body is not null)
            request.Content = JsonContent.Create(body, options: JsonOptions);
        return await SendJsonAsync<T>(request, ct: ct);
    }

    private async Task<byte[]> DownloadBytesAsync(
        string path, Dictionary<string, string?>? query = null,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(LongTimeout, ct);
        using var response = await _httpClient.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, BuildUrl(path, query)),
            HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.EnsureSuccessStatusCode();

        if (progress is null)
            return await response.Content.ReadAsByteArrayAsync(cts.Token);

        var totalBytes = response.Content.Headers.ContentLength;
        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        long bytesRead = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, cts.Token)) > 0)
        {
            ms.Write(buffer, 0, read);
            bytesRead += read;
            if (totalBytes is > 0)
                progress.Report((double)bytesRead / totalBytes.Value);
        }
        return ms.ToArray();
    }

    private async Task<ApiResponse<T>> SendMultipartAsync<T>(
        string path, MultipartFormDataContent content,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl(path));
        request.Content = content;
        return await SendJsonAsync<T>(request, timeout ?? LongTimeout, ct);
    }

    // ── Server (11) ──

    public Task<ApiResponse<JsonElement>> PingAsync(CancellationToken ct = default)
        => GetAsync<JsonElement>("ping", ct: ct);

    public async Task<ApiResponse<ServerInfo>> GetServerInfoAsync(CancellationToken ct = default)
    {
        if (_serverInfoCache is not null
            && DateTime.UtcNow - _serverInfoCacheTime < TimeSpan.FromMinutes(1))
            return _serverInfoCache;

        var result = await GetAsync<ServerInfo>("server/info", ct: ct);
        _serverInfoCache = result;
        _serverInfoCacheTime = DateTime.UtcNow;
        return result;
    }

    public Task<ApiResponse<JsonElement>> SoftRestartAsync(CancellationToken ct = default)
        => GetAsync<JsonElement>("server/restart/soft", ct: ct);

    public Task<ApiResponse<JsonElement>> HardRestartAsync(CancellationToken ct = default)
        => GetAsync<JsonElement>("server/restart/hard", ct: ct);

    public Task<ApiResponse<JsonElement>> CheckUpdateAsync(CancellationToken ct = default)
        => GetAsync<JsonElement>("server/update/check", ct: ct);

    public Task<ApiResponse<JsonElement>> InstallUpdateAsync(CancellationToken ct = default)
        => PostAsync<JsonElement>("server/update/install", ct: ct);

    public Task<ApiResponse<JsonElement>> GetStatTotalsAsync(CancellationToken ct = default)
        => GetAsync<JsonElement>("server/statistics/totals", ct: ct);

    public Task<ApiResponse<JsonElement>> GetStatMediaAsync(
        bool byChat = false, CancellationToken ct = default)
        => GetAsync<JsonElement>(
            byChat ? "server/statistics/media/chat" : "server/statistics/media", ct: ct);

    public Task<ApiResponse<JsonElement>> GetServerLogsAsync(
        int count = 10000, CancellationToken ct = default)
        => GetAsync<JsonElement>("server/logs",
            new Dictionary<string, string?> { ["count"] = count.ToString() }, ct: ct);

    public Task<ApiResponse<JsonElement>> LockMacAsync(CancellationToken ct = default)
        => PostAsync<JsonElement>("mac/lock", ct: ct);

    public Task<ApiResponse<JsonElement>> RestartImessageAsync(CancellationToken ct = default)
        => PostAsync<JsonElement>("mac/imessage/restart", ct: ct);

    // ── FCM (2) ──

    public Task<ApiResponse<JsonElement>> AddFcmDeviceAsync(
        string name, string identifier, CancellationToken ct = default)
        => PostAsync<JsonElement>("fcm/device",
            new { name, identifier }, ct: ct);

    public Task<ApiResponse<JsonElement>> GetFcmClientAsync(CancellationToken ct = default)
        => GetAsync<JsonElement>("fcm/client", ct: ct);

    // ── Attachments (5) ──

    public Task<ApiResponse<Attachment>> GetAttachmentInfoAsync(
        string guid, CancellationToken ct = default)
        => GetAsync<Attachment>($"attachment/{guid}", ct: ct);

    public Task<byte[]> DownloadAttachmentAsync(
        string guid, bool original = false,
        IProgress<double>? progress = null, CancellationToken ct = default)
        => DownloadBytesAsync($"attachment/{guid}/download",
            new Dictionary<string, string?> { ["original"] = original.ToString().ToLowerInvariant() },
            progress, ct);

    public Task<byte[]> ForceDownloadAttachmentAsync(
        string guid, IProgress<double>? progress = null, CancellationToken ct = default)
        => DownloadBytesAsync($"attachment/{guid}/download/force", progress: progress, ct: ct);

    public Task<byte[]> DownloadLivePhotoAsync(
        string guid, IProgress<double>? progress = null,
        CancellationToken ct = default)
        => DownloadBytesAsync($"attachment/{guid}/live", progress: progress, ct: ct);

    public Task<byte[]> GetAttachmentBlurhashAsync(
        string guid, CancellationToken ct = default)
        => DownloadBytesAsync($"attachment/{guid}/blurhash", ct: ct);

    public Task<ApiResponse<JsonElement>> GetAttachmentCountAsync(
        CancellationToken ct = default)
        => GetAsync<JsonElement>("attachment/count", ct: ct);

    // ── Chats (16) ──

    public Task<ApiResponse<List<Chat>>> QueryChatsAsync(
        List<string>? withQuery = null, int offset = 0, int limit = 100,
        string? sort = null, CancellationToken ct = default)
        => PostAsync<List<Chat>>("chat/query", new
        {
            with = withQuery ?? new List<string>(),
            offset,
            limit,
            sort
        }, ct: ct);

    public Task<ApiResponse<Chat>> GetChatAsync(
        string guid, string? withQuery = null, CancellationToken ct = default)
        => GetAsync<Chat>($"chat/{guid}",
            new Dictionary<string, string?> { ["with"] = withQuery ?? string.Empty },
            ct: ct);

    public Task<ApiResponse<List<Message>>> GetChatMessagesAsync(
        string guid, string? withQuery = null, string sort = "DESC",
        long? before = null, long? after = null,
        int offset = 0, int limit = 100, CancellationToken ct = default)
        => GetAsync<List<Message>>($"chat/{guid}/message",
            new Dictionary<string, string?>
            {
                ["with"] = withQuery ?? string.Empty,
                ["sort"] = sort,
                ["before"] = before?.ToString(),
                ["after"] = after?.ToString(),
                ["offset"] = offset.ToString(),
                ["limit"] = limit.ToString()
            }, ct: ct);

    public Task<ApiResponse<JsonElement>> GetChatCountAsync(CancellationToken ct = default)
        => GetAsync<JsonElement>("chat/count", ct: ct);

    public Task<ApiResponse<Chat>> CreateChatAsync(
        List<string> addresses, string? message, string service,
        string method = "private-api", CancellationToken ct = default)
        => PostAsync<Chat>("chat/new", new
        {
            addresses,
            message,
            service,
            method
        }, ct: ct);

    public Task<ApiResponse<Chat>> UpdateChatAsync(
        string guid, string displayName, CancellationToken ct = default)
        => PutAsync<Chat>($"chat/{guid}", new { displayName }, ct: ct);

    public Task<ApiResponse<JsonElement>> DeleteChatAsync(
        string guid, CancellationToken ct = default)
        => DeleteAsync<JsonElement>($"chat/{guid}", ct: ct);

    public Task<ApiResponse<JsonElement>> MarkChatReadAsync(
        string guid, CancellationToken ct = default)
        => PostAsync<JsonElement>($"chat/{guid}/read", ct: ct);

    public Task<ApiResponse<JsonElement>> MarkChatUnreadAsync(
        string guid, CancellationToken ct = default)
        => PostAsync<JsonElement>($"chat/{guid}/unread", ct: ct);

    public Task<byte[]> GetChatIconAsync(
        string guid, CancellationToken ct = default)
        => DownloadBytesAsync($"chat/{guid}/icon", ct: ct);

    public async Task<ApiResponse<JsonElement>> SetChatIconAsync(
        string guid, Stream iconStream, string fileName,
        CancellationToken ct = default)
    {
        var content = new MultipartFormDataContent();
        content.Add(new StreamContent(iconStream), "icon", fileName);
        return await SendMultipartAsync<JsonElement>($"chat/{guid}/icon", content, ct: ct);
    }

    public Task<ApiResponse<JsonElement>> DeleteChatIconAsync(
        string guid, CancellationToken ct = default)
        => DeleteAsync<JsonElement>($"chat/{guid}/icon", ct: ct);

    public Task<ApiResponse<Chat>> AddParticipantAsync(
        string chatGuid, string address, CancellationToken ct = default)
        => PostAsync<Chat>($"chat/{chatGuid}/participant/add",
            new { address }, ct: ct);

    public Task<ApiResponse<Chat>> RemoveParticipantAsync(
        string chatGuid, string address, CancellationToken ct = default)
        => PostAsync<Chat>($"chat/{chatGuid}/participant/remove",
            new { address }, ct: ct);

    public Task<ApiResponse<JsonElement>> LeaveChatAsync(
        string guid, CancellationToken ct = default)
        => PostAsync<JsonElement>($"chat/{guid}/leave", ct: ct);

    public Task<ApiResponse<JsonElement>> DeleteMessageFromChatAsync(
        string chatGuid, string messageGuid, CancellationToken ct = default)
        => DeleteAsync<JsonElement>($"chat/{chatGuid}/{messageGuid}", ct: ct);

    // ── Messages (17) ──

    public Task<ApiResponse<List<Message>>> QueryMessagesAsync(
        List<string>? withQuery = null, List<object>? where = null,
        string sort = "DESC", long? before = null, long? after = null,
        string? chatGuid = null, int offset = 0, int limit = 100,
        bool convertAttachments = true, CancellationToken ct = default)
        => PostAsync<List<Message>>("message/query", new
        {
            with = withQuery ?? new List<string>(),
            where = where ?? new List<object>(),
            sort,
            before,
            after,
            chatGuid,
            offset,
            limit,
            convertAttachments
        }, ct: ct);

    public Task<ApiResponse<Message>> GetMessageAsync(
        string guid, string? withQuery = null, CancellationToken ct = default)
        => GetAsync<Message>($"message/{guid}",
            new Dictionary<string, string?> { ["with"] = withQuery ?? string.Empty },
            ct: ct);

    public Task<byte[]> GetEmbeddedMediaAsync(
        string guid, CancellationToken ct = default)
        => DownloadBytesAsync($"message/{guid}/embedded-media", ct: ct);

    public Task<ApiResponse<JsonElement>> GetMessageCountAsync(
        long? after = null, long? before = null, CancellationToken ct = default)
        => GetAsync<JsonElement>("message/count",
            new Dictionary<string, string?>
            {
                ["after"] = after?.ToString(),
                ["before"] = before?.ToString()
            }, ct: ct);

    public Task<ApiResponse<JsonElement>> GetUpdatedMessageCountAsync(
        long? after = null, long? before = null, CancellationToken ct = default)
        => GetAsync<JsonElement>("message/count/updated",
            new Dictionary<string, string?>
            {
                ["after"] = after?.ToString(),
                ["before"] = before?.ToString()
            }, ct: ct);

    public Task<ApiResponse<JsonElement>> GetMyMessageCountAsync(
        long? after = null, long? before = null, CancellationToken ct = default)
        => GetAsync<JsonElement>("message/count/me",
            new Dictionary<string, string?>
            {
                ["after"] = after?.ToString(),
                ["before"] = before?.ToString()
            }, ct: ct);

    public Task<ApiResponse<Message>> SendTextAsync(
        string chatGuid, string tempGuid, string message,
        string? method = null, string? effectId = null,
        string? subject = null, string? selectedMessageGuid = null,
        int? partIndex = null, bool? ddScan = null,
        CancellationToken ct = default)
    {
        var text = string.IsNullOrEmpty(message) && !string.IsNullOrEmpty(subject)
            ? " " : message;

        return PostAsync<Message>("message/text", new
        {
            chatGuid,
            tempGuid,
            message = text,
            method,
            effectId,
            subject,
            selectedMessageGuid,
            partIndex,
            ddScan
        }, ct: ct);
    }

    public async Task<ApiResponse<Message>> SendAttachmentAsync(
        string chatGuid, string tempGuid, Stream fileStream, string fileName,
        string? method = null, string? effectId = null, string? subject = null,
        string? selectedMessageGuid = null, int? partIndex = null,
        bool? isAudioMessage = null, IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var content = new MultipartFormDataContent();
        content.Add(new StreamContent(fileStream), "attachment", fileName);
        content.Add(new StringContent(chatGuid), "chatGuid");
        content.Add(new StringContent(tempGuid), "tempGuid");
        content.Add(new StringContent(fileName), "name");
        if (method is not null) content.Add(new StringContent(method), "method");
        if (effectId is not null) content.Add(new StringContent(effectId), "effectId");
        if (subject is not null) content.Add(new StringContent(subject), "subject");
        if (selectedMessageGuid is not null)
            content.Add(new StringContent(selectedMessageGuid), "selectedMessageGuid");
        if (partIndex is not null)
            content.Add(new StringContent(partIndex.Value.ToString()), "partIndex");
        if (isAudioMessage is not null)
            content.Add(new StringContent(isAudioMessage.Value.ToString().ToLowerInvariant()),
                "isAudioMessage");

        return await SendMultipartAsync<Message>("message/attachment", content, ct: ct);
    }

    public Task<ApiResponse<Message>> SendMultipartAsync(
        string chatGuid, string tempGuid,
        List<Dictionary<string, object?>> parts,
        string? effectId = null, string? subject = null,
        string? selectedMessageGuid = null, int? partIndex = null,
        bool? ddScan = null, CancellationToken ct = default)
        => PostAsync<Message>("message/multipart", new
        {
            chatGuid,
            tempGuid,
            parts,
            effectId,
            subject,
            selectedMessageGuid,
            partIndex,
            ddScan
        }, ct: ct);

    public Task<ApiResponse<Message>> SendTapbackAsync(
        string chatGuid, string selectedMessageText,
        string selectedMessageGuid, string reaction,
        int? partIndex = null, CancellationToken ct = default)
        => PostAsync<Message>("message/react", new
        {
            chatGuid,
            selectedMessageText,
            selectedMessageGuid,
            reaction,
            partIndex
        }, ct: ct);

    public Task<ApiResponse<Message>> UnsendMessageAsync(
        string messageGuid, int partIndex = 0, CancellationToken ct = default)
        => PostAsync<Message>($"message/{messageGuid}/unsend",
            new { partIndex }, ct: ct);

    public Task<ApiResponse<Message>> EditMessageAsync(
        string messageGuid, string editedMessage,
        string backwardsCompatMessage, int partIndex = 0,
        CancellationToken ct = default)
        => PostAsync<Message>($"message/{messageGuid}/edit", new
        {
            editedMessage,
            backwardsCompatibilityMessage = backwardsCompatMessage,
            partIndex
        }, ct: ct);

    public Task<ApiResponse<JsonElement>> NotifyMessageAsync(
        string messageGuid, CancellationToken ct = default)
        => PostAsync<JsonElement>($"message/{messageGuid}/notify", ct: ct);

    public Task<ApiResponse<List<ScheduledMessage>>> GetScheduledMessagesAsync(
        CancellationToken ct = default)
        => GetAsync<List<ScheduledMessage>>("message/schedule", ct: ct);

    public Task<ApiResponse<ScheduledMessage>> CreateScheduledMessageAsync(
        string chatGuid, string message, long scheduledForMs,
        string method = "private-api", string? effectId = null,
        string? subject = null, string? selectedMessageGuid = null,
        int? partIndex = null, Dictionary<string, object?>? schedule = null,
        CancellationToken ct = default)
        => PostAsync<ScheduledMessage>("message/schedule", new
        {
            type = "send-message",
            payload = new { chatGuid, message, method, effectId, subject,
                selectedMessageGuid, partIndex },
            scheduledFor = scheduledForMs,
            // The server validator requires schedule.type ("once"/"recurring"); an empty
            // schedule object is rejected.
            schedule = schedule ?? new Dictionary<string, object?> { ["type"] = "once" }
        }, ct: ct);

    public Task<ApiResponse<ScheduledMessage>> UpdateScheduledMessageAsync(
        int id, string chatGuid, string message, long scheduledForMs,
        string method = "private-api", string? effectId = null,
        string? subject = null, string? selectedMessageGuid = null,
        int? partIndex = null, Dictionary<string, object?>? schedule = null,
        CancellationToken ct = default)
        => PutAsync<ScheduledMessage>($"message/schedule/{id}", new
        {
            type = "send-message",
            payload = new { chatGuid, message, method, effectId, subject,
                selectedMessageGuid, partIndex },
            scheduledFor = scheduledForMs,
            schedule = schedule ?? new Dictionary<string, object?> { ["type"] = "once" }
        }, ct: ct);

    public Task<ApiResponse<JsonElement>> DeleteScheduledMessageAsync(
        int id, CancellationToken ct = default)
        => DeleteAsync<JsonElement>($"message/schedule/{id}", ct: ct);

    // ── Handles (6) ──

    public Task<ApiResponse<List<Handle>>> QueryHandlesAsync(
        List<string>? withQuery = null, string? address = null,
        int offset = 0, int limit = 100, CancellationToken ct = default)
        => PostAsync<List<Handle>>("handle/query", new
        {
            with = withQuery ?? new List<string>(),
            address,
            offset,
            limit
        }, ct: ct);

    public Task<ApiResponse<Handle>> GetHandleAsync(
        string guid, CancellationToken ct = default)
        => GetAsync<Handle>($"handle/{guid}", ct: ct);

    public Task<ApiResponse<JsonElement>> GetHandleFocusStateAsync(
        string address, CancellationToken ct = default)
        => GetAsync<JsonElement>($"handle/{address}/focus", ct: ct);

    public Task<ApiResponse<JsonElement>> GetIMessageAvailabilityAsync(
        string address, CancellationToken ct = default)
        => GetAsync<JsonElement>("handle/availability/imessage",
            new Dictionary<string, string?> { ["address"] = address }, ct: ct);

    public Task<ApiResponse<JsonElement>> GetFaceTimeAvailabilityAsync(
        string address, CancellationToken ct = default)
        => GetAsync<JsonElement>("handle/availability/facetime",
            new Dictionary<string, string?> { ["address"] = address }, ct: ct);

    public Task<ApiResponse<JsonElement>> GetHandleCountAsync(
        CancellationToken ct = default)
        => GetAsync<JsonElement>("handle/count", ct: ct);

    // ── iCloud / FindMy (7) ──

    public Task<ApiResponse<List<FindMyDevice>>> GetFindMyDevicesAsync(
        CancellationToken ct = default)
        => GetAsync<List<FindMyDevice>>("icloud/findmy/devices", ct: ct);

    public Task<ApiResponse<List<FindMyDevice>>> RefreshFindMyDevicesAsync(
        CancellationToken ct = default)
        => PostAsync<List<FindMyDevice>>("icloud/findmy/devices/refresh",
            timeout: LongTimeout, ct: ct);

    public Task<ApiResponse<List<FindMyFriend>>> GetFindMyFriendsAsync(
        CancellationToken ct = default)
        => GetAsync<List<FindMyFriend>>("icloud/findmy/friends", ct: ct);

    public Task<ApiResponse<List<FindMyFriend>>> RefreshFindMyFriendsAsync(
        CancellationToken ct = default)
        => PostAsync<List<FindMyFriend>>("icloud/findmy/friends/refresh", ct: ct);

    public Task<ApiResponse<JsonElement>> GetAccountInfoAsync(
        CancellationToken ct = default)
        => GetAsync<JsonElement>("icloud/account", ct: ct);

    public Task<ApiResponse<JsonElement>> GetAccountContactAsync(
        CancellationToken ct = default)
        => GetAsync<JsonElement>("icloud/contact", ct: ct);

    public Task<ApiResponse<JsonElement>> SetAccountAliasAsync(
        string alias, CancellationToken ct = default)
        => PostAsync<JsonElement>("icloud/account/alias",
            new { alias }, ct: ct);

    // ── FaceTime (2) ──

    public Task<ApiResponse<JsonElement>> AnswerFaceTimeAsync(
        string callUuid, CancellationToken ct = default)
        => PostAsync<JsonElement>($"facetime/answer/{callUuid}",
            new { }, ct: ct);

    public Task<ApiResponse<JsonElement>> LeaveFaceTimeAsync(
        string callUuid, CancellationToken ct = default)
        => PostAsync<JsonElement>($"facetime/leave/{callUuid}",
            new { }, ct: ct);

    // ── Backup (6) ──

    public Task<ApiResponse<JsonElement>> GetThemeAsync(CancellationToken ct = default)
        => GetAsync<JsonElement>("backup/theme", ct: ct);

    public Task<ApiResponse<JsonElement>> SetThemeAsync(
        string name, Dictionary<string, object?> data,
        CancellationToken ct = default)
        => PostAsync<JsonElement>("backup/theme",
            new { name, data }, ct: ct);

    public Task<ApiResponse<JsonElement>> DeleteThemeAsync(
        string name, CancellationToken ct = default)
        => DeleteAsync<JsonElement>("backup/theme",
            new { name }, ct: ct);

    public Task<ApiResponse<JsonElement>> GetSettingsBackupAsync(
        CancellationToken ct = default)
        => GetAsync<JsonElement>("backup/settings", ct: ct);

    public Task<ApiResponse<JsonElement>> SetSettingsBackupAsync(
        string name, Dictionary<string, object?> data,
        CancellationToken ct = default)
        => PostAsync<JsonElement>("backup/settings",
            new { name, data }, ct: ct);

    public Task<ApiResponse<JsonElement>> DeleteSettingsBackupAsync(
        string name, CancellationToken ct = default)
        => DeleteAsync<JsonElement>("backup/settings",
            new { name }, ct: ct);
}
