using System.ComponentModel;
using BlueBubbles.Core.Configuration;
using BlueBubbles.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace BlueBubbles.Windows.Services;

/// <summary>
/// Wires a settings category page so any two-way edit of the shared <see cref="AppSettings"/>
/// (via x:Bind) is persisted to disk. Attaches while the page is loaded and detaches when it
/// leaves the visual tree so we don't accumulate handlers across category navigations.
/// </summary>
public static class SettingsAutoSave
{
    public static void Attach(Page page, AppSettings settings)
    {
        var service = App.Services.GetRequiredService<ISettingsService>();
        PropertyChangedEventHandler handler = (_, _) => service.Save();

        page.Loaded += (_, _) =>
        {
            settings.PropertyChanged -= handler;
            settings.PropertyChanged += handler;
        };
        page.Unloaded += (_, _) => settings.PropertyChanged -= handler;
    }
}
