using System.Text.Json;
using System.Text.Json.Serialization;
using BlueBubbles.Core.Services;

namespace BlueBubbles.Core.Utils.Json;

/// <summary>
/// Deserializes a field the BlueBubbles server sends in two interchangeable shapes: a
/// string-keyed object (<c>{"0": [...]}</c>) or a bare array (<c>[...]</c>). Apple emits the
/// bare-array form for single-part edited messages and the keyed form for multi-part, so the
/// same field flips shape from message to message.
///
/// This mirrors the Flutter client's MessageSummaryInfo.fromJson tolerance
/// (.TRASH/lib/database/global/message_summary_info.dart) — wrapping a bare array under key
/// "0" — and additionally <b>fails soft to null on any shape it can't parse</b>. Without it a
/// single edited message poisons the whole 1000-message delta-sync batch (one
/// <see cref="System.Text.Json.JsonException"/> aborts the batch, the watermark never advances,
/// and the next sync refetches the same cursor forever).
/// </summary>
/// <typeparam name="TValue">The dictionary value type — e.g. <c>List&lt;EditedContent&gt;</c>
/// for editedContent or <c>List&lt;int&gt;</c> for originalTextRange. A bare array is
/// deserialized directly into this type and stored under key "0".</typeparam>
public sealed class FlexibleStringKeyedMapConverter<TValue> : JsonConverter<Dictionary<string, TValue>?>
{
    public override Dictionary<string, TValue>? Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Capture the whole value up front so a parse failure below can never abort the
        // surrounding read — the reader is already past the value either way.
        using var doc = JsonDocument.ParseValue(ref reader);
        var element = doc.RootElement;

        try
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    // The attribute is property-scoped, so this resolves the default Dictionary
                    // converter rather than recursing into this one.
                    return element.Deserialize<Dictionary<string, TValue>>(options);

                case JsonValueKind.Array:
                    // Bare array → wrap under "0" to match the keyed shape, as Flutter does.
                    var list = element.Deserialize<TValue>(options);
                    return list is null ? null : new Dictionary<string, TValue> { ["0"] = list };

                default:
                    return null;
            }
        }
        catch (JsonException ex)
        {
            // A shape we don't model yet (e.g. an edited-content variant). Degrading to null
            // shows the original text rather than stalling the whole sync.
            AppLog.Debug(LogCategory.Sync, $"Dropping unparseable message-summary field: {ex.Message}");
            return null;
        }
    }

    public override void Write(
        Utf8JsonWriter writer, Dictionary<string, TValue>? value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value, options);
}
