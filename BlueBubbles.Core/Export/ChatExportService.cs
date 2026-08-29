using BlueBubbles.Core.Data;
using BlueBubbles.Core.Data.Entities;
using BlueBubbles.Core.Services;
using BlueBubbles.Core.Utils;
using Microsoft.EntityFrameworkCore;

namespace BlueBubbles.Core.Export;

public sealed record ChatExportOptions(
    bool WriteTranscript = true,
    bool CopyAttachments = true);

public sealed record ChatExportProgress(
    int Completed,
    int Total,
    string CurrentChatTitle);

public sealed record ChatExportResult(
    int ChatCount,
    int MessageCount,
    int AttachmentsCopied,
    int AttachmentsMissing,
    int IncompleteChatCount,
    string DestinationFolder);

public interface IChatExportService
{
    Task<ChatExportResult> ExportAsync(
        IReadOnlyList<int> chatIds,
        string destinationFolder,
        ChatExportOptions options,
        IProgress<ChatExportProgress>? progress = null,
        CancellationToken ct = default);
}

/// <summary>
/// Writes selected conversations to disk. Reads the local cache only - an export never pulls from
/// the server, so what lands on disk is exactly what this PC already had, and
/// <see cref="ChatExportCoverage"/> is what tells the user how much that is.
/// </summary>
public sealed class ChatExportService : IChatExportService
{
    private readonly IDbContextFactory<BlueBubblesDbContext> _dbFactory;
    private readonly ICachedAttachmentLookup _attachmentCache;

    public ChatExportService(
        IDbContextFactory<BlueBubblesDbContext> dbFactory,
        ICachedAttachmentLookup attachmentCache)
    {
        _dbFactory = dbFactory;
        _attachmentCache = attachmentCache;
    }

    public async Task<ChatExportResult> ExportAsync(
        IReadOnlyList<int> chatIds,
        string destinationFolder,
        ChatExportOptions options,
        IProgress<ChatExportProgress>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(destinationFolder);
        var attachmentDir = Path.Combine(destinationFolder, "attachments");
        if (options.CopyAttachments) Directory.CreateDirectory(attachmentDir);

        var offset = DateTimeOffset.Now.Offset;
        var now = DateTimeOffset.Now;

        var entries = new List<ExportManifestEntry>();
        var totalMessages = 0;
        var copied = 0;
        var missing = 0;
        var completed = 0;

        foreach (var chatId in chatIds)
        {
            ct.ThrowIfCancellationRequested();

            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            var chat = await db.Chats
                .Include(c => c.ChatParticipants).ThenInclude(cp => cp.Handle)
                .FirstOrDefaultAsync(c => c.Id == chatId, ct);
            if (chat is null) continue;

            // Deliberately NOT MessagesService.LoadMessagesAsync: that filters out every row with
            // an AssociatedMessageGuid, so tapbacks would never arrive to be folded onto parents.
            var messages = await db.Messages
                .Include(m => m.Handle)
                .Include(m => m.Attachments)
                .Where(m => m.ChatId == chatId && m.DateDeleted == null)
                .OrderBy(m => m.DateCreated)
                .ThenBy(m => m.OriginalRowId ?? int.MaxValue)
                .ToListAsync(ct);

            var participants = chat.ChatParticipants
                .Select(cp => cp.Handle)
                .Where(h => h is not null)
                .Select(h => h!)
                .ToList();

            var copiedForChat = new Dictionary<string, string>(StringComparer.Ordinal);

            var export = ChatExportBuilder.Build(
                chat.Guid,
                chat.DisplayName,
                participants,
                chat.OldestSyncedMessageDate,
                messages,
                offset,
                now,
                resolveSender: null,
                resolveArchivePath: a =>
                {
                    if (!options.CopyAttachments) return null;
                    if (copiedForChat.TryGetValue(a.Guid, out var already)) return already;

                    var source = _attachmentCache.GetCachedPath(a.Guid);
                    if (source is null) return null;

                    try
                    {
                        var name = BuildAttachmentFileName(a, source);
                        File.Copy(source, Path.Combine(attachmentDir, name), overwrite: true);
                        var rel = $"attachments/{name}";
                        copiedForChat[a.Guid] = rel;
                        return rel;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        AppLog.Warn(LogCategory.App,
                            $"Export could not copy attachment {a.Guid}: {ex.GetType().Name}: {ex.Message}");
                        return null;
                    }
                });

            var baseName = ExportFileNames.ForChat(chat.Guid, export.Title);
            var jsonlName = $"{baseName}.jsonl";
            string? transcriptName = options.WriteTranscript ? $"{baseName}.txt" : null;

            await File.WriteAllLinesAsync(
                Path.Combine(destinationFolder, jsonlName),
                ChatExportSerializer.ToJsonl(export), ct);

            if (transcriptName is not null)
            {
                await File.WriteAllLinesAsync(
                    Path.Combine(destinationFolder, transcriptName),
                    ChatExportTranscript.Render(export), ct);
            }

            entries.Add(ChatExportSerializer.ToManifestEntry(export, jsonlName, transcriptName));

            totalMessages += export.Messages.Count;
            var atts = export.Messages.SelectMany(m => m.Attachments).ToList();
            copied += atts.Count(a => a.IsCached);
            missing += atts.Count(a => !a.IsCached);

            completed++;
            progress?.Report(new ChatExportProgress(completed, chatIds.Count, export.Title));
        }

        await File.WriteAllTextAsync(
            Path.Combine(destinationFolder, "manifest.json"),
            ChatExportSerializer.ToManifestJson(entries, now), ct);

        return new ChatExportResult(
            entries.Count, totalMessages, copied, missing,
            entries.Count(e => !e.ReachesBeginning), destinationFolder);
    }

    /// <summary>Prefixes the attachment GUID so two files that share a transfer name cannot
    /// overwrite each other in the flat attachments folder.</summary>
    private static string BuildAttachmentFileName(AttachmentEntity a, string sourcePath)
    {
        var name = a.TransferName;
        if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileName(sourcePath);

        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string((name ?? "file").Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        if (safe.Length == 0 || safe is "." or "..") safe = "file";

        return $"{ExportFileNames.ShortHash(a.Guid)}-{safe}";
    }
}
