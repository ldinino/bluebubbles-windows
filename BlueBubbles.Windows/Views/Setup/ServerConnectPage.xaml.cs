using BlueBubbles.Core.Models;
using BlueBubbles.Windows.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace BlueBubbles.Windows.Views.Setup;

public sealed partial class ServerConnectPage : Page
{
    private readonly SetupViewModel _vm;

    public ServerConnectPage()
    {
        _vm = App.Services.GetRequiredService<SetupViewModel>();
        InitializeComponent();

        _vm.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(SetupViewModel.ErrorMessage):
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        ErrorBar.Message = _vm.ErrorMessage ?? string.Empty;
                        ErrorBar.IsOpen = _vm.ErrorMessage is not null;
                    });
                    break;
                case nameof(SetupViewModel.IsConnecting):
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        ManualProgress.IsActive = _vm.IsConnecting;
                        ConnectButton.IsEnabled = !_vm.IsConnecting;
                    });
                    break;
                case nameof(SetupViewModel.StatusMessage):
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        StatusText.Text = _vm.StatusMessage;
                        StatusText.Visibility = string.IsNullOrEmpty(_vm.StatusMessage)
                            ? Visibility.Collapsed : Visibility.Visible;
                    });
                    break;
                case nameof(SetupViewModel.IsBrowserAuthInProgress):
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        BrowserAuthPanel.Visibility = _vm.IsBrowserAuthInProgress
                            ? Visibility.Visible : Visibility.Collapsed;
                        if (!_vm.IsBrowserAuthInProgress && _vm.DiscoveredServers.Count == 0 && !_vm.IsDiscovering)
                            PreDiscoveryPanel.Visibility = Visibility.Visible;
                        else if (_vm.IsBrowserAuthInProgress)
                            PreDiscoveryPanel.Visibility = Visibility.Collapsed;
                    });
                    break;
                case nameof(SetupViewModel.IsDiscovering):
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        DiscoveringPanel.Visibility = _vm.IsDiscovering
                            ? Visibility.Visible : Visibility.Collapsed;
                        PreDiscoveryPanel.Visibility = _vm.IsDiscovering || _vm.DiscoveredServers.Count > 0
                            || _vm.IsBrowserAuthInProgress
                            ? Visibility.Collapsed : Visibility.Visible;
                    });
                    break;
                case nameof(SetupViewModel.DiscoveredServers):
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        ServerListView.ItemsSource = _vm.DiscoveredServers;
                        ServerListPanel.Visibility = _vm.DiscoveredServers.Count > 0
                            ? Visibility.Visible : Visibility.Collapsed;
                        PreDiscoveryPanel.Visibility = _vm.DiscoveredServers.Count > 0
                            ? Visibility.Collapsed : Visibility.Visible;
                    });
                    break;
            }
        };

        // Restore state if returning from Google sign-in
        if (_vm.DiscoveredServers.Count > 0)
        {
            ServerListView.ItemsSource = _vm.DiscoveredServers;
            ServerListPanel.Visibility = Visibility.Visible;
            PreDiscoveryPanel.Visibility = Visibility.Collapsed;
            ConnectPivot.SelectedIndex = 1;
        }
    }

    // Launching the system browser does NOT navigate this page, so a normal OAuth flow is
    // unaffected. This only fires when the wizard actually moves to another step, where we tear
    // down any started-but-unfinished loopback listener so it can't leave port 8641 bound or the
    // busy flag wedged — which otherwise breaks the next attempt (the VM is a singleton, e.g. after a reset).
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _vm.CancelBrowserAuth();
    }

    private void OnUrlTextChanged(object sender, TextChangedEventArgs e)
        => _vm.ServerUrl = UrlTextBox.Text;

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox pb)
            _vm.Password = pb.Password;
    }

    private async void OnConnectClick(object sender, RoutedEventArgs e)
        => await _vm.ConnectManualCommand.ExecuteAsync(null);

    private void OnGoogleSignInClick(object sender, RoutedEventArgs e)
        => _vm.GoToGoogleSignInCommand.Execute(null);

    private async void OnBrowserSignInClick(object sender, RoutedEventArgs e)
        => await _vm.SignInViaBrowserCommand.ExecuteAsync(null);

    private void OnCancelBrowserAuthClick(object sender, RoutedEventArgs e)
        => _vm.CancelBrowserAuth();

    private async void OnConnectDiscoveredClick(object sender, RoutedEventArgs e)
    {
        if (ServerListView.SelectedItem is DiscoveredServer server)
            await _vm.ConnectDiscoveredCommand.ExecuteAsync(server);
        else
            _vm.ErrorMessage = "Please select a server from the list.";
    }
}
