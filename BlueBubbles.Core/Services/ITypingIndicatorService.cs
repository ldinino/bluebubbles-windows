using BlueBubbles.Core.Models;

namespace BlueBubbles.Core.Services;

/// <summary>Sends this client's typing state for a chat. The UI expresses intent
/// (<see cref="TypingState"/>); mapping that onto the transport's event names lives here.</summary>
public interface ITypingIndicatorService
{
    Task SetTypingStateAsync(string chatGuid, TypingState state);
}

public sealed class TypingIndicatorService(ISocketService socket) : ITypingIndicatorService
{
    public static string EventNameFor(TypingState state) =>
        state == TypingState.Started ? "started-typing" : "stopped-typing";

    public Task SetTypingStateAsync(string chatGuid, TypingState state) =>
        socket.SendMessageAsync(
            EventNameFor(state),
            new Dictionary<string, object?> { ["chatGuid"] = chatGuid });
}
