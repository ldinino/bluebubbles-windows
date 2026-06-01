using BlueBubbles.Core.Configuration;
using BlueBubbles.Windows.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace BlueBubbles.Windows.Views.Settings;

public sealed partial class NotificationSettingsPage : Page
{
    public AppSettings Settings { get; }

    public NotificationSettingsPage()
    {
        Settings = App.Services.GetRequiredService<AppSettings>();
        InitializeComponent();
        SettingsAutoSave.Attach(this, Settings);
    }
}
