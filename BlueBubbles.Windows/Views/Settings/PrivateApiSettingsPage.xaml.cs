using BlueBubbles.Core.Configuration;
using BlueBubbles.Windows.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace BlueBubbles.Windows.Views.Settings;

public sealed partial class PrivateApiSettingsPage : Page
{
    public AppSettings Settings { get; }

    public PrivateApiSettingsPage()
    {
        Settings = App.Services.GetRequiredService<AppSettings>();
        InitializeComponent();
        SettingsAutoSave.Attach(this, Settings);
        UpdatePrivateApiStatus();
    }

    // Reflects the server's reported Private API support instead of a hardcoded "enabled" banner.
    private void UpdatePrivateApiStatus()
    {
        if (Settings.ServerPrivateAPI == true)
        {
            StatusInfoBar.Severity = InfoBarSeverity.Success;
            StatusInfoBar.Title = "Private API enabled";
            StatusInfoBar.Message = "Your server reports Private API support, so reactions, replies, "
                + "edits, typing indicators and read receipts are available.";
        }
        else
        {
            StatusInfoBar.Severity = InfoBarSeverity.Warning;
            StatusInfoBar.Title = "Private API not detected";
            StatusInfoBar.Message = "Your server hasn't reported Private API support. These features may "
                + "be unavailable until the Private API is enabled on the BlueBubbles server.";
        }
    }
}
