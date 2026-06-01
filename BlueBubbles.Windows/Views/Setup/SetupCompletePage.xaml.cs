using BlueBubbles.Windows.ViewModels;
using BlueBubbles.Windows.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BlueBubbles.Windows.Views.Setup;

public sealed partial class SetupCompletePage : Page
{
    private readonly SetupViewModel _vm;

    public SetupCompletePage()
    {
        _vm = App.Services.GetRequiredService<SetupViewModel>();
        InitializeComponent();
    }

    private async void OnEnterAppClick(object sender, RoutedEventArgs e)
    {
        await _vm.FinishSetupCommand.ExecuteAsync(null);
        App.MainWindow.RootNavigationFrame.Navigate(typeof(ShellPage));
    }
}
