using BlueBubbles.Core.Configuration;
using BlueBubbles.Core.Models;
using BlueBubbles.Windows.Converters;
using BlueBubbles.Windows.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BlueBubbles.Windows.Views;

public sealed partial class ConversationListPage : Page
{
    private readonly ConversationListViewModel _vm;
    private readonly AppSettings _settings;
    private bool _restoredSelection;

    public event EventHandler<ConversationTileViewModel>? ConversationSelected;
    public event EventHandler? ConversationUnloadRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? NewChatRequested;

    public ConversationListPage()
    {
        _vm = App.Services.GetRequiredService<ConversationListViewModel>();
        _settings = App.Services.GetRequiredService<AppSettings>();
        InitializeComponent();

        ((DateTimeToRelativeConverter)Resources["RelativeTimeConverter"]).Use24HourFormat = _settings.Use24HrFormat;
        _settings.PropertyChanged += OnSettingsChanged;
        PinnedGrid.SizeChanged += (_, _) => UpdatePinnedGridSizing();

        _vm.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(ConversationListViewModel.IsLoading):
                    DispatcherQueue.TryEnqueue(() =>
                        LoadingRing.IsActive = _vm.IsLoading);
                    break;
                case nameof(ConversationListViewModel.ConnectionState):
                case nameof(ConversationListViewModel.IsSyncing):
                    DispatcherQueue.TryEnqueue(UpdateConnectionBar);
                    break;
                case nameof(ConversationListViewModel.IsShowingArchived):
                    DispatcherQueue.TryEnqueue(UpdateArchivedMode);
                    break;
            }
        };

        _vm.PinnedConversations.CollectionChanged += (_, _) =>
            DispatcherQueue.TryEnqueue(() =>
            {
                PinnedSection.Visibility = _vm.PinnedConversations.Count > 0
                    ? Visibility.Visible : Visibility.Collapsed;
                UpdatePinnedGridSizing();
            });

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ConversationList.ItemsSource = _vm.Conversations;
        PinnedGrid.ItemsSource = _vm.PinnedConversations;
        UpdateConnectionBar();

        await _vm.LoadCommand.ExecuteAsync(null);

        RestoreLastSelectedChat();
        RefreshTimestampFormat();
        UpdatePinnedGridSizing();
    }

    /// <summary>
    /// Re-opens the conversation that was active when the app last closed (Phase 18 session
    /// restore). Only runs on the first load and only when nothing is already selected, so it
    /// never fights a user's in-session navigation.
    /// </summary>
    private void RestoreLastSelectedChat()
    {
        if (_restoredSelection || _vm.SelectedConversation is not null) return;
        _restoredSelection = true;

        var settings = App.Services.GetRequiredService<AppSettings>();
        var guid = settings.LastSelectedChatGuid;
        if (string.IsNullOrEmpty(guid)) return;

        var tile = _vm.Conversations.Concat(_vm.PinnedConversations)
            .FirstOrDefault(t => t.ChatGuid == guid);
        if (tile is null) return;

        _vm.SelectedConversation = tile;
        if (_vm.PinnedConversations.Contains(tile))
            PinnedGrid.SelectedItem = tile;
        else
            ConversationList.SelectedItem = tile;
        ConversationSelected?.Invoke(this, tile);
    }

    private void UpdateConnectionBar()
    {
        switch (_vm.ConnectionState)
        {
            case SocketState.Connected when _vm.IsSyncing:
                ShowConnectionBar("Syncing new messages…", spinner: true, glyph: null,
                    background: "SystemFillColorAttentionBackgroundBrush",
                    foreground: "TextFillColorPrimaryBrush");
                break;
            case SocketState.Connecting:
                ShowConnectionBar("Connecting…", spinner: true, glyph: null,
                    background: "SystemFillColorAttentionBackgroundBrush",
                    foreground: "TextFillColorPrimaryBrush");
                break;
            case SocketState.Error:
            case SocketState.Disconnected:
                ShowConnectionBar("Disconnected from server", spinner: false, glyph: "",
                    background: "SystemFillColorCautionBackgroundBrush",
                    foreground: "SystemFillColorCautionBrush");
                break;
            case SocketState.Connected:
            default:
                ConnectionBar.Visibility = Visibility.Collapsed;
                ConnectionBarRing.IsActive = false;
                break;
        }
    }

    private void ShowConnectionBar(string text, bool spinner, string? glyph, string background, string foreground)
    {
        var fg = ResolveBrush(foreground);

        ConnectionBar.Background = ResolveBrush(background);
        ConnectionBar.Visibility = Visibility.Visible;

        ConnectionBarRing.IsActive = spinner;
        ConnectionBarRing.Visibility = spinner ? Visibility.Visible : Visibility.Collapsed;

        if (glyph is null)
        {
            ConnectionBarIcon.Visibility = Visibility.Collapsed;
        }
        else
        {
            ConnectionBarIcon.Glyph = glyph;
            ConnectionBarIcon.Foreground = fg;
            ConnectionBarIcon.Visibility = Visibility.Visible;
        }

        ConnectionBarText.Text = text;
        ConnectionBarText.Foreground = fg;
    }

    // System theme brushes (SystemFillColor*) live in the framework resource dictionary; look them up
    // defensively so a renamed/missing key degrades to a sane default instead of throwing.
    private Microsoft.UI.Xaml.Media.Brush ResolveBrush(string key)
    {
        if (Application.Current.Resources.TryGetValue(key, out var value)
            && value is Microsoft.UI.Xaml.Media.Brush brush)
            return brush;
        return (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
    }

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            _vm.SearchQuery = sender.Text;
    }

    private void OnConversationItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ConversationTileViewModel tile)
            SelectConversation(tile, owner: ConversationList, other: PinnedGrid);
    }

    private void OnPinnedItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ConversationTileViewModel tile)
            SelectConversation(tile, owner: PinnedGrid, other: ConversationList);
    }

    // Single source of truth for selection: the clicked list owns the highlight and the other list's
    // selection is cleared, so only one tile is ever highlighted. Ctrl+click instead unloads the
    // conversation and returns to the empty state.
    private void SelectConversation(ConversationTileViewModel tile, ListViewBase owner, ListViewBase other)
    {
        if (IsCtrlDown())
        {
            ClearSelection();
            // ListViewBase applies its own click-selection of the clicked item *after* raising
            // ItemClick, which would silently re-highlight the tile we just cleared (B5). Re-clear
            // once the click pipeline has finished.
            DispatcherQueue.TryEnqueue(ClearSelection);
            ConversationUnloadRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        other.SelectedItem = null;
        owner.SelectedItem = tile;
        _vm.SelectedConversation = tile;
        ConversationSelected?.Invoke(this, tile);
    }

    private static bool IsCtrlDown()
    {
        var state = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(global::Windows.System.VirtualKey.Control);
        return (state & global::Windows.UI.Core.CoreVirtualKeyStates.Down)
            == global::Windows.UI.Core.CoreVirtualKeyStates.Down;
    }

    private async void OnMarkReadClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: string guid })
            await _vm.MarkReadCommand.ExecuteAsync(guid);
    }

    private async void OnTogglePinClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: string guid })
            await _vm.TogglePinCommand.ExecuteAsync(guid);
    }

    private async void OnArchiveClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: string guid })
            await _vm.ArchiveCommand.ExecuteAsync(guid);
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string guid }) return;

        // Deleting now writes through to the server (it removes the chat from Messages on the
        // Mac), so it's destructive and deserves a confirmation.
        var confirm = new ContentDialog
        {
            Title = "Delete Conversation",
            Content = "This conversation will be deleted from your devices. This can't be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        if (!await _vm.DeleteChatAsync(guid))
        {
            var error = new ContentDialog
            {
                Title = "Couldn't Delete Conversation",
                Content = "The conversation couldn't be deleted. Check the server connection and try again.",
                CloseButtonText = "OK",
                XamlRoot = XamlRoot
            };
            await error.ShowAsync();
        }
    }

    private async void OnPinnedDragCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        var reorderedGuids = _vm.PinnedConversations.Select(t => t.ChatGuid).ToList();
        await _vm.ReorderPinsCommand.ExecuteAsync(reorderedGuids);
    }

    private void OnArchiveViewClick(object sender, RoutedEventArgs e)
    {
        _vm.IsShowingArchived = true;
    }

    private void OnBackFromArchiveClick(object sender, RoutedEventArgs e)
    {
        _vm.IsShowingArchived = false;
    }

    private async void OnUnarchiveClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: string guid })
            await _vm.UnarchiveCommand.ExecuteAsync(guid);
    }

    private void UpdateArchivedMode()
    {
        var showing = _vm.IsShowingArchived;
        ArchivedHeader.Visibility = showing ? Visibility.Visible : Visibility.Collapsed;
        BottomBar.Visibility = showing ? Visibility.Collapsed : Visibility.Visible;
    }

    // --- Notification deep-link ---
    // A toast body-click asks us to open a specific chat. On a cold start (app launched BY the click)
    // the conversation list may not have populated yet, so when the tile isn't there we watch the
    // lists and retry as they fill, giving up after a few seconds so we never watch forever.
    private string? _pendingDeepLinkGuid;
    private DispatcherTimer? _deepLinkTimeout;

    /// <summary>Opens a conversation by GUID (notification deep-link), reusing the normal selection
    /// path so the chat frame navigates and the tile highlights. If the list hasn't loaded yet, waits
    /// for the target tile to appear (cold start) rather than silently doing nothing.</summary>
    public void SelectChatByGuid(string chatGuid)
    {
        if (string.IsNullOrEmpty(chatGuid)) return;

        StopDeepLinkWatch();                 // cancel any earlier pending deep-link
        if (TrySelectByGuid(chatGuid)) return;

        _pendingDeepLinkGuid = chatGuid;
        _vm.Conversations.CollectionChanged += OnDeepLinkListChanged;
        _vm.PinnedConversations.CollectionChanged += OnDeepLinkListChanged;
        (_deepLinkTimeout ??= CreateDeepLinkTimeout()).Start();
    }

    private bool TrySelectByGuid(string chatGuid)
    {
        // A live search filter can hide the target tile — clear it so the tile materializes, then look.
        if (!string.IsNullOrEmpty(_vm.SearchQuery))
            _vm.SearchQuery = string.Empty;

        // A notification deep-link carries the underlying chat's GUID, which for a merged conversation
        // is a constituent rather than the tile's primary GUID — match on the full constituent set.
        var tile = _vm.Conversations.Concat(_vm.PinnedConversations)
            .FirstOrDefault(t => t.ContainsGuid(chatGuid));
        if (tile is null) return false;

        // Drive the same selection state a user click produces: highlight in the owning list, clear the
        // other, set the VM selection, and raise ConversationSelected so the shell navigates the frame.
        var pinned = _vm.PinnedConversations.Contains(tile);
        ConversationList.SelectedItem = pinned ? null : tile;
        PinnedGrid.SelectedItem = pinned ? tile : null;
        _vm.SelectedConversation = tile;
        ConversationSelected?.Invoke(this, tile);
        return true;
    }

    private void OnDeepLinkListChanged(object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_pendingDeepLinkGuid is not null && TrySelectByGuid(_pendingDeepLinkGuid))
            StopDeepLinkWatch();
    }

    private DispatcherTimer CreateDeepLinkTimeout()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        timer.Tick += (_, _) => StopDeepLinkWatch();
        return timer;
    }

    private void StopDeepLinkWatch()
    {
        _deepLinkTimeout?.Stop();
        if (_pendingDeepLinkGuid is null) return;

        _pendingDeepLinkGuid = null;
        _vm.Conversations.CollectionChanged -= OnDeepLinkListChanged;
        _vm.PinnedConversations.CollectionChanged -= OnDeepLinkListChanged;
    }

    /// <summary>Moves focus to the search box (Ctrl+F accelerator).</summary>
    public void FocusSearch() => SearchBox.Focus(FocusState.Programmatic);

    /// <summary>Clears the current selection in both lists (Escape / Ctrl+click close the open chat).</summary>
    public void ClearSelection()
    {
        _vm.SelectedConversation = null;
        ConversationList.SelectedItem = null;
        PinnedGrid.SelectedItem = null;
    }

    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.Use24HrFormat))
            DispatcherQueue.TryEnqueue(RefreshTimestampFormat);
    }

    /// <summary>Pushes the "24-hour time" setting into the relative-time converter and re-runs the
    /// timestamp bindings on the visible tiles.</summary>
    private void RefreshTimestampFormat()
    {
        ((DateTimeToRelativeConverter)Resources["RelativeTimeConverter"]).Use24HourFormat = _settings.Use24HrFormat;
        foreach (var t in _vm.Conversations) t.RaiseTimestampChanged();
        foreach (var t in _vm.PinnedConversations) t.RaiseTimestampChanged();
    }

    /// <summary>Sizes the pinned grid into 3 columns that fill the pane (no scrollbar), scaling cell
    /// width as the conversation pane is resized.</summary>
    private void UpdatePinnedGridSizing()
    {
        if (PinnedGrid.ItemsPanelRoot is ItemsWrapGrid wrap && PinnedGrid.ActualWidth > 0)
            wrap.ItemWidth = Math.Max(72, (PinnedGrid.ActualWidth - 4) / 3);
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
        => SettingsRequested?.Invoke(this, EventArgs.Empty);

    private void OnNewChatClick(object sender, RoutedEventArgs e)
        => NewChatRequested?.Invoke(this, EventArgs.Empty);
}
