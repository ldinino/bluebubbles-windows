using System.Globalization;

namespace BlueBubbles.Core.Utils;

/// <summary>
/// Minimal 3-part version with numeric ordering. Exists because release tags (<c>vX.Y.Z</c>) are
/// compared against <see cref="Services.AppInfo.Version"/> (<c>X.Y.Z</c>), and an ordinal string
/// compare ranks "0.9.0" above "0.10.0".
/// </summary>
public readonly record struct SemanticVersion(int Major, int Minor, int Patch)
    : IComparable<SemanticVersion>
{
    /// <summary>
    /// Parses <c>X.Y.Z</c> or <c>vX.Y.Z</c>. Returns false for anything else rather than throwing —
    /// a malformed upstream tag must never break the launch path.
    /// </summary>
    public static bool TryParse(string? text, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim();
        if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[1..];

        var parts = trimmed.Split('.');
        if (parts.Length != 3)
            return false;

        if (!TryParsePart(parts[0], out var major) ||
            !TryParsePart(parts[1], out var minor) ||
            !TryParsePart(parts[2], out var patch))
            return false;

        version = new SemanticVersion(major, minor, patch);
        return true;
    }

    // NumberStyles.None rejects signs, whitespace and thousands separators, so "-1" and " 1" fail.
    private static bool TryParsePart(string part, out int value) =>
        int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    public int CompareTo(SemanticVersion other)
    {
        var major = Major.CompareTo(other.Major);
        if (major != 0)
            return major;

        var minor = Minor.CompareTo(other.Minor);
        return minor != 0 ? minor : Patch.CompareTo(other.Patch);
    }

    public static bool operator <(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) >= 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}
