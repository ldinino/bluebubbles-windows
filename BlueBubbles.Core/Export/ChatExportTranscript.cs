using System.Globalization;
using System.Text;
using BlueBubbles.Core.Utils;

namespace BlueBubbles.Core.Export;

/// <summary>
/// Renders a <see cref="ChatExport"/> as a human-readable plain-text transcript. Plain text, not
/// HTML: embedded media would mean copying assets and rewriting relative paths, and the point of
/// this file is that it stays readable with no application at all.
/// </summary>
public static class ChatExportTranscript
{
    public static IReadOnlyList<string> Render(ChatExport export)
    {
        var lines = new List<string>
        {
            export.Title,
            new('=', Math.Max(export.Title.Length, 3)),
            string.Empty,
        };

        if (export.Participants.Count > 0)
            lines.Add($"Participants: {string.Join(", ", export.Participants)}");

        lines.Add($"Conversation ID: {export.ChatGuid}");
        lines.Add(string.Empty);

        // The coverage statement goes at the top, before the messages, so a reader cannot get to
        // the transcript without first learning how much of it is missing.
        lines.Add("COVERAGE");
        lines.Add("--------");
        foreach (var wrapped in Wrap(export.Coverage.Statement, 92)) lines.Add(wrapped);
        lines.Add(string.Empty);
        lines.Add("Timestamps are ISO 8601 with UTC offset.");
        lines.Add(string.Empty);

        string? currentDay = null;
        foreach (var m in export.Messages)
        {
            var day = DayOf(m.Date);
            if (day is not null && day != currentDay)
            {
                currentDay = day;
                lines.Add(string.Empty);
                lines.Add($"--- {day} ---");
                lines.Add(string.Empty);
            }

            foreach (var line in RenderMessage(m)) lines.Add(line);
        }

        return lines;
    }

    private static IEnumerable<string> RenderMessage(ExportedMessage m)
    {
        var stamp = m.Date ?? "(no timestamp)";

        // A system event is not speech. It is never rendered as "Name: text".
        if (m.Kind == ExportedMessageKind.SystemEvent)
        {
            yield return $"[{stamp}] * {m.EventDescription}";
            yield break;
        }

        var header = $"[{stamp}] {m.Sender}:";

        if (!string.IsNullOrWhiteSpace(m.Subject))
            yield return $"{header} (subject) {m.Subject}";

        var text = m.Text?.Replace(MessagePreviewObjectChar, string.Empty).Trim();
        var wroteBody = false;

        if (!string.IsNullOrEmpty(text))
        {
            var parts = text.Replace("\r\n", "\n").Split('\n');
            yield return $"{header} {parts[0]}";
            for (var i = 1; i < parts.Length; i++) yield return $"    {parts[i]}";
            wroteBody = true;
        }

        foreach (var a in m.Attachments)
        {
            var label = string.IsNullOrWhiteSpace(a.FileName) ? "(unnamed file)" : a.FileName;
            var detail = $"{label}, {a.MimeType ?? "unknown type"}, {a.TotalBytes} bytes";
            yield return a.IsCached
                ? (wroteBody ? $"    [Attachment: {detail} -> {a.ArchivePath}]"
                             : $"{header} [Attachment: {detail} -> {a.ArchivePath}]")
                : (wroteBody ? $"    [Attachment NOT INCLUDED: {detail} - was never downloaded to this PC]"
                             : $"{header} [Attachment NOT INCLUDED: {detail} - was never downloaded to this PC]");
            wroteBody = true;
        }

        // Never emit a bare "Name:" with nothing after it - if there is no text and no
        // attachment row, say so explicitly.
        if (!wroteBody)
            yield return $"{header} [no content]";

        if (m.Reactions.Count > 0)
        {
            var summary = string.Join(", ", m.Reactions.Select(r =>
                $"{ReactionTypes.ToVerb(r.Type)} by {r.Sender}"));
            yield return $"    ({summary})";
        }
    }

    private const string MessagePreviewObjectChar = "\uFFFC";

    private static string? DayOf(string? iso)
    {
        if (string.IsNullOrEmpty(iso)) return null;
        try
        {
            return ExportTimestamp.ParseIso(iso).ToString("dddd, dd MMMM yyyy", CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static IEnumerable<string> Wrap(string text, int width)
    {
        var sb = new StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (sb.Length > 0 && sb.Length + 1 + word.Length > width)
            {
                yield return sb.ToString();
                sb.Clear();
            }
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(word);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }
}
