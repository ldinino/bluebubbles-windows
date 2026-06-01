namespace BlueBubbles.Core.Utils;

/// <summary>A single stored/received tapback, reduced to the fields needed for display logic.</summary>
public record ReactionRecord(
    string Guid,
    string ReactionType,
    bool IsFromMe,
    string? ReactorAddress,
    long DateCreated);

/// <summary>One grouped reaction badge: a reaction type with its count and whether the local user is among the reactors.</summary>
public record ReactionSummaryItem(
    string ReactionType,
    string Emoji,
    int Count,
    bool IncludesMe);

/// <summary>
/// Reduces a flat list of tapback records into the badges shown beneath a message.
///
/// Mirrors Flutter's <c>getUniqueReactionMessages</c> + the popup's self-reaction logic:
///   - each reactor contributes only their most recent reaction;
///   - a reactor whose latest reaction is a removal ("-type") contributes nothing;
///   - the surviving reactions are grouped by type and ordered canonically.
/// </summary>
public static class ReactionSummarizer
{
    private const string SelfKey = "\0me";

    public static IReadOnlyList<ReactionSummaryItem> Summarize(IEnumerable<ReactionRecord> records)
    {
        var latestByReactor = LatestByReactor(records);

        var groups = new Dictionary<string, (int Count, bool IncludesMe)>();
        foreach (var r in latestByReactor.Values)
        {
            if (ReactionTypes.IsRemoval(r.ReactionType)) continue;

            var type = ReactionTypes.BaseType(r.ReactionType);
            groups.TryGetValue(type, out var agg);
            groups[type] = (agg.Count + 1, agg.IncludesMe || r.IsFromMe);
        }

        return ReactionTypes.All
            .Where(groups.ContainsKey)
            .Select(t => new ReactionSummaryItem(t, ReactionTypes.ToEmoji(t), groups[t].Count, groups[t].IncludesMe))
            .ToList();
    }

    /// <summary>The local user's currently-active reaction type (base form), or null if none.
    /// Used to decide whether tapping a type adds it or removes it.</summary>
    public static string? SelfReaction(IEnumerable<ReactionRecord> records)
    {
        ReactionRecord? mine = null;
        foreach (var r in records)
        {
            if (!r.IsFromMe || !ReactionTypes.IsReaction(r.ReactionType)) continue;
            if (mine is null || r.DateCreated >= mine.DateCreated) mine = r;
        }

        return mine is null || ReactionTypes.IsRemoval(mine.ReactionType)
            ? null
            : ReactionTypes.BaseType(mine.ReactionType);
    }

    private static Dictionary<string, ReactionRecord> LatestByReactor(IEnumerable<ReactionRecord> records)
    {
        var latest = new Dictionary<string, ReactionRecord>();
        foreach (var r in records)
        {
            if (!ReactionTypes.IsReaction(r.ReactionType)) continue;

            var key = r.IsFromMe ? SelfKey : (r.ReactorAddress ?? "\0unknown");
            if (!latest.TryGetValue(key, out var existing) || r.DateCreated >= existing.DateCreated)
                latest[key] = r;
        }
        return latest;
    }
}
