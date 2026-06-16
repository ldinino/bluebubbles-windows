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
        // A merged conversation has several underlying chats on screen — register them all so toasts for
        // any of them are suppressed, and reconcile the read on each.
        _windowState.SetActiveChats(value?.ConstituentGuids);

        if (value is not null)
            foreach (var guid in value.ConstituentGuids)
                _ = _chatsService.MarkChatReadAsync(guid, true);
    }

    private void RebuildList()
    {
        var previousGuid = SelectedConversation?.ChatGuid;
        var merged = ConversationMerger.Merge(_chatsService.Chats, _contacts);

        var existingByGuid = _allTiles.ToDictionary(t => t.ChatGuid);

        _allTiles = merged.Select(m =>
        {
            if (existingByGuid.TryGetValue(m.PrimaryChat.Guid, out var existing))
            {
                existing.Refresh(m);
                return existing;
            }
            return new ConversationTileViewModel(m, _contacts, _appSettings);
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
        // The updated chat may be a constituent of a merged conversation, so re-merge to get the
        // aggregated state and refresh the tile that owns it (keyed by its primary GUID).
        var merged = ConversationMerger.Merge(_chatsService.Chats, _contacts);
        var m = merged.FirstOrDefault(x => x.ConstituentGuids.Contains(chatGuid, StringComparer.OrdinalIgnoreCase));
        if (m is null) return;

        var tile = _allTiles.FirstOrDefault(t => t.ChatGuid == m.PrimaryChat.Guid);
        tile?.Refresh(m);
    }

    private async void OnIncomingMessageProcessed(object? sender, IncomingMessageProcessedEventArgs e)
    {
        // Only auto-mark-read when you're actually looking at the chat: the window must be focused AND
        // this chat the one on screen. Marking read while the window is minimized/in the tray/unfocused
        // would immediately clear the toast we just raised for it — NotificationService drops a chat's
        // toasts as soon as it reads as read. That was the N1 "no notification for the selected chat" bug.
        if (!e.IsFromMe
            && SelectedConversation is not null
            && SelectedConversation.ContainsGuid(e.ChatGuid)
            && _windowState.IsWindowFocused)
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

    /// <summary>Deletes the chat on the server, then locally. Returns false — with local state left
    /// untouched — when the server call fails, so the page can surface the error. A plain method
    /// rather than a [RelayCommand] because the caller needs the result.</summary>
    public Task<bool> DeleteChatAsync(string chatGuid) => _chatsService.DeleteChatAsync(chatGuid);

    [RelayCommand]
    private async Task ReorderPinsAsync(List<string> chatGuids)
    {
        await _chatsService.ReorderPinsAsync(chatGuids);
    }

    private void RebuildArchivedList()
    {
        var merged = ConversationMerger.Merge(_chatsService.ArchivedChats, _contacts);
        var existingByGuid = _archivedTiles.ToDictionary(t => t.ChatGuid);

        _archivedTiles = merged.Select(m =>
        {
            if (existingByGuid.TryGetValue(m.PrimaryChat.Guid, out var existing))
            {
                existing.Refresh(m);
                return existing;
            }
            return new ConversationTileViewModel(m, _contacts, _appSettings);
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

    // Reconcile the observable collection toward the source by identity, emitting the minimum set of
    // Remove/Insert/Move operations rather than positional Replaces. RebuildList reuses the same
    // ConversationTileViewModel instance per chat (keyed by GUID), so reference equality is the right
    // identity here. The payoff: pinning a chat becomes one Remove + one Insert instead of replacing
    // every tile below it — untouched rows keep their realized containers (and already-decoded avatars),
    // and the list transitions animate only the tiles that genuinely moved.
    private static void SyncCollection(ObservableCollection<ConversationTileViewModel> target,
        List<ConversationTileViewModel> source)
    {
        // 1. Drop anything no longer present.
        var desired = new HashSet<ConversationTileViewModel>(source);
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!desired.Contains(target[i]))
                target.RemoveAt(i);
        }

        // 2. Place each source item at its target index, reusing the existing container via Move when the
        //    item is already in the collection (just out of position) and Insert only for genuinely new ones.
        for (var i = 0; i < source.Count; i++)
        {
            var item = source[i];
            if (i < target.Count && ReferenceEquals(target[i], item))
                continue;

            var existing = IndexOfFrom(target, item, i);
            if (existing >= 0)
                target.Move(existing, i);
            else
                target.Insert(i, item);
        }
    }

    private static int IndexOfFrom(ObservableCollection<ConversationTileViewModel> target,
        ConversationTileViewModel item, int start)
    {
        for (var i = start; i < target.Count; i++)
        {
            if (ReferenceEquals(target[i], item))
                return i;
        }
        return -1;
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
