using System.Runtime.InteropServices;
using BlueBubbles.Core.Configuration;
using BlueBubbles.Core.Services;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace BlueBubbles.Windows.Services;

/// <summary>
/// Plays the user's chosen notification sound, tiered to avoid per-notification cost:
///   - OS default → we play nothing (the toast plays it out-of-process).
///   - WAV        → Win32 <c>PlaySound</c> (winmm), fire-and-forget, no managed media stack.
///   - compressed → a single lazily-created <see cref="MediaPlayer"/>, reused across notifications
///                  rather than created/destroyed per fire.
/// The common path (OS default, or a bundled WAV) never touches Media Foundation.
/// </summary>
internal sealed class NotificationSoundService : INotificationSoundService
{
    [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(string? pszSound, IntPtr hmod, uint fdwSound);

    private const uint SND_ASYNC = 0x0001;        // return immediately, play on its own
    private const uint SND_NODEFAULT = 0x0002;    // silence (don't beep) if the file can't be played
    private const uint SND_FILENAME = 0x00020000; // pszSound is a file path

    private static readonly string SoundsDir =
        Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds");

    private readonly AppSettings _settings;
    private readonly object _playerLock = new();
    private MediaPlayer? _player;
    private MediaSource? _source;
    private string? _loadedPath;

    public NotificationSoundService(AppSettings settings) => _settings = settings;

    public bool WillPlayCustomSound =>
        Resolve().Kind != NotificationSoundResolver.SoundKind.OsDefault;

    public void PlayConfiguredSound()
    {
        var resolved = Resolve();
        switch (resolved.Kind)
        {
            case NotificationSoundResolver.SoundKind.Wav:
                try
                {
                    PlaySound(resolved.Path, IntPtr.Zero, SND_FILENAME | SND_ASYNC | SND_NODEFAULT);
                }
                catch { /* sound is best-effort — never let it break the notification */ }
                break;

            case NotificationSoundResolver.SoundKind.Compressed:
                PlayCompressed(resolved.Path!);
                break;

            // OsDefault: nothing to do — the (un-muted) toast plays the system sound.
        }
    }

    private NotificationSoundResolver.ResolvedSound Resolve() =>
        NotificationSoundResolver.Resolve(
            _settings.NotificationSound, _settings.NotificationSoundCustomPath, SoundsDir, File.Exists);

    private void PlayCompressed(string path)
    {
        try
        {
            lock (_playerLock)
            {
                if (_player is null)
                {
                    _player = new MediaPlayer { AudioCategory = MediaPlayerAudioCategory.Alerts };

                    // Opt this player out of the System Media Transport Controls. By default a
                    // MediaPlayer registers as the active media session, which surfaces a tile in
                    // Control Center / the volume flyout (and lets the OS Play button replay the
                    // sound, and media keys target us). AudioCategory.Alerts only affects ducking,
                    // not SMTC participation — disabling the command manager is what removes the tile.
                    _player.CommandManager.IsEnabled = false;
                }

                // Reuse one player AND keep the decoded source warm: a burst of notifications then
                // just rewinds and replays instead of allocating (and leaking) a MediaSource per
                // fire. A rapid second notification restarts playback rather than layering sounds.
                if (!string.Equals(_loadedPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    var previous = _source;
                    _source = MediaSource.CreateFromUri(new Uri(path));
                    _loadedPath = path;
                    _player.Source = _source;
                    previous?.Dispose();
                }
                else
                {
                    _player.PlaybackSession.Position = TimeSpan.Zero;
                }

                _player.Play();
            }
        }
        catch { /* best-effort */ }
    }
}
