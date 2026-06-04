using BlueBubbles.Core.Services;

namespace BlueBubbles.Windows.Tests;

public class NotificationSoundResolverTests
{
    private const string SoundsDir = @"C:\app\Assets\Sounds";

    // A file-existence probe that treats the given set of paths as present (case-insensitive, since
    // Windows paths are).
    private static Func<string, bool> Existing(params string[] paths)
    {
        var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        return set.Contains;
    }

    [Theory]
    [InlineData("default")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Resolve_DefaultOrBlank_ReturnsOsDefault(string? key)
    {
        var result = NotificationSoundResolver.Resolve(key, null, SoundsDir, _ => true);

        Assert.Equal(NotificationSoundResolver.SoundKind.OsDefault, result.Kind);
        Assert.Null(result.Path);
    }

    [Fact]
    public void Resolve_BundledWav_ReturnsWavWithFullPath()
    {
        var expected = Path.Combine(SoundsDir, "twig.wav");

        var result = NotificationSoundResolver.Resolve("twig.wav", null, SoundsDir, Existing(expected));

        Assert.Equal(NotificationSoundResolver.SoundKind.Wav, result.Kind);
        Assert.Equal(expected, result.Path);
    }

    [Fact]
    public void Resolve_BundledMp3_ReturnsCompressed()
    {
        var expected = Path.Combine(SoundsDir, "msn-sound.mp3");

        var result = NotificationSoundResolver.Resolve("msn-sound.mp3", null, SoundsDir, Existing(expected));

        Assert.Equal(NotificationSoundResolver.SoundKind.Compressed, result.Kind);
        Assert.Equal(expected, result.Path);
    }

    [Fact]
    public void Resolve_BundledButFileMissing_FallsBackToOsDefault()
    {
        // Key is a known bundled sound, but the file isn't on disk (e.g. a partial install).
        var result = NotificationSoundResolver.Resolve("walrus.wav", null, SoundsDir, _ => false);

        Assert.Equal(NotificationSoundResolver.SoundKind.OsDefault, result.Kind);
        Assert.Null(result.Path);
    }

    [Fact]
    public void Resolve_UnknownKey_ReturnsOsDefault()
    {
        var result = NotificationSoundResolver.Resolve("not-a-real-sound.wav", null, SoundsDir, _ => true);

        Assert.Equal(NotificationSoundResolver.SoundKind.OsDefault, result.Kind);
        Assert.Null(result.Path);
    }

    [Fact]
    public void Resolve_CustomWav_ReturnsWav()
    {
        const string custom = @"D:\sounds\my alert.wav";

        var result = NotificationSoundResolver.Resolve("custom", custom, SoundsDir, Existing(custom));

        Assert.Equal(NotificationSoundResolver.SoundKind.Wav, result.Kind);
        Assert.Equal(custom, result.Path);
    }

    [Fact]
    public void Resolve_CustomCompressed_ReturnsCompressed()
    {
        const string custom = @"D:\sounds\ringtone.m4a";

        var result = NotificationSoundResolver.Resolve("custom", custom, SoundsDir, Existing(custom));

        Assert.Equal(NotificationSoundResolver.SoundKind.Compressed, result.Kind);
        Assert.Equal(custom, result.Path);
    }

    [Fact]
    public void Resolve_CustomExtension_IsCaseInsensitive()
    {
        const string custom = @"D:\sounds\ALERT.WAV";

        var result = NotificationSoundResolver.Resolve("custom", custom, SoundsDir, Existing(custom));

        Assert.Equal(NotificationSoundResolver.SoundKind.Wav, result.Kind);
    }

    [Fact]
    public void Resolve_CustomMissingFile_FallsBackToOsDefault()
    {
        var result = NotificationSoundResolver.Resolve("custom", @"D:\gone.mp3", SoundsDir, _ => false);

        Assert.Equal(NotificationSoundResolver.SoundKind.OsDefault, result.Kind);
        Assert.Null(result.Path);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Resolve_CustomWithNoPath_ReturnsOsDefault(string? customPath)
    {
        var result = NotificationSoundResolver.Resolve("custom", customPath, SoundsDir, _ => true);

        Assert.Equal(NotificationSoundResolver.SoundKind.OsDefault, result.Kind);
    }

    [Fact]
    public void BundledSounds_AreTheSevenShippedFiles()
    {
        Assert.Equal(
            new[]
            {
                "twig.wav", "walrus.wav", "sugarfree.wav", "raspberry.wav",
                "msn-sound.mp3", "skype.mp3", "what-was-that-noise.mp3"
            },
            NotificationSoundResolver.BundledSounds);
    }

    [Fact]
    public void AcceptedCustomExtensions_ExcludeOgg()
    {
        Assert.DoesNotContain(".ogg", NotificationSoundResolver.AcceptedCustomExtensions);
        Assert.Contains(".wav", NotificationSoundResolver.AcceptedCustomExtensions);
        Assert.Contains(".mp3", NotificationSoundResolver.AcceptedCustomExtensions);
        Assert.Contains(".m4a", NotificationSoundResolver.AcceptedCustomExtensions);
        Assert.Contains(".flac", NotificationSoundResolver.AcceptedCustomExtensions);
    }
}
