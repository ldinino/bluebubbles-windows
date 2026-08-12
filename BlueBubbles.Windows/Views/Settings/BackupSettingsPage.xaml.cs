using System.Text.Json;
using BlueBubbles.Core.Configuration;
using BlueBubbles.Core.Models;
using BlueBubbles.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BlueBubbles.Windows.Views.Settings;

public sealed partial class BackupSettingsPage : Page
{
    private const string SettingsBackupName = "BlueBubbles WinUI Settings";
    private const string ThemeBackupName = "BlueBubbles WinUI Theme";

    private readonly IBlueBubblesApiService _api;
    private readonly ISettingsService _settingsService;
    private readonly AppSettings _settings;

    public BackupSettingsPage()
    {
        _api = App.Services.GetRequiredService<IBlueBubblesApiService>();
        _settingsService = App.Services.GetRequiredService<ISettingsService>();
        _settings = App.Services.GetRequiredService<AppSettings>();
        InitializeComponent();
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
    }

    // ── Settings backup ──

    private Dictionary<string, object?> BuildSettingsPayload() => new()
    {
        ["autoDownload"] = _settings.AutoDownload,
        ["sendWithReturn"] = _settings.SendWithReturn,
        ["showDeliveryTimestamps"] = _settings.ShowDeliveryTimestamps,
        ["statusIndicatorsOnChats"] = _settings.StatusIndicatorsOnChats,
        ["sendDelay"] = _settings.SendDelay,
        ["scrollToLastUnread"] = _settings.ScrollToLastUnread,
        ["notifyOnChatList"] = _settings.NotifyOnChatList,
        ["notifyReactions"] = _settings.NotifyReactions,
        ["notificationSound"] = _settings.NotificationSound,
        ["filterUnknownSenders"] = _settings.FilterUnknownSenders,
        ["privateSendTypingIndicators"] = _settings.PrivateSendTypingIndicators,
        ["privateMarkChatAsRead"] = _settings.PrivateMarkChatAsRead,
        ["privateManualMarkAsRead"] = _settings.PrivateManualMarkAsRead,
    };

    private void ApplySettingsPayload(JsonElement data)
    {
        if (TryBool(data, "autoDownload", out var b)) _settings.AutoDownload = b;
        if (TryBool(data, "sendWithReturn", out b)) _settings.SendWithReturn = b;
        if (TryBool(data, "showDeliveryTimestamps", out b)) _settings.ShowDeliveryTimestamps = b;
        if (TryBool(data, "statusIndicatorsOnChats", out b)) _settings.StatusIndicatorsOnChats = b;
        if (TryInt(data, "sendDelay", out var i)) _settings.SendDelay = i;
        if (TryBool(data, "scrollToLastUnread", out b)) _settings.ScrollToLastUnread = b;
        if (TryBool(data, "notifyOnChatList", out b)) _settings.NotifyOnChatList = b;
        if (TryBool(data, "notifyReactions", out b)) _settings.NotifyReactions = b;
        if (TryString(data, "notificationSound", out var s)) _settings.NotificationSound = s;
        if (TryBool(data, "filterUnknownSenders", out b)) _settings.FilterUnknownSenders = b;
        if (TryBool(data, "privateSendTypingIndicators", out b)) _settings.PrivateSendTypingIndicators = b;
        if (TryBool(data, "privateMarkChatAsRead", out b)) _settings.PrivateMarkChatAsRead = b;
        if (TryBool(data, "privateManualMarkAsRead", out b)) _settings.PrivateManualMarkAsRead = b;
    }

    private async void OnSaveSettingsClick(object sender, RoutedEventArgs e)
        => await RunAsync(SaveSettingsButton, async () =>
        {
            var response = await _api.SetSettingsBackupAsync(SettingsBackupName, BuildSettingsPayload());
            Report(response, "Settings backed up to the server.");
        });

    private async void OnRestoreSettingsClick(object sender, RoutedEventArgs e)
        => await RunAsync(RestoreSettingsButton, async () =>
        {
            var response = await _api.GetSettingsBackupAsync();
            if (TryExtractPayload(response.Data, SettingsBackupName, out var payload))
            {
                ApplySettingsPayload(payload);
                _settingsService.Save();
                ShowStatus("Settings restored from the server.", InfoBarSeverity.Success);
            }
            else
            {
                ShowStatus("No settings backup was found on the server.", InfoBarSeverity.Warning);
            }
        });

    private async void OnDeleteSettingsClick(object sender, RoutedEventArgs e)
        => await RunAsync(DeleteSettingsButton, async () =>
        {
            var response = await _api.DeleteSettingsBackupAsync(SettingsBackupName);
            Report(response, "Settings backup deleted from the server.");
        });

    // ── Theme backup ──

    private Dictionary<string, object?> BuildThemePayload() => new()
    {
        ["theme"] = _settings.Theme,
        ["colorfulAvatars"] = _settings.ColorfulAvatars,
        ["use24HrFormat"] = _settings.Use24HrFormat,
    };

    private void ApplyThemePayload(JsonElement data)
    {
        if (TryInt(data, "theme", out var i)) _settings.Theme = i;
        if (TryBool(data, "colorfulAvatars", out var b)) _settings.ColorfulAvatars = b;
        if (TryBool(data, "use24HrFormat", out b)) _settings.Use24HrFormat = b;
    }

    private async void OnSaveThemeClick(object sender, RoutedEventArgs e)
        => await RunAsync(SaveThemeButton, async () =>
        {
            var response = await _api.SetThemeAsync(ThemeBackupName, BuildThemePayload());
            Report(response, "Theme backed up to the server.");
        });

    private async void OnRestoreThemeClick(object sender, RoutedEventArgs e)
        => await RunAsync(RestoreThemeButton, async () =>
        {
            var response = await _api.GetThemeAsync();
            if (TryExtractPayload(response.Data, ThemeBackupName, out var payload))
            {
                ApplyThemePayload(payload);
                _settingsService.Save();
                Services.ThemeHelper.Apply(_settings.Theme);
                ShowStatus("Theme restored from the server.", InfoBarSeverity.Success);
            }
            else
            {
                ShowStatus("No theme backup was found on the server.", InfoBarSeverity.Warning);
            }
        });

    private async void OnDeleteThemeClick(object sender, RoutedEventArgs e)
        => await RunAsync(DeleteThemeButton, async () =>
        {
            var response = await _api.DeleteThemeAsync(ThemeBackupName);
            Report(response, "Theme backup deleted from the server.");
        });

    // ── Helpers ──

    private async Task RunAsync(Button button, Func<Task> action)
    {
        button.IsEnabled = false;
        try { await action(); }
        catch (Exception ex) { ShowStatus($"Request failed: {ex.Message}", InfoBarSeverity.Error); }
        finally { button.IsEnabled = true; }
    }

    private void Report(ApiResponse<JsonElement> response, string successMessage)
    {
        if (response.Status is >= 200 and < 300)
            ShowStatus(successMessage, InfoBarSeverity.Success);
        else
            ShowStatus(response.Error?.ErrorMessage ?? response.Message, InfoBarSeverity.Warning);
    }

    /// <summary>
    /// The server returns saved backups as an array of <c>{ name, data }</c> entries (or sometimes a
    /// single object). Find the entry matching <paramref name="name"/> and hand back its inner data.
    /// </summary>
    private static bool TryExtractPayload(JsonElement root, string name, out JsonElement data)
    {
        data = default;
        if (root.ValueKind == JsonValueKind.Array)
        {
            JsonElement? fallback = null;
            foreach (var entry in root.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                fallback ??= entry;
                if (entry.TryGetProperty("name", out var n) && n.GetString() == name)
                    return entry.TryGetProperty("data", out data);
            }
            if (fallback is { } f && f.TryGetProperty("data", out data)) return true;
            return false;
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("data", out data)) return true;
            data = root;
            return true;
        }

        return false;
    }

    private static bool TryBool(JsonElement obj, string name, out bool value)
    {
        value = false;
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v)) return false;
        if (v.ValueKind is JsonValueKind.True or JsonValueKind.False) { value = v.GetBoolean(); return true; }
        return false;
    }

    private static bool TryInt(JsonElement obj, string name, out int value)
    {
        value = 0;
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v)) return false;
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out value);
    }

    private static bool TryString(JsonElement obj, string name, out string value)
    {
        value = string.Empty;
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v)) return false;
        if (v.ValueKind == JsonValueKind.String) { value = v.GetString() ?? string.Empty; return true; }
        return false;
    }
}
