using System.Text.Json;
using BlueBubbles.Core.Models;
using BlueBubbles.Core.Services;
using BlueBubbles.Core.Utils;

namespace BlueBubbles.Windows.Tests;

public class ScheduledMessageServiceTests
{
    private static long FutureMs => DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds();

    private static (ScheduledMessageService Service, MockApiService Api) CreateService()
    {
        var api = new MockApiService();
        return (new ScheduledMessageService(api), api);
    }

    [Fact]
    public async Task CreateAsync_PassesThrough_AndDefaultsScheduleToOnce()
    {
        var (svc, api) = CreateService();
        var ms = FutureMs;

        var response = await svc.CreateAsync("chat;+11234567890", "Hello", ms);

        Assert.Equal(200, response.Status);
        Assert.NotNull(api.LastScheduledCall);
        Assert.Equal("chat;+11234567890", api.LastScheduledCall!.ChatGuid);
        Assert.Equal("Hello", api.LastScheduledCall.Message);
        Assert.Equal(ms, api.LastScheduledCall.ScheduledForMs);
        // The server validator requires schedule.type; the service must default it.
        Assert.NotNull(api.LastScheduledCall.Schedule);
        Assert.Equal("once", api.LastScheduledCall.Schedule!["type"]);
    }

    [Fact]
    public async Task CreateAsync_PreservesExplicitSchedule()
    {
        var (svc, api) = CreateService();
        var recurring = new Dictionary<string, object?>
        {
            ["type"] = "recurring",
            ["interval"] = 1,
            ["intervalType"] = "daily"
        };

        await svc.CreateAsync("chat;+11234567890", "Hello", FutureMs, schedule: recurring);

        Assert.Same(recurring, api.LastScheduledCall!.Schedule);
    }

    [Fact]
    public async Task CreateAsync_TrimsMessageText()
    {
        var (svc, api) = CreateService();

        await svc.CreateAsync("chat;+11234567890", "  Hello  ", FutureMs);

        Assert.Equal("Hello", api.LastScheduledCall!.Message);
    }

    [Fact]
    public async Task CreateAsync_RejectsEmptyText_WithoutCallingApi()
    {
        var (svc, api) = CreateService();

        var response = await svc.CreateAsync("chat;+11234567890", "   ", FutureMs);

        Assert.Equal(400, response.Status);
        Assert.NotNull(response.Error);
        Assert.Null(api.LastScheduledCall);
    }

    [Fact]
    public async Task CreateAsync_RejectsPastTime_WithoutCallingApi()
    {
        var (svc, api) = CreateService();
        var past = DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds();

        var response = await svc.CreateAsync("chat;+11234567890", "Hello", past);

        Assert.Equal(400, response.Status);
        Assert.NotNull(response.Error);
        Assert.Null(api.LastScheduledCall);
    }

    [Fact]
    public async Task UpdateAsync_ValidatesAndForwardsId()
    {
        var (svc, api) = CreateService();
        var ms = FutureMs;

        var ok = await svc.UpdateAsync(42, "chat;+11234567890", "Updated", ms);
        Assert.Equal(200, ok.Status);
        Assert.Equal(42, api.LastScheduledCall!.Id);
        Assert.Equal("Updated", api.LastScheduledCall.Message);
        Assert.Equal("once", api.LastScheduledCall.Schedule!["type"]);

        var rejected = await svc.UpdateAsync(42, "chat;+11234567890", "", ms);
        Assert.Equal(400, rejected.Status);
    }

    [Fact]
    public async Task DeleteAsync_ForwardsId()
    {
        var (svc, api) = CreateService();

        var response = await svc.DeleteAsync(7);

        Assert.Equal(200, response.Status);
        Assert.Equal(7, api.LastDeletedScheduledId);
    }

    [Fact]
    public async Task GetAllAsync_PassesThrough()
    {
        var (svc, api) = CreateService();
        api.GetScheduledResponse = new ApiResponse<List<ScheduledMessage>>(
            200, "OK", [MockApiService.MockScheduledMessage(3)], null);

        var response = await svc.GetAllAsync();

        Assert.Equal(200, response.Status);
        Assert.Single(response.Data!);
        Assert.Equal(3, response.Data![0].Id);
    }

    [Fact]
    public void ScheduledMessage_DeserializesServerJson()
    {
        // Realistic server response shape: ISO date strings, nested payload/schedule.
        var json = """
        {
            "id": 12,
            "type": "send-message",
            "payload": {
                "chatGuid": "iMessage;-;+11234567890",
                "message": "Happy birthday!",
                "method": "private-api"
            },
            "scheduledFor": "2026-06-11T15:30:00.000Z",
            "schedule": { "type": "once" },
            "status": "pending",
            "error": null,
            "sentAt": null,
            "created": "2026-06-10T15:30:00.000Z"
        }
        """;

        var msg = JsonSerializer.Deserialize<ScheduledMessage>(json, JsonDefaults.Options);

        Assert.NotNull(msg);
        Assert.Equal(12, msg!.Id);
        Assert.Equal("Happy birthday!", msg.Payload!.MessageText);
        Assert.Equal(ScheduledMessageStatus.Pending, msg.Status);
        Assert.Equal("once", msg.Schedule!.Type);
        var local = msg.ScheduledForLocal;
        Assert.NotNull(local);
        Assert.Equal(new DateTimeOffset(2026, 6, 11, 15, 30, 0, TimeSpan.Zero), local!.Value.ToUniversalTime());
    }

    [Fact]
    public void ScheduledMessage_ToleratesMissingScheduleAndBadDate()
    {
        var json = """
        {
            "id": 13,
            "type": "send-message",
            "scheduledFor": "not-a-date",
            "status": "pending",
            "created": "2026-06-10T15:30:00.000Z"
        }
        """;

        var msg = JsonSerializer.Deserialize<ScheduledMessage>(json, JsonDefaults.Options);

        Assert.NotNull(msg);
        Assert.Null(msg!.Schedule);
        Assert.Null(msg.Payload);
        Assert.Null(msg.ScheduledForLocal);
    }
}
