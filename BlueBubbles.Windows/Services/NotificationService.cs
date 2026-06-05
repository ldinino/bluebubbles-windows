using BlueBubbles.Core.Configuration;
using BlueBubbles.Core.Services;
using BlueBubbles.Core.Utils;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace BlueBubbles.Windows.Services;

internal sealed class NotificationService : INotificationService
{
    // Activation-argument keys, shared with the handler in App.xaml.cs.
    internal const string ActionKey = "action";
    internal const string ChatGuidKey = "chatGuid";
    internal const string MessageGuidKey = "messageGuid";
    internal const string SelectedTextKey = "selectedText";
    internal const string ReactionKey = "reaction";
    internal const string ReplyInputId = "replyText";
    internal const string ActionOpenChat = "openChat";
    internal const string ActionOpenApp = "openApp";
    internal const string ActionReply = "reply";
    internal const string ActionReact = "react";

    // The quick tapbacks offered on a toast. A toast allows at most five buttons; the inline-reply
    // send button consumes one, leaving these four. Mirrors iMessage's first four tapbacks.
    private static readonly string[] ToastReactions =
        [ReactionTypes.Love, ReactionTypes.Like, ReactionTypes.Dislike, ReactionTypes.Laugh];

    private readonly AppSettings _settings;
    private readonly IWindowStateService _windowState;
    private readonly IContactResolverService _contacts;
    private readonly IChatsService _chats;
    private readonly INotificationSoundService _sound;

    private readonly object _lock = new();
    private readonly Dictionary<string, int> _notificationCounts = new();

    public NotificationService(
        AppSettings settings,
        IWindowStateService windowState,
        IContactResolverService contacts,
        IChatsService chats,
        INotificationSoundService sound)
    {
        _settings = settings;
        _windowState = windowState;
        _contacts = contacts;
        _chats = chats;
        _sound = sound;

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

        if (!NotificationPolicy.ShouldShowForWindowState(
                _windowState.IsWindowFocused, _windowState.ActiveChatGuid,
                n.ChatGuid, _settings.NotifyOnChatList))
            return;

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
            // Top-level arguments are used when the toast body is clicked (openChat); each button
            // added in AddInlineActions carries its own arguments, used when that button is pressed.
            var builder = new AppNotificationBuilder()
                .AddArgument(ActionKey, ActionOpenChat)
                .AddArgument(ChatGuidKey, n.ChatGuid)
                .AddText(title)
                .AddText(body)
                .SetGroup(group)
                .SetTag(tag);

            AddInlineActions(builder, n);

            // When the user has chosen a custom sound we render it ourselves; mute the toast so the
            // OS doesn't also play the default sound on top of it.
            if (_sound.WillPlayCustomSound)
                builder.MuteAudio();

            AppNotificationManager.Default.Show(builder.BuildNotification());
            _sound.PlayConfiguredSound();
            AppLog.Info(LogCategory.Ui, $"Toast shown for chat {group} (msg {tag}).");
        }
        catch (Exception ex)
        {
            AppLog.Error(LogCategory.Ui, $"Toast show failed: {ex.Message}");
        }
    }

    /// <summary>Adds the inline quick-reply box and — for real messages, not reactions — the
    /// quick-tapback buttons. The reply box's send button is bound to the text input so it renders
    /// inline beside it. The full (untruncated) chat/message GUIDs travel as arguments because the
    /// toast tag/group are truncated and can't be used to address the send.</summary>
    private static void AddInlineActions(AppNotificationBuilder builder, NewMessageNotification n)
    {
        builder.AddTextBox(ReplyInputId, "Reply", string.Empty);
        builder.AddButton(new AppNotificationButton("Send")
            .AddArgument(ActionKey, ActionReply)
            .AddArgument(ChatGuidKey, n.ChatGuid)
            .SetInputId(ReplyInputId));

        if (n.IsReaction) return;

        var selectedText = Truncate(n.MessageText ?? string.Empty, 256);
        foreach (var reaction in ToastReactions)
        {
            builder.AddButton(new AppNotificationButton(ReactionTypes.ToEmoji(reaction))
                .AddArgument(ActionKey, ActionReact)
                .AddArgument(ChatGuidKey, n.ChatGuid)
                .AddArgument(MessageGuidKey, n.MessageGuid)
                .AddArgument(ReactionKey, reaction)
                .AddArgument(SelectedTextKey, selectedText));
        }
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max];

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
                .AddArgument(ActionKey, ActionOpenApp)
                .AddText("BlueBubbles")
                .AddText($"{totalMessages} messages from {chatCount} chats")
                .SetTag("summary");

            if (_sound.WillPlayCustomSound)
                builder.MuteAudio();

            AppNotificationManager.Default.Show(builder.BuildNotification());
            _sound.PlayConfiguredSound();
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
