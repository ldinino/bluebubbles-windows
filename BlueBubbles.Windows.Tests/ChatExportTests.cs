using BlueBubbles.Core.Data.Entities;
using BlueBubbles.Core.Export;

namespace BlueBubbles.Windows.Tests;

public class ChatExportTests
{
    private static readonly TimeSpan Offset = TimeSpan.FromHours(-5);
    private static readonly DateTimeOffset Now =
        new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    // 2026-01-02T02:57:25.678Z
    private const long T0 = 1767322645678;

    private static HandleEntity Handle(string address) =>
        new() { Id = address.GetHashCode() & 0x7fffffff, Address = address, Service = "iMessage" };

    private static MessageEntity Msg(
        string guid,
        string? text = null,
        bool isFromMe = false,
        long date = T0,
        HandleEntity? handle = null,
        int itemType = 0,
        int groupActionType = 0,
        string? groupTitle = null,
        string? associatedGuid = null,
        string? associatedType = null,
        long? dateEdited = null,
        long? dateDeleted = null,
        int? rowId = null,
        params AttachmentEntity[] attachments) => new()
        {
            Guid = guid,
            Text = text,
            IsFromMe = isFromMe,
            DateCreated = date,
            DateEdited = dateEdited,
            DateDeleted = dateDeleted,
            Handle = handle,
            ItemType = itemType,
            GroupActionType = groupActionType,
            GroupTitle = groupTitle,
            AssociatedMessageGuid = associatedGuid,
            AssociatedMessageType = associatedType,
            OriginalRowId = rowId,
            Attachments = attachments.ToList(),
        };

    private static AttachmentEntity Att(
        string guid, string? name = "photo.jpg", string? mime = "image/jpeg", long bytes = 1234) =>
        new() { Guid = guid, TransferName = name, MimeType = mime, TotalBytes = bytes };

    private static ChatExport BuildExport(
        IReadOnlyList<MessageEntity> messages,
        long? watermark = 0,
        Func<AttachmentEntity, string?>? resolveArchivePath = null,
        IReadOnlyList<HandleEntity>? participants = null) =>
        ChatExportBuilder.Build(
            "iMessage;-;+15550001111",
            "Test Chat",
            participants ?? [Handle("+15550001111")],
            watermark,
            messages,
            Offset,
            Now,
            resolveSender: null,
            resolveArchivePath: resolveArchivePath);

    private static string Transcript(ChatExport export) =>
        string.Join(Environment.NewLine, ChatExportTranscript.Render(export));

    // ---------- tapbacks ----------

    [Fact]
    public void Tapback_IsNotItsOwnMessageRecord()
    {
        var alice = Handle("+15550001111");
        var export = BuildExport([
            Msg("parent", "Dinner at 7?", handle: alice, rowId: 1),
            Msg("tap", "Liked \u201CDinner at 7?\u201D", isFromMe: true, date: T0 + 1000,
                associatedGuid: "parent", associatedType: "love", rowId: 2),
        ]);

        Assert.Single(export.Messages);
        Assert.Equal("parent", export.Messages[0].Guid);
    }

    [Fact]
    public void Tapback_DoesNotAppearAsItsOwnTranscriptLine()
    {
        var alice = Handle("+15550001111");
        var export = BuildExport([
            Msg("parent", "Dinner at 7?", handle: alice, rowId: 1),
            Msg("tap", "Liked \u201CDinner at 7?\u201D", isFromMe: true, date: T0 + 1000,
                associatedGuid: "parent", associatedType: "love", rowId: 2),
        ]);

        var transcript = Transcript(export);

        // The reaction's own body text - the "Liked ..." noise - must never be a transcript line.
        Assert.DoesNotContain("Liked \u201CDinner at 7?\u201D", transcript);
        // It is folded onto the parent instead.
        Assert.Contains("(Loved by Me)", transcript);
    }

    [Fact]
    public void Tapback_WithNumericType_IsStillFoldedNotExportedAsSpeech()
    {
        // Measured against the real cache: associatedMessageType is often "2006"/"4000"/"sticker",
        // which ReactionTypes.IsReaction does NOT recognise. Folding must key on the presence of
        // associatedMessageGuid, not on the type being a known tapback.
        var export = BuildExport([
            Msg("parent", "look at this", rowId: 1),
            Msg("tap", "Reacted with a sticker", isFromMe: true, date: T0 + 1000,
                associatedGuid: "parent", associatedType: "2006", rowId: 2),
        ]);

        Assert.Single(export.Messages);
        Assert.DoesNotContain("Reacted with a sticker", Transcript(export));
    }

    [Fact]
    public void Tapback_Removal_NetsOutTheAdd()
    {
        var export = BuildExport([
            Msg("parent", "hello", rowId: 1),
            Msg("t1", isFromMe: true, date: T0 + 1000, associatedGuid: "parent",
                associatedType: "love", rowId: 2),
            Msg("t2", isFromMe: true, date: T0 + 2000, associatedGuid: "parent",
                associatedType: "-love", rowId: 3),
        ]);

        Assert.Empty(export.Messages[0].Reactions);
    }

    // ---------- system events ----------

    [Fact]
    public void SystemEvent_IsNotRenderedAsSpeech()
    {
        var bob = Handle("+15552223333");
        var export = BuildExport([
            Msg("evt", itemType: 2, groupTitle: "Beach Trip", handle: bob, rowId: 1),
        ]);

        Assert.Equal(ExportedMessageKind.SystemEvent, export.Messages[0].Kind);

        var transcript = Transcript(export);
        Assert.Contains("* +15552223333 named the conversation \"Beach Trip\".", transcript);
        // A speech line would be "<sender>: ..." - the event must not produce one.
        Assert.DoesNotContain("+15552223333: ", transcript);
    }

    [Fact]
    public void SystemEvent_WithNullText_DoesNotProduceABlankSpeechLine()
    {
        // Measured: all 37 ItemType != 0 rows in the real cache had NULL text.
        var export = BuildExport([
            Msg("evt", text: null, itemType: 3, handle: Handle("+15552223333"), rowId: 1),
        ]);

        var transcript = Transcript(export);
        Assert.Contains("left the conversation.", transcript);
        Assert.DoesNotContain("[no content]", transcript);
    }

    [Fact]
    public void SystemEvent_UnknownItemType_IsLabelledNotGuessed()
    {
        var export = BuildExport([Msg("evt", itemType: 6, isFromMe: true, rowId: 1)]);
        Assert.Contains("Unrecognised system event", export.Messages[0].EventDescription);
    }

    // ---------- attachments ----------

    [Fact]
    public void AttachmentOnlyMessage_ProducesPlaceholderNotAnEmptyLine()
    {
        var export = BuildExport(
            [Msg("m1", text: null, handle: Handle("+15550001111"), rowId: 1,
                attachments: Att("att-1", "beach.jpg", "image/jpeg", 5120))],
            resolveArchivePath: _ => "attachments/att-1-beach.jpg");

        var lines = ChatExportTranscript.Render(export);
        var body = lines.Single(l => l.Contains("beach.jpg"));

        Assert.Contains("[Attachment: beach.jpg, image/jpeg, 5120 bytes -> attachments/att-1-beach.jpg]", body);
        Assert.DoesNotContain(lines, l => l.TrimEnd().EndsWith("+15550001111:", StringComparison.Ordinal));
    }

    [Fact]
    public void AttachmentNotCached_IsMarkedMissingNotSilentlyDropped()
    {
        var export = BuildExport(
            [Msg("m1", text: null, rowId: 1, attachments: Att("att-1", "video.mov", "video/quicktime", 90))],
            resolveArchivePath: _ => null);

        Assert.False(export.Messages[0].Attachments[0].IsCached);
        Assert.Contains("[Attachment NOT INCLUDED: video.mov, video/quicktime, 90 bytes - was never downloaded to this PC]",
            Transcript(export));
    }

    [Fact]
    public void AttachmentIsFoundViaNavigation_NotTheUnreliableHasAttachmentsFlag()
    {
        // Measured: all 661 messages owning attachment rows had HasAttachments = 0. Relying on
        // the flag would drop every attachment silently.
        var m = Msg("m1", text: null, rowId: 1, attachments: Att("att-1"));
        m.HasAttachments = false;

        var export = BuildExport([m], resolveArchivePath: _ => "attachments/att-1-photo.jpg");
        Assert.Single(export.Messages[0].Attachments);
    }

    [Fact]
    public void MessageWithNoTextAndNoAttachments_SaysSoExplicitly()
    {
        var export = BuildExport([Msg("m1", text: null, rowId: 1)]);
        Assert.Contains("[no content]", Transcript(export));
    }

    // ---------- timestamps ----------

    [Fact]
    public void Timestamp_RoundTripsWithOffset()
    {
        var iso = ExportTimestamp.ToIso(T0, Offset);

        Assert.Equal("2026-01-01T21:57:25.678-05:00", iso);
        Assert.Equal(T0, ExportTimestamp.ParseIso(iso).ToUnixTimeMilliseconds());
    }

    [Fact]
    public void Timestamp_SameInstantInDifferentOffsets_RoundTripsToTheSameInstant()
    {
        var a = ExportTimestamp.ToIso(T0, TimeSpan.FromHours(-5));
        var b = ExportTimestamp.ToIso(T0, TimeSpan.FromHours(9));

        Assert.NotEqual(a, b);
        Assert.Equal(ExportTimestamp.ParseIso(a).ToUnixTimeMilliseconds(),
                     ExportTimestamp.ParseIso(b).ToUnixTimeMilliseconds());
    }

    [Fact]
    public void ExportedMessageDate_CarriesTheOffset()
    {
        var export = BuildExport([Msg("m1", "hi", rowId: 1)]);
        Assert.Equal("2026-01-01T21:57:25.678-05:00", export.Messages[0].Date);
    }

    // ---------- edited / deleted ----------

    [Fact]
    public void EditedMessage_ExportsFinalTextAndRecordsTheEdit()
    {
        var export = BuildExport([
            Msg("m1", "the corrected text", rowId: 1, dateEdited: T0 + 60000),
        ]);

        Assert.True(export.Messages[0].WasEdited);
        Assert.Equal("the corrected text", export.Messages[0].Text);
        Assert.Equal("2026-01-01T21:58:25.678-05:00", export.Messages[0].DateEdited);
    }

    [Fact]
    public void SoftDeletedMessage_IsExcluded()
    {
        var export = BuildExport([
            Msg("m1", "kept", rowId: 1),
            Msg("m2", "deleted", rowId: 2, dateDeleted: T0 + 5),
        ]);

        Assert.Equal(["m1"], export.Messages.Select(m => m.Guid));
    }

    [Fact]
    public void Messages_AreOrderedByDateThenRowId()
    {
        var export = BuildExport([
            Msg("caption", "look", date: T0, rowId: 20),
            Msg("photo", "photo", date: T0, rowId: 10),
            Msg("later", "later", date: T0 + 1000, rowId: 5),
        ]);

        Assert.Equal(["photo", "caption", "later"], export.Messages.Select(m => m.Guid));
    }

    // ---------- filenames ----------

    [Fact]
    public void FileName_IsDeterministicForTheSameChat()
    {
        var a = ExportFileNames.ForChat("iMessage;+;chat123", "Beach Trip \uD83C\uDFD6\uFE0F!");
        var b = ExportFileNames.ForChat("iMessage;+;chat123", "Beach Trip \uD83C\uDFD6\uFE0F!");

        Assert.Equal(a, b);
        Assert.Equal("beach-trip-", a[..11]);
        Assert.DoesNotContain(a, c => Path.GetInvalidFileNameChars().Contains(c));
    }

    [Fact]
    public void FileName_DistinguishesChatsWithTheSameTitle()
    {
        Assert.NotEqual(
            ExportFileNames.ForChat("iMessage;+;chatAAA", "Into the light"),
            ExportFileNames.ForChat("iMessage;+;chatBBB", "Into the light"));
    }

    [Fact]
    public void Slug_FallsBackWhenTitleHasNoUsableCharacters()
        => Assert.Equal(ExportFileNames.Fallback, ExportFileNames.Slug("\uD83D\uDE00\uD83D\uDE00"));

    // ---------- jsonl ----------

    [Fact]
    public void Jsonl_EmitsAHeaderPlusOneLinePerMessage()
    {
        var export = BuildExport([Msg("m1", "a", rowId: 1), Msg("m2", "b", rowId: 2)]);
        var lines = ChatExportSerializer.ToJsonl(export).ToList();

        Assert.Equal(3, lines.Count);
        Assert.Contains("\"type\":\"header\"", lines[0]);
        Assert.All(lines, l => Assert.DoesNotContain("\n", l));
    }

    [Fact]
    public void Jsonl_EscapesNewlinesSoOneMessageStaysOnOneLine()
    {
        var export = BuildExport([Msg("m1", "line one\nline two", rowId: 1)]);
        var lines = ChatExportSerializer.ToJsonl(export).ToList();

        Assert.Equal(2, lines.Count);
        Assert.Contains("line one\\nline two", lines[1]);
    }

    [Fact]
    public void Jsonl_KeepsCommonPunctuationLiteralAndStaysLossless()
    {
        // The default encoder turns every '+' into \u002B and every curly quote into \u2019,
        // which makes a "readable record" unreadable; the relaxed encoder must not be undone.
        // Astral-plane emoji ARE still emitted as escaped surrogate pairs - measured, not
        // assumed - which is valid JSON and round-trips exactly, so it is left alone.
        const string body = "it\u2019s +1 \uD83C\uDFD6\uFE0F\rtail";
        var export = BuildExport([Msg("m1", body, rowId: 1)]);
        var line = ChatExportSerializer.ToJsonl(export).Last();

        Assert.Contains("it\u2019s +1 ", line);
        Assert.DoesNotContain("\\u002B", line);
        Assert.DoesNotContain("\\u2019", line);

        // The line contract: a real carriage return would split one message across two lines.
        Assert.Contains("\\r", line);
        Assert.DoesNotContain("\r", line);

        using var doc = System.Text.Json.JsonDocument.Parse(line);
        Assert.Equal(body, doc.RootElement.GetProperty("text").GetString());
    }
}
