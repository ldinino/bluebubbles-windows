using BlueBubbles.Core.Configuration;
using BlueBubbles.Core.Services;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace BlueBubbles.Windows.Services;

internal sealed class NotificationService : INotificationService
{
    private readonly AppSettings _settings;
    private readonly IWindowStateService _windowState;
    private readonly IContactResolverService _contacts;
    private readonly IChatsService _chats;

    private readonly object _lock = new();
    private readonly Dictionary<string, int> _notificationCounts = new();

    public NotificationService(
        AppSettings settings,
        IWindowStateService windowState,
        IContactResolverService contacts,
        IChatsService chats)
    {
        _settings = settings;
        _windowState = windowState;
        _contacts = contacts;
        _chats = chats;

        _chats.ChatUpdated += OnChatUpdated;
    }

    private void OnChatUpdated(object? sender, string chatGuid)
    {
        var chat = _chats.Chats.FirstOrDefault(c => c.Chat.Guid == chatGuid);
        if (chat is not null && !chat.Chat.HasUnreadMessage)
            ClearNotificationsForChat(chatGuid);
    }

    public void HandleNewMessage(NewMessageNotification n)
    {
        if (n.IsFromMe) return;
        if (n.WasDeliveredQuietly) return;
        if (n.IsReaction && !_settings.NotifyReactions) return;

        var chat = _chats.Chats.FirstOrDefault(c => c.Chat.Guid == n.ChatGuid);
        if (chat is null) return;

        if (IsChatMuted(chat.Chat.MuteType, chat.Chat.MuteArgs, n.MessageText)) return;

        if (_settings.FilterUnknownSenders && n.SenderAddress is not null && chat.Participants.Count == 1)
        {
            var resolved = _contacts.GetDisplayName(n.SenderAddress);
            if (resolved == n.SenderAddress) return;
        }

        if (_windowState.IsWindowFocused)
        {
            if (_windowState.ActiveChatGuid == n.ChatGuid) return;
            if (_windowState.ActiveChatGuid is null && !_settings.NotifyOnChatList) return;
        }

        ShowToast(n, chat);
    }

    public void ClearNotificationsForChat(string chatGuid)
    {
        lock (_lock) _notificationCounts.Remove(chatGuid);

        try
        {
            var group = SanitizeGroupTag(chatGuid);
            AppNotificationManager.Default.RemoveByGroupAsync(group).AsTask().ContinueWith(_ => { });
        }
        catch { }
    }

    public void ClearAllNotifications()
    {
        lock (_lock) _notificationCounts.Clear();

        try
        {
            AppNotificationManager.Default.RemoveAllAsync().AsTask().ContinueWith(_ => { });
        }
        catch { }
    }

    private void ShowToast(NewMessageNotification n, ChatWithParticipants chat)
    {
        lock (_lock)
        {
            _notificationCounts.TryGetValue(n.ChatGuid, out var count);
            _notificationCounts[n.ChatGuid] = count + 1;

            if (_notificationCounts.Count > 2)
            {
                ShowSummaryToast();
                return;
            }
        }

        var senderName = n.SenderAddress is not null
            ? _contacts.GetDisplayName(n.SenderAddress)
            : "Unknown";

        var title = chat.Participants.Count > 1 && chat.Chat.DisplayName is not null
            ? $"{chat.Chat.DisplayName}: {FirstName(senderName)}"
            : senderName;

        var body = !string.IsNullOrEmpty(n.MessageText)
            ? n.MessageText
            : n.IsReaction ? "Reacted to a message" : "Sent an attachment";

        var group = SanitizeGroupTag(n.ChatGuid);
        var tag = SanitizeGroupTag(n.MessageGuid);

        try
        {
            var builder = new AppNotificationBuilder()
                .AddArgument("action", "openChat")
                .AddArgument("chatGuid", n.ChatGuid)
                .AddText(title)
                .AddText(body)
                .SetGroup(group)
                .SetTag(tag);

            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch { }
    }

    private void ShowSummaryToast()
    {
        int totalMessages;
        int chatCount;
        lock (_lock)
        {
            totalMessages = _notificationCounts.Values.Sum();
            chatCount = _notificationCounts.Count;
        }

        try
        {
            var builder = new AppNotificationBuilder()
                .AddArgument("action", "openApp")
                .AddText("BlueBubbles")
                .AddText($"{totalMessages} messages from {chatCount} chats")
                .SetTag("summary");

            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch { }
    }

    private static bool IsChatMuted(string? muteType, string? muteArgs, string? messageText)
    {
        if (muteType is null) return false;
        if (muteType == "mute") return true;

        if (muteType == "temporary_mute" && muteArgs is not null)
        {
            if (DateTimeOffset.TryParse(muteArgs, out var muteUntil))
                return DateTimeOffset.UtcNow < muteUntil;
            return true;
        }

        if (muteType == "text_detection" && muteArgs is not null)
        {
            if (string.IsNullOrEmpty(messageText)) return true;
            var keywords = muteArgs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return !keywords.Any(k => messageText.Contains(k, StringComparison.OrdinalIgnoreCase));
        }

        return false;
    }

    private static string SanitizeGroupTag(string value)
    {
        if (value.Length <= 16) return value;
        return value[..16];
    }

    private static string FirstName(string displayName)
    {
        var space = displayName.IndexOf(' ');
        return space > 0 ? displayName[..space] : displayName;
    }
}
