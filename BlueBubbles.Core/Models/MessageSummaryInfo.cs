using System.Text.Json.Serialization;
using BlueBubbles.Core.Utils.Json;

namespace BlueBubbles.Core.Models;

public record MessageSummaryInfo(
    [property: JsonPropertyName("retractedParts")] List<int>? RetractedParts,
    [property: JsonPropertyName("editedContent"),
               JsonConverter(typeof(FlexibleStringKeyedMapConverter<List<EditedContent>>))]
    Dictionary<string, List<EditedContent>>? EditedContent,
    [property: JsonPropertyName("originalTextRange"),
               JsonConverter(typeof(FlexibleStringKeyedMapConverter<List<int>>))]
    Dictionary<string, List<int>>? OriginalTextRange,
    [property: JsonPropertyName("editedParts")] List<int>? EditedParts
);

public record EditedContent(
    [property: JsonPropertyName("text")] EditedContentValues? Text,
    [property: JsonPropertyName("date")] double? Date
);

public record EditedContentValues(
    [property: JsonPropertyName("values")] List<AttributedBody>? Values
);
