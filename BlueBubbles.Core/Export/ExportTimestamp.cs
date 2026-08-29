namespace BlueBubbles.Core.Export;

/// <summary>
/// Unix-milliseconds to ISO 8601 <b>with offset</b>. The cache stores a bare epoch; writing a
/// local wall-clock time into an archive makes it ambiguous a year later (and unreadable across
/// DST boundaries), so every exported timestamp carries its offset.
/// </summary>
public static class ExportTimestamp
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss.fffzzz";

    public static string? ToIso(long? unixMilliseconds, TimeSpan offset)
        => unixMilliseconds is null ? null : ToIso(unixMilliseconds.Value, offset);

    public static string ToIso(long unixMilliseconds, TimeSpan offset)
        => DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds)
            .ToOffset(offset)
            .ToString(Format, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Parses a value produced by <see cref="ToIso(long, TimeSpan)"/> back to the exact
    /// instant it came from. Used to prove the round-trip is lossless.</summary>
    public static DateTimeOffset ParseIso(string iso)
        => DateTimeOffset.ParseExact(iso, Format, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None);
}
