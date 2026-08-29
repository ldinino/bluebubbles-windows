using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlueBubbles.Core.Export;

public sealed record ExportManifestEntry(
    string ChatGuid,
    string Title,
    IReadOnlyList<string> Participants,
    string JsonlFile,
    string? TranscriptFile,
    int MessageCount,
    int AttachmentsIncluded,
    int AttachmentsMissing,
    ExportCoverageKind CoverageKind,
    bool ReachesBeginning,
    string? OldestSynced,
    string? OldestExported,
    string? NewestExported,
    string CoverageStatement);

public sealed record ExportManifest(
    string Application,
    string SchemaVersion,
    string ExportedAt,
    int ChatCount,
    int IncompleteChatCount,
    string Notice,
    IReadOnlyList<ExportManifestEntry> Chats);

/// <summary>
/// JSONL and manifest serialization. JSONL rather than XML or a single JSON array: message text
/// is full of &lt;, &gt;, &amp;, emoji and newlines, and in a single-document format one bad
/// control character invalidates the whole file. One object per line means a corrupt line costs
/// one message.
/// </summary>
public static class ChatExportSerializer
{
    public const string SchemaVersion = "1";

    public const string PlaintextNotice =
        "This export is unencrypted plain text. Anyone who can read these files can read the "
        + "conversations in them.";

    private static readonly JsonSerializerOptions LineOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        // These files are an archive read by humans, never injected into HTML. The default
        // encoder turns every '+' into \u002B and every curly quote or emoji into a \uXXXX
        // escape, which makes a "readable record" unreadable. Control characters - including
        // the newlines that would break the one-object-per-line contract - are still escaped.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>One JSON object per line. The first line is a header carrying the chat identity
    /// and its coverage, so a single .jsonl file is self-describing without the manifest.</summary>
    public static IEnumerable<string> ToJsonl(ChatExport export)
    {
        yield return JsonSerializer.Serialize(new
        {
            type = "header",
            schemaVersion = SchemaVersion,
            chatGuid = export.ChatGuid,
            title = export.Title,
            participants = export.Participants,
            coverage = export.Coverage,
            notice = PlaintextNotice,
        }, LineOptions);

        foreach (var m in export.Messages)
            yield return JsonSerializer.Serialize(m, LineOptions);
    }

    public static ExportManifestEntry ToManifestEntry(
        ChatExport export, string jsonlFile, string? transcriptFile)
    {
        var all = export.Messages.SelectMany(m => m.Attachments).ToList();
        return new ExportManifestEntry(
            export.ChatGuid,
            export.Title,
            export.Participants,
            jsonlFile,
            transcriptFile,
            export.Messages.Count,
            all.Count(a => a.IsCached),
            all.Count(a => !a.IsCached),
            export.Coverage.Kind,
            export.Coverage.ReachesBeginning,
            export.Coverage.OldestSynced,
            export.Coverage.OldestExported,
            export.Coverage.NewestExported,
            export.Coverage.Statement);
    }

    public static string ToManifestJson(
        IReadOnlyList<ExportManifestEntry> entries, DateTimeOffset exportedAt)
    {
        var manifest = new ExportManifest(
            "BlueBubbles for Windows",
            SchemaVersion,
            exportedAt.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz",
                System.Globalization.CultureInfo.InvariantCulture),
            entries.Count,
            entries.Count(e => !e.ReachesBeginning),
            PlaintextNotice,
            entries);

        return JsonSerializer.Serialize(manifest, ManifestOptions);
    }
}
