using BlueBubbles.Core.Configuration;
using BlueBubbles.Windows.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace BlueBubbles.Windows.Views.Settings;

public sealed partial class AppearanceSettingsPage : Page
{
    public AppSettings Settings { get; }

    public AppearanceSettingsPage()
    {
        Settings = App.Services.GetRequiredService<AppSettings>();
        InitializeComponent();
        SettingsAutoSave.Attach(this, Settings);

        ThemeCombo.SelectedIndex = Settings.Theme is >= 0 and <= 2 ? Settings.Theme : 0;
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        var theme = ThemeCombo.SelectedIndex;
        if (theme < 0 || theme == Settings.Theme) return;

        Settings.Theme = theme;
        ThemeHelper.Apply(theme);
    }
}
