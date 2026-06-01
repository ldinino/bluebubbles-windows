using Microsoft.UI.Xaml;

namespace BlueBubbles.Windows.Services;

/// <summary>
/// Applies the user's theme preference (<see cref="Core.Configuration.AppSettings.Theme"/>:
/// 0 = System, 1 = Light, 2 = Dark) to the window's root element. WinUI applies
/// <see cref="FrameworkElement.RequestedTheme"/> from the root content down, so setting it on
/// the window content re-themes the entire visual tree live.
/// </summary>
public static class ThemeHelper
{
    public static ElementTheme ToElementTheme(int theme) => theme switch
    {
        1 => ElementTheme.Light,
        2 => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    public static void Apply(int theme)
    {
        if (App.MainWindow?.Content is FrameworkElement root)
            root.RequestedTheme = ToElementTheme(theme);
    }
}
