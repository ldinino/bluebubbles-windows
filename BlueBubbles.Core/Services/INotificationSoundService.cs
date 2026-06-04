namespace BlueBubbles.Core.Services;

public interface INotificationSoundService
{
    /// <summary>True when the user has chosen a non-default sound that we render ourselves, so the
    /// toast must be muted to avoid playing two sounds at once.</summary>
    bool WillPlayCustomSound { get; }

    /// <summary>Plays the configured notification sound. No-op when the preference is the OS default
    /// (the toast plays that out-of-process) or the chosen file can't be resolved. Fire-and-forget
    /// and safe to call from any thread.</summary>
    void PlayConfiguredSound();
}
