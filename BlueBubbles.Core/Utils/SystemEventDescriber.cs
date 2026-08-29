using BlueBubbles.Core.Data.Entities;

namespace BlueBubbles.Core.Utils;

/// <summary>
/// Recognises group/system event rows (name changes, adds, removes, leaves) and renders them as
/// plain language. Measured against a real cache: every row with ItemType != 0 had NULL text, so
/// without this they render as blank message bubbles. Item types 1-3 are the well-established
/// Apple group actions; anything else is labelled generically rather than guessed at.
/// </summary>
public static class SystemEventDescriber
{
    public const string DefaultSelfLabel = "Me";

    /// <summary>True when the row is a group/system event rather than a chat message.</summary>
    public static bool IsSystemEvent(MessageEntity m) => m.ItemType != 0 || m.GroupActionType != 0;

    public static string Describe(
        MessageEntity m,
        Func<string, string>? resolveSender = null,
        string selfLabel = DefaultSelfLabel)
    {
        var actor = m.IsFromMe ? selfLabel : ResolveName(m.Handle?.Address, resolveSender);

        return (m.ItemType, m.GroupActionType) switch
        {
            (1, 0) => $"{actor} added someone to the conversation.",
            (1, 1) => $"{actor} removed someone from the conversation.",
            (2, _) when !string.IsNullOrWhiteSpace(m.GroupTitle)
                => $"{actor} named the conversation \"{m.GroupTitle}\".",
            (2, _) => $"{actor} changed the conversation name.",
            (3, _) => $"{actor} left the conversation.",
            _ => $"Unrecognised system event from {actor} (itemType {m.ItemType}, "
                 + $"groupActionType {m.GroupActionType}).",
        };
    }

    public static string ResolveName(string? address, Func<string, string>? resolveSender)
    {
        if (string.IsNullOrWhiteSpace(address)) return "Unknown";
        var resolved = resolveSender?.Invoke(address);
        return string.IsNullOrWhiteSpace(resolved) ? address : resolved!;
    }
}
