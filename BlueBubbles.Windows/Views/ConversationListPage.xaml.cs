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
                ConnectionBar.IsOpen = true;
                ConnectionBar.Severity = InfoBarSeverity.Informational;
                ConnectionBar.Title = "Syncing new messages...";
                ConnectionBar.Content = new Microsoft.UI.Xaml.Controls.ProgressRing
                {
                    IsActive = true,
                    Width = 16,
                    Height = 16
                };
                break;
            case SocketState.Connected:
                ConnectionBar.IsOpen = false;
                ConnectionBar.Content = null;
                break;
            case SocketState.Connecting:
                ConnectionBar.IsOpen = true;
                ConnectionBar.Severity = InfoBarSeverity.Informational;
                ConnectionBar.Title = "Connecting...";
                ConnectionBar.Content = null;
                break;
            case SocketState.Error:
            case SocketState.Disconnected:
                ConnectionBar.IsOpen = true;
                ConnectionBar.Severity = InfoBarSeverity.Warning;
                ConnectionBar.Title = "Disconnected from server";
                ConnectionBar.Content = null;
                break;
        }
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
        if (sender is MenuFlyoutItem { Tag: string guid })
            await _vm.DeleteCommand.ExecuteAsync(guid);
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
