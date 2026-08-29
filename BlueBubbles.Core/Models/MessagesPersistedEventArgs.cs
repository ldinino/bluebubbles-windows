namespace BlueBubbles.Core.Models;

/// <summary>Why messages were persisted. The announcement used to carry a chat GUID and nothing
/// else, so a backfill of year-old history and a brand-new latest message were indistinguishable:
/// every subscriber had to assume the worst, which is precisely why the backfill write paths were
/// left announcing nothing at all rather than driving a needless conversation-list reload. Saying
/// *why* is what lets every path announce.</summary>
public enum MessagePersistKind
{
    /// <summary>A live socket message, an in-place edit/unsend, or a delta-sync batch — writes at
    /// the head of the thread that change what the conversation list shows.</summary>
    NewOrUpdated,

    /// <summary>A write driven by catching the cache up to the server: an older history page, a
    /// re-fetched window reconcile, or a confirmed soft delete. No subscriber acts on these today,
    /// which preserves pre-W1a-2 behaviour exactly.</summary>
    ServerTrueUp,
}

public sealed class MessagesPersistedEventArgs : EventArgs
{
    public MessagesPersistedEventArgs(string chatGuid, MessagePersistKind kind)
    {
        ChatGuid = chatGuid;
        Kind = kind;
    }

    public string ChatGuid { get; }
    public MessagePersistKind Kind { get; }

    /// <summary>The single definition of "this write can change a conversation-list tile".
    /// Subscribers filter on this instead of re-deriving it, so collapsing the kinds together shows
    /// up as a failing test rather than as a full list reload per backfilled page.</summary>
    public bool AffectsConversationList => Kind == MessagePersistKind.NewOrUpdated;
}
