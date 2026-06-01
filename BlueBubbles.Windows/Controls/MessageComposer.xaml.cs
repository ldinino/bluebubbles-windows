using BlueBubbles.Windows.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace BlueBubbles.Windows.Controls;

public sealed partial class MessageComposer : UserControl
{
    public event EventHandler? SendRequested;
    public event EventHandler<string>? AttachmentPicked;
    public event EventHandler<StagedAttachment>? AttachmentRemoved;
    public event EventHandler<string>? TextChanged;
    public event EventHandler? ReplyCancelled;
    public event EventHandler? EditCancelled;

    public MessageComposer()
    {
        InitializeComponent();
    }

    public string Text
    {
        get => InputBox.Text;
        set => InputBox.Text = value;
    }

    public bool IsSendEnabled
    {
        get => SendButton.IsEnabled;
        set
        {
            SendButton.IsEnabled = value;
            SendButton.Style = value
                ? (Style)Application.Current.Resources["AccentButtonStyle"]
                : null;
        }
    }

    public void SetStagingSource(object source)
    {
        StagingRepeater.ItemsSource = source;
    }

    public void UpdateStagingVisibility(bool hasItems)
    {
        StagingScroller.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
    }

    public void FocusInput()
    {
        InputBox.Focus(FocusState.Programmatic);
    }

    public void ShowReply(string sender, string snippet)
    {
        ReplyPreviewSender.Text = $"Replying to {sender}";
        ReplyPreviewSnippet.Text = snippet;
        EditPreviewBar.Visibility = Visibility.Collapsed;   // reply and edit share a row; only one shows
        ReplyPreviewBar.Visibility = Visibility.Visible;
    }

    public void HideReply()
    {
        ReplyPreviewBar.Visibility = Visibility.Collapsed;
    }

    private void OnReplyCancelClick(object sender, RoutedEventArgs e)
    {
        ReplyCancelled?.Invoke(this, EventArgs.Empty);
    }

    public void ShowEdit(string snippet)
    {
        EditPreviewSnippet.Text = snippet;
        ReplyPreviewBar.Visibility = Visibility.Collapsed;   // reply and edit share a row; only one shows
        EditPreviewBar.Visibility = Visibility.Visible;
    }

    public void HideEdit()
    {
        EditPreviewBar.Visibility = Visibility.Collapsed;
    }

    private void OnEditCancelClick(object sender, RoutedEventArgs e)
    {
        EditCancelled?.Invoke(this, EventArgs.Empty);
    }

    public bool SendWithReturn { get; set; }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        TextChanged?.Invoke(this, InputBox.Text);
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == global::Windows.System.VirtualKey.Enter)
        {
            var shift = Microsoft.UI.Input.InputKeyboardSource
                .GetKeyStateForCurrentThread(global::Windows.System.VirtualKey.Shift)
                .HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down);

            if (SendWithReturn && !shift)
            {
                e.Handled = true;
                SendRequested?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private void OnSendClick(object sender, RoutedEventArgs e)
    {
        SendRequested?.Invoke(this, EventArgs.Empty);
    }

    private async void OnPickPhoto(object sender, RoutedEventArgs e)
    {
        await PickFilesAsync(new[] { ".jpg", ".jpeg", ".png", ".gif", ".heic", ".mp4", ".mov" });
    }

    private async void OnPickFile(object sender, RoutedEventArgs e)
    {
        await PickFilesAsync(new[] { "*" });
    }

    private async Task PickFilesAsync(string[] fileTypes)
    {
        var picker = new FileOpenPicker();
        var hwnd = WindowNative.GetWindowHandle(App.MainWindow);
        InitializeWithWindow.Initialize(picker, hwnd);

        foreach (var type in fileTypes)
            picker.FileTypeFilter.Add(type);

        var files = await picker.PickMultipleFilesAsync();
        if (files is null) return;

        foreach (var file in files)
            AttachmentPicked?.Invoke(this, file.Path);
    }

    private void OnRemoveAttachment(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: StagedAttachment attachment })
            AttachmentRemoved?.Invoke(this, attachment);
    }
}
