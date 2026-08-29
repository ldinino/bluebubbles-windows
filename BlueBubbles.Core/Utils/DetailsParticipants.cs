using BlueBubbles.Core.Data;
using BlueBubbles.Core.Data.Entities;
using BlueBubbles.Core.Models;

namespace BlueBubbles.Core.Utils;

/// <summary>Chooses the participant set an open chat-details pane should render for a
/// <c>chat-update</c>. The pane and the persistence path both hang off the same event and are not
/// ordered against each other, so the cached list is usually still the pre-update set when the pane
/// refreshes; the payload is the same source the display name already trusts. The cached list stays
/// as the fallback because a chat-update payload can omit participants entirely, and blanking a
/// populated pane is worse than showing a set one beat stale.</summary>
public static class DetailsParticipants
{
    public static List<HandleEntity> Resolve(
        IReadOnlyList<Handle>? payload,
        IReadOnlyList<HandleEntity>? cached)
    {
        if (payload is not { Count: > 0 })
            return cached?.ToList() ?? [];

        var byAddress = new Dictionary<string, HandleEntity>(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in cached ?? [])
            byAddress.TryAdd(entity.Address, entity);

        // Reuse the cached entity where the address matches so rows keep their DB identity and any
        // stored handle metadata; only genuinely new participants are materialised from the payload.
        return payload
            .Select(h => byAddress.TryGetValue(h.Address, out var known) ? known : h.ToEntity())
            .ToList();
    }
}
