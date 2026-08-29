using System.Text.Json;
using BlueBubbles.Core.Models;
using BlueBubbles.Core.Services;

namespace BlueBubbles.Windows.Tests;

/// <summary>Covers the abstractions the UI binds to instead of the wire protocol (W2).</summary>
public class TransportAbstractionTests
{
    // ── Connection state mapping ──

    [Theory]
    [InlineData(SocketState.Disconnected, ConnectionState.Disconnected)]
    [InlineData(SocketState.Connecting, ConnectionState.Connecting)]
    [InlineData(SocketState.Connected, ConnectionState.Connected)]
    [InlineData(SocketState.Error, ConnectionState.Error)]
    public void FromSocketState_MapsEveryTransportState(SocketState socket, ConnectionState expected)
        => Assert.Equal(expected, ConnectionStatusPolicy.FromSocketState(socket));

    [Theory]
    [InlineData(ConnectionState.Connected, false, ConnectionBanner.Hidden)]
    [InlineData(ConnectionState.Connected, true, ConnectionBanner.Syncing)]
    [InlineData(ConnectionState.Connecting, false, ConnectionBanner.Connecting)]
    [InlineData(ConnectionState.Connecting, true, ConnectionBanner.Connecting)]
    [InlineData(ConnectionState.Disconnected, false, ConnectionBanner.Disconnected)]
    [InlineData(ConnectionState.Error, false, ConnectionBanner.Disconnected)]
    // Syncing must not outrank a lost connection: the banner has to keep saying "Disconnected".
    [InlineData(ConnectionState.Error, true, ConnectionBanner.Disconnected)]
    [InlineData(ConnectionState.Disconnected, true, ConnectionBanner.Disconnected)]
    public void ResolveBanner_MatchesShippedBannerBehaviour(
        ConnectionState state, bool isSyncing, ConnectionBanner expected)
        => Assert.Equal(expected, ConnectionStatusPolicy.ResolveBanner(state, isSyncing));

    [Theory]
    [InlineData(ConnectionState.Connected, "Connected")]
    [InlineData(ConnectionState.Connecting, "Connecting...")]
    [InlineData(ConnectionState.Disconnected, "Disconnected")]
    [InlineData(ConnectionState.Error, "Disconnected")]
    public void DescribeStatus_MatchesShippedLabels(ConnectionState state, string expected)
        => Assert.Equal(expected, ConnectionStatusPolicy.DescribeStatus(state));

    // ── Chat update classification ──

    [Theory]
    [InlineData(SocketEvents.GroupNameChange, ChatUpdateKind.GroupNameChanged)]
    [InlineData(SocketEvents.ParticipantAdded, ChatUpdateKind.ParticipantAdded)]
    [InlineData(SocketEvents.ParticipantRemoved, ChatUpdateKind.ParticipantRemoved)]
    [InlineData(SocketEvents.ParticipantLeft, ChatUpdateKind.ParticipantLeft)]
    [InlineData(SocketEvents.NewMessage, ChatUpdateKind.Unknown)]
    [InlineData("", ChatUpdateKind.Unknown)]
    [InlineData(null, ChatUpdateKind.Unknown)]
    public void FromEventName_ClassifiesChatUpdates(string? eventName, ChatUpdateKind expected)
        => Assert.Equal(expected, ChatUpdateKinds.FromEventName(eventName));

    [Theory]
    [InlineData(ChatUpdateKind.ParticipantAdded, true)]
    [InlineData(ChatUpdateKind.ParticipantRemoved, true)]
    [InlineData(ChatUpdateKind.ParticipantLeft, true)]
    [InlineData(ChatUpdateKind.GroupNameChanged, false)]
    [InlineData(ChatUpdateKind.Unknown, false)]
    public void IsParticipantChange_OnlyForMembershipEvents(ChatUpdateKind kind, bool expected)
        => Assert.Equal(expected, kind.IsParticipantChange());

    [Fact]
    public void ChatUpdatedEventArgs_ExposesKindFromItsWireEventName()
    {
        var args = new ChatUpdatedEventArgs(SocketEvents.GroupNameChange, default);
        Assert.Equal(ChatUpdateKind.GroupNameChanged, args.Kind);
    }

    // ── Typing indicator ──

    [Theory]
    [InlineData(TypingState.Started, "started-typing")]
    [InlineData(TypingState.Stopped, "stopped-typing")]
    public void TypingIndicator_MapsStateToWireEventName(TypingState state, string expected)
        => Assert.Equal(expected, TypingIndicatorService.EventNameFor(state));

    [Theory]
    [InlineData(TypingState.Started, "started-typing")]
    [InlineData(TypingState.Stopped, "stopped-typing")]
    public async Task TypingIndicator_SendsEventNameAndChatGuid(TypingState state, string expected)
    {
        var socket = new RecordingSocketService();
        var sut = new TypingIndicatorService(socket);

        await sut.SetTypingStateAsync("iMessage;-;+15551234567", state);

        Assert.Equal(expected, socket.LastEventName);
        Assert.Equal("iMessage;-;+15551234567", socket.LastPayload["chatGuid"]);
    }

    // ── ApiResponse success classification ──

    [Theory]
    [InlineData(199, false)]
    [InlineData(200, true)]
    [InlineData(299, true)]
    [InlineData(300, false)]
    [InlineData(500, false)]
    public void ApiResponse_IsSuccess_OnlyFor2xx(int status, bool expected)
        => Assert.Equal(expected, new ApiResponse<string>(status, "m", null, null).IsSuccess);

    [Fact]
    public void ApiResponse_FailureMessage_PrefersTheErrorBody()
    {
        var response = new ApiResponse<string>(500, "message", null, new ApiError("t", "error body"));
        Assert.Equal("error body", response.FailureMessage);
    }

    [Fact]
    public void ApiResponse_FailureMessage_FallsBackToMessage()
    {
        var response = new ApiResponse<string>(500, "message", null, null);
        Assert.Equal("message", response.FailureMessage);
    }

    [Fact]
    public void ApiResponse_FailureMessage_EmptyOnSuccess()
        => Assert.Equal(string.Empty, new ApiResponse<string>(200, "m", null, null).FailureMessage);

    private sealed class RecordingSocketService : ISocketService
    {
        public string? LastEventName { get; private set; }
        public Dictionary<string, object?> LastPayload { get; private set; } = [];

        public SocketState State => SocketState.Connected;
        public string LastError => string.Empty;
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged
        {
            add { }
            remove { }
        }

        public Task ConnectAsync() => Task.CompletedTask;
        public void Disconnect() { }
        public Task ReconnectAsync() => Task.CompletedTask;
        public Task RestartSocketAsync() => Task.CompletedTask;
        public Task EnsureHealthyAsync() => Task.CompletedTask;

        public Task<JsonElement> SendMessageAsync(
            string eventName, Dictionary<string, object?> data, CancellationToken ct = default)
        {
            LastEventName = eventName;
            LastPayload = data;
            return Task.FromResult(default(JsonElement));
        }
    }
}
