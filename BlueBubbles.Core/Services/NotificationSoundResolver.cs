namespace BlueBubbles.Core.Services;

/// <summary>
/// Classifies the user's notification-sound preference into a concrete playback plan, independent of
/// the actual Win32/Media Foundation playback (which lives in the Windows layer). Kept in Core so the
/// mapping — preference key → file path, and WAV-vs-compressed — is unit-testable without WinRT.
///
/// Preference values (<c>AppSettings.NotificationSound</c>):
///   - <c>"default"</c> / empty → the OS notification sound, played by the toast itself.
///   - a bundled filename (one of <see cref="BundledSounds"/>) → resolved under the app's Assets\Sounds.
///   - <c>"custom"</c> → the user-picked file at <c>AppSettings.NotificationSoundCustomPath</c>.
/// </summary>
public static class NotificationSoundResolver
{
    public const string DefaultKey = "default";
    public const string CustomKey = "custom";

    /// <summary>Bundled sound files shipped under Assets\Sounds, in settings display order: the four
    /// original Flutter WAVs first, then the three for-charm MP3s the user added.</summary>
    public static readonly IReadOnlyList<string> BundledSounds =
    [
        "twig.wav", "walrus.wav", "sugarfree.wav", "raspberry.wav",
        "msn-sound.mp3", "skype.mp3", "what-was-that-noise.mp3"
    ];

    /// <summary>Custom-sound file extensions we accept. OGG is intentionally excluded — its
    /// Vorbis/Opus codecs aren't guaranteed present on a clean machine and would silently fail.</summary>
    public static readonly IReadOnlyList<string> AcceptedCustomExtensions =
        [".wav", ".mp3", ".m4a", ".flac", ".wma"];

    public enum SoundKind
    {
        /// <summary>Let the toast play the OS notification sound; we play nothing ourselves.</summary>
        OsDefault,
        /// <summary>An uncompressed WAV — play via the featherweight Win32 PlaySound path.</summary>
        Wav,
        /// <summary>A compressed format (mp3/m4a/flac/…) — play via the reused singleton MediaPlayer.</summary>
        Compressed
    }

    public readonly record struct ResolvedSound(SoundKind Kind, string? Path);

    /// <summary>
    /// Resolves the preference into a playback plan. Falls back to <see cref="SoundKind.OsDefault"/>
    /// whenever the chosen file can't be located, so a stale/missing pick degrades to the OS sound
    /// rather than silently dropping the notification's audio entirely.
    /// </summary>
    /// <param name="key">The <c>AppSettings.NotificationSound</c> value.</param>
    /// <param name="customPath">The <c>AppSettings.NotificationSoundCustomPath</c> value (used for "custom").</param>
    /// <param name="bundledSoundsDir">Absolute path to the bundled <c>Assets\Sounds</c> directory.</param>
    /// <param name="fileExists">File-existence probe (injected so the logic is testable without disk).</param>
    public static ResolvedSound Resolve(
        string? key, string? customPath, string bundledSoundsDir, Func<string, bool> fileExists)
    {
        if (string.IsNullOrWhiteSpace(key) || key == DefaultKey)
            return new ResolvedSound(SoundKind.OsDefault, null);

        string? path;
        if (key == CustomKey)
            path = string.IsNullOrWhiteSpace(customPath) ? null : customPath;
        else if (BundledSounds.Contains(key))
            path = Path.Combine(bundledSoundsDir, key);
        else
            path = null; // unrecognized key → OS default

        if (path is null || !fileExists(path))
            return new ResolvedSound(SoundKind.OsDefault, null);

        var kind = path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
            ? SoundKind.Wav
            : SoundKind.Compressed;
        return new ResolvedSound(kind, path);
    }
}
