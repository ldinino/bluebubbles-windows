using BlueBubbles.Core.Configuration;
using BlueBubbles.Core.Models;
using BlueBubbles.Windows.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
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

        LogTextBox.Text = _vm.LogText;
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
            case nameof(SettingsViewModel.LogText):
                DispatcherQueue.TryEnqueue(() =>
                {
                    LogTextBox.Text = _vm.LogText;
                    LogTextBox.SelectionStart = LogTextBox.Text.Length;
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

        switch (_vm.ConnectionState)
        {
            case SocketState.Connected:
                StatusText.Text = "Connected";
                StatusText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.LimeGreen);
                break;
            case SocketState.Connecting:
                StatusText.Text = "Connecting...";
                StatusText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Orange);
                break;
            default:
                StatusText.Text = "Disconnected";
                StatusText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed);
                break;
        }
    }

    private void UpdateVCardDisplay() => VCardStatusText.Text = _vm.VCardStatus;

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

    private void OnCopyLogClick(object sender, RoutedEventArgs e)
    {
        var dp = new DataPackage();
        dp.SetText(_vm.LogText);
        Clipboard.SetContent(dp);
    }

    private void OnClearLogClick(object sender, RoutedEventArgs e)
        => _vm.ClearLogCommand.Execute(null);
}
