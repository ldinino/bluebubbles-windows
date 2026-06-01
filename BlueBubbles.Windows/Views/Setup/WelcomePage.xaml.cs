using BlueBubbles.Windows.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BlueBubbles.Windows.Views.Setup;

public sealed partial class WelcomePage : Page
{
    private readonly SetupViewModel _vm;

    public WelcomePage()
    {
        _vm = App.Services.GetRequiredService<SetupViewModel>();
        InitializeComponent();
    }

    private void OnGetStartedClick(object sender, RoutedEventArgs e)
        => _vm.GetStartedCommand.Execute(null);
}
