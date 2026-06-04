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
                // Reuse one player; assigning a new Source releases the previous one, and a rapid
                // second notification simply restarts playback instead of layering sounds.
                _player ??= new MediaPlayer { AudioCategory = MediaPlayerAudioCategory.Alerts };
                _player.Source = MediaSource.CreateFromUri(new Uri(path));
                _player.Play();
            }
        }
        catch { /* best-effort */ }
    }
}
