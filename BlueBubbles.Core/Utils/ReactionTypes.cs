namespace BlueBubbles.Core.Utils;

/// <summary>
/// Tapback (reaction) protocol constants and helpers.
///
/// Mirrors the BlueBubbles server/Flutter contract:
///   - A reaction is a <c>Message</c> whose <c>associatedMessageType</c> is one of the six
///     known types, and whose <c>associatedMessageGuid</c> points at the parent message.
///   - Removing a reaction is expressed by prefixing the type with '-' (e.g. "-love").
///   - The server's <c>associatedMessageGuid</c> may carry a part prefix such as
///     "p:0/&lt;guid&gt;" or "bp:0/&lt;guid&gt;"; the bare GUID is the last '/'-segment.
///
/// This is a protocol reference (what the server expects); the emoji glyphs are a native
/// Windows choice rendered with Segoe UI Emoji. Glyphs use Unicode escapes so the mapping
/// is independent of this source file's text encoding.
/// </summary>
public static class ReactionTypes
{
    public const string Love = "love";
    public const string Like = "like";
    public const string Dislike = "dislike";
    public const string Laugh = "laugh";
    public const string Emphasize = "emphasize";
    public const string Question = "question";

    /// <summary>The six reaction types, in iMessage picker order.</summary>
    public static readonly IReadOnlyList<string> All =
        [Love, Like, Dislike, Laugh, Emphasize, Question];

    private static readonly Dictionary<string, string> Emoji = new()
    {
        [Love] = "❤️",      // red heart
        [Like] = "\U0001F44D",        // thumbs up
        [Dislike] = "\U0001F44E",     // thumbs down
        [Laugh] = "\U0001F602",       // face with tears of joy
        [Emphasize] = "‼️", // double exclamation mark
        [Question] = "❓"         // question mark ornament
    };

    private static readonly Dictionary<string, string> Verb = new()
    {
        [Love] = "Loved",
        [Like] = "Liked",
        [Dislike] = "Disliked",
        [Laugh] = "Laughed at",
        [Emphasize] = "Emphasized",
        [Question] = "Questioned"
    };

    /// <summary>True if <paramref name="associatedMessageType"/> is a known reaction type
    /// (ignoring a leading '-' removal marker).</summary>
    public static bool IsReaction(string? associatedMessageType)
        => associatedMessageType is not null && Emoji.ContainsKey(BaseType(associatedMessageType));

    /// <summary>True if the type is a reaction removal (e.g. "-love").</summary>
    public static bool IsRemoval(string? associatedMessageType)
        => associatedMessageType is not null && associatedMessageType.StartsWith('-');

    /// <summary>Strips a leading '-' removal marker, returning the base reaction type.</summary>
    public static string BaseType(string associatedMessageType)
        => associatedMessageType.StartsWith('-') ? associatedMessageType[1..] : associatedMessageType;

    /// <summary>Emoji glyph for a reaction type (removal marker ignored). Empty if unknown.</summary>
    public static string ToEmoji(string associatedMessageType)
        => Emoji.TryGetValue(BaseType(associatedMessageType), out var e) ? e : string.Empty;

    /// <summary>Past-tense verb for a reaction type, used in notifications and accessibility text.</summary>
    public static string ToVerb(string associatedMessageType)
        => Verb.TryGetValue(BaseType(associatedMessageType), out var v) ? v : "Reacted to";

    /// <summary>
    /// Normalizes the server's <c>associatedMessageGuid</c> to the bare parent message GUID,
    /// stripping any "p:N/" or "bp:N/" part prefix. Returns null for null input.
    /// Port of <c>message.dart</c> fromMap (associatedMessageGuid resolution).
    /// </summary>
    public static string? NormalizeAssociatedGuid(string? rawAssociatedGuid)
    {
        if (string.IsNullOrEmpty(rawAssociatedGuid)) return rawAssociatedGuid;
        var withoutPrefix = rawAssociatedGuid.Replace("bp:", string.Empty);
        var slash = withoutPrefix.LastIndexOf('/');
        return slash >= 0 ? withoutPrefix[(slash + 1)..] : withoutPrefix;
    }

    /// <summary>
    /// Resolves the reaction's target part index. Prefers the explicit
    /// <c>associatedMessagePart</c>; otherwise parses the leading "p:N/" segment of the raw
    /// <c>associatedMessageGuid</c>. Defaults to 0.
    /// </summary>
    public static int ResolveAssociatedPart(string? rawAssociatedGuid, int? explicitPart)
    {
        if (explicitPart.HasValue) return explicitPart.Value;
        if (string.IsNullOrEmpty(rawAssociatedGuid)) return 0;

        var firstSegment = rawAssociatedGuid.Replace("p:", string.Empty).Split('/')[0];
        return int.TryParse(firstSegment, out var part) ? part : 0;
    }
}
