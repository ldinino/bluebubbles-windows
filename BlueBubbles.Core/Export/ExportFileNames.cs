using System.Security.Cryptography;
using System.Text;

namespace BlueBubbles.Core.Export;

/// <summary>
/// Deterministic, filesystem-safe names for exported files. A re-export of the same conversation
/// must land on the same filename so an archive diffs cleanly instead of accumulating duplicates,
/// which rules out anything derived from the export time. The short GUID hash keeps two chats
/// with the same display name apart.
/// </summary>
public static class ExportFileNames
{
    private const int MaxSlugLength = 48;
    public const string Fallback = "conversation";

    /// <summary>Base name for a conversation's files: <c>slug-abcd1234</c>.</summary>
    public static string ForChat(string chatGuid, string? title)
        => $"{Slug(title)}-{ShortHash(chatGuid)}";

    /// <summary>Lowercase, hyphen-separated, ASCII-safe slug. Non-alphanumeric runs collapse to a
    /// single hyphen so emoji and punctuation in a group name cannot produce an illegal path.</summary>
    public static string Slug(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Fallback;

        var sb = new StringBuilder(value.Length);
        var pendingSeparator = false;
        foreach (var ch in value)
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                if (pendingSeparator && sb.Length > 0) sb.Append('-');
                pendingSeparator = false;
                sb.Append(ch);
            }
            else if (ch is >= 'A' and <= 'Z')
            {
                if (pendingSeparator && sb.Length > 0) sb.Append('-');
                pendingSeparator = false;
                sb.Append(char.ToLowerInvariant(ch));
            }
            else
            {
                pendingSeparator = true;
            }

            if (sb.Length >= MaxSlugLength) break;
        }

        return sb.Length == 0 ? Fallback : sb.ToString();
    }

    /// <summary>First 8 hex characters of SHA-256 over the chat GUID. Stable across runs and
    /// machines, unlike <see cref="string.GetHashCode()"/>.</summary>
    public static string ShortHash(string chatGuid)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(chatGuid ?? string.Empty));
        return Convert.ToHexString(bytes, 0, 4).ToLowerInvariant();
    }
}
