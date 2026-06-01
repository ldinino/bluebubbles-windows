using System.Text.Json;
using BlueBubbles.Core.Models;

namespace BlueBubbles.Core.Services;

public interface IActionHandler
{
    event EventHandler<MessageEventArgs>? NewMessageReceived;
    event EventHandler<MessageEventArgs>? MessageUpdated;
    event EventHandler<ReactionEventArgs>? ReactionReceived;
    event EventHandler<TypingIndicatorPayload>? TypingIndicatorChanged;
    event EventHandler<ChatReadStatusPayload>? ChatReadStatusChanged;
    event EventHandler<ChatUpdatedEventArgs>? ChatUpdated;
    event EventHandler<JsonElement>? IncomingFaceTime;
    event EventHandler<JsonElement>? FaceTimeStatusChanged;
    event EventHandler<List<string>>? AliasesRemoved;

    void HandleEvent(string eventName, JsonElement data, string source);
    bool ShouldNotifyForNewMessageGuid(string guid);
    void AddOutOfOrderGuid(string guid);
    bool RemoveOutOfOrderGuid(string guid);
    bool ContainsOutOfOrderGuid(string guid);
}
