using System.Text.Json;
using BlueBubbles.Core.Configuration;
using BlueBubbles.Core.Export;
using BlueBubbles.Core.Models;
using BlueBubbles.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BlueBubbles.Windows.Views.Settings;

public sealed partial class BackupSettingsPage : Page
{
    private const string SettingsBackupName = "BlueBubbles WinUI Settings";

    private readonly IBlueBubblesApiService _api;
    private readonly ISettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly IChatsService _chatsService;
    private readonly IChatExportService _exportService;

    private readonly List<ExportChatRow> _allChats = [];
    private readonly HashSet<int> _selectedChatIds = [];
    private CancellationTokenSource? _exportCts;

    /// <summary>A selectable conversation, carrying its coverage up front so a partial history is
    /// visible before the user picks it rather than only in the finished export.</summary>
    public sealed record ExportChatRow(int ChatId, string Title, string Coverage);

    public BackupSettingsPage()
    {
        _api = App.Services.GetRequiredService<IBlueBubblesApiService>();
        _settingsService = App.Services.GetRequiredService<ISettingsService>();
        _settings = App.Services.GetRequiredService<AppSettings>();
        _chatsService = App.Services.GetRequiredService<IChatsService>();
        _exportService = App.Services.GetRequiredService<IChatExportService>();
        InitializeComponent();
        Loaded += OnLoaded;
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
        ["sendDelay"] = _settings.SendDelay,
        ["notifyOnChatList"] = _settings.NotifyOnChatList,
        ["notifyReactions"] = _settings.NotifyReactions,
        ["notificationSound"] = _settings.NotificationSound,
        ["filterUnknownSenders"] = _settings.FilterUnknownSenders,
        ["privateSendTypingIndicators"] = _settings.PrivateSendTypingIndicators,
        ["privateMarkChatAsRead"] = _settings.PrivateMarkChatAsRead,
        ["privateManualMarkAsRead"] = _settings.PrivateManualMarkAsRead,
        ["theme"] = _settings.Theme,
        ["colorfulAvatars"] = _settings.ColorfulAvatars,
        ["use24HrFormat"] = _settings.Use24HrFormat,
    };

    private void ApplySettingsPayload(JsonElement data)
    {
        if (TryBool(data, "autoDownload", out var b)) _settings.AutoDownload = b;
        if (TryBool(data, "sendWithReturn", out b)) _settings.SendWithReturn = b;
        if (TryBool(data, "showDeliveryTimestamps", out b)) _settings.ShowDeliveryTimestamps = b;
        if (TryInt(data, "sendDelay", out var i)) _settings.SendDelay = i;
        if (TryBool(data, "notifyOnChatList", out b)) _settings.NotifyOnChatList = b;
        if (TryBool(data, "notifyReactions", out b)) _settings.NotifyReactions = b;
        if (TryString(data, "notificationSound", out var s)) _settings.NotificationSound = s;
        if (TryBool(data, "filterUnknownSenders", out b)) _settings.FilterUnknownSenders = b;
        if (TryBool(data, "privateSendTypingIndicators", out b)) _settings.PrivateSendTypingIndicators = b;
        if (TryBool(data, "privateMarkChatAsRead", out b)) _settings.PrivateMarkChatAsRead = b;
        if (TryBool(data, "privateManualMarkAsRead", out b)) _settings.PrivateManualMarkAsRead = b;
        if (TryInt(data, "theme", out i)) _settings.Theme = i;
        if (TryBool(data, "colorfulAvatars", out b)) _settings.ColorfulAvatars = b;
        if (TryBool(data, "use24HrFormat", out b)) _settings.Use24HrFormat = b;
    }

    private async void OnSaveSettingsClick(object sender, RoutedEventArgs e)
        => await RunAsync(SaveSettingsButton, async () =>
        {
            var response = await _api.SetSettingsBackupAsync(SettingsBackupName, BuildSettingsPayload());
            Report(response.IsSuccess, response.FailureMessage, "Settings backed up to the server.");
        });

    private async void OnRestoreSettingsClick(object sender, RoutedEventArgs e)
        => await RunAsync(RestoreSettingsButton, async () =>
        {
            var response = await _api.GetSettingsBackupAsync();
            if (TryExtractPayload(response.Data, SettingsBackupName, out var payload))
            {
                ApplySettingsPayload(payload);
                _settingsService.Save();
                Services.ThemeHelper.Apply(_settings.Theme);
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
            Report(response.IsSuccess, response.FailureMessage, "Settings backup deleted from the server.");
        });

    // ── Helpers ──

    private async Task RunAsync(Button button, Func<Task> action)
    {
        button.IsEnabled = false;
        try { await action(); }
        catch (Exception ex) { ShowStatus($"Request failed: {ex.Message}", InfoBarSeverity.Error); }
        finally { button.IsEnabled = true; }
    }

    // Conversation export

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_chatsService.Chats.Count == 0) await _chatsService.LoadChatsAsync();

            var offset = DateTimeOffset.Now.Offset;
            _allChats.Clear();
            foreach (var c in _chatsService.Chats.Concat(_chatsService.ArchivedChats))
            {
                var title = !string.IsNullOrWhiteSpace(c.Chat.DisplayName)
                    ? c.Chat.DisplayName!
                    : c.Participants.Count > 0
                        ? string.Join(", ", c.Participants.Select(p => p.FormattedAddress ?? p.Address))
                        : c.Chat.ChatIdentifier ?? c.Chat.Guid;

                _allChats.Add(new ExportChatRow(
                    c.Chat.Id,
                    title,
                    ChatExportCoverage.ShortLabel(c.Chat.OldestSyncedMessageDate, offset)));
            }

            ApplyChatFilter(string.Empty);
        }
        catch (Exception ex)
        {
            ShowStatus($"Could not load the conversation list: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private void ApplyChatFilter(string query)
    {
        var rows = string.IsNullOrWhiteSpace(query)
            ? _allChats
            : _allChats.Where(r => r.Title.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        // Reassigning the source drops the ListView's selection, so re-apply the remembered set.
        ChatList.SelectionChanged -= OnChatSelectionChanged;
        ChatList.ItemsSource = rows;
        foreach (var row in rows.Where(r => _selectedChatIds.Contains(r.ChatId)))
            ChatList.SelectedItems.Add(row);
        ChatList.SelectionChanged += OnChatSelectionChanged;

        UpdateSelectionSummary();
    }

    private void OnChatFilterChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs e)
    {
        if (e.Reason == AutoSuggestionBoxTextChangeReason.ProgrammaticChange) return;
        ApplyChatFilter(sender.Text);
    }

    private void OnChatSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach (var removed in e.RemovedItems.OfType<ExportChatRow>())
            _selectedChatIds.Remove(removed.ChatId);
        foreach (var added in e.AddedItems.OfType<ExportChatRow>())
            _selectedChatIds.Add(added.ChatId);

        UpdateSelectionSummary();
    }

    private void OnSelectAllClick(object sender, RoutedEventArgs e)
    {
        foreach (var row in _allChats) _selectedChatIds.Add(row.ChatId);
        ApplyChatFilter(ChatFilterBox.Text);
    }

    private void OnSelectNoneClick(object sender, RoutedEventArgs e)
    {
        _selectedChatIds.Clear();
        ApplyChatFilter(ChatFilterBox.Text);
    }

    private void UpdateSelectionSummary()
    {
        var partial = _allChats
            .Where(r => _selectedChatIds.Contains(r.ChatId))
            .Count(r => r.Coverage != "Complete history");

        ExportExpander.Description = _selectedChatIds.Count == 0
            ? "Nothing selected"
            : partial == 0
                ? $"{_selectedChatIds.Count} selected"
                : $"{_selectedChatIds.Count} selected - {partial} will be incomplete";

        ExportButton.IsEnabled = _selectedChatIds.Count > 0 && _exportCts is null;
    }

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (_selectedChatIds.Count == 0) return;

        var picker = new global::Windows.Storage.Pickers.FolderPicker
        {
            SuggestedStartLocation = global::Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeFilter.Add("*");
        // Required when running unpackaged: without a parent HWND the picker throws.
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));

        global::Windows.Storage.StorageFolder? folder;
        try
        {
            folder = await picker.PickSingleFolderAsync();
        }
        catch (Exception ex)
        {
            ShowStatus($"Could not open the folder picker: {ex.Message}", InfoBarSeverity.Error);
            return;
        }

        if (folder is null) return;

        var chatIds = _allChats
            .Where(r => _selectedChatIds.Contains(r.ChatId))
            .Select(r => r.ChatId)
            .ToList();

        _exportCts = new CancellationTokenSource();
        SetExportRunning(true);

        var progress = new Progress<ChatExportProgress>(p =>
            ExportProgressText.Text = $"{p.Completed} of {p.Total}: {p.CurrentChatTitle}");

        try
        {
            var options = new ChatExportOptions(
                WriteTranscript: TranscriptToggle.IsOn,
                CopyAttachments: AttachmentsToggle.IsOn);

            var result = await Task.Run(
                () => _exportService.ExportAsync(
                    chatIds, folder.Path, options, progress, _exportCts.Token),
                _exportCts.Token);

            var summary =
                $"Exported {result.ChatCount} conversation(s), {result.MessageCount} message(s) to "
                + $"{result.DestinationFolder}. Attachments included: {result.AttachmentsCopied}; "
                + $"not downloaded to this PC: {result.AttachmentsMissing}.";

            if (result.IncompleteChatCount > 0)
            {
                ShowStatus(
                    summary + $" {result.IncompleteChatCount} of them do NOT reach the start of the "
                    + "conversation - see manifest.json and the COVERAGE section of each transcript.",
                    InfoBarSeverity.Warning);
            }
            else
            {
                ShowStatus(summary, InfoBarSeverity.Success);
            }
        }
        catch (OperationCanceledException)
        {
            ShowStatus("Export cancelled. Files already written were left in place.",
                InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            ShowStatus($"Export failed: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            _exportCts?.Dispose();
            _exportCts = null;
            SetExportRunning(false);
        }
    }

    private void OnCancelExportClick(object sender, RoutedEventArgs e) => _exportCts?.Cancel();

    private void SetExportRunning(bool running)
    {
        ExportProgressRing.IsActive = running;
        ExportProgressText.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        CancelExportButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        ExportButton.IsEnabled = !running && _selectedChatIds.Count > 0;
        SelectAllButton.IsEnabled = !running;
        SelectNoneButton.IsEnabled = !running;
        ChatList.IsEnabled = !running;
    }

    private void Report(bool succeeded, string failureMessage, string successMessage)
    {
        if (succeeded)
            ShowStatus(successMessage, InfoBarSeverity.Success);
        else
            ShowStatus(failureMessage, InfoBarSeverity.Warning);
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
