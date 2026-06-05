using System.Text.Json;
using System.Text.Json.Serialization;
using BlueBubbles.Core.Models;
using BlueBubbles.Core.Services;

namespace BlueBubbles.Core.Utils.Json;

/// <summary>
/// Deserializes <c>payloadData</c>, which the server sends in unrelated shapes: a clean object
/// (<c>{"type": 0, "urlData": [...]}</c>), or a raw NSKeyedArchiver array for rich-link /
/// iMessage-app messages, or an object whose internals we don't fully model.
///
/// <c>payloadData</c> is always optional decoration on top of the message's text/URL, so this
/// converter <b>fails soft to null on any shape it can't cleanly parse</b> rather than letting
/// the exception abort the whole <see cref="Message"/> — which would drop a live new-message or
/// poison an entire 1000-message delta-sync batch (one bad message stalling the cursor forever).
/// Decoding the NSKeyedArchiver form into a rich preview (Flutter's replaceDollar/extractUIDs in
/// .TRASH/lib/database/global/payload_data.dart) is intentionally deferred.
/// </summary>
public sealed class PayloadDataConverter : JsonConverter<PayloadData?>
{
    public override PayloadData? Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Capture the whole value up front so a parse failure below can never abort the
        // surrounding Message/batch read — the reader is already past the value either way.
        using var doc = JsonDocument.ParseValue(ref reader);
        var element = doc.RootElement;

        if (element.ValueKind != JsonValueKind.Object)
            return null; // raw NSKeyedArchiver array, null, etc.

        try
        {
            return element.Deserialize<PayloadData>(options);
        }
        catch (JsonException ex)
        {
            // An object shape we don't fully model yet. The message still renders from its
            // text/URL. Debug (not Warn) so common rich-link traffic doesn't spam the log.
            AppLog.Debug(LogCategory.Sync, $"Dropping unparseable payloadData: {ex.Message}");
            return null;
        }
    }

    public override void Write(
        Utf8JsonWriter writer, PayloadData? value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value, options);
}
