using BlueBubbles.Core.Configuration;
using BlueBubbles.Windows.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace BlueBubbles.Windows.Views.Settings;

public sealed partial class GeneralSettingsPage : Page
{
    private readonly StartupTaskService _startupTask;
    private bool _initializing;

    public AppSettings Settings { get; }

    public GeneralSettingsPage()
    {
        Settings = App.Services.GetRequiredService<AppSettings>();
        _startupTask = App.Services.GetRequiredService<StartupTaskService>();
        InitializeComponent();
        SettingsAutoSave.Attach(this, Settings);

        // Reflect the actual registry state (it may have changed outside the app).
        Loaded += (_, _) =>
        {
            _initializing = true;
            LaunchAtStartupToggle.IsOn = _startupTask.IsEnabled();
            _initializing = false;
        };
    }

    private void OnLaunchAtStartupToggled(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_initializing) return;

        var requested = LaunchAtStartupToggle.IsOn;
        var actual = _startupTask.SetEnabled(requested, Settings.LaunchAtStartupMinimized);

        StartupBlockedBar.IsOpen = requested != actual;
        if (LaunchAtStartupToggle.IsOn != actual)
            LaunchAtStartupToggle.IsOn = actual;

        Settings.LaunchAtStartup = actual;
        App.Services.GetRequiredService<Core.Services.ISettingsService>().Save();
    }

    private void OnStartMinimizedToggled(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_initializing) return;

        // Re-register so the Run command reflects the new minimized preference immediately.
        if (Settings.LaunchAtStartup)
            _startupTask.SetEnabled(true, Settings.LaunchAtStartupMinimized);
    }
}
