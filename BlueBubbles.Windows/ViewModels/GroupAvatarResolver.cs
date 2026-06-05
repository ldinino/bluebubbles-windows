using BlueBubbles.Core.Services;

namespace BlueBubbles.Windows.ViewModels;

/// <summary>The two stacked sub-avatars shown for a group chat. Each sub-avatar is an initials
/// string (empty → generic person glyph) plus an optional contact photo.</summary>
public readonly record struct GroupAvatar(
    string Initials1, string Initials2, byte[]? Bytes1, byte[]? Bytes2);

/// <summary>Resolves the two faces shown for a group chat from its most-recent senders, so the
/// same pair appears everywhere a group is rendered (list tile, conversation header, details pane).
/// Centralising this keeps those surfaces in sync.</summary>
public static class GroupAvatarResolver
{
    public static GroupAvatar Resolve(ChatWithParticipants data, IContactResolverService contacts)
    {
        // front = left circle (second-most-recent sender); back = right circle (most recent).
        string? frontAddress = null;
        string? backAddress = null;

        if (data.RecentSenders is { Count: > 0 })
        {
            backAddress = data.RecentSenders[0].Address;
            frontAddress = data.RecentSenders.Count > 1
                ? data.RecentSenders[1].Address
                : data.Participants.FirstOrDefault(p =>
                    !p.Address.Equals(backAddress, StringComparison.OrdinalIgnoreCase))?.Address;
        }

        frontAddress ??= data.Participants.ElementAtOrDefault(0)?.Address;
        backAddress ??= data.Participants.ElementAtOrDefault(1)?.Address
                     ?? data.Participants.ElementAtOrDefault(0)?.Address;

        var (initials1, bytes1) = ResolveOne(frontAddress, contacts);
        var (initials2, bytes2) = ResolveOne(backAddress, contacts);
        return new GroupAvatar(initials1, initials2, bytes1, bytes2);
    }

    private static (string Initials, byte[]? Bytes) ResolveOne(string? address, IContactResolverService contacts)
        => address is null
            ? (string.Empty, null)
            : (contacts.GetAvatarInitials(address), contacts.GetAvatar(address));
}
