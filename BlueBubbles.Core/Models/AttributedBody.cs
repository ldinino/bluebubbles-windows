using System.Text.Json.Serialization;

namespace BlueBubbles.Core.Models;

public record AttributedBody(
    [property: JsonPropertyName("string")] string String,
    [property: JsonPropertyName("runs")] List<Run>? Runs
);

public record Run(
    [property: JsonPropertyName("range")] List<int>? Range,
    [property: JsonPropertyName("attributes")] RunAttributes? Attributes
);

public record RunAttributes(
    [property: JsonPropertyName("__kIMMessagePartAttributeName")] int? MessagePart,
    [property: JsonPropertyName("__kIMFileTransferGUIDAttributeName")] string? AttachmentGuid,
    [property: JsonPropertyName("__kIMMentionConfirmedMention")] string? Mention,
    [property: JsonPropertyName("IMAudioTranscription")] string? AudioTranscript
);
