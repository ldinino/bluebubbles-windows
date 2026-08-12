using System.Collections.ObjectModel;
using System.Text.Json;
using BlueBubbles.Core.Data.Entities;
using BlueBubbles.Core.Models;
using BlueBubbles.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlueBubbles.Windows.ViewModels;

public partial class ChatDetailsViewModel : ObservableObject
{
    private readonly IChatsService _chatsService;
    private readonly IMessagesService _messagesService;
    private readonly IContactResolverService _contacts;
    private readonly IAttachmentCacheService _attachmentCache;
    private readonly IActionHandler _actionHandler;

    private IReadOnlyList<int> _chatIds = [];
    private string _chatGuid = string.Empty;
    private int _mediaOffset;
    private const int MediaPageSize = 30;

    public ObservableCollection<ParticipantItemViewModel> Participants { get; } = [];
    public ObservableCollection<AttachmentViewModel> MediaAttachments { get; } = [];

    [ObservableProperty] public partial string ChatDisplayName { get; set; }
    [ObservableProperty] public partial string EditableName { get; set; }
    [ObservableProperty] public partial bool IsGroupChat { get; set; }
    [ObservableProperty] public partial bool IsMuted { get; set; }
    [ObservableProperty] public partial bool IsEditing { get; set; }
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial bool HasMoreMedia { get; set; }
    [ObservableProperty] public partial string Initials { get; set; }
    [ObservableProperty] public partial byte[]? AvatarBytes { get; set; }
    [ObservableProperty] public partial string GroupInitials1 { get; set; }
    [ObservableProperty] public partial string GroupInitials2 { get; set; }
    [ObservableProperty] public partial byte[]? GroupAvatarBytes1 { get; set; }
    [ObservableProperty] public partial byte[]? GroupAvatarBytes2 { get; set; }
    [ObservableProperty] public partial string ParticipantCountText { get; set; }
    [ObservableProperty] public partial string NewParticipantAddress { get; set; }
    [ObservableProperty] public partial string? StatusMessage { get; set; }

    public event EventHandler? GoBackRequested;
    public event EventHandler? ChatLeft;

    public ChatDetailsViewModel(
        IChatsService chatsService,
        IMessagesService messagesService,
        IContactResolverService contacts,
        IAttachmentCacheService attachmentCache,
        IActionHandler actionHandler)
    {
        _chatsService = chatsService;
        _messagesService = messagesService;
        _contacts = contacts;
        _attachmentCache = attachmentCache;
        _actionHandler = actionHandler;

        ChatDisplayName = string.Empty;
        EditableName = string.Empty;
        Initials = string.Empty;
        GroupInitials1 = string.Empty;
        GroupInitials2 = string.Empty;
        ParticipantCountText = string.Empty;
        NewParticipantAddress = string.Empty;

        _actionHandler.ChatUpdated += OnChatUpdated;
    }

    public async Task LoadAsync(ConversationTileViewModel tile)
    {
        _chatIds = tile.ConstituentChatIds;
        _chatGuid = tile.ChatGuid;
        IsGroupChat = tile.IsGroup;
        ChatDisplayName = tile.DisplayName;
        EditableName = tile.Chat.DisplayName ?? string.Empty;
        Initials = tile.Initials;
        AvatarBytes = tile.AvatarBytes;
        GroupInitials1 = tile.GroupInitials1;
        GroupInitials2 = tile.GroupInitials2;
        GroupAvatarBytes1 = tile.GroupAvatarBytes1;
        GroupAvatarBytes2 = tile.GroupAvatarBytes2;
        IsMuted = tile.Chat.MuteType is not null;
        IsEditing = false;
        StatusMessage = null;
        NewParticipantAddress = string.Empty;

        Participants.Clear();
        if (tile.IsMerged)
        {
            // A merged conversation is one person reached at several addresses — show a single row whose
            // address line reads "phone / email" (phone first).
            var ordered = tile.Participants
                .OrderByDescending(h => ContactResolverService.IsPhone(h.Address))
                .ToList();
            var primary = ordered[0];
            var combined = string.Join(" / ",
                ordered.Select(h => ContactResolverService.FormatAddress(h.Address)));
            Participants.Add(new ParticipantItemViewModel(
                primary,
                _contacts.GetDisplayName(primary.Address),
                _contacts.GetAvatarInitials(primary.Address),
                _contacts.GetAvatar(primary.Address),
                combined));
        }
        else
        {
            foreach (var handle in tile.Participants)
            {
                Participants.Add(new ParticipantItemViewModel(
                    handle,
                    _contacts.GetDisplayName(handle.Address),
                    _contacts.GetAvatarInitials(handle.Address),
                    _contacts.GetAvatar(handle.Address)));
            }
        }
        UpdateParticipantCount();

        MediaAttachments.Clear();
        _mediaOffset = 0;
        HasMoreMedia = true;
        await LoadMoreMediaAsync();
    }

    [RelayCommand]
    private async Task LoadMoreMediaAsync()
    {
        if (!HasMoreMedia || IsLoading) return;
        IsLoading = true;
        try
        {
            var attachments = await _messagesService.LoadMediaAttachmentsAsync(
                _chatIds, MediaPageSize, _mediaOffset);

            if (attachments.Count < MediaPageSize)
                HasMoreMedia = false;

            foreach (var entity in attachments)
                MediaAttachments.Add(new AttachmentViewModel(entity, _attachmentCache));

            _mediaOffset += attachments.Count;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void StartEditing()
    {
        EditableName = ChatDisplayName;
        IsEditing = true;
    }

    [RelayCommand]
    private void CancelEditing()
    {
        IsEditing = false;
    }

    [RelayCommand]
    private async Task SaveNameAsync()
    {
        var newName = EditableName.Trim();
        if (string.IsNullOrEmpty(newName) || newName == ChatDisplayName)
        {
            IsEditing = false;
            return;
        }

        IsLoading = true;
        try
        {
            var success = await _chatsService.RenameChatAsync(_chatGuid, newName);
            if (success)
            {
                ChatDisplayName = newName;
                StatusMessage = null;
            }
            else
            {
                StatusMessage = "Failed to rename group";
            }
        }
        finally
        {
            IsEditing = false;
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task ToggleMuteAsync()
    {
        await _chatsService.ToggleMuteAsync(_chatGuid);
        IsMuted = !IsMuted;
    }

    [RelayCommand]
    private async Task AddParticipantAsync()
    {
        var address = NewParticipantAddress.Trim();
        if (string.IsNullOrEmpty(address)) return;

        IsLoading = true;
        StatusMessage = null;
        try
        {
            var success = await _chatsService.AddParticipantAsync(_chatGuid, address);
            if (success)
            {
                NewParticipantAddress = string.Empty;
                await RefreshParticipantsAsync();
            }
            else
            {
                StatusMessage = "Failed to add participant";
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task RemoveParticipantAsync(string address)
    {
        IsLoading = true;
        StatusMessage = null;
        try
        {
            var success = await _chatsService.RemoveParticipantAsync(_chatGuid, address);
            if (success)
            {
                await RefreshParticipantsAsync();
            }
            else
            {
                StatusMessage = "Failed to remove participant";
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task LeaveGroupAsync()
    {
        IsLoading = true;
        try
        {
            var success = await _chatsService.LeaveChatAsync(_chatGuid);
            if (success)
                ChatLeft?.Invoke(this, EventArgs.Empty);
            else
                StatusMessage = "Failed to leave group";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task<bool> SetIconAsync(Stream iconStream, string fileName)
    {
        IsLoading = true;
        try
        {
            return await _chatsService.SetChatIconAsync(_chatGuid, iconStream, fileName);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task<bool> DeleteIconAsync()
    {
        IsLoading = true;
        try
        {
            return await _chatsService.DeleteChatIconAsync(_chatGuid);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void GoBack()
    {
        GoBackRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task RefreshParticipantsAsync()
    {
        var chats = _chatsService.Chats;
        var chatData = chats.FirstOrDefault(c => c.Chat.Guid == _chatGuid);
        if (chatData is null) return;

        Participants.Clear();
        foreach (var handle in chatData.Participants)
        {
            Participants.Add(new ParticipantItemViewModel(
                handle,
                _contacts.GetDisplayName(handle.Address),
                _contacts.GetAvatarInitials(handle.Address),
                _contacts.GetAvatar(handle.Address)));
        }
        UpdateParticipantCount();
        await Task.CompletedTask;
    }

    private void UpdateParticipantCount()
    {
        ParticipantCountText = Participants.Count == 1
            ? "1 participant"
            : $"{Participants.Count} participants";
    }

    private void OnChatUpdated(object? sender, ChatUpdatedEventArgs e)
    {
        // e.Chat is parsed from the payload's chats[0]; the payload itself is a message, so
        // deserializing it as a chat (as this used to) yielded the message's GUID and never matched.
        var chat = e.Chat;
        if (chat is null || chat.Guid != _chatGuid) return;

        var dispatcher = App.MainWindow?.DispatcherQueue;
        if (dispatcher is null) return;

        dispatcher.TryEnqueue(() => _ = ApplyChatUpdateAsync(e.EventType, chat));
    }

    private async Task ApplyChatUpdateAsync(string eventType, Chat chat)
    {
        try
        {
            if (eventType == SocketEvents.GroupNameChange && chat.DisplayName is not null)
                ChatDisplayName = chat.DisplayName;

            if (eventType is SocketEvents.ParticipantAdded
                or SocketEvents.ParticipantRemoved
                or SocketEvents.ParticipantLeft)
            {
                await RefreshParticipantsAsync();
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogCategory.Ui, $"Chat details update for {chat.Guid} failed: {ex.Message}");
        }
    }
}

public class ParticipantItemViewModel
{
    private readonly string? _displayAddress;

    public HandleEntity Handle { get; }
    public string DisplayName { get; }
    public string Initials { get; }
    public byte[]? AvatarBytes { get; }

    /// <summary>The address line shown under the name. Defaults to the handle's address; a merged
    /// conversation passes an explicit "phone / email" string.</summary>
    public string Address => _displayAddress ?? Handle.Address;

    public ParticipantItemViewModel(HandleEntity handle, string displayName, string initials,
        byte[]? avatarBytes, string? displayAddress = null)
    {
        Handle = handle;
        DisplayName = displayName;
        Initials = initials;
        AvatarBytes = avatarBytes;
        _displayAddress = displayAddress;
    }
}
