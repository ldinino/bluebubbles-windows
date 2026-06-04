using System.Collections.ObjectModel;
using BlueBubbles.Core.Configuration;
using BlueBubbles.Core.Models;
using BlueBubbles.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlueBubbles.Windows.ViewModels;

public partial class ConversationListViewModel : ObservableObject
{
    private readonly IChatsService _chatsService;
    private readonly IContactResolverService _contacts;
    private readonly IActionHandler _actionHandler;
    private readonly ISocketService _socketService;
    private readonly IIncomingMessageProcessor _incomingProcessor;
    private readonly ISyncService _syncService;
    private readonly IWindowStateService _windowState;
    private readonly AppSettings _appSettings;

    private List<ConversationTileViewModel> _allTiles = [];
    private List<ConversationTileViewModel> _archivedTiles = [];

    public ObservableCollection<ConversationTileViewModel> Conversations { get; } = [];
    public ObservableCollection<ConversationTileViewModel> PinnedConversations { get; } = [];

    [ObservableProperty] public partial ConversationTileViewModel? SelectedConversation { get; set; }
    [ObservableProperty] public partial string SearchQuery { get; set; }
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial bool IsShowingArchived { get; set; }
    [ObservableProperty] public partial SocketState ConnectionState { get; set; }
    [ObservableProperty] public partial bool IsSyncing { get; set; }

    public ConversationListViewModel(
        IChatsService chatsService,
        IContactResolverService contacts,
        IActionHandler actionHandler,
        ISocketService socketService,
        IIncomingMessageProcessor incomingProcessor,
        ISyncService syncService,
        IWindowStateService windowState,
        AppSettings appSettings)
    {
        _chatsService = chatsService;
        _contacts = contacts;
        _actionHandler = actionHandler;
        _socketService = socketService;
        _incomingProcessor = incomingProcessor;
        _syncService = syncService;
        _windowState = windowState;
        _appSettings = appSettings;
        SearchQuery = string.Empty;

        _appSettings.PropertyChanged += OnAppearanceSettingChanged;

        _chatsService.ChatsChanged += (_, _) => RunOnUI(RebuildList);
        _chatsService.ChatUpdated += (_, guid) => RunOnUI(() => RefreshTile(guid));
        _chatsService.ArchivedChatsChanged += (_, _) => RunOnUI(RebuildArchivedList);
        _contacts.ContactsChanged += (_, _) => RunOnUI(RebuildList);

        _incomingProcessor.MessageProcessed += OnIncomingMessageProcessed;
        _actionHandler.MessageUpdated += OnMessageUpdated;
        _actionHandler.ChatReadStatusChanged += OnChatReadStatusChanged;
        _actionHandler.ChatUpdated += OnChatUpdated;

        if (_socketService is ObservableObject observable)
        {
            observable.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ISocketService.State))
                    RunOnUI(() => ConnectionState = _socketService.State);
            };
        }
        ConnectionState = _socketService.State;

        _syncService.SyncStateChanged += (_, syncing) =>
            RunOnUI(() => IsSyncing = syncing);
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            await _contacts.LoadContactsAsync();
            await _chatsService.LoadChatsAsync();
        }
        finally
        {
            IsLoading = false;
        }

        if (_socketService.State != SocketState.Connected)
        {
            try { await _socketService.ConnectAsync(); }
            catch { /* socket will retry via built-in reconnection */ }
        }
    }

    partial void OnSearchQueryChanged(string value) => ApplyFilter();

    partial void OnSelectedConversationChanged(ConversationTileViewModel? value)
    {
        // Single source of truth for "which chat is on screen", consumed by the notification
        // suppression logic. Setting null on deselection (e.g. navigating back to the list) is the
        // point — otherwise the last-opened chat would stay suppressed while it's no longer visible.
        _windowState.SetActiveChatGuid(value?.ChatGuid);

        if (value is not null)
            _ = _chatsService.MarkChatReadAsync(value.ChatGuid, true);
    }

    private void RebuildList()
    {
        var previousGuid = SelectedConversation?.ChatGuid;
        var chats = _chatsService.Chats;

        var existingByGuid = _allTiles.ToDictionary(t => t.ChatGuid);

        _allTiles = chats.Select(c =>
        {
            if (existingByGuid.TryGetValue(c.Chat.Guid, out var existing))
            {
                existing.Refresh(c);
                return existing;
            }
            return new ConversationTileViewModel(c, _contacts, _appSettings);
        }).ToList();

        ApplyFilter();

        if (previousGuid is not null)
            SelectedConversation = _allTiles.FirstOrDefault(t => t.ChatGuid == previousGuid);
    }

    private void ApplyFilter()
    {
        var source = IsShowingArchived ? _archivedTiles : _allTiles;
        var filtered = string.IsNullOrWhiteSpace(SearchQuery)
            ? source
            : source.Where(t =>
                t.DisplayName.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase) ||
                t.Preview.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase))
                .ToList();

        if (IsShowingArchived)
        {
            SyncCollection(PinnedConversations, []);
            SyncCollection(Conversations, filtered);
        }
        else
        {
            var pinned = filtered.Where(t => t.IsPinned).ToList();
            var unpinned = filtered.Where(t => !t.IsPinned).ToList();
            SyncCollection(PinnedConversations, pinned);
            SyncCollection(Conversations, unpinned);
        }
    }

    private void RefreshTile(string chatGuid)
    {
        var data = _chatsService.Chats.FirstOrDefault(c => c.Chat.Guid == chatGuid);
        if (data is null) return;

        var tile = _allTiles.FirstOrDefault(t => t.ChatGuid == chatGuid);
        tile?.Refresh(data);
    }

    private async void OnIncomingMessageProcessed(object? sender, IncomingMessageProcessedEventArgs e)
    {
        if (!e.IsFromMe && SelectedConversation?.ChatGuid == e.ChatGuid)
        {
            try { await _chatsService.MarkChatReadAsync(e.ChatGuid, true); }
            catch { }
        }
    }

    private void OnMessageUpdated(object? sender, MessageEventArgs e)
    {
        var chatGuid = e.Message.Chats?.FirstOrDefault()?.Guid;
        if (chatGuid is not null)
            RunOnUI(() => RefreshTile(chatGuid));
    }

    private async void OnChatReadStatusChanged(object? sender, ChatReadStatusPayload e)
    {
        await _chatsService.MarkChatReadAsync(e.ChatGuid, e.Read, notifyServer: false);
    }

    private async void OnChatUpdated(object? sender, ChatUpdatedEventArgs e)
    {
        await _chatsService.LoadChatsAsync();
    }

    [RelayCommand]
    private async Task MarkReadAsync(string chatGuid)
    {
        await _chatsService.MarkChatReadAsync(chatGuid, true);
    }

    [RelayCommand]
    private async Task TogglePinAsync(string chatGuid)
    {
        await _chatsService.TogglePinAsync(chatGuid);
    }

    [RelayCommand]
    private async Task ArchiveAsync(string chatGuid)
    {
        await _chatsService.ArchiveChatAsync(chatGuid);
    }

    [RelayCommand]
    private async Task UnarchiveAsync(string chatGuid)
    {
        await _chatsService.UnarchiveChatAsync(chatGuid);
    }

    [RelayCommand]
    private async Task DeleteAsync(string chatGuid)
    {
        await _chatsService.DeleteChatAsync(chatGuid);
    }

    [RelayCommand]
    private async Task ReorderPinsAsync(List<string> chatGuids)
    {
        await _chatsService.ReorderPinsAsync(chatGuids);
    }

    private void RebuildArchivedList()
    {
        var chats = _chatsService.ArchivedChats;
        var existingByGuid = _archivedTiles.ToDictionary(t => t.ChatGuid);

        _archivedTiles = chats.Select(c =>
        {
            if (existingByGuid.TryGetValue(c.Chat.Guid, out var existing))
            {
                existing.Refresh(c);
                return existing;
            }
            return new ConversationTileViewModel(c, _contacts, _appSettings);
        }).ToList();

        if (IsShowingArchived)
            ApplyFilter();
    }

    partial void OnIsShowingArchivedChanged(bool value)
    {
        if (value)
            _ = LoadArchivedAsync();
        else
            RebuildList();
    }

    private async Task LoadArchivedAsync()
    {
        IsLoading = true;
        try
        {
            await _chatsService.LoadArchivedChatsAsync();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static void SyncCollection(ObservableCollection<ConversationTileViewModel> target,
        List<ConversationTileViewModel> source)
    {
        for (var i = 0; i < source.Count; i++)
        {
            if (i < target.Count)
            {
                if (target[i].ChatGuid != source[i].ChatGuid)
                    target[i] = source[i];
            }
            else
            {
                target.Add(source[i]);
            }
        }

        while (target.Count > source.Count)
            target.RemoveAt(target.Count - 1);
    }

    private void OnAppearanceSettingChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.DenseChatTiles):
            case nameof(AppSettings.HideDividers):
            case nameof(AppSettings.StatusIndicatorsOnChats):
                RunOnUI(() =>
                {
                    foreach (var t in _allTiles) t.ApplyAppearance(_appSettings);
                    foreach (var t in _archivedTiles) t.ApplyAppearance(_appSettings);
                });
                break;
        }
    }

    private static void RunOnUI(Action action)
    {
        var dispatcher = App.MainWindow?.DispatcherQueue;
        if (dispatcher is not null)
            dispatcher.TryEnqueue(() => action());
        else
            action();
    }
}
