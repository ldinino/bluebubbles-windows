using BlueBubbles.Windows.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace BlueBubbles.Windows.Views;

public sealed partial class ChatDetailsPage : Page
{
    private readonly ChatDetailsViewModel _vm;

    public event EventHandler? GoBackRequested;
    public event EventHandler? ChatLeft;

    public ChatDetailsPage()
    {
        _vm = App.Services.GetRequiredService<ChatDetailsViewModel>();
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _vm.GoBackRequested += OnVmGoBack;
        _vm.ChatLeft += OnVmChatLeft;
        _vm.MediaAttachments.CollectionChanged += OnMediaChanged;

        if (e.Parameter is ConversationTileViewModel tile)
        {
            await _vm.LoadAsync(tile);
            UpdateUI();
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _vm.GoBackRequested -= OnVmGoBack;
        _vm.ChatLeft -= OnVmChatLeft;
        _vm.MediaAttachments.CollectionChanged -= OnMediaChanged;
    }

    private void UpdateUI()
    {
        DetailsAvatar.Initials = _vm.Initials;
        DetailsAvatar.AvatarImage = _vm.AvatarBytes;
        DetailsAvatar.IsGroup = _vm.IsGroupChat;
        DetailsAvatar.GroupInitials1 = _vm.GroupInitials1;
        DetailsAvatar.GroupInitials2 = _vm.GroupInitials2;
        DetailsAvatar.GroupAvatarImage1 = _vm.GroupAvatarBytes1;
        DetailsAvatar.GroupAvatarImage2 = _vm.GroupAvatarBytes2;

        DisplayNameText.Text = _vm.ChatDisplayName;
        ParticipantCountText.Text = _vm.ParticipantCountText;
        MuteToggle.IsOn = _vm.IsMuted;

        EditNameButton.Visibility = _vm.IsGroupChat ? Visibility.Visible : Visibility.Collapsed;
        AddParticipantPanel.Visibility = _vm.IsGroupChat ? Visibility.Visible : Visibility.Collapsed;
        GroupActionsPanel.Visibility = _vm.IsGroupChat ? Visibility.Visible : Visibility.Collapsed;

        ParticipantsList.ItemsSource = _vm.Participants;
        MediaGrid.ItemsSource = _vm.MediaAttachments;

        UpdateMediaVisibility();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            switch (e.PropertyName)
            {
                case nameof(ChatDetailsViewModel.ChatDisplayName):
                    DisplayNameText.Text = _vm.ChatDisplayName;
                    break;
                case nameof(ChatDetailsViewModel.ParticipantCountText):
                    ParticipantCountText.Text = _vm.ParticipantCountText;
                    break;
                case nameof(ChatDetailsViewModel.IsEditing):
                    DisplayNamePanel.Visibility = _vm.IsEditing ? Visibility.Collapsed : Visibility.Visible;
                    EditNamePanel.Visibility = _vm.IsEditing ? Visibility.Visible : Visibility.Collapsed;
                    if (_vm.IsEditing)
                    {
                        EditNameBox.Text = _vm.EditableName;
                        EditNameBox.Focus(FocusState.Programmatic);
                        EditNameBox.SelectAll();
                    }
                    break;
                case nameof(ChatDetailsViewModel.IsLoading):
                    LoadingRing.IsActive = _vm.IsLoading;
                    break;
                case nameof(ChatDetailsViewModel.StatusMessage):
                    if (_vm.StatusMessage is not null)
                    {
                        StatusBar.Message = _vm.StatusMessage;
                        StatusBar.IsOpen = true;
                    }
                    else
                    {
                        StatusBar.IsOpen = false;
                    }
                    break;
                case nameof(ChatDetailsViewModel.IsMuted):
                    MuteToggle.IsOn = _vm.IsMuted;
                    break;
                case nameof(ChatDetailsViewModel.HasMoreMedia):
                    UpdateMediaVisibility();
                    break;
            }
        });
    }

    private void UpdateMediaVisibility()
    {
        EmptyMediaText.Visibility = _vm.MediaAttachments.Count == 0 && !_vm.HasMoreMedia
            ? Visibility.Visible : Visibility.Collapsed;
        LoadMoreMediaButton.Visibility = _vm.HasMoreMedia && _vm.MediaAttachments.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnMediaChanged(object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(UpdateMediaVisibility);
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
        => GoBackRequested?.Invoke(this, EventArgs.Empty);

    private void OnVmGoBack(object? sender, EventArgs e)
        => GoBackRequested?.Invoke(this, EventArgs.Empty);

    private void OnVmChatLeft(object? sender, EventArgs e)
        => ChatLeft?.Invoke(this, EventArgs.Empty);

    private void OnEditNameClick(object sender, RoutedEventArgs e)
        => _vm.StartEditingCommand.Execute(null);

    private async void OnSaveNameClick(object sender, RoutedEventArgs e)
    {
        _vm.EditableName = EditNameBox.Text;
        await _vm.SaveNameCommand.ExecuteAsync(null);
    }

    private void OnCancelEditClick(object sender, RoutedEventArgs e)
        => _vm.CancelEditingCommand.Execute(null);

    private void OnEditNameKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == global::Windows.System.VirtualKey.Enter)
        {
            _vm.EditableName = EditNameBox.Text;
            _ = _vm.SaveNameCommand.ExecuteAsync(null);
            e.Handled = true;
        }
        else if (e.Key == global::Windows.System.VirtualKey.Escape)
        {
            _vm.CancelEditingCommand.Execute(null);
            e.Handled = true;
        }
    }

    private async void OnMuteToggled(object sender, RoutedEventArgs e)
    {
        if (MuteToggle.IsOn != _vm.IsMuted)
            await _vm.ToggleMuteCommand.ExecuteAsync(null);
    }

    private void OnRemoveParticipantClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string address)
            _ = _vm.RemoveParticipantCommand.ExecuteAsync(address);
    }

    private void OnAddParticipantKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == global::Windows.System.VirtualKey.Enter)
        {
            _vm.NewParticipantAddress = AddParticipantBox.Text;
            _ = _vm.AddParticipantCommand.ExecuteAsync(null);
            e.Handled = true;
        }
    }

    private void OnAddParticipantClick(object sender, RoutedEventArgs e)
    {
        _vm.NewParticipantAddress = AddParticipantBox.Text;
        _ = _vm.AddParticipantCommand.ExecuteAsync(null);
    }

    private async void OnSetIconClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".png");

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        using var stream = (await file.OpenReadAsync()).AsStreamForRead();
        var success = await _vm.SetIconAsync(stream, file.Name);
        if (!success)
            _vm.StatusMessage = "Failed to set group photo";
    }

    private async void OnDeleteIconClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Remove group photo?",
            Content = "The group photo will be removed for all participants.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var success = await _vm.DeleteIconAsync();
        if (!success)
            _vm.StatusMessage = "Failed to remove group photo";
    }

    private async void OnLeaveGroupClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Leave group?",
            Content = "You will no longer receive messages in this group.",
            PrimaryButtonText = "Leave",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        await _vm.LeaveGroupCommand.ExecuteAsync(null);
    }

    private void OnParticipantContainerChanging(ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || !_vm.IsGroupChat) return;
        if (args.ItemContainer?.ContentTemplateRoot is not Grid grid) return;

        foreach (var child in grid.Children)
        {
            if (child is Button btn && btn.Tag is string)
            {
                btn.Visibility = Visibility.Visible;
                break;
            }
        }
    }

    private void OnLoadMoreMediaClick(object sender, RoutedEventArgs e)
        => _ = _vm.LoadMoreMediaCommand.ExecuteAsync(null);

    // Loads each gallery tile's thumbnail as its container is realized, and clears it on
    // recycle. Late downloads are awaited inline; the image's Tag guards against the
    // container being recycled to a different item before the load completes.
    private void OnMediaContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Phase != 0) return;
        if (args.ItemContainer?.ContentTemplateRoot is not FrameworkElement root) return;

        var image = root.FindName("ThumbImage") as Image;
        var overlay = root.FindName("VideoOverlay") as FontIcon;
        if (image is null) return;

        if (args.InRecycleQueue || args.Item is not AttachmentViewModel vm)
        {
            image.Source = null;
            image.Tag = null;
            if (overlay is not null) overlay.Visibility = Visibility.Collapsed;
            return;
        }

        if (overlay is not null)
            overlay.Visibility = vm.Category == AttachmentCategory.Video
                ? Visibility.Visible : Visibility.Collapsed;

        image.Tag = vm; // recycling guard for the async load below
        _ = LoadThumbnailAsync(image, vm);
    }

    private static async Task LoadThumbnailAsync(Image image, AttachmentViewModel vm)
    {
        if (vm.LocalPath is null && vm.State == AttachmentState.NotDownloaded)
            await vm.DownloadAsync();

        if (!ReferenceEquals(image.Tag, vm) || vm.LocalPath is null) return;

        var bitmap = await Helpers.ImageLoader.ThumbnailAsync(vm.LocalPath);
        if (ReferenceEquals(image.Tag, vm))
            image.Source = bitmap;
    }

    private async void OnMediaItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not AttachmentViewModel att) return;

        if (att.Category == AttachmentCategory.Image)
        {
            Frame.Navigate(typeof(FullscreenMediaPage), att);
            return;
        }

        // Video / other: ensure it's downloaded, then open with the default app.
        if (att.LocalPath is null) await att.DownloadAsync();
        if (att.LocalPath is null) return;
        try
        {
            var file = await global::Windows.Storage.StorageFile.GetFileFromPathAsync(att.LocalPath);
            await global::Windows.System.Launcher.LaunchFileAsync(file);
        }
        catch { }
    }
}
