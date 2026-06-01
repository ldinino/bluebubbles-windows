using BlueBubbles.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace BlueBubbles.Windows.Views.Settings;

public sealed partial class AboutSettingsPage : Page
{
    private const string GitHubUrl = "https://github.com/ldinino/bluebubbles-windows";
    private const string DocsUrl = "https://bluebubbles.app/install/";

    private readonly IBlueBubblesApiService _api;

    public AboutSettingsPage()
    {
        _api = App.Services.GetRequiredService<IBlueBubblesApiService>();
        InitializeComponent();

        // 3-part semantic version (Major.Minor.Patch) from the assembly. Read from the assembly
        // rather than Package.Current so it works unpackaged; $(Version) in the csproj flows into
        // AssemblyInformationalVersion at build time.
        var versionText = GetAppVersion();
        AppVersionText.Text = $"Version {versionText}";
        AppVersionValue.Text = versionText;

        // Load the logo from the deployed file — ms-appx:/// asset URIs don't resolve unpackaged.
        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (System.IO.File.Exists(iconPath))
            AppLogoImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(iconPath));

        Loaded += async (_, _) => await LoadServerVersionAsync();
    }

    private static string GetAppVersion()
    {
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    private async Task LoadServerVersionAsync()
    {
        try
        {
            var response = await _api.GetServerInfoAsync();
            ServerVersionValue.Text = response.Data?.ServerVersion ?? "Unknown";
        }
        catch
        {
            ServerVersionValue.Text = "Unavailable";
        }
    }

    private async void OnGitHubClick(object sender, RoutedEventArgs e)
        => await Launcher.LaunchUriAsync(new Uri(GitHubUrl));

    private async void OnDocsClick(object sender, RoutedEventArgs e)
        => await Launcher.LaunchUriAsync(new Uri(DocsUrl));
}
