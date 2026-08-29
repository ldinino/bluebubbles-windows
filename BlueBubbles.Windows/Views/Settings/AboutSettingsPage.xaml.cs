using BlueBubbles.Core.Configuration;
using BlueBubbles.Core.Diagnostics;
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

    private readonly SettingsViewModel _vm;
    private readonly AppSettings _settings;
    private readonly ISettingsService _settingsService;
    private readonly IUpdateService _updates;

    // The release the user has been offered; null unless a check found a strictly newer version.
    private UpdateCheckResult? _pendingUpdate;

    // Guards the async void handlers against a second click while one is still in flight.
    private bool _updateBusy;

    // Suppresses the Toggled handler while we set the switch's initial state in the constructor,
    // so loading the page doesn't trigger a redundant save.
    private bool _initializing;

    public AboutSettingsPage()
    {
        _vm = App.Services.GetRequiredService<SettingsViewModel>();
        _settings = App.Services.GetRequiredService<AppSettings>();
        _settingsService = App.Services.GetRequiredService<ISettingsService>();
        _updates = App.Services.GetRequiredService<IUpdateService>();
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

        // Surface whatever the launch-time check already found, without re-hitting the API.
        ShowUpdateResult(_updates.LastResult);

        Loaded += async (_, _) => await LoadServerVersionAsync();
        Loaded += (_, _) => _vm.PropertyChanged += OnVmPropertyChanged;
        Unloaded += (_, _) => _vm.PropertyChanged -= OnVmPropertyChanged;
    }

    private async Task LoadServerVersionAsync()
    {
        ServerVersionValue.Text = await _vm.GetServerVersionDisplayAsync();
    }

    private async void OnCheckForUpdatesClick(object sender, RoutedEventArgs e)
    {
        if (_updateBusy) return;
        _updateBusy = true;
        try
        {
            CheckForUpdatesButton.IsEnabled = false;
            UpdateStatusText.Text = "Checking...";

            var result = await _updates.CheckForUpdateAsync();

            CheckForUpdatesButton.IsEnabled = true;
            ShowUpdateResult(result);
        }
        catch (Exception ex)
        {
            // This is async void: an escaping exception terminates the process, so none may escape.
            AppLog.Error(LogCategory.App, $"Update check UI failed: {ex}");
            CheckForUpdatesButton.IsEnabled = true;
            UpdateStatusText.Text = "Update check failed";
        }
        finally
        {
            _updateBusy = false;
        }
    }

    private void ShowUpdateResult(UpdateCheckResult? result)
    {
        _pendingUpdate = result?.UpdateAvailable == true ? result : null;

        if (_pendingUpdate is null)
        {
            UpdateStatusText.Text = result is null ? string.Empty : $"Up to date ({AppInfo.Version})";
            UpdateInfoBar.IsOpen = false;
            return;
        }

        UpdateStatusText.Text = $"Version {_pendingUpdate.LatestVersion} available";
        UpdateInfoBar.Severity = InfoBarSeverity.Informational;
        UpdateInfoBar.Title = $"Update available: {_pendingUpdate.LatestVersion}";
        UpdateInfoBar.Message =
            "The installer is downloaded from GitHub and its SHA-256 checksum is verified before it " +
            "runs. It is not code-signed, so Windows SmartScreen will warn you - choose " +
            "\"More info\" then \"Run anyway\" to continue.";
        UpdateActionButton.IsEnabled = true;
        UpdateActionButton.Visibility = Visibility.Visible;
        UpdateInfoBar.IsOpen = true;
    }

    private async void OnDownloadUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate is null || _updateBusy)
            return;

        _updateBusy = true;
        try
        {
            UpdateActionButton.IsEnabled = false;
            UpdateProgressBar.Value = 0;
            UpdateProgressBar.Visibility = Visibility.Visible;

            var progress = new Progress<double>(value => UpdateProgressBar.Value = value);
            var result = await _updates.DownloadAndLaunchAsync(_pendingUpdate, progress);

            UpdateProgressBar.Visibility = Visibility.Collapsed;

            if (result.Success)
            {
                UpdateInfoBar.Severity = InfoBarSeverity.Success;
                UpdateInfoBar.Title = "Installer verified and started";
                UpdateInfoBar.Message =
                    "Follow the installer prompts. If SmartScreen appears, choose \"More info\" then " +
                    "\"Run anyway\".";
                UpdateActionButton.Visibility = Visibility.Collapsed;
                return;
            }

            // A checksum failure is not transient - say so loudly rather than inviting a retry.
            var tampered = result.Status is UpdateDownloadStatus.DigestMismatch
                or UpdateDownloadStatus.DigestMissing or UpdateDownloadStatus.UntrustedHost;

            UpdateInfoBar.Severity = tampered ? InfoBarSeverity.Error : InfoBarSeverity.Warning;
            UpdateInfoBar.Title = result.Status switch
            {
                UpdateDownloadStatus.DigestMismatch => "Checksum verification FAILED - nothing was run",
                UpdateDownloadStatus.DigestMissing => "Update cannot be verified - nothing was run",
                UpdateDownloadStatus.UntrustedHost => "Untrusted download location - nothing was run",
                _ => "Update download failed"
            };
            UpdateInfoBar.Message = result.Message;
            UpdateActionButton.IsEnabled = !tampered;
            UpdateInfoBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            // This is async void: an escaping exception terminates the process, so none may escape.
            AppLog.Error(LogCategory.App, $"Update download UI failed: {ex}");
            UpdateProgressBar.Visibility = Visibility.Collapsed;
            UpdateInfoBar.Severity = InfoBarSeverity.Error;
            UpdateInfoBar.Title = "Update download failed";
            UpdateInfoBar.Message = ex.Message;
            UpdateInfoBar.IsOpen = true;
            UpdateActionButton.IsEnabled = true;
        }
        finally
        {
            _updateBusy = false;
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

    // Writes the draw/decode rollup into the log at Info, so it stays readable after verbose
    // logging is switched back off (B2b).
    private void OnPerfSummaryClick(object sender, RoutedEventArgs e)
        => PerfStats.Dump();

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
