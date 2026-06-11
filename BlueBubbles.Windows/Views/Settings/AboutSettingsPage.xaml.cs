using BlueBubbles.Core.Configuration;
using BlueBubbles.Core.Services;
using BlueBubbles.Windows.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace BlueBubbles.Windows.Views.Settings;

public sealed partial class AboutSettingsPage : Page
{
    private const string GitHubUrl = "https://github.com/ldinino/bluebubbles-windows";
    private const string DocsUrl = "https://bluebubbles.app/install/";

    private readonly IBlueBubblesApiService _api;
    private readonly SettingsViewModel _vm;
    private readonly AppSettings _settings;
    private readonly ISettingsService _settingsService;

    // Suppresses the Toggled handler while we set the switch's initial state in the constructor,
    // so loading the page doesn't trigger a redundant save.
    private bool _initializing;

    public AboutSettingsPage()
    {
        _api = App.Services.GetRequiredService<IBlueBubblesApiService>();
        _vm = App.Services.GetRequiredService<SettingsViewModel>();
        _settings = App.Services.GetRequiredService<AppSettings>();
        _settingsService = App.Services.GetRequiredService<ISettingsService>();
        InitializeComponent();

        _initializing = true;
        VerboseLoggingToggle.IsOn = _settings.VerboseLogging;
        _initializing = false;

        // 3-part semantic version (Major.Minor.Patch), read unpackaged-safe from the assembly.
        var versionText = AppInfo.Version;
        AppVersionText.Text = $"Version {versionText}";
        AppVersionValue.Text = versionText;

        // Load the logo from the deployed file — ms-appx:/// asset URIs don't resolve unpackaged.
        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (System.IO.File.Exists(iconPath))
            AppLogoImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(iconPath));

        PopulateLogCategories();
        LogTextBox.Text = _vm.LogText;

        Loaded += async (_, _) => await LoadServerVersionAsync();
        Loaded += (_, _) => _vm.PropertyChanged += OnVmPropertyChanged;
        Unloaded += (_, _) => _vm.PropertyChanged -= OnVmPropertyChanged;
    }

    private async Task LoadServerVersionAsync()
    {
        try
        {
            var response = await _api.GetServerInfoAsync();
            ServerVersionValue.Text = response.Data?.ServerVersion ?? "Unknown";
        }
        catch
        {
            ServerVersionValue.Text = "Unavailable";
        }
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.LogText))
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                LogTextBox.Text = _vm.LogText;
                LogTextBox.SelectionStart = LogTextBox.Text.Length;
            });
        }
    }

    private void PopulateLogCategories()
    {
        LogCategoryCombo.Items.Add(SettingsViewModel.AllCategories);
        foreach (var name in Enum.GetNames<LogCategory>())
            LogCategoryCombo.Items.Add(name);
        LogCategoryCombo.SelectedItem = _vm.LogCategoryFilter;
    }

    private void OnLogCategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LogCategoryCombo.SelectedItem is string category)
            _vm.LogCategoryFilter = category;
    }

    private void OnVerboseLoggingToggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;

        _settings.VerboseLogging = VerboseLoggingToggle.IsOn;
        AppLog.MinLevel = _settings.VerboseLogging ? LogLevel.Debug : LogLevel.Info;
        _settingsService.Save();
        AppLog.Info(LogCategory.App,
            $"Verbose logging {(_settings.VerboseLogging ? "enabled" : "disabled")}.");
    }

    private void OnCopyLogClick(object sender, RoutedEventArgs e)
    {
        var dp = new DataPackage();
        dp.SetText(_vm.LogText);
        Clipboard.SetContent(dp);
    }

    private void OnClearLogClick(object sender, RoutedEventArgs e)
        => _vm.ClearLogCommand.Execute(null);

    private async void OnExportLogsClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        picker.SuggestedStartLocation = PickerLocationId.Desktop;
        picker.FileTypeChoices.Add("Zip archive", new List<string> { ".zip" });
        picker.SuggestedFileName = LogExport.SuggestedFileName();

        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        try
        {
            await Task.Run(() => LogExport.WriteZip(AppLog.LogDirectory, file.Path));
        }
        catch (Exception ex)
        {
            AppLog.Error(LogCategory.App, $"Log export failed: {ex.Message}");
        }
    }

    private async void OnGitHubClick(object sender, RoutedEventArgs e)
        => await Launcher.LaunchUriAsync(new Uri(GitHubUrl));

    private async void OnDocsClick(object sender, RoutedEventArgs e)
        => await Launcher.LaunchUriAsync(new Uri(DocsUrl));

    private async void OnResetAppClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Reset App?",
            Content = "This will delete all local data — chats, messages, contacts, and settings — then restart the app into setup. This cannot be undone.",
            PrimaryButtonText = "Reset",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        await _vm.ResetAppCommand.ExecuteAsync(null);

        // Relaunch instead of navigating in place: the still-running process holds in-memory
        // chat/contact/message caches that re-sync into the wiped DB and can cross-wire
        // conversations (B11). A fresh process matches the true first-run experience. Drop the
        // tray icon first — Restart terminates without running Closed handlers, which would
        // leave a ghost icon next to the new instance's.
        App.MainWindow.RemoveTrayIcon();
        var reason = Microsoft.Windows.AppLifecycle.AppInstance.Restart(string.Empty);

        // Restart only returns on failure — fall back to the old in-place setup navigation.
        AppLog.Warn(LogCategory.App, $"Restart after reset failed ({reason}); navigating to setup in-place.");
        App.MainWindow.RootNavigationFrame.Navigate(typeof(Setup.SetupPage));
    }
}
