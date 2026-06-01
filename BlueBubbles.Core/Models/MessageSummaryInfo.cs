using System.Text.Json.Serialization;

namespace BlueBubbles.Core.Models;

public record MessageSummaryInfo(
    [property: JsonPropertyName("retractedParts")] List<int>? RetractedParts,
    [property: JsonPropertyName("editedContent")] Dictionary<string, List<EditedContent>>? EditedContent,
    [property: JsonPropertyName("originalTextRange")] Dictionary<string, List<int>>? OriginalTextRange,
    [property: JsonPropertyName("editedParts")] List<int>? EditedParts
);

public record EditedContent(
    [property: JsonPropertyName("text")] EditedContentValues? Text,
    [property: JsonPropertyName("date")] double? Date
);

public record EditedContentValues(
    [property: JsonPropertyName("values")] List<AttributedBody>? Values
);
