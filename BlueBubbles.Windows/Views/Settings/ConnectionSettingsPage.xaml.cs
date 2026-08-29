using BlueBubbles.Core.Configuration;
using BlueBubbles.Core.Models;
using BlueBubbles.Windows.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace BlueBubbles.Windows.Views.Settings;

public sealed partial class ConnectionSettingsPage : Page
{
    private readonly SettingsViewModel _vm;
    private readonly ServerConfiguration _config;

    public ConnectionSettingsPage()
    {
        _vm = App.Services.GetRequiredService<SettingsViewModel>();
        _config = App.Services.GetRequiredService<ServerConfiguration>();
        InitializeComponent();

        UpdateConnectionDisplay();
        UpdateVCardDisplay();
        UpdateLocalConnectionDisplay();

        Loaded += (_, _) => _vm.PropertyChanged += OnVmPropertyChanged;
        Unloaded += (_, _) => _vm.PropertyChanged -= OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SettingsViewModel.ConnectionState):
            case nameof(SettingsViewModel.ServerUrl):
                DispatcherQueue.TryEnqueue(UpdateConnectionDisplay);
                break;
            case nameof(SettingsViewModel.IsFetchingUrl):
                DispatcherQueue.TryEnqueue(() =>
                {
                    FetchUrlButton.IsEnabled = !_vm.IsFetchingUrl;
                    FetchUrlButton.Content = _vm.IsFetchingUrl ? "Fetching..." : "Fetch Latest URL";
                });
                break;
            case nameof(SettingsViewModel.VCardStatus):
            case nameof(SettingsViewModel.ContactCount):
            case nameof(SettingsViewModel.HasVCard):
                DispatcherQueue.TryEnqueue(UpdateVCardDisplay);
                break;
            case nameof(SettingsViewModel.LocalConnectionStatus):
            case nameof(SettingsViewModel.IsTestingLocal):
                DispatcherQueue.TryEnqueue(UpdateLocalConnectionStatus);
                break;
        }
    }

    private void UpdateConnectionDisplay()
    {
        UrlText.Text = string.IsNullOrEmpty(_vm.ServerUrl) ? "Not configured" : _vm.ServerUrl;
        ProxyText.Text = string.IsNullOrEmpty(_config.ProxyService) ? "Unknown" : _config.ProxyService;

        StatusText.Text = ConnectionStatusPolicy.DescribeStatus(_vm.ConnectionState);
        StatusText.Foreground = new SolidColorBrush(_vm.ConnectionState switch
        {
            ConnectionState.Connected => Microsoft.UI.Colors.LimeGreen,
            ConnectionState.Connecting => Microsoft.UI.Colors.Orange,
            _ => Microsoft.UI.Colors.OrangeRed
        });
    }

    private void UpdateVCardDisplay()
    {
        VCardStatusText.Text = _vm.VCardStatus;
        ResetVCardButton.IsEnabled = _vm.HasVCard;
    }

    private void UpdateLocalConnectionDisplay()
    {
        LocalConnectionToggle.IsOn = _vm.UseLocalConnection;
        LocalPortTextBox.Text = _vm.LocalhostPort;
        UpdateLocalConnectionStatus();
    }

    private void UpdateLocalConnectionStatus()
    {
        LocalStatusText.Text = _vm.LocalConnectionStatus;
        TestLocalButton.IsEnabled = !_vm.IsTestingLocal;
        TestLocalButton.Content = _vm.IsTestingLocal ? "Testing..." : "Test Connection";
    }

    private async void OnFetchUrlClick(object sender, RoutedEventArgs e)
        => await _vm.FetchUrlCommand.ExecuteAsync(null);

    private async void OnLocalConnectionToggled(object sender, RoutedEventArgs e)
    {
        _vm.LocalhostPort = LocalPortTextBox.Text;
        await _vm.ToggleLocalConnectionCommand.ExecuteAsync(LocalConnectionToggle.IsOn);
    }

    private async void OnTestLocalClick(object sender, RoutedEventArgs e)
    {
        _vm.LocalhostPort = LocalPortTextBox.Text;
        await _vm.TestLocalConnectionCommand.ExecuteAsync(null);
    }

    private async void OnImportVCardClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add(".vcf");

        var file = await picker.PickSingleFileAsync();
        if (file is not null)
            await _vm.ImportVCardCommand.ExecuteAsync(file.Path);
    }

    private async void OnResetVCardClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Remove imported contacts?",
            Content = "This clears all contacts loaded from the vCard. Conversations will show raw phone "
                + "numbers and emails (and any merged threads will split apart) until you import again.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            _vm.ResetContactsCommand.Execute(null);
    }
}
