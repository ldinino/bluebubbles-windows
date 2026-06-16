using BlueBubbles.Core.Data.Entities;

namespace BlueBubbles.Core.Services;

/// <summary>
/// A conversation as shown in the list: one or more underlying 1:1 chats that resolve to the same
/// imported contact card, presented as a single thread. Most conversations are a single chat (one
/// constituent). A merge happens only for "sticky bifurcation" — when a contact links, say, an iCloud
/// email and a phone number that each ended up with their own server chat. The server keeps both chats;
/// this is a pure client-side projection, recomputed on every list rebuild and contact import.
/// </summary>
public sealed record MergedConversation(
    IReadOnlyList<ChatWithParticipants> Constituents,
    ChatWithParticipants Primary,
    ChatWithParticipants MostRecent,
    IReadOnlyList<HandleEntity> Participants,
    string PrimaryAddress,
    string? LastMessageText,
    long Timestamp,
    bool HasUnread,
    bool IsPinned,
    bool IsArchived,
    bool LastMessageIsFromMe,
    long? LastMessageDateDelivered,
    long? LastMessageDateRead)
{
    /// <summary>True when more than one underlying chat was folded together (the bifurcation case).</summary>
    public bool IsMerged => Constituents.Count > 1;

    /// <summary>The chat whose GUID is the merged conversation's stable identity (tile key, active-chat,
    /// reads). Phone-preferred so it matches the address shown on the info bar.</summary>
    public ChatEntity PrimaryChat => Primary.Chat;

    public IReadOnlyList<string> ConstituentGuids =>
        Constituents.Select(c => c.Chat.Guid).ToList();

    public IReadOnlyList<int> ConstituentChatIds =>
        Constituents.Select(c => c.Chat.Id).ToList();
}

/// <summary>
/// Folds the chat list into <see cref="MergedConversation"/>s. Only 1:1 chats whose sole participant
/// resolves to the same contact card are merged; group chats and unknown addresses always stand alone.
/// </summary>
public static class ConversationMerger
{
    public static IReadOnlyList<MergedConversation> Merge(
        IReadOnlyList<ChatWithParticipants> chats, IContactResolverService contacts)
    {
        var groups = new Dictionary<string, List<ChatWithParticipants>>();
        // Emit single chats and merged groups in source order. The source is recency-sorted (pinned,
        // then latest message), so a merged group takes the slot of its first-seen (most recent)
        // constituent and the merged tile sorts where the user expects.
        var slots = new List<(ChatWithParticipants? Single, string? Key)>();

        foreach (var chat in chats)
        {
            var key = MergeKey(chat, contacts);
            if (key is null)
            {
                slots.Add((chat, null));
                continue;
            }

            if (!groups.TryGetValue(key, out var list))
            {
                list = [];
                groups[key] = list;
                slots.Add((null, key));
            }
            list.Add(chat);
        }

        var result = new List<MergedConversation>(slots.Count);
        foreach (var (single, key) in slots)
        {
            var group = single is not null
                ? new List<ChatWithParticipants> { single }
                : groups[key!];
            result.Add(Build(group, contacts));
        }
        return result;
    }

    /// <summary>A chat is mergeable only when it's 1:1 and its sole participant resolves to a known
    /// contact card; that card's id is the merge key. Group chats and unknown addresses return null.</summary>
    private static string? MergeKey(ChatWithParticipants chat, IContactResolverService contacts)
        => chat.Participants.Count == 1 ? contacts.GetContactId(chat.Participants[0].Address) : null;

    private static MergedConversation Build(List<ChatWithParticipants> group, IContactResolverService contacts)
    {
        // Source order is recency, so group[0] is the most recent — the send target.
        var mostRecent = group[0];

        // Primary identity prefers a phone number (shown on the info bar); fall back to the first chat.
        var primary = group.FirstOrDefault(
                c => c.Participants.Count == 1 && ContactResolverService.IsPhone(c.Participants[0].Address))
            ?? group[0];

        var primaryAddress = primary.Participants.Count == 1
            ? primary.Participants[0].Address
            : string.Empty;

        // Single chats keep their exact participant list (so group chats are untouched). A genuine merge
        // unions the per-chat participants, phones first then emails, driving the "phone / email" row.
        var participants = group.Count == 1
            ? group[0].Participants
            : group.SelectMany(c => c.Participants)
                .OrderByDescending(h => ContactResolverService.IsPhone(h.Address))
                .ToList();

        return new MergedConversation(
            Constituents: group,
            Primary: primary,
            MostRecent: mostRecent,
            Participants: participants,
            PrimaryAddress: primaryAddress,
            LastMessageText: mostRecent.LastMessageText,
            Timestamp: group.Max(c => c.Chat.LatestMessageDate ?? 0),
            HasUnread: group.Any(c => c.Chat.HasUnreadMessage),
            IsPinned: group.Any(c => c.Chat.IsPinned),
            IsArchived: primary.Chat.IsArchived,
            LastMessageIsFromMe: mostRecent.LastMessageIsFromMe,
            LastMessageDateDelivered: mostRecent.LastMessageDateDelivered,
            LastMessageDateRead: mostRecent.LastMessageDateRead);
    }
}
