using BlueBubbles.Windows.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace BlueBubbles.Windows.Views.Setup;

public sealed partial class SetupPage : Page
{
    private readonly SetupViewModel _vm;

    public SetupPage()
    {
        _vm = App.Services.GetRequiredService<SetupViewModel>();
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SetupViewModel.CurrentStep))
                NavigateToStep(_vm.CurrentStep);
        };

        InitializeComponent();
        NavigateToStep(_vm.CurrentStep);
    }

    private void NavigateToStep(SetupStep step)
    {
        var pageType = step switch
        {
            SetupStep.Welcome => typeof(WelcomePage),
            SetupStep.ServerConnect => typeof(ServerConnectPage),
            SetupStep.GoogleSignIn => typeof(GoogleSignInPage),
            SetupStep.Syncing => typeof(SyncPage),
            SetupStep.Complete => typeof(SetupCompletePage),
            _ => typeof(WelcomePage)
        };

        SetupFrame.Navigate(pageType);
    }
}
