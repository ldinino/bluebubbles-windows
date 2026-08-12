using System.Text.Json;
using BlueBubbles.Core.Configuration;
using BlueBubbles.Core.Models;
using BlueBubbles.Core.Services;

namespace BlueBubbles.Windows.Tests;

public class OutgoingMessageServiceTests
{
    private static Message MakeMessage(string guid, string? text = null, List<Attachment>? attachments = null) =>
        new(null, guid, null, null, text, null, null, 0,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null, null,
            false, true, false, null, 0, null, 0, null, null, null, null, null,
            null, attachments is not null, false, null, null, null, null, attachments, null,
            null, null, null, false, null, false, false, false);

    private static (OutgoingMessageService Service, MockApiService Api, ActionHandler ActionHandler, AttachmentCacheService Cache)
        CreateService(int sendDelay = 0)
    {
        var api = new MockApiService();
        var actionHandler = new ActionHandler();
        var settings = new AppSettings { SendDelay = sendDelay };
        var cache = new AttachmentCacheService(api,
            Path.Combine(Path.GetTempPath(), "bb-outgoing-tests-" + Guid.NewGuid().ToString("N")));
        var service = new OutgoingMessageService(api, actionHandler, cache, settings);
        return (service, api, actionHandler, cache);
    }

    [Fact]
    public void EnqueueText_ReturnsTempGuid()
    {
        var (svc, _, _, _) = CreateService();
        var tempGuid = svc.EnqueueText("chat;+11234567890", "Hello");
        Assert.StartsWith("temp-", tempGuid);
        Assert.Equal(25, tempGuid.Length);
    }

    [Fact]
    public void EnqueueAttachment_ReturnsTempGuid()
    {
        var (svc, _, _, _) = CreateService();
        var tempGuid = svc.EnqueueAttachment("chat;+11234567890", @"C:\fake\image.jpg");
        Assert.StartsWith("temp-", tempGuid);
    }

    [Fact]
    public void GenerateTempGuid_IsUnique()
    {
        var guids = Enumerable.Range(0, 100)
            .Select(_ => OutgoingMessageService.GenerateTempGuid())
            .ToHashSet();
        Assert.Equal(100, guids.Count);
    }

    [Fact]
    public async Task EnqueueText_FiresSentState_OnSuccess()
    {
        var (svc, api, _, _) = CreateService();
        var events = new List<OutgoingMessageEvent>();
        svc.MessageStateChanged += (_, e) => events.Add(e);

        api.SendTextResponse = new ApiResponse<Message>(
            200, "OK",
            MakeMessage("server-guid-1", "Hello"),
            null);

        svc.EnqueueText("chat;+11234567890", "Hello");

        await WaitForEvents(events, 2, timeout: 3000);

        Assert.Equal(OutgoingMessageState.Sending, events[0].State);
        Assert.Equal(OutgoingMessageState.Sent, events[1].State);
        Assert.Equal("server-guid-1", events[1].ServerMessage!.Guid);
    }

    [Fact]
    public async Task EnqueueText_FiresFailedState_OnApiError()
    {
        var (svc, api, _, _) = CreateService();
        var events = new List<OutgoingMessageEvent>();
        svc.MessageStateChanged += (_, e) => events.Add(e);

        api.SendTextResponse = new ApiResponse<Message>(
            500, "Error", null,
            new ApiError("ServerError", "Something went wrong"));

        svc.EnqueueText("chat;+11234567890", "Hello");

        await WaitForEvents(events, 2, timeout: 3000);

        Assert.Equal(OutgoingMessageState.Sending, events[0].State);
        Assert.Equal(OutgoingMessageState.Failed, events[1].State);
        Assert.Equal("Something went wrong", events[1].ErrorMessage);
    }

    [Fact]
    public async Task EnqueueText_FiresFailedState_OnException()
    {
        var (svc, api, _, _) = CreateService();
        var events = new List<OutgoingMessageEvent>();
        svc.MessageStateChanged += (_, e) => events.Add(e);

        api.ThrowOnSendText = new HttpRequestException("Network error");

        svc.EnqueueText("chat;+11234567890", "Hello");

        await WaitForEvents(events, 2, timeout: 3000);

        Assert.Equal(OutgoingMessageState.Failed, events[1].State);
        Assert.Contains("Network error", events[1].ErrorMessage);
    }

    [Fact]
    public async Task SendDelay_CanBeCancelled()
    {
        var (svc, api, _, _) = CreateService(sendDelay: 10);
        var cancelledTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        svc.MessageStateChanged += (_, e) =>
        {
            if (e.State == OutgoingMessageState.Cancelled)
                cancelledTcs.TrySetResult();
        };

        api.SendTextResponse = new ApiResponse<Message>(
            200, "OK",
            MakeMessage("server-guid", "Hello"),
            null);

        var tempGuid = svc.EnqueueText("chat;+11234567890", "Hello");

        await Task.Delay(200);
        svc.CancelPending(tempGuid);

        var completed = await Task.WhenAny(cancelledTcs.Task, Task.Delay(5000));
        Assert.True(cancelledTcs.Task.IsCompleted,
            "Cancelled event was not received within timeout");
    }

    [Fact]
    public async Task MessagesProcessedSequentially()
    {
        var (svc, api, _, _) = CreateService();
        var events = new List<OutgoingMessageEvent>();
        svc.MessageStateChanged += (_, e) => events.Add(e);

        var msgCount = 0;
        api.SendTextFunc = async (chatGuid, tempGuid, text) =>
        {
            var n = Interlocked.Increment(ref msgCount);
            await Task.Delay(50);
            return new ApiResponse<Message>(
                200, "OK",
                MakeMessage($"server-{n}", text),
                null);
        };

        svc.EnqueueText("chat;+11234567890", "First");
        svc.EnqueueText("chat;+11234567890", "Second");
        svc.EnqueueText("chat;+11234567890", "Third");

        await WaitForEvents(events, 6, timeout: 5000);

        var sentEvents = events.Where(e => e.State == OutgoingMessageState.Sent).ToList();
        Assert.Equal(3, sentEvents.Count);
        Assert.Equal("server-1", sentEvents[0].ServerMessage!.Guid);
        Assert.Equal("server-2", sentEvents[1].ServerMessage!.Guid);
        Assert.Equal("server-3", sentEvents[2].ServerMessage!.Guid);
    }

    [Fact]
    public async Task PrivateApi_SendsCorrectMethod()
    {
        var (svc, api, _, _) = CreateService();

        var sentTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        svc.MessageStateChanged += (_, e) =>
        {
            if (e.State == OutgoingMessageState.Sent)
                sentTcs.TrySetResult();
        };

        api.SendTextResponse = new ApiResponse<Message>(
            200, "OK", MakeMessage("server-guid", "Hello"), null);

        svc.EnqueueText("chat;+11234567890", "Hello");

        var completed = await Task.WhenAny(sentTcs.Task, Task.Delay(5000));
        Assert.True(sentTcs.Task.IsCompleted,
            "Sent event was not received within timeout");
        Assert.Equal("private-api", api.LastMethod);
    }

    [Fact]
    public async Task SentMessage_RemovesFromOutOfOrderTempGuids()
    {
        var (svc, api, actionHandler, _) = CreateService();

        var sentTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        svc.MessageStateChanged += (_, e) =>
        {
            if (e.State == OutgoingMessageState.Sent)
                sentTcs.TrySetResult();
        };

        actionHandler.AddOutOfOrderGuid("server-guid-1");

        api.SendTextResponse = new ApiResponse<Message>(
            200, "OK",
            MakeMessage("server-guid-1", "Hello"),
            null);

        svc.EnqueueText("chat;+11234567890", "Hello");

        var completed = await Task.WhenAny(sentTcs.Task, Task.Delay(5000));
        Assert.True(sentTcs.Task.IsCompleted,
            "Sent event was not received within timeout");
        Assert.False(actionHandler.ContainsOutOfOrderGuid("server-guid-1"));
    }

    [Fact]
    public async Task SentAttachment_SeedsCacheUnderServerGuid()
    {
        var (svc, api, _, cache) = CreateService();

        var sourceFile = Path.Combine(Path.GetTempPath(), $"bb-test-{Guid.NewGuid():N}.jpg");
        await File.WriteAllBytesAsync(sourceFile, [1, 2, 3, 4]);
        try
        {
            var attachment = new Attachment(null, "server-att-1", null, "image/jpeg",
                true, Path.GetFileName(sourceFile), 4, null, null, false, null);
            api.SendAttachmentResponse = new ApiResponse<Message>(
                200, "OK",
                MakeMessage("server-guid-1", attachments: [attachment]),
                null);

            var sentTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            svc.MessageStateChanged += (_, e) =>
            {
                if (e.State == OutgoingMessageState.Sent)
                    sentTcs.TrySetResult();
            };

            svc.EnqueueAttachment("chat;+11234567890", sourceFile);

            await Task.WhenAny(sentTcs.Task, Task.Delay(5000));
            Assert.True(sentTcs.Task.IsCompleted, "Sent event was not received within timeout");

            // B13: the local file must now be in the cache under the *server* attachment guid,
            // so a bubble rebuilt from the DB finds it without a delta sync.
            var cachedPath = cache.GetCachedPath("server-att-1");
            Assert.NotNull(cachedPath);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(cachedPath!));
        }
        finally
        {
            File.Delete(sourceFile);
        }
    }

    private static async Task WaitForEvents(List<OutgoingMessageEvent> events, int expectedCount, int timeout)
    {
        var deadline = Environment.TickCount64 + timeout;
        while (events.Count < expectedCount && Environment.TickCount64 < deadline)
            await Task.Delay(50);
    }
}

internal class MockApiService : IBlueBubblesApiService
{
    public string? OriginOverride { get; set; }
    public ApiResponse<Message>? SendTextResponse { get; set; }
    public ApiResponse<Message>? SendAttachmentResponse { get; set; }
    public Func<string, string, string, Task<ApiResponse<Message>>>? SendTextFunc { get; set; }
    public Exception? ThrowOnSendText { get; set; }
    public string? LastMethod { get; private set; }
    public Func<string, Task<ApiResponse<Chat>>>? GetChatFunc { get; set; }
    public Func<string, Task<ApiResponse<JsonElement>>>? DeleteChatFunc { get; set; }
    public Func<string, string, Task<ApiResponse<JsonElement>>>? DeleteMessageFunc { get; set; }
    public Func<string, Task<byte[]>>? DownloadAttachmentFunc { get; set; }
    public int DownloadAttachmentCalls;
    public Func<string, Task<byte[]>>? ForceDownloadAttachmentFunc { get; set; }
    public int ForceDownloadAttachmentCalls;

    internal static Message MockMessage(string guid, string? text = null) =>
        new(null, guid, null, null, text, null, null, 0,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null, null,
            false, true, false, null, 0, null, 0, null, null, null, null, null,
            null, false, false, null, null, null, null, null, null, null, null,
            null, false, null, false, false, false);

    public async Task<ApiResponse<Message>> SendTextAsync(
        string chatGuid, string tempGuid, string message,
        string? method = null, string? effectId = null, string? subject = null,
        string? selectedMessageGuid = null, int? partIndex = null, bool? ddScan = null,
        CancellationToken ct = default)
    {
        LastMethod = method;
        if (ThrowOnSendText is not null) throw ThrowOnSendText;
        if (SendTextFunc is not null) return await SendTextFunc(chatGuid, tempGuid, message);
        return SendTextResponse ?? new ApiResponse<Message>(200, "OK",
            MockMessage("default-guid", message), null);
    }

    public Task<ApiResponse<Message>> SendAttachmentAsync(
        string chatGuid, string tempGuid, Stream fileStream, string fileName,
        string? method = null, string? effectId = null, string? subject = null,
        string? selectedMessageGuid = null, int? partIndex = null, bool? isAudioMessage = null,
        IProgress<double>? progress = null, CancellationToken ct = default)
        => Task.FromResult(SendAttachmentResponse ?? new ApiResponse<Message>(200, "OK",
            MockMessage("attach-guid", fileName), null));

    // Stubs — only SendText/SendAttachment are wired; rest throw
    public Task<ApiResponse<JsonElement>> PingAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<ServerInfo>> GetServerInfoAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> SoftRestartAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> HardRestartAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> CheckUpdateAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> InstallUpdateAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> GetStatTotalsAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> GetStatMediaAsync(bool byChat = false, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> GetServerLogsAsync(int count = 10000, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> LockMacAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> RestartImessageAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> AddFcmDeviceAsync(string name, string identifier, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> GetFcmClientAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<Attachment>> GetAttachmentInfoAsync(string guid, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> DownloadAttachmentAsync(string guid, bool original = false, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Interlocked.Increment(ref DownloadAttachmentCalls);
        return DownloadAttachmentFunc is not null
            ? DownloadAttachmentFunc(guid)
            : throw new NotImplementedException();
    }

    public Task<byte[]> ForceDownloadAttachmentAsync(string guid, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Interlocked.Increment(ref ForceDownloadAttachmentCalls);
        return ForceDownloadAttachmentFunc is not null
            ? ForceDownloadAttachmentFunc(guid)
            : throw new NotImplementedException();
    }
    public Task<byte[]> DownloadLivePhotoAsync(string guid, IProgress<double>? progress = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> GetAttachmentBlurhashAsync(string guid, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> GetAttachmentCountAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<List<Chat>>> QueryChatsAsync(List<string>? withQuery = null, int offset = 0, int limit = 100, string? sort = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<Chat>> GetChatAsync(string guid, string? withQuery = null, CancellationToken ct = default) =>
        GetChatFunc is not null ? GetChatFunc(guid) : throw new NotImplementedException();
    public Task<ApiResponse<List<Message>>> GetChatMessagesAsync(string guid, string? withQuery = null, string sort = "DESC", long? before = null, long? after = null, int offset = 0, int limit = 100, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> GetChatCountAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<Chat>> CreateChatAsync(List<string> addresses, string? message, string service, string method = "private-api", CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<Chat>> UpdateChatAsync(string guid, string displayName, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> DeleteChatAsync(string guid, CancellationToken ct = default) =>
        DeleteChatFunc is not null ? DeleteChatFunc(guid) : throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> MarkChatReadAsync(string guid, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> MarkChatUnreadAsync(string guid, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> GetChatIconAsync(string guid, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> SetChatIconAsync(string guid, Stream iconStream, string fileName, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> DeleteChatIconAsync(string guid, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<Chat>> AddParticipantAsync(string chatGuid, string address, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<Chat>> RemoveParticipantAsync(string chatGuid, string address, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> LeaveChatAsync(string guid, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> DeleteMessageFromChatAsync(string chatGuid, string messageGuid, CancellationToken ct = default) =>
        DeleteMessageFunc is not null ? DeleteMessageFunc(chatGuid, messageGuid) : throw new NotImplementedException();
    public Task<ApiResponse<List<Message>>> QueryMessagesAsync(List<string>? withQuery = null, List<object>? where = null, string sort = "DESC", long? before = null, long? after = null, string? chatGuid = null, int offset = 0, int limit = 100, bool convertAttachments = true, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<Message>> GetMessageAsync(string guid, string? withQuery = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<byte[]> GetEmbeddedMediaAsync(string guid, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> GetMessageCountAsync(long? after = null, long? before = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> GetUpdatedMessageCountAsync(long? after = null, long? before = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> GetMyMessageCountAsync(long? after = null, long? before = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<Message>> SendMultipartAsync(string chatGuid, string tempGuid, List<Dictionary<string, object?>> parts, string? effectId = null, string? subject = null, string? selectedMessageGuid = null, int? partIndex = null, bool? ddScan = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<Message>> SendTapbackAsync(string chatGuid, string selectedMessageText, string selectedMessageGuid, string reaction, int? partIndex = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<Message>> UnsendMessageAsync(string messageGuid, int partIndex = 0, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<Message>> EditMessageAsync(string messageGuid, string editedMessage, string backwardsCompatMessage, int partIndex = 0, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> NotifyMessageAsync(string messageGuid, CancellationToken ct = default) => throw new NotImplementedException();
    // Scheduled messages: capture hooks for ScheduledMessageServiceTests
    public record ScheduledCall(int? Id, string ChatGuid, string Message, long ScheduledForMs,
        string Method, Dictionary<string, object?>? Schedule);
    public ScheduledCall? LastScheduledCall { get; private set; }
    public int? LastDeletedScheduledId { get; private set; }
    public ApiResponse<List<ScheduledMessage>>? GetScheduledResponse { get; set; }
    public ApiResponse<ScheduledMessage>? ScheduledResponse { get; set; }

    internal static ScheduledMessage MockScheduledMessage(int id = 1) =>
        new(id, "send-message",
            new ScheduledMessagePayload("chat;+11234567890", "Hello", "private-api"),
            "2026-06-11T15:30:00.000Z", new ScheduledMessageSchedule("once", null, null),
            "pending", null, null, "2026-06-10T15:30:00.000Z");

    public Task<ApiResponse<List<ScheduledMessage>>> GetScheduledMessagesAsync(CancellationToken ct = default)
        => Task.FromResult(GetScheduledResponse
            ?? new ApiResponse<List<ScheduledMessage>>(200, "OK", [], null));
    public Task<ApiResponse<ScheduledMessage>> CreateScheduledMessageAsync(string chatGuid, string message, long scheduledForMs, string method = "private-api", string? effectId = null, string? subject = null, string? selectedMessageGuid = null, int? partIndex = null, Dictionary<string, object?>? schedule = null, CancellationToken ct = default)
    {
        LastScheduledCall = new ScheduledCall(null, chatGuid, message, scheduledForMs, method, schedule);
        return Task.FromResult(ScheduledResponse
            ?? new ApiResponse<ScheduledMessage>(200, "OK", MockScheduledMessage(), null));
    }
    public Task<ApiResponse<ScheduledMessage>> UpdateScheduledMessageAsync(int id, string chatGuid, string message, long scheduledForMs, string method = "private-api", string? effectId = null, string? subject = null, string? selectedMessageGuid = null, int? partIndex = null, Dictionary<string, object?>? schedule = null, CancellationToken ct = default)
    {
        LastScheduledCall = new ScheduledCall(id, chatGuid, message, scheduledForMs, method, schedule);
        return Task.FromResult(ScheduledResponse
            ?? new ApiResponse<ScheduledMessage>(200, "OK", MockScheduledMessage(id), null));
    }
    public Task<ApiResponse<JsonElement>> DeleteScheduledMessageAsync(int id, CancellationToken ct = default)
    {
        LastDeletedScheduledId = id;
        return Task.FromResult(new ApiResponse<JsonElement>(200, "OK", default, null));
    }
    public Task<ApiResponse<List<Handle>>> QueryHandlesAsync(List<string>? withQuery = null, string? address = null, int offset = 0, int limit = 100, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<Handle>> GetHandleAsync(string guid, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> GetHandleFocusStateAsync(string address, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> GetIMessageAvailabilityAsync(string address, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> GetFaceTimeAvailabilityAsync(string address, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> GetHandleCountAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<List<FindMyDevice>>> GetFindMyDevicesAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<List<FindMyDevice>>> RefreshFindMyDevicesAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<List<FindMyFriend>>> GetFindMyFriendsAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<List<FindMyFriend>>> RefreshFindMyFriendsAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> GetAccountInfoAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> GetAccountContactAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> SetAccountAliasAsync(string alias, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> AnswerFaceTimeAsync(string callUuid, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> LeaveFaceTimeAsync(string callUuid, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> GetSettingsBackupAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> SetSettingsBackupAsync(string name, Dictionary<string, object?> data, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<JsonElement>> DeleteSettingsBackupAsync(string name, CancellationToken ct = default) => throw new NotImplementedException();
}
