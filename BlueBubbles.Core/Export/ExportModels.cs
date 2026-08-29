namespace BlueBubbles.Core.Export;

/// <summary>What a row in an exported conversation actually is. Group/system events are
/// persisted as messages but are not speech, so they are labelled rather than mixed in.</summary>
public enum ExportedMessageKind
{
    Message,
    SystemEvent
}

/// <summary>A tapback folded onto its parent message. Tapbacks are separate rows in the
/// message table; exporting them as their own records fills a transcript with
/// <c>Liked "..."</c> noise, so they are attached to the message they point at.</summary>
public sealed record ExportedReaction(
    string Type,
    string Sender,
    bool IsFromMe,
    string? Date,
    bool IsRemoval);

/// <summary>An attachment referenced by the export. Files are never fetched from the server
/// during an export: a locally cached file is copied into <c>attachments/</c> and anything
/// else is recorded with <see cref="IsCached"/> false so the gap is visible.</summary>
public sealed record ExportedAttachment(
    string Guid,
    string? FileName,
    string? MimeType,
    long TotalBytes,
    bool IsCached,
    string? ArchivePath);

/// <summary>One JSONL line of an exported conversation.</summary>
public sealed record ExportedMessage(
    string Guid,
    ExportedMessageKind Kind,
    string? Text,
    string? Subject,
    bool IsFromMe,
    string Sender,
    string? Date,
    string? DateEdited,
    bool WasEdited,
    string? ThreadOriginatorGuid,
    int ItemType,
    int GroupActionType,
    string? EventDescription,
    IReadOnlyList<ExportedAttachment> Attachments,
    IReadOnlyList<ExportedReaction> Reactions);

/// <summary>A fully built conversation export: the records plus the honest statement of how
/// much of the conversation they actually represent.</summary>
public sealed record ChatExport(
    string ChatGuid,
    string Title,
    IReadOnlyList<string> Participants,
    ExportCoverage Coverage,
    IReadOnlyList<ExportedMessage> Messages);
