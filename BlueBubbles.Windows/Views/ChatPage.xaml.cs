using System.Linq;
using BlueBubbles.Core.Configuration;
using BlueBubbles.Windows.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace BlueBubbles.Windows.Views;

public sealed partial class ChatPage : Page
{
    private readonly ChatViewModel _vm;
    private readonly AppSettings _settings;
    private ScrollViewer? _scrollViewer;
    private bool _suppressScrollLoadMore;
    private ConversationTileViewModel? _currentTile;

    public event EventHandler<ConversationTileViewModel>? DetailsRequested;

    /// <summary>Raised when the narrow-layout back button is tapped, asking the shell to return
    /// to the conversation list (the list isn't visible alongside the chat in narrow layout).</summary>
    public event EventHandler? BackToListRequested;

    /// <summary>Shows or hides the in-header "back to conversations" button. The shell calls this
    /// to reflect the current adaptive layout: visible only when the list pane is hidden.</summary>
    public void SetNarrow(bool isNarrow)
        => NarrowBackButton.Visibility = isNarrow ? Visibility.Visible : Visibility.Collapsed;

    private void OnBackToListClick(object sender, RoutedEventArgs e)
        => BackToListRequested?.Invoke(this, EventArgs.Empty);

    public ChatPage()
    {
        _vm = App.Services.GetRequiredService<ChatViewModel>();
        _settings = App.Services.GetRequiredService<AppSettings>();
        InitializeComponent();
        Loaded += OnLoaded;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        _vm.NewMessageAppended += OnNewMessageAppended;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _vm.ScrollToMessageRequested += OnScrollToMessageRequested;

        Composer.SendRequested += OnSendRequested;
        Composer.AttachmentPicked += OnAttachmentPicked;
        Composer.AttachmentRemoved += OnAttachmentRemoved;
        Composer.TextChanged += OnComposerTextChanged;
        Composer.ReplyCancelled += OnReplyCancelled;
        Composer.EditCancelled += OnEditCancelled;
        Composer.SendWithReturn = _settings.SendWithReturn;
        Composer.SetStagingSource(_vm.StagedAttachments);

        _vm.StagedAttachments.CollectionChanged += OnStagedAttachmentsChanged;

        if (e.Parameter is ConversationTileViewModel tile)
        {
            _currentTile = tile;
            _suppressScrollLoadMore = true;
            MessagesList.Opacity = 0;
            MessagesList.ItemsSource = null;
            await _vm.LoadChatAsync(tile);
            MessagesList.ItemsSource = _vm.Items;
            MessagesList.UpdateLayout();
            ScrollToInitialPosition();
            MessagesList.Opacity = 1;
            _suppressScrollLoadMore = false;

            // The page is cached (NavigationCacheMode=Required), so Loaded only fires once. Refresh
            // the header and focus the composer here so every switch — not just the first open —
            // shows the right contact and is ready to type.
            UpdateHeader();
            Composer.FocusInput();
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _vm.NewMessageAppended -= OnNewMessageAppended;
        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _vm.ScrollToMessageRequested -= OnScrollToMessageRequested;

        Composer.SendRequested -= OnSendRequested;
        Composer.AttachmentPicked -= OnAttachmentPicked;
        Composer.AttachmentRemoved -= OnAttachmentRemoved;
        Composer.TextChanged -= OnComposerTextChanged;
        Composer.ReplyCancelled -= OnReplyCancelled;
        Composer.EditCancelled -= OnEditCancelled;

        _vm.StagedAttachments.CollectionChanged -= OnStagedAttachmentsChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_scrollViewer is not null)
            _scrollViewer.ViewChanged -= OnScrollViewChanged;

        _scrollViewer = FindDescendant<ScrollViewer>(MessagesList);
        if (_scrollViewer is not null)
            _scrollViewer.ViewChanged += OnScrollViewChanged;

        UpdateHeader();
        Composer.FocusInput();
    }

    private void UpdateHeader()
    {
        HeaderName.Text = _vm.ChatDisplayName;
        HeaderInfo.Text = _vm.ParticipantSummary;
        HeaderAvatar.Initials = _vm.Initials;
        HeaderAvatar.AvatarImage = _vm.AvatarBytes;
        HeaderAvatar.IsGroup = _vm.IsGroupChat;
        HeaderAvatar.GroupInitials1 = _vm.GroupInitials1;
        HeaderAvatar.GroupInitials2 = _vm.GroupInitials2;
        HeaderAvatar.GroupAvatarImage1 = _vm.GroupAvatarBytes1;
        HeaderAvatar.GroupAvatarImage2 = _vm.GroupAvatarBytes2;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            switch (e.PropertyName)
            {
                case nameof(ChatViewModel.ChatDisplayName):
                case nameof(ChatViewModel.ParticipantSummary):
                case nameof(ChatViewModel.Initials):
                case nameof(ChatViewModel.AvatarBytes):
                case nameof(ChatViewModel.IsGroupChat):
                case nameof(ChatViewModel.GroupInitials1):
                case nameof(ChatViewModel.GroupInitials2):
                case nameof(ChatViewModel.GroupAvatarBytes1):
                case nameof(ChatViewModel.GroupAvatarBytes2):
                    UpdateHeader();
                    break;
                case nameof(ChatViewModel.IsTyping):
                    TypingDots.Visibility = _vm.IsTyping ? Visibility.Visible : Visibility.Collapsed;
                    break;
                case nameof(ChatViewModel.IsLoading):
                    LoadingRing.IsActive = _vm.IsLoading;
                    break;
                case nameof(ChatViewModel.CanSend):
                    Composer.IsSendEnabled = _vm.CanSend;
                    break;
                case nameof(ChatViewModel.MessageText):
                    if (Composer.Text != _vm.MessageText)
                        Composer.Text = _vm.MessageText;
                    break;
                case nameof(ChatViewModel.ReplyingTo):
                    if (_vm.ReplyingTo is { } reply)
                    {
                        Composer.ShowReply(reply.SenderLabel, reply.Preview);
                        Composer.FocusInput();
                    }
                    else
                    {
                        Composer.HideReply();
                    }
                    break;
                case nameof(ChatViewModel.EditingMessage):
                    if (_vm.EditingMessage is { } edit)
                    {
                        Composer.ShowEdit(edit.OriginalText);
                        Composer.FocusInput();
                    }
                    else
                    {
                        Composer.HideEdit();
                    }
                    break;
            }
        });
    }

    private void OnReplyCancelled(object? sender, EventArgs e)
    {
        _vm.CancelReplyCommand.Execute(null);
    }

    private void OnEditCancelled(object? sender, EventArgs e)
    {
        _vm.CancelEditCommand.Execute(null);
    }

    private void OnScrollToMessageRequested(object? sender, string guid)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var target = _vm.Items.OfType<MessageBubbleViewModel>()
                .FirstOrDefault(b => b.MessageGuid == guid);
            if (target is not null)
                MessagesList.ScrollIntoView(target, ScrollIntoViewAlignment.Leading);
        });
    }

    private void OnNewMessageAppended(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (IsNearBottom())
            {
                ScrollToBottom();
            }
            else
            {
                ScrollToBottomBtn.Visibility = Visibility.Visible;
            }
        });
    }

    private async void OnScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv || e.IsIntermediate) return;

        var nearBottom = IsNearBottom();
        DispatcherQueue.TryEnqueue(() =>
        {
            ScrollToBottomBtn.Visibility = nearBottom ? Visibility.Collapsed : Visibility.Visible;
        });

        if (!_suppressScrollLoadMore && sv.VerticalOffset < 50 && _vm.HasMoreMessages && !_vm.IsLoading)
        {
            await _vm.LoadMoreMessagesCommand.ExecuteAsync(null);
        }
    }

    private void OnScrollToBottomClick(object sender, RoutedEventArgs e)
    {
        ScrollToBottom();
        ScrollToBottomBtn.Visibility = Visibility.Collapsed;
    }

    private void OnInfoClick(object sender, RoutedEventArgs e)
    {
        if (_currentTile is not null)
            DetailsRequested?.Invoke(this, _currentTile);
    }

    private void OnSendRequested(object? sender, EventArgs e)
    {
        _vm.SendMessageCommand.Execute(null);
    }

    private void OnComposerTextChanged(object? sender, string text)
    {
        _vm.MessageText = text;
    }

    private void OnAttachmentPicked(object? sender, string filePath)
    {
        _vm.AddStagedAttachment(filePath);
        Composer.UpdateStagingVisibility(true);
    }

    private void OnAttachmentRemoved(object? sender, ViewModels.StagedAttachment attachment)
    {
        _vm.RemoveStagedAttachment(attachment);
        Composer.UpdateStagingVisibility(_vm.StagedAttachments.Count > 0);
    }

    private void OnStagedAttachmentsChanged(object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
            Composer.UpdateStagingVisibility(_vm.StagedAttachments.Count > 0));
    }

    private void ScrollToBottom()
    {
        if (_vm.Items.Count > 0)
            MessagesList.ScrollIntoView(_vm.Items[^1]);
    }

    /// <summary>Opens the thread at the bottom, or — when "Scroll to last unread" is enabled and the
    /// chat has unread messages — at the first unread message.</summary>
    private void ScrollToInitialPosition()
    {
        if (_settings.ScrollToLastUnread && _vm.FirstUnreadGuid is { } guid)
        {
            var target = _vm.Items.OfType<MessageBubbleViewModel>()
                .FirstOrDefault(b => b.MessageGuid == guid);
            if (target is not null)
            {
                MessagesList.ScrollIntoView(target, ScrollIntoViewAlignment.Leading);
                return;
            }
        }
        ScrollToBottom();
    }

    private bool IsNearBottom()
    {
        if (_scrollViewer is null) return true;
        return _scrollViewer.VerticalOffset >= _scrollViewer.ScrollableHeight - 100;
    }

    private static T? FindDescendant<T>(DependencyObject element) where T : DependencyObject
    {
        if (element is T match) return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            var result = FindDescendant<T>(VisualTreeHelper.GetChild(element, i));
            if (result is not null) return result;
        }
        return null;
    }
}
