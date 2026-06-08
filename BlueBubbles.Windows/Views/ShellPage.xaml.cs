using BlueBubbles.Core.Configuration;
using BlueBubbles.Core.Services;
using BlueBubbles.Windows.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;

namespace BlueBubbles.Windows.Views;

public sealed partial class ShellPage : Page
{
    public ShellViewModel ViewModel { get; }
    private bool _hadContentBeforeSettings;

    // GUID of the conversation currently shown in ChatFrame (null = empty state). Used to skip
    // re-loading a chat that's already open.
    private string? _currentChatGuid;

    // Adaptive master/detail. Below this width there's only room for one pane, so we swap between
    // the conversation list and the open chat instead of showing both.
    private const double NarrowThreshold = 768;
    private bool _isNarrow;
    // In narrow layout, true = the list pane is showing, false = the open chat/detail is showing.
    // Ignored in wide layout (both panes are always visible there).
    private bool _narrowShowList = true;

    public ShellPage()
    {
        ViewModel = App.Services.GetRequiredService<ShellViewModel>();
        InitializeComponent();

        ConversationListPane.ConversationSelected += OnConversationSelected;
        ConversationListPane.ConversationUnloadRequested += OnConversationUnloadRequested;
        ConversationListPane.SettingsRequested += OnSettingsRequested;
        ConversationListPane.NewChatRequested += OnNewChatRequested;
        ChatFrame.Navigated += OnChatFrameNavigated;
    }

    // --- Adaptive master/detail layout ---

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool narrow = e.NewSize.Width < NarrowThreshold;
        if (narrow == _isNarrow) return;
        _isNarrow = narrow;

        // When collapsing to narrow, default to whichever pane the user was effectively using:
        // the open chat if there is one, otherwise the list.
        if (_isNarrow)
            _narrowShowList = !(ChatFrame.Visibility == Visibility.Visible && ChatFrame.Content is not null);

        ApplyPaneLayout();
    }

    /// <summary>Positions/shows the panes for the current width and master/detail state. Wide layout
    /// shows the list, divider, and detail side by side; narrow layout shows a single full-width pane.</summary>
    private void ApplyPaneLayout()
    {
        if (!_isNarrow)
        {
            Grid.SetColumn(LeftPane, 0); Grid.SetColumnSpan(LeftPane, 1);
            Grid.SetColumn(RightPane, 2); Grid.SetColumnSpan(RightPane, 1);
            LeftPane.Visibility = Visibility.Visible;
            PaneDivider.Visibility = Visibility.Visible;
            RightPane.Visibility = Visibility.Visible;
        }
        else
        {
            PaneDivider.Visibility = Visibility.Collapsed;
            bool hasChat = ChatFrame.Visibility == Visibility.Visible && ChatFrame.Content is not null;
            if (hasChat && !_narrowShowList)
            {
                Grid.SetColumn(RightPane, 0); Grid.SetColumnSpan(RightPane, 3);
                RightPane.Visibility = Visibility.Visible;
                LeftPane.Visibility = Visibility.Collapsed;
            }
            else
            {
                Grid.SetColumn(LeftPane, 0); Grid.SetColumnSpan(LeftPane, 3);
                LeftPane.Visibility = Visibility.Visible;
                RightPane.Visibility = Visibility.Collapsed;
            }
        }

        if (ChatFrame.Content is ChatPage cp)
            cp.SetNarrow(_isNarrow);
    }

    /// <summary>Narrow layout: bring the open chat/detail pane to the front.</summary>
    private void ShowDetailPane()
    {
        _narrowShowList = false;
        ApplyPaneLayout();
    }

    /// <summary>Narrow layout: bring the conversation list back to the front (chat stays loaded).</summary>
    private void ShowListPane()
    {
        _narrowShowList = true;
        ApplyPaneLayout();
    }

    private void OnConversationSelected(object? sender, ConversationTileViewModel tile)
    {
        // Clicking the conversation that's already open is a no-op — don't reload/flash the thread.
        // In narrow layout, still surface it (the user may have backed out to the list).
        if (_currentChatGuid == tile.ChatGuid && ChatFrame.Content is ChatPage)
        {
            if (_isNarrow) ShowDetailPane();
            return;
        }

        _currentChatGuid = tile.ChatGuid;
        EmptyState.Visibility = Visibility.Collapsed;
        ChatFrame.Visibility = Visibility.Visible;
        ChatFrame.Navigate(typeof(ChatPage), tile);
        ShowDetailPane();

        // Remember the open conversation so it can be restored on next launch (Phase 18).
        var settings = App.Services.GetRequiredService<AppSettings>();
        if (settings.LastSelectedChatGuid != tile.ChatGuid)
        {
            settings.LastSelectedChatGuid = tile.ChatGuid;
            App.Services.GetRequiredService<ISettingsService>().Save();
        }
    }

    private void OnConversationUnloadRequested(object? sender, EventArgs e) => CloseOpenConversation();

    // Narrow-layout back button on the chat header: return to the list, keeping the chat loaded.
    private void OnBackToListRequested(object? sender, EventArgs e) => ShowListPane();

    /// <summary>Tears down the open conversation and returns to the empty state. Shared by the
    /// Escape accelerator and Ctrl+click on a conversation tile.</summary>
    private void CloseOpenConversation()
    {
        _currentChatGuid = null;
        ChatFrame.Content = null;
        ChatFrame.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Visible;
        ConversationListPane.ClearSelection();
        if (_isNarrow) ShowListPane();
    }

    private void OnSettingsRequested(object? sender, EventArgs e)
    {
        // Settings opens as its own full-window navigation context (spec 8.8) so the
        // conversation list is not shown alongside it. ShellPage stays cached
        // (NavigationCacheMode="Required") and is restored when Settings is dismissed.
        App.MainWindow.RootNavigationFrame.Navigate(typeof(SettingsPage));
    }

    private void OnChatFrameNavigated(object sender, NavigationEventArgs e)
    {
        if (e.Content is ChatPage chatPage)
        {
            // ChatPage is NavigationCacheMode="Required", so the Frame reuses one instance across
            // conversations and this handler fires on every (re)navigation. Guard both subscriptions
            // with -=/+= so switching conversations doesn't stack duplicate handlers — otherwise a
            // later Info click would Navigate to the details page once per accumulated handler,
            // pushing extra back-stack entries that each need their own Back press (B2).
            chatPage.DetailsRequested -= OnDetailsRequested;
            chatPage.DetailsRequested += OnDetailsRequested;
            chatPage.BackToListRequested -= OnBackToListRequested;
            chatPage.BackToListRequested += OnBackToListRequested;
            chatPage.SetNarrow(_isNarrow);
        }

        if (e.Content is ChatDetailsPage detailsPage)
        {
            detailsPage.GoBackRequested += OnDetailsGoBack;
            detailsPage.ChatLeft += OnChatLeft;
        }

        if (e.Content is NewChatPage newChatPage)
        {
            newChatPage.GoBackRequested += OnNewChatGoBack;
            newChatPage.ChatCreated += OnNewChatCreated;
        }
    }

    private void OnDetailsRequested(object? sender, ConversationTileViewModel tile)
    {
        // No self-unsubscribe needed: OnChatFrameNavigated re-establishes exactly one subscription
        // each time the cached ChatPage is navigated to (see the -=/+= guard there).
        ChatFrame.Navigate(typeof(ChatDetailsPage), tile);
    }

    private void OnDetailsGoBack(object? sender, EventArgs e)
    {
        if (sender is ChatDetailsPage dp)
        {
            dp.GoBackRequested -= OnDetailsGoBack;
            dp.ChatLeft -= OnChatLeft;
        }

        if (ChatFrame.CanGoBack)
            ChatFrame.GoBack();
    }

    private void OnChatLeft(object? sender, EventArgs e)
    {
        if (sender is ChatDetailsPage dp)
        {
            dp.GoBackRequested -= OnDetailsGoBack;
            dp.ChatLeft -= OnChatLeft;
        }

        _currentChatGuid = null;
        ChatFrame.Content = null;
        ChatFrame.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Visible;
        if (_isNarrow) ShowListPane();

        _ = Task.Run(async () =>
        {
            var chatsService = App.Services.GetRequiredService<IChatsService>();
            await chatsService.LoadChatsAsync();
        });
    }

    private void OnNewChatRequested(object? sender, EventArgs e)
    {
        _hadContentBeforeSettings = ChatFrame.Content is not null;
        EmptyState.Visibility = Visibility.Collapsed;
        ChatFrame.Visibility = Visibility.Visible;
        ChatFrame.Navigate(typeof(NewChatPage));
        ShowDetailPane();
    }

    private void OnNewChatGoBack(object? sender, EventArgs e)
    {
        if (sender is NewChatPage ncp)
        {
            ncp.GoBackRequested -= OnNewChatGoBack;
            ncp.ChatCreated -= OnNewChatCreated;
        }

        if (_hadContentBeforeSettings && ChatFrame.CanGoBack)
        {
            ChatFrame.GoBack();
            if (_isNarrow) ShowDetailPane();
        }
        else
        {
            _currentChatGuid = null;
            ChatFrame.Content = null;
            ChatFrame.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Visible;
            if (_isNarrow) ShowListPane();
        }
    }

    private async void OnNewChatCreated(object? sender, string chatGuid)
    {
        if (sender is NewChatPage ncp)
        {
            ncp.GoBackRequested -= OnNewChatGoBack;
            ncp.ChatCreated -= OnNewChatCreated;
        }

        var chatsService = App.Services.GetRequiredService<IChatsService>();
        await chatsService.LoadChatsAsync();

        var convListVm = App.Services.GetRequiredService<ConversationListViewModel>();
        var tile = convListVm.Conversations
            .Concat(convListVm.PinnedConversations)
            .FirstOrDefault(t => t.ChatGuid == chatGuid);

        if (tile is not null)
        {
            convListVm.SelectedConversation = tile;
            _currentChatGuid = tile.ChatGuid;
            ChatFrame.Navigate(typeof(ChatPage), tile);
            ShowDetailPane();
        }
        else
        {
            _currentChatGuid = null;
            ChatFrame.Content = null;
            ChatFrame.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Visible;
            if (_isNarrow) ShowListPane();
        }
    }

    public void NavigateToSettings()
    {
        OnSettingsRequested(this, EventArgs.Empty);
    }

    /// <summary>Opens a conversation by GUID from a notification deep-link. Delegates to the list pane,
    /// which reuses the normal click path (so the chat frame navigates) and waits for the conversation
    /// to load if the app was cold-started by the toast.</summary>
    public void OpenChat(string chatGuid) => ConversationListPane.SelectChatByGuid(chatGuid);

    // --- Keyboard accelerators (Phase 18) ---

    private void OnNewChatAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        OnNewChatRequested(this, EventArgs.Empty);
    }

    private void OnFindAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ConversationListPane.FocusSearch();
    }

    private void OnEscapeAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // Escape closes the open conversation and returns to the empty state.
        if (ChatFrame.Content is null) return;

        args.Handled = true;
        CloseOpenConversation();
    }

    private void OnDividerDrag(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        var newWidth = LeftColumn.ActualWidth + e.Delta.Translation.X;
        newWidth = Math.Clamp(newWidth, LeftColumn.MinWidth, LeftColumn.MaxWidth);
        LeftColumn.Width = new GridLength(newWidth);
    }
}
