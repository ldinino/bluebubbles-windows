using BlueBubbles.Core.Data.Entities;
using BlueBubbles.Core.Utils;

namespace BlueBubbles.Core.Export;

/// <summary>
/// Turns cached chat rows into export records. Pure: no database, no file system, no network.
/// The caller supplies the sender-name and archive-path lookups, so the whole shape of an export
/// is unit-testable without WinUI or a server.
/// </summary>
public static class ChatExportBuilder
{
    public const string SelfSender = "Me";

    public static ChatExport Build(
        string chatGuid,
        string? displayName,
        IReadOnlyList<HandleEntity> participants,
        long? oldestSyncedMessageDate,
        IReadOnlyList<MessageEntity> messages,
        TimeSpan offset,
        DateTimeOffset now,
        Func<string, string>? resolveSender = null,
        Func<AttachmentEntity, string?>? resolveArchivePath = null)
    {
        var names = participants.Select(p => ResolveName(p.Address, resolveSender)).ToList();
        var title = !string.IsNullOrWhiteSpace(displayName)
            ? displayName!
            : names.Count > 0 ? string.Join(", ", names) : chatGuid;

        var live = messages.Where(m => m.DateDeleted is null).ToList();

        // Tapbacks are separate message rows pointing at a parent. Key on the PRESENCE of
        // AssociatedMessageGuid rather than on ReactionTypes.IsReaction: the real cache also
        // carries numeric types ("2006", "4000") and "sticker", which IsReaction does not
        // recognise but which are equally not speech.
        var reactionRows = live.Where(m => !string.IsNullOrEmpty(m.AssociatedMessageGuid)).ToList();
        var primary = live.Where(m => string.IsNullOrEmpty(m.AssociatedMessageGuid))
            .OrderBy(m => m.DateCreated ?? 0)
            .ThenBy(m => m.OriginalRowId ?? int.MaxValue)
            .ToList();

        var reactionsByParent = FoldReactions(reactionRows, offset, resolveSender);

        var records = new List<ExportedMessage>(primary.Count);
        foreach (var m in primary)
        {
            var isSystemEvent = SystemEventDescriber.IsSystemEvent(m);

            // The HasAttachments flag is NOT usable: measured against a real cache, all 661
            // messages that owned attachment rows had HasAttachments = 0. Trusting it would drop
            // every attachment silently, which is exactly the failure a placeholder is for.
            var attachments = (m.Attachments ?? [])
                .OrderBy(a => a.OriginalRowId ?? int.MaxValue)
                .Select(a =>
                {
                    var archivePath = resolveArchivePath?.Invoke(a);
                    return new ExportedAttachment(
                        a.Guid,
                        a.TransferName,
                        a.MimeType,
                        a.TotalBytes,
                        archivePath is not null,
                        archivePath);
                })
                .ToList();

            reactionsByParent.TryGetValue(m.Guid, out var reactions);

            records.Add(new ExportedMessage(
                Guid: m.Guid,
                Kind: isSystemEvent ? ExportedMessageKind.SystemEvent : ExportedMessageKind.Message,
                Text: m.Text,
                Subject: m.Subject,
                IsFromMe: m.IsFromMe,
                Sender: m.IsFromMe ? SelfSender : ResolveName(m.Handle?.Address, resolveSender),
                Date: ExportTimestamp.ToIso(m.DateCreated, offset),
                DateEdited: ExportTimestamp.ToIso(m.DateEdited, offset),
                WasEdited: m.DateEdited is > 0,
                ThreadOriginatorGuid: m.ThreadOriginatorGuid,
                ItemType: m.ItemType,
                GroupActionType: m.GroupActionType,
                EventDescription: isSystemEvent ? DescribeEvent(m, resolveSender) : null,
                Attachments: attachments,
                Reactions: reactions ?? []));
        }

        var dates = primary.Select(m => m.DateCreated).Where(d => d is > 0).Select(d => d!.Value).ToList();
        var coverage = ChatExportCoverage.Describe(
            oldestSyncedMessageDate,
            dates.Count > 0 ? dates.Min() : null,
            dates.Count > 0 ? dates.Max() : null,
            records.Count,
            offset,
            now);

        return new ChatExport(chatGuid, title, names, coverage, records);
    }

    /// <summary>Groups tapbacks by parent GUID and nets removals ("-love") against the matching
    /// add, so the export records the reactions that were actually still on the message.</summary>
    private static Dictionary<string, List<ExportedReaction>> FoldReactions(
        IReadOnlyList<MessageEntity> reactionRows,
        TimeSpan offset,
        Func<string, string>? resolveSender)
    {
        var result = new Dictionary<string, List<ExportedReaction>>(StringComparer.Ordinal);

        foreach (var group in reactionRows.GroupBy(r =>
                     ReactionTypes.NormalizeAssociatedGuid(r.AssociatedMessageGuid) ?? string.Empty))
        {
            if (group.Key.Length == 0) continue;

            var active = new List<ExportedReaction>();
            foreach (var r in group.OrderBy(r => r.DateCreated ?? 0)
                         .ThenBy(r => r.OriginalRowId ?? int.MaxValue))
            {
                var rawType = r.AssociatedMessageType ?? string.Empty;
                if (rawType.Length == 0) continue;

                var sender = r.IsFromMe ? SelfSender : ResolveName(r.Handle?.Address, resolveSender);
                var baseType = ReactionTypes.BaseType(rawType);

                if (ReactionTypes.IsRemoval(rawType))
                {
                    var idx = active.FindLastIndex(a =>
                        a.IsFromMe == r.IsFromMe &&
                        string.Equals(a.Type, baseType, StringComparison.Ordinal));
                    if (idx >= 0) active.RemoveAt(idx);
                    continue;
                }

                active.Add(new ExportedReaction(
                    baseType, sender, r.IsFromMe,
                    ExportTimestamp.ToIso(r.DateCreated, offset), false));
            }

            if (active.Count > 0) result[group.Key] = active;
        }

        return result;
    }

    /// <summary>Plain-language description of a group/system event. The transcript and the in-app
    /// timeline share one implementation so they can never drift.</summary>
    public static string DescribeEvent(MessageEntity m, Func<string, string>? resolveSender = null)
        => SystemEventDescriber.Describe(m, resolveSender, SelfSender);

    private static string ResolveName(string? address, Func<string, string>? resolveSender)
        => SystemEventDescriber.ResolveName(address, resolveSender);
}
