using BlueBubbles.Core.Data.Entities;
using BlueBubbles.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace BlueBubbles.Windows.ViewModels;

public partial class ConversationTileViewModel : ObservableObject
{
    private readonly IContactResolverService _contacts;

    private bool _lastMessageIsFromMe;
    private long? _lastMessageDelivered;
    private long? _lastMessageRead;

    public ChatEntity Chat { get; private set; }
    public List<HandleEntity> Participants { get; private set; }

    /// <summary>The underlying server chats folded into this conversation. One for a normal chat; several
    /// for a merged ("sticky bifurcation") conversation. Their GUIDs/ids drive loading, reads, and
    /// notification suppression.</summary>
    public IReadOnlyList<string> ConstituentGuids { get; private set; }
    public IReadOnlyList<int> ConstituentChatIds { get; private set; }

    /// <summary>True when this tile folds more than one underlying chat together.</summary>
    public bool IsMerged { get; private set; }

    /// <summary>The phone (or fallback) address shown on the info bar for a merged conversation.</summary>
    public string PrimaryAddress { get; private set; }

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

    public ConversationTileViewModel(MergedConversation data, IContactResolverService contacts)
    {
        _contacts = contacts;
        StatusText = string.Empty;
        DisplayName = string.Empty;
        Initials = string.Empty;
        Preview = string.Empty;
        GroupInitials1 = string.Empty;
        GroupInitials2 = string.Empty;
        Chat = data.PrimaryChat;
        Participants = [];
        ConstituentGuids = [];
        ConstituentChatIds = [];
        PrimaryAddress = string.Empty;
        Apply(data);
    }

    public void Refresh(MergedConversation data) => Apply(data);

    private void Apply(MergedConversation data)
    {
        // Refresh only matches a tile by its primary GUID, so the identity (ChatGuid) is unchanged.
        Chat = data.PrimaryChat;
        Participants = data.Participants as List<HandleEntity> ?? data.Participants.ToList();
        ConstituentGuids = data.ConstituentGuids;
        ConstituentChatIds = data.ConstituentChatIds;
        IsMerged = data.IsMerged;
        PrimaryAddress = data.PrimaryAddress;
        // A merged 1:1 is one person with several addresses, never a group.
        IsGroup = data.Primary.Participants.Count > 1;

        if (IsMerged)
        {
            // Resolve as the single underlying contact — GetChatDisplayName over the union would read
            // "Name & Name" since both addresses map to the same card.
            DisplayName = _contacts.GetDisplayName(PrimaryAddress);
            Initials = _contacts.GetAvatarInitials(PrimaryAddress);
            AvatarBytes = _contacts.GetAvatar(PrimaryAddress);
        }
        else
        {
            var addresses = Participants.Select(p => p.Address).ToList();
            DisplayName = _contacts.GetChatDisplayName(addresses, Chat.DisplayName);
            Initials = _contacts.GetChatInitials(addresses, Chat.DisplayName);
            // Re-resolve the 1:1 avatar too — a contact import can make a photo available
            // for a tile that previously had none.
            AvatarBytes = Participants.Count == 1 ? _contacts.GetAvatar(Participants[0].Address) : null;
        }

        Preview = data.LastMessageText ?? string.Empty;
        Timestamp = data.Timestamp;
        HasUnread = data.HasUnread;
        IsPinned = data.IsPinned;
        IsArchived = data.IsArchived;
        ResolveGroupAvatars(data.Primary);
        CaptureLastMessageStatus(data);
        ApplyAppearance();
    }

    /// <summary>True when the given underlying chat GUID belongs to this conversation (any constituent).
    /// Lets per-GUID events (refresh, deep-links, active-chat) resolve a merged tile.</summary>
    public bool ContainsGuid(string chatGuid)
        => ConstituentGuids.Contains(chatGuid, StringComparer.OrdinalIgnoreCase);

    private void CaptureLastMessageStatus(MergedConversation data)
    {
        _lastMessageIsFromMe = data.LastMessageIsFromMe;
        _lastMessageDelivered = data.LastMessageDateDelivered;
        _lastMessageRead = data.LastMessageDateRead;
    }

    /// <summary>Applies the tile's layout and its Sent/Delivered/Read indicator, which shows only
    /// when the conversation's last message is ours.</summary>
    public void ApplyAppearance()
    {
        TilePadding = new Thickness(8, 10, 8, 10);
        DividerThickness = new Thickness(0, 0, 0, 1);

        var show = _lastMessageIsFromMe;
        ShowStatusIndicator = show;
        StatusText = show
            ? _lastMessageRead is not null ? "Read"
                : _lastMessageDelivered is not null ? "Delivered"
                : "Sent"
            : string.Empty;
    }

    /// <summary>Re-runs the timestamp binding (e.g. after the 24-hour-time setting changes).</summary>
    public void RaiseTimestampChanged() => OnPropertyChanged(nameof(Timestamp));

    private void ResolveGroupAvatars(ChatWithParticipants primary)
    {
        if (!IsGroup)
        {
            GroupInitials1 = GroupInitials2 = string.Empty;
            GroupAvatarBytes1 = GroupAvatarBytes2 = null;
            return;
        }

        var group = GroupAvatarResolver.Resolve(primary, _contacts);
        GroupInitials1 = group.Initials1;
        GroupInitials2 = group.Initials2;
        GroupAvatarBytes1 = group.Bytes1;
        GroupAvatarBytes2 = group.Bytes2;
    }
}
