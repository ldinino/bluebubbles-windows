using System.Text.Json;
using BlueBubbles.Core.Models;

namespace BlueBubbles.Core.Utils;

/// <summary>
/// Helpers for the Private API edit / unsend protocol. An unsent message part is reported in
/// <c>messageSummaryInfo[0].retractedParts</c>; an edited message carries an updated <c>text</c>
/// plus a non-null <c>dateEdited</c> (see <c>lib/database/io/message.dart</c> merge logic, where
/// <c>existing.text</c> is overwritten by the edited text on an updated-message event).
/// </summary>
public static class MessageEdits
{
    /// <summary>True when the given part index was retracted (unsent), read from a deserialized
    /// message's summary info.</summary>
    public static bool IsPartRetracted(IReadOnlyList<MessageSummaryInfo>? summary, int part)
        => summary is { Count: > 0 } && (summary[0].RetractedParts?.Contains(part) ?? false);

    /// <summary>True when the given part index was retracted, read from the persisted
    /// <see cref="Data.Entities.MessageEntity.MessageSummaryInfoJson"/> column.</summary>
    public static bool IsPartRetracted(string? summaryInfoJson, int part)
    {
        if (string.IsNullOrEmpty(summaryInfoJson)) return false;
        try
        {
            var summary = JsonSerializer.Deserialize<List<MessageSummaryInfo>>(
                summaryInfoJson, JsonDefaults.Options);
            return IsPartRetracted(summary, part);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The fallback text shown to non-Apple / pre-Ventura recipients of an edit. Mirrors the
    /// Flutter client's <c>"Edited to: “{text}”"</c> backwards-compatibility string.</summary>
    public static string BuildBackwardsCompatText(string editedText)
        => $"Edited to: “{editedText}”";
}
