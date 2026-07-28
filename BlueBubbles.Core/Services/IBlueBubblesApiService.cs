using System.Text.Json;
using BlueBubbles.Core.Models;

namespace BlueBubbles.Core.Services;

public interface IBlueBubblesApiService
{
    string? OriginOverride { get; set; }

    // ── Server ──

    Task<ApiResponse<JsonElement>> PingAsync(CancellationToken ct = default);
    Task<ApiResponse<ServerInfo>> GetServerInfoAsync(CancellationToken ct = default);
    Task<ApiResponse<JsonElement>> SoftRestartAsync(CancellationToken ct = default);
    Task<ApiResponse<JsonElement>> HardRestartAsync(CancellationToken ct = default);
    Task<ApiResponse<JsonElement>> CheckUpdateAsync(CancellationToken ct = default);
    Task<ApiResponse<JsonElement>> InstallUpdateAsync(CancellationToken ct = default);
    Task<ApiResponse<JsonElement>> GetStatTotalsAsync(CancellationToken ct = default);
    Task<ApiResponse<JsonElement>> GetStatMediaAsync(bool byChat = false, CancellationToken ct = default);
    Task<ApiResponse<JsonElement>> GetServerLogsAsync(int count = 10000, CancellationToken ct = default);
    Task<ApiResponse<JsonElement>> LockMacAsync(CancellationToken ct = default);
    Task<ApiResponse<JsonElement>> RestartImessageAsync(CancellationToken ct = default);

    // ── FCM ──

    Task<ApiResponse<JsonElement>> AddFcmDeviceAsync(string name, string identifier, CancellationToken ct = default);
    Task<ApiResponse<JsonElement>> GetFcmClientAsync(CancellationToken ct = default);

    // ── Attachments ──

    Task<ApiResponse<Attachment>> GetAttachmentInfoAsync(string guid, CancellationToken ct = default);
    Task<byte[]> DownloadAttachmentAsync(string guid, bool original = false,
        IProgress<double>? progress = null, CancellationToken ct = default);
    /// <summary>Downloads an attachment the Mac hasn't got on disk (iCloud-purged). Asks the server
    /// to pull it down via the Private API first, then serves it. Plain download returns a 500
    /// ("Attachment does not exist in disk!") for these, so this is the recovery path.</summary>
    Task<byte[]> ForceDownloadAttachmentAsync(string guid,
        IProgress<double>? progress = null, CancellationToken ct = default);
    Task<byte[]> DownloadLivePhotoAsync(string guid,
        IProgress<double>? progress = null, CancellationToken ct = default);
    Task<byte[]> GetAttachmentBlurhashAsync(string guid, CancellationToken ct = default);
    Task<ApiResponse<JsonElement>> GetAttachmentCountAsync(CancellationToken ct = default);

    // ── Chats ──

    Task<ApiResponse<List<Chat>>> QueryChatsAsync(
        List<string>? withQuery = null, int offset = 0, int limit = 100,
        string? sort = null, CancellationToken ct = default);
    Task<ApiResponse<Chat>> GetChatAsync(string guid, string? withQuery = null,
        CancellationToken ct = default);
    Task<ApiResponse<List<Message>>> GetChatMessagesAsync(string guid,
        string? withQuery = null, string sort = "DESC",
        long? before = null, long? after = null,
        int offset = 0, int limit = 100, CancellationToken ct = default);
    Task<ApiResponse<JsonElement>> GetChatCountAsync(CancellationToken ct = default);
    Task<ApiResponse<Chat>> CreateChatAsync(List<string> addresses,
        string? message, string service,
        string method = "private-api", CancellationToken ct = default);
    Task<ApiResponse<Chat>> UpdateChatAsync(string guid, string displayName,
        CancellationToken ct = default);
    Task<ApiResponse<JsonElement>> DeleteChatAsync(string guid,
        CancellationToken ct = default);
    Task<ApiResponse<JsonElement>> MarkChatReadAsync(string guid,
        CancellationToken ct = default);
    Task<ApiResponse<JsonElement>> MarkChatUnreadAsync(string guid,
        CancellationToken ct = default);
    Task<byte[]> GetChatIconAsync(string guid, CancellationToken ct = default);
    Task<ApiResponse<JsonElement>> SetChatIconAsync(string guid,
        Stream iconStream, string fileName, CancellationToken ct = default);
    Task<ApiResponse<JsonElement>> DeleteChatIconAsync(string guid,
        CancellationToken ct = default);
    Task<ApiResponse<Chat>> AddParticipantAsync(string chatGuid,
        string address, CancellationToken ct = default);
    Task<ApiResponse<Chat>> RemoveParticipantAsync(string chatGuid,
        string address, CancellationToken ct = default);
    Task<ApiResponse<JsonElement>> LeaveChatAsync(string guid,
        CancellationToken ct = default);
    Task<ApiResponse<JsonElement>> DeleteMessageFromChatAsync(string chatGuid,
        string messageGuid, CancellationToken ct = default);

    // ── Messages ──

    Task<ApiResponse<List<Message>>> QueryMessagesAsync(
        List<string>? withQuery = null, List<object>? where = null,
        string sort = "DESC", long? before = null, long? after = null,
        string? chatGuid = null, int offset = 0, int limit = 100,
        bool convertAttachments = true, CancellationToken ct = default);
    Task<ApiResponse<Message>> GetMessageAsync(string guid,
        string? withQuery = null, CancellationToken ct = default);
    Task<byte[]> GetEmbeddedMediaAsync(string guid, CancellationToken ct = default);
    Task<ApiResponse<JsonElement>> GetMessageCountAsync(
        long? after = null, long? before = null, CancellationToken ct = default);
    Task<ApiResponse<JsonElement>> GetUpdatedMessageCountAsync(
        long? after = null, long? before = null, CancellationToken ct = default);
    Task<ApiResponse<JsonElement>> GetMyMessageCountAsync(
        long? after = null, long? before = null, CancellationToken ct = default);
    Task<ApiResponse<Message>> SendTextAsync(string chatGuid, string tempGuid,
        string message, string? method = null, string? effectId = null,
        string? subject = null, string? selectedMessageGuid = null,
        int? partIndex = null, bool? ddScan = null,
        CancellationToken ct = default);
    Task<ApiResponse<Message>> SendAttachmentAsync(string chatGuid,
        string tempGuid, Stream fileStream, string fileName,
        string? method = null, string? effectId = null, string? subject = null,
        string? selectedMessageGuid = null, int? partIndex = null,
        bool? isAudioMessage = null, IProgress<double>? progress = null,
        CancellationToken ct = default);
    Task<ApiResponse<Message>> SendMultipartAsync(string chatGuid,
        string tempGuid, List<Dictionary<string, object?>> parts,
        string? effectId = null, string? subject = null,
        string? selectedMessageGuid = null, int? partIndex = null,
        bool? ddScan = null, CancellationToken ct = default);
    Task<ApiResponse<Message>> SendTapbackAsync(string chatGuid,
        string selectedMessageText, string selectedMessageGuid,
        string reaction, int? partIndex = null,
        CancellationToken ct = default);
    Task<ApiResponse<Message>> UnsendMessageAsync(string messageGuid,
        int partIndex = 0, CancellationToken ct = default);
    Task<ApiResponse<Message>> EditMessageAsync(string messageGuid,
        string editedMessage, string backwardsCompatMessage,
        int partIndex = 0, CancellationToken ct = default);
    Task<ApiResponse<JsonElement>> NotifyMessageAsync(string messageGuid,
        CancellationToken ct = default);
    Task<ApiResponse<List<ScheduledMessage>>> GetScheduledMessagesAsync(
        CancellationToken ct = default);
    Task<ApiResponse<ScheduledMessage>> CreateScheduledMessageAsync(
        string chatGuid, string message, long scheduledForMs,
        string method = "private-api", string? effectId = null,
        string? subject = null, string? selectedMessageGuid = null,
        int? partIndex = null, Dictionary<string, object?>? schedule = null,
        CancellationToken ct = default);
    Task<ApiResponse<ScheduledMessage>> UpdateScheduledMessageAsync(int id,
        string chatGuid, string message, long scheduledForMs,
        string method = "private-api", string? effectId = null,
        string? subject = null, string? selectedMessageGuid = null,
        int? partIndex = null, Dictionary<string, object?>? schedule = null,
        CancellationToken ct = default);
    Task<ApiResponse<JsonElement>> DeleteScheduledMessageAsync(int id,
        CancellationToken ct = default);

    // ── Handles ──

    Task<ApiResponse<List<Handle>>> QueryHandlesAsync(
        List<string>? withQuery = null, string? address = null,
        int offset = 0, int limit = 100, CancellationToken ct = default);
    Task<ApiResponse<Handle>> GetHandleAsync(string guid,
        CancellationToken ct = default);
    Task<ApiResponse<JsonElement>> GetHandleFocusStateAsync(string address,
        CancellationToken ct = default);
    Task<ApiResponse<JsonElement>> GetIMessageAvailabilityAsync(string address,
        CancellationToken ct = default);
    Task<ApiResponse<JsonElement>> GetFaceTimeAvailabilityAsync(string address,
        CancellationToken ct = default);
    Task<ApiResponse<JsonElement>> GetHandleCountAsync(
        CancellationToken ct = default);

    // ── iCloud / FindMy ──

    Task<ApiResponse<List<FindMyDevice>>> GetFindMyDevicesAsync(
        CancellationToken ct = default);
    Task<ApiResponse<List<FindMyDevice>>> RefreshFindMyDevicesAsync(
        CancellationToken ct = default);
    Task<ApiResponse<List<FindMyFriend>>> GetFindMyFriendsAsync(
        CancellationToken ct = default);
    Task<ApiResponse<List<FindMyFriend>>> RefreshFindMyFriendsAsync(
        CancellationToken ct = default);
    Task<ApiResponse<JsonElement>> GetAccountInfoAsync(
        CancellationToken ct = default);
    Task<ApiResponse<JsonElement>> GetAccountContactAsync(
        CancellationToken ct = default);
    Task<ApiResponse<JsonElement>> SetAccountAliasAsync(string alias,
        CancellationToken ct = default);

    // ── FaceTime ──

    Task<ApiResponse<JsonElement>> AnswerFaceTimeAsync(string callUuid,
        CancellationToken ct = default);
    Task<ApiResponse<JsonElement>> LeaveFaceTimeAsync(string callUuid,
        CancellationToken ct = default);

    // ── Backup ──

    Task<ApiResponse<JsonElement>> GetThemeAsync(CancellationToken ct = default);
    Task<ApiResponse<JsonElement>> SetThemeAsync(string name,
        Dictionary<string, object?> data, CancellationToken ct = default);
    Task<ApiResponse<JsonElement>> DeleteThemeAsync(string name,
        CancellationToken ct = default);
    Task<ApiResponse<JsonElement>> GetSettingsBackupAsync(
        CancellationToken ct = default);
    Task<ApiResponse<JsonElement>> SetSettingsBackupAsync(string name,
        Dictionary<string, object?> data, CancellationToken ct = default);
    Task<ApiResponse<JsonElement>> DeleteSettingsBackupAsync(string name,
        CancellationToken ct = default);
}
