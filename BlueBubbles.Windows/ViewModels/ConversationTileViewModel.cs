using BlueBubbles.Core.Configuration;
using BlueBubbles.Core.Data.Entities;
using BlueBubbles.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace BlueBubbles.Windows.ViewModels;

public partial class ConversationTileViewModel : ObservableObject
{
    private readonly IContactResolverService _contacts;
    private readonly AppSettings _settings;

    private bool _lastMessageIsFromMe;
    private long? _lastMessageDelivered;
    private long? _lastMessageRead;

    public ChatEntity Chat { get; }
    public List<HandleEntity> Participants { get; }

    [ObservableProperty] public partial string DisplayName { get; set; }
    [ObservableProperty] public partial string Initials { get; set; }
    [ObservableProperty] public partial string Preview { get; set; }
    [ObservableProperty] public partial long Timestamp { get; set; }
    [ObservableProperty] public partial bool HasUnread { get; set; }
    [ObservableProperty] public partial bool IsPinned { get; set; }
    [ObservableProperty] public partial bool IsArchived { get; set; }
    [ObservableProperty] public partial bool IsGroup { get; set; }
    [ObservableProperty] public partial byte[]? AvatarBytes { get; set; }
    [ObservableProperty] public partial string GroupInitials1 { get; set; }
    [ObservableProperty] public partial string GroupInitials2 { get; set; }
    [ObservableProperty] public partial byte[]? GroupAvatarBytes1 { get; set; }
    [ObservableProperty] public partial byte[]? GroupAvatarBytes2 { get; set; }

    // Appearance, driven by settings (refreshed live via ConversationListViewModel).
    [ObservableProperty] public partial Thickness TilePadding { get; set; }
    [ObservableProperty] public partial Thickness DividerThickness { get; set; }
    [ObservableProperty] public partial bool ShowStatusIndicator { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; }

    public string ChatGuid => Chat.Guid;

    public ConversationTileViewModel(ChatWithParticipants data, IContactResolverService contacts, AppSettings settings)
    {
        _contacts = contacts;
        _settings = settings;
        StatusText = string.Empty;
        Chat = data.Chat;
        Participants = data.Participants;

        var addresses = Participants.Select(p => p.Address).ToList();
        DisplayName = contacts.GetChatDisplayName(addresses, data.Chat.DisplayName);
        Initials = contacts.GetChatInitials(addresses, data.Chat.DisplayName);
        Preview = data.LastMessageText ?? string.Empty;
        Timestamp = data.Chat.LatestMessageDate ?? 0;
        HasUnread = data.Chat.HasUnreadMessage;
        IsPinned = data.Chat.IsPinned;
        IsArchived = data.Chat.IsArchived;
        IsGroup = Participants.Count > 1;
        AvatarBytes = Participants.Count == 1 ? contacts.GetAvatar(Participants[0].Address) : null;
        GroupInitials1 = string.Empty;
        GroupInitials2 = string.Empty;
        ResolveGroupAvatars(data);
        CaptureLastMessageStatus(data);
        ApplyAppearance(_settings);
    }

    public void Refresh(ChatWithParticipants data)
    {
        var addresses = data.Participants.Select(p => p.Address).ToList();
        DisplayName = _contacts.GetChatDisplayName(addresses, data.Chat.DisplayName);
        Initials = _contacts.GetChatInitials(addresses, data.Chat.DisplayName);
        Preview = data.LastMessageText ?? string.Empty;
        Timestamp = data.Chat.LatestMessageDate ?? 0;
        HasUnread = data.Chat.HasUnreadMessage;
        IsPinned = data.Chat.IsPinned;
        IsArchived = data.Chat.IsArchived;
        IsGroup = data.Participants.Count > 1;
        // Re-resolve the 1:1 avatar too — a contact import can make a photo available
        // for a tile that previously had none.
        AvatarBytes = data.Participants.Count == 1
            ? _contacts.GetAvatar(data.Participants[0].Address)
            : null;
        ResolveGroupAvatars(data);
        CaptureLastMessageStatus(data);
        ApplyAppearance(_settings);
    }

    private void CaptureLastMessageStatus(ChatWithParticipants data)
    {
        _lastMessageIsFromMe = data.LastMessageIsFromMe;
        _lastMessageDelivered = data.LastMessageDateDelivered;
        _lastMessageRead = data.LastMessageDateRead;
    }

    /// <summary>Applies the appearance settings (dense tiles, dividers, status indicators) to this
    /// tile. Called on construction/refresh and live when the settings change.</summary>
    public void ApplyAppearance(AppSettings settings)
    {
        TilePadding = settings.DenseChatTiles ? new Thickness(8, 5, 8, 5) : new Thickness(8, 10, 8, 10);
        DividerThickness = settings.HideDividers ? new Thickness(0) : new Thickness(0, 0, 0, 1);

        var show = settings.StatusIndicatorsOnChats && _lastMessageIsFromMe;
        ShowStatusIndicator = show;
        StatusText = show
            ? _lastMessageRead is not null ? "Read"
                : _lastMessageDelivered is not null ? "Delivered"
                : "Sent"
            : string.Empty;
    }

    /// <summary>Re-runs the timestamp binding (e.g. after the 24-hour-time setting changes).</summary>
    public void RaiseTimestampChanged() => OnPropertyChanged(nameof(Timestamp));

    private void ResolveGroupAvatars(ChatWithParticipants data)
    {
        if (!IsGroup)
        {
            GroupInitials1 = GroupInitials2 = string.Empty;
            GroupAvatarBytes1 = GroupAvatarBytes2 = null;
            return;
        }

        var group = GroupAvatarResolver.Resolve(data, _contacts);
        GroupInitials1 = group.Initials1;
        GroupInitials2 = group.Initials2;
        GroupAvatarBytes1 = group.Bytes1;
        GroupAvatarBytes2 = group.Bytes2;
    }
}
