using BlueBubbles.Core.Configuration;
using BlueBubbles.Windows.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;

namespace BlueBubbles.Windows.Views;

public sealed partial class NewChatPage : Page
{
    private readonly NewChatViewModel _vm;
    private readonly AppSettings _settings;

    public event EventHandler? GoBackRequested;
    public event EventHandler<string>? ChatCreated;

    public NewChatPage()
    {
        _vm = App.Services.GetRequiredService<NewChatViewModel>();
        _settings = App.Services.GetRequiredService<AppSettings>();
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        _vm.Reset();

        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _vm.ChatReady += OnChatReady;
        _vm.Recipients.CollectionChanged += OnRecipientsChanged;

        Composer.SendRequested += OnSendRequested;
        Composer.AttachmentPicked += OnAttachmentPicked;
        Composer.AttachmentRemoved += OnAttachmentRemoved;
        Composer.TextChanged += OnComposerTextChanged;
        Composer.SendWithReturn = _settings.SendWithReturn;
        Composer.SetStagingSource(_vm.StagedAttachments);
        _vm.StagedAttachments.CollectionChanged += OnStagedAttachmentsChanged;

        ResultsList.ItemsSource = _vm.SearchResults;
        ChipsRepeater.ItemsSource = _vm.Recipients;

        RecipientSearchBox.Focus(FocusState.Programmatic);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _vm.ChatReady -= OnChatReady;
        _vm.Recipients.CollectionChanged -= OnRecipientsChanged;

        Composer.SendRequested -= OnSendRequested;
        Composer.AttachmentPicked -= OnAttachmentPicked;
        Composer.AttachmentRemoved -= OnAttachmentRemoved;
        Composer.TextChanged -= OnComposerTextChanged;
        _vm.StagedAttachments.CollectionChanged -= OnStagedAttachmentsChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            switch (e.PropertyName)
            {
                case nameof(NewChatViewModel.ErrorMessage):
                    if (_vm.ErrorMessage is not null)
                    {
                        ErrorBar.Title = _vm.ErrorMessage;
                        ErrorBar.IsOpen = true;
                    }
                    else
                    {
                        ErrorBar.IsOpen = false;
                    }
                    break;

                case nameof(NewChatViewModel.IsSending):
                    Composer.IsEnabled = !_vm.IsSending && _vm.HasRecipients;
                    break;

                case nameof(NewChatViewModel.HasRecipients):
                    Composer.IsEnabled = _vm.HasRecipients && !_vm.IsSending;
                    UpdateSendEnabled();
                    break;

                case nameof(NewChatViewModel.ShowManualAddOption):
                case nameof(NewChatViewModel.ManualAddLabel):
                    UpdateManualAddVisibility();
                    break;
            }
        });
    }

    private void OnRecipientsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ChipsScroller.Visibility = _vm.Recipients.Count > 0
                ? Visibility.Visible : Visibility.Collapsed;
            UpdateEmptyHint();
        });
    }

    private void OnChatReady(object? sender, string chatGuid)
    {
        DispatcherQueue.TryEnqueue(() => ChatCreated?.Invoke(this, chatGuid));
    }

    private void OnGoBackClick(object sender, RoutedEventArgs e)
        => GoBackRequested?.Invoke(this, EventArgs.Empty);

    private void OnRecipientSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _vm.SearchQuery = RecipientSearchBox.Text;
        UpdateEmptyHint();
    }

    private void OnRecipientSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == global::Windows.System.VirtualKey.Enter)
        {
            var text = RecipientSearchBox.Text.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                _vm.AddManualRecipientCommand.Execute(null);
                e.Handled = true;
            }
        }
        else if (e.Key == global::Windows.System.VirtualKey.Back
                 && string.IsNullOrEmpty(RecipientSearchBox.Text)
                 && _vm.Recipients.Count > 0)
        {
            _vm.RemoveRecipientCommand.Execute(_vm.Recipients[^1]);
            e.Handled = true;
        }
    }

    private void OnResultItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ContactAddressItem item)
        {
            _vm.AddRecipientCommand.Execute(item);
            RecipientSearchBox.Focus(FocusState.Programmatic);
        }
    }

    private void OnRemoveChipClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SelectedRecipient recipient })
        {
            _vm.RemoveRecipientCommand.Execute(recipient);
            RecipientSearchBox.Focus(FocusState.Programmatic);
        }
    }

    private void OnManualAddClick(object sender, RoutedEventArgs e)
    {
        _vm.AddManualRecipientCommand.Execute(null);
        RecipientSearchBox.Focus(FocusState.Programmatic);
    }

    private async void OnSendRequested(object? sender, EventArgs e)
    {
        var text = Composer.Text;
        Composer.IsSendEnabled = false;
        await _vm.SendCommand.ExecuteAsync(text);
        if (_vm.ErrorMessage is null)
            Composer.Text = string.Empty;
    }

    private void OnAttachmentPicked(object? sender, string filePath)
    {
        _vm.StageAttachment(filePath);
        UpdateSendEnabled();
    }

    private void OnAttachmentRemoved(object? sender, StagedAttachment attachment)
    {
        _vm.RemoveStagedAttachment(attachment);
        UpdateSendEnabled();
    }

    private void OnComposerTextChanged(object? sender, string text)
    {
        UpdateSendEnabled();
    }

    private void OnStagedAttachmentsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            Composer.UpdateStagingVisibility(_vm.StagedAttachments.Count > 0);
            UpdateSendEnabled();
        });
    }

    private void UpdateSendEnabled()
    {
        var hasContent = !string.IsNullOrWhiteSpace(Composer.Text) || _vm.StagedAttachments.Count > 0;
        Composer.IsSendEnabled = _vm.HasRecipients && hasContent && !_vm.IsSending;
    }

    private void UpdateManualAddVisibility()
    {
        ManualAddPanel.Visibility = _vm.ShowManualAddOption
            ? Visibility.Visible : Visibility.Collapsed;
        if (_vm.ManualAddLabel is not null)
            ManualAddText.Text = _vm.ManualAddLabel;
    }

    private void UpdateEmptyHint()
    {
        var showHint = _vm.Recipients.Count == 0
                       && string.IsNullOrWhiteSpace(RecipientSearchBox.Text)
                       && _vm.SearchResults.Count == 0;
        EmptyHint.Visibility = showHint ? Visibility.Visible : Visibility.Collapsed;
    }

}
