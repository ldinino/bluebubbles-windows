using System.Text;
using System.Text.Json;
using BlueBubbles.Core.Models;
using BlueBubbles.Core.Services;
using BlueBubbles.Windows.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BlueBubbles.Windows.Views.Settings;

public sealed partial class ServerManagementSettingsPage : Page
{
    private readonly IBlueBubblesApiService _api;
    private readonly SettingsViewModel _vm;

    public ServerManagementSettingsPage()
    {
        _api = App.Services.GetRequiredService<IBlueBubblesApiService>();
        _vm = App.Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();

        Loaded += async (_, _) => await LoadServerInfoAsync();
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
    }

    private async Task LoadServerInfoAsync()
    {
        try
        {
            var response = await _api.GetServerInfoAsync();
            var info = response.Data;
            if (info is null)
            {
                ShowStatus("Could not reach the server.", InfoBarSeverity.Warning);
                return;
            }

            ServerVersionText.Text = $"Server version: {info.ServerVersion ?? "unknown"}";
            OsVersionText.Text = $"macOS version: {info.OsVersion ?? "unknown"}";
            ProxyServiceText.Text = $"Proxy service: {info.ProxyService ?? "unknown"}";
            PrivateApiText.Text = $"Private API: {(info.PrivateApi ? "enabled" : "disabled")}";
            HelperText.Text = $"Helper connected: {(info.HelperConnected ? "yes" : "no")}";
            IcloudText.Text = $"iCloud account: {info.DetectedIcloud ?? "unknown"}";
        }
        catch (Exception ex)
        {
            ShowStatus($"Failed to load server info: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private async void OnRefreshInfoClick(object sender, RoutedEventArgs e)
    {
        RefreshInfoButton.IsEnabled = false;
        await LoadServerInfoAsync();
        RefreshInfoButton.IsEnabled = true;
    }

    private async void OnLoadStatsClick(object sender, RoutedEventArgs e)
    {
        LoadStatsButton.IsEnabled = false;
        try
        {
            var response = await _api.GetStatTotalsAsync();
            StatsText.Text = response.Data.ValueKind == JsonValueKind.Undefined
                ? "No statistics returned."
                : FormatStats(response.Data);
        }
        catch (Exception ex)
        {
            ShowStatus($"Failed to load statistics: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            LoadStatsButton.IsEnabled = true;
        }
    }

    private static string FormatStats(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object)
            return data.ToString();

        var sb = new StringBuilder();
        foreach (var prop in data.EnumerateObject())
        {
            var label = char.ToUpper(prop.Name[0]) + prop.Name[1..].Replace('_', ' ');
            sb.AppendLine($"{label}: {prop.Value}");
        }
        return sb.ToString().TrimEnd();
    }

    private async void OnSoftRestartClick(object sender, RoutedEventArgs e)
        => await RunServerActionAsync(
            "Soft restart the server?",
            "The server services will restart. This may briefly drop the connection.",
            () => _api.SoftRestartAsync(),
            "Soft restart requested.");

    private async void OnHardRestartClick(object sender, RoutedEventArgs e)
        => await RunServerActionAsync(
            "Hard restart the server?",
            "The BlueBubbles server application will fully restart. The connection will drop until it comes back up.",
            () => _api.HardRestartAsync(),
            "Hard restart requested.");

    private async void OnRestartImessageClick(object sender, RoutedEventArgs e)
        => await RunServerActionAsync(
            "Restart iMessage?",
            "The Messages app on the Mac will be restarted.",
            () => _api.RestartImessageAsync(),
            "iMessage restart requested.");

    private async void OnCheckUpdateClick(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        try
        {
            var response = await _api.CheckUpdateAsync();
            ShowStatus(DescribeUpdate(response.Data), InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            ShowStatus($"Update check failed: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private static string DescribeUpdate(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("available", out var available) &&
            available.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            if (available.GetBoolean())
            {
                var version = data.TryGetProperty("metadata", out var meta) &&
                              meta.TryGetProperty("version", out var v)
                    ? v.ToString() : "";
                return string.IsNullOrEmpty(version)
                    ? "An update is available."
                    : $"Update available: {version}";
            }
            return "The server is up to date.";
        }
        return "Update check complete.";
    }

    private async void OnInstallUpdateClick(object sender, RoutedEventArgs e)
        => await RunServerActionAsync(
            "Install server update?",
            "The server will download and install the latest update, then restart.",
            () => _api.InstallUpdateAsync(),
            "Update installation requested.");

    private async void OnLoadLogsClick(object sender, RoutedEventArgs e)
    {
        LoadLogsButton.IsEnabled = false;
        try
        {
            var response = await _api.GetServerLogsAsync(1000);
            ServerLogsBox.Text = FormatLogs(response.Data);
        }
        catch (Exception ex)
        {
            ShowStatus($"Failed to load logs: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            LoadLogsButton.IsEnabled = true;
        }
    }

    private static string FormatLogs(JsonElement data) => data.ValueKind switch
    {
        JsonValueKind.String => data.GetString() ?? "",
        JsonValueKind.Array => string.Join(Environment.NewLine,
            data.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.ToString())),
        JsonValueKind.Undefined => "No logs returned.",
        _ => data.ToString(),
    };

    private async Task RunServerActionAsync(
        string title, string body, Func<Task<ApiResponse<JsonElement>>> action, string successMessage)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = body,
            PrimaryButtonText = "Continue",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            var response = await action();
            if (response.Status is >= 200 and < 300)
                ShowStatus(successMessage, InfoBarSeverity.Success);
            else
                ShowStatus(response.Error?.ErrorMessage ?? response.Message, InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            ShowStatus($"Request failed: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private async void OnResetAppClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Reset App?",
            Content = "This will delete all local data — chats, messages, contacts, and settings — and return to setup. This cannot be undone.",
            PrimaryButtonText = "Reset",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        await _vm.ResetAppCommand.ExecuteAsync(null);
        App.MainWindow.RootNavigationFrame.Navigate(typeof(Setup.SetupPage));
    }
}
