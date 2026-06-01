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

    private void OnConversationSelected(object? sender, ConversationTileViewModel tile)
    {
        // Clicking the conversation that's already open is a no-op — don't reload/flash the thread.
        if (_currentChatGuid == tile.ChatGuid && ChatFrame.Content is ChatPage) return;

        _currentChatGuid = tile.ChatGuid;
        EmptyState.Visibility = Visibility.Collapsed;
        ChatFrame.Visibility = Visibility.Visible;
        ChatFrame.Navigate(typeof(ChatPage), tile);

        // Remember the open conversation so it can be restored on next launch (Phase 18).
        var settings = App.Services.GetRequiredService<AppSettings>();
        if (settings.LastSelectedChatGuid != tile.ChatGuid)
        {
            settings.LastSelectedChatGuid = tile.ChatGuid;
            App.Services.GetRequiredService<ISettingsService>().Save();
        }
    }

    private void OnConversationUnloadRequested(object? sender, EventArgs e) => CloseOpenConversation();

    /// <summary>Tears down the open conversation and returns to the empty state. Shared by the
    /// Escape accelerator and Ctrl+click on a conversation tile.</summary>
    private void CloseOpenConversation()
    {
        _currentChatGuid = null;
        ChatFrame.Content = null;
        ChatFrame.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Visible;
        ConversationListPane.ClearSelection();
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
            chatPage.DetailsRequested += OnDetailsRequested;

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
        if (sender is ChatPage cp)
            cp.DetailsRequested -= OnDetailsRequested;

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
        }
        else
        {
            _currentChatGuid = null;
            ChatFrame.Content = null;
            ChatFrame.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Visible;
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
        }
        else
        {
            _currentChatGuid = null;
            ChatFrame.Content = null;
            ChatFrame.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Visible;
        }
    }

    public void NavigateToSettings()
    {
        OnSettingsRequested(this, EventArgs.Empty);
    }

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
