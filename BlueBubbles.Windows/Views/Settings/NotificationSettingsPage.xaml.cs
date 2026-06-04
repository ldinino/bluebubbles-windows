using BlueBubbles.Core.Configuration;
using BlueBubbles.Core.Services;
using BlueBubbles.Windows.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace BlueBubbles.Windows.Views.Settings;

public sealed partial class NotificationSettingsPage : Page
{
    public AppSettings Settings { get; }

    private readonly INotificationSoundService _sound;

    // Set while we mutate the ComboBox selection programmatically so SelectionChanged ignores it
    // (init, revert-on-cancel, browse). The last user-committed option, used to revert a cancel.
    private bool _suppressSelectionChanged;
    private SoundOption? _committedOption;

    public NotificationSettingsPage()
    {
        Settings = App.Services.GetRequiredService<AppSettings>();
        _sound = App.Services.GetRequiredService<INotificationSoundService>();
        InitializeComponent();
        SettingsAutoSave.Attach(this, Settings);
        InitializeSoundPicker();
    }

    /// <summary>Friendly label + persisted key (matches <see cref="NotificationSoundResolver"/>).</summary>
    public sealed record SoundOption(string Label, string Key);

    private static List<SoundOption> BuildOptions() =>
    [
        new("Default (Windows sound)", NotificationSoundResolver.DefaultKey),
        new("Twig", "twig.wav"),
        new("Walrus", "walrus.wav"),
        new("Sugarfree", "sugarfree.wav"),
        new("Raspberry", "raspberry.wav"),
        new("MSN", "msn-sound.mp3"),
        new("Skype", "skype.mp3"),
        new("What Was That Noise", "what-was-that-noise.mp3"),
        new("Custom file…", NotificationSoundResolver.CustomKey),
    ];

    private void InitializeSoundPicker()
    {
        var options = BuildOptions();
        var selected = options.FirstOrDefault(o => o.Key == Settings.NotificationSound) ?? options[0];

        _suppressSelectionChanged = true;
        SoundPicker.ItemsSource = options;
        SoundPicker.SelectedItem = selected;
        _suppressSelectionChanged = false;

        _committedOption = selected;
        UpdateCustomUi(selected.Key);
    }

    private async void OnSoundSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionChanged || SoundPicker.SelectedItem is not SoundOption option) return;

        // Choosing "Custom" with no file yet prompts immediately; cancelling reverts the picker.
        if (option.Key == NotificationSoundResolver.CustomKey &&
            string.IsNullOrEmpty(Settings.NotificationSoundCustomPath) &&
            !await PickCustomFileAsync())
        {
            SelectOption(_committedOption);
            return;
        }

        Settings.NotificationSound = option.Key; // raises PropertyChanged → SettingsAutoSave persists
        _committedOption = option;
        UpdateCustomUi(option.Key);
    }

    private async void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        if (!await PickCustomFileAsync()) return;

        Settings.NotificationSound = NotificationSoundResolver.CustomKey;
        SelectOption(SoundOptionForKey(NotificationSoundResolver.CustomKey));
        _committedOption = (SoundOption)SoundPicker.SelectedItem;
        UpdateCustomUi(NotificationSoundResolver.CustomKey);
    }

    private void OnPreviewClick(object sender, RoutedEventArgs e) => _sound.PlayConfiguredSound();

    /// <summary>Shows the native file picker; on success stores the path (auto-saved) and returns true.</summary>
    private async Task<bool> PickCustomFileAsync()
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        picker.SuggestedStartLocation = PickerLocationId.MusicLibrary;
        foreach (var ext in NotificationSoundResolver.AcceptedCustomExtensions)
            picker.FileTypeFilter.Add(ext);

        var file = await picker.PickSingleFileAsync();
        if (file is null) return false;

        Settings.NotificationSoundCustomPath = file.Path; // raises PropertyChanged → persisted
        return true;
    }

    private void UpdateCustomUi(string key)
    {
        var isCustom = key == NotificationSoundResolver.CustomKey;
        CustomFileCard.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
        CustomPathText.Text = string.IsNullOrEmpty(Settings.NotificationSoundCustomPath)
            ? "No file selected"
            : Settings.NotificationSoundCustomPath;

        // Preview only does something when we render the sound ourselves (not the OS default, and a
        // custom pick needs an actual file).
        PreviewButton.IsEnabled = isCustom
            ? !string.IsNullOrEmpty(Settings.NotificationSoundCustomPath)
            : key != NotificationSoundResolver.DefaultKey;
    }

    private SoundOption SoundOptionForKey(string key) =>
        ((IEnumerable<SoundOption>)SoundPicker.ItemsSource).First(o => o.Key == key);

    private void SelectOption(SoundOption? option)
    {
        _suppressSelectionChanged = true;
        SoundPicker.SelectedItem = option;
        _suppressSelectionChanged = false;
    }
}
