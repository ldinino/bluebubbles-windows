using BlueBubbles.Core.Configuration;
using BlueBubbles.Windows.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace BlueBubbles.Windows.Views.Settings;

public sealed partial class MessagingSettingsPage : Page
{
    public AppSettings Settings { get; }

    public MessagingSettingsPage()
    {
        Settings = App.Services.GetRequiredService<AppSettings>();
        InitializeComponent();
        SettingsAutoSave.Attach(this, Settings);

        SendDelayBox.Value = Settings.SendDelay;
    }

    private void OnSendDelayChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        // NumberBox emits NaN while the field is empty/mid-edit; ignore it.
        if (double.IsNaN(args.NewValue)) return;

        var value = (int)Math.Clamp(args.NewValue, 0, 10);
        if (value != Settings.SendDelay)
            Settings.SendDelay = value;
    }
}
