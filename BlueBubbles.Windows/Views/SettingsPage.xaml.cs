using BlueBubbles.Core.Configuration;
using BlueBubbles.Windows.Views.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BlueBubbles.Windows.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();

        // Private API category is gated on the server reporting Private API support (spec 8.8).
        var settings = App.Services.GetRequiredService<AppSettings>();
        PrivateApiItem.Visibility = settings.ServerPrivateAPI == true
            ? Visibility.Visible : Visibility.Collapsed;

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        SettingsNav.SelectedItem = SettingsNav.MenuItems[0];
    }

    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item) return;

        var pageType = item.Tag switch
        {
            "Connection" => typeof(ConnectionSettingsPage),
            "General" => typeof(GeneralSettingsPage),
            "Notifications" => typeof(NotificationSettingsPage),
            "Appearance" => typeof(AppearanceSettingsPage),
            "Messaging" => typeof(MessagingSettingsPage),
            "PrivateApi" => typeof(PrivateApiSettingsPage),
            "ServerManagement" => typeof(ServerManagementSettingsPage),
            "Backup" => typeof(BackupSettingsPage),
            "About" => typeof(AboutSettingsPage),
            _ => null,
        };

        if (pageType is not null && ContentFrame.CurrentSourcePageType != pageType)
            ContentFrame.Navigate(pageType, null, args.RecommendedNavigationTransitionInfo);
    }

    private void OnBackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
    {
        var rootFrame = App.MainWindow.RootNavigationFrame;
        if (rootFrame.CanGoBack)
            rootFrame.GoBack();
    }
}
