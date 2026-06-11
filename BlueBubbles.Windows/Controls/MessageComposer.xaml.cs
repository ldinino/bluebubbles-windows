using BlueBubbles.Windows.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
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
    public event EventHandler? ScheduleRequested;
    /// <summary>Raised when the send button's context flyout opens, so the page can refresh
    /// <see cref="IsScheduleEnabled"/> from the view model's current state.</summary>
    public event EventHandler? ScheduleMenuOpening;

    public MessageComposer()
    {
        InitializeComponent();
    }

    public string Text
    {
        get => InputBox.Text;
        set => InputBox.Text = value;
    }

    public string PlaceholderText
    {
        get => InputBox.PlaceholderText;
        set => InputBox.PlaceholderText = value;
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

    /// <summary>Enables/disables "Send later…" (scheduling is text-only; reply/edit excluded).</summary>
    public bool IsScheduleEnabled
    {
        get => SendLaterItem.IsEnabled;
        set
        {
            SendLaterItem.IsEnabled = value;
            ToolTipService.SetToolTip(SendLaterItem,
                value ? null : "Scheduling supports text-only messages");
        }
    }

    private void OnSendFlyoutOpening(object sender, object e)
    {
        ScheduleMenuOpening?.Invoke(this, EventArgs.Empty);
    }

    private void OnSendLaterClick(object sender, RoutedEventArgs e)
    {
        ScheduleRequested?.Invoke(this, EventArgs.Empty);
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

    // Paste-to-send: a pasted bitmap (e.g. a screenshot) or pasted file(s) are staged as
    // attachments instead of going into the text box. Plain text falls through to the default
    // TextBox paste handling. Covers the "screenshot, paste, send" flow.
    private async void OnInputPaste(object sender, TextControlPasteEventArgs e)
    {
        DataPackageView view;
        try { view = Clipboard.GetContent(); }
        catch { return; }  // clipboard busy/locked — let the default handler try

        // Files copied from Explorer keep their original name/format — stage them directly.
        if (view.Contains(StandardDataFormats.StorageItems))
        {
            e.Handled = true;
            try
            {
                var items = await view.GetStorageItemsAsync();
                foreach (var file in items.OfType<StorageFile>())
                    AttachmentPicked?.Invoke(this, file.Path);
            }
            catch { /* paste failed — nothing staged */ }
            return;
        }

        // A raw bitmap (screenshot, "copy image") has no file on disk — write one ourselves.
        if (view.Contains(StandardDataFormats.Bitmap))
        {
            e.Handled = true;
            try
            {
                var reference = await view.GetBitmapAsync();
                using var stream = await reference.OpenReadAsync();
                var path = await SavePastedBitmapAsync(stream);
                if (path is not null)
                    AttachmentPicked?.Invoke(this, path);
            }
            catch { /* decode/encode failed — nothing staged */ }
        }

        // Otherwise (plain text, etc.) leave e.Handled false so the TextBox pastes normally.
    }

    private static async Task<string?> SavePastedBitmapAsync(global::Windows.Storage.Streams.IRandomAccessStream stream)
    {
        // Re-encode to PNG so the file is a valid image the server recognises, regardless of the
        // clipboard's source format.
        var decoder = await BitmapDecoder.CreateAsync(stream);
        // SoftwareBitmap holds unmanaged pixel memory; dispose deterministically rather than
        // leaving a full-resolution frame to the finalizer on every paste.
        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BlueBubbles", "outgoing");
        Directory.CreateDirectory(dir);

        var fileName = $"Pasted image {DateTime.Now:yyyy-MM-dd HHmmss}.png";
        var folder = await StorageFolder.GetFolderFromPathAsync(dir);
        var file = await folder.CreateFileAsync(fileName, CreationCollisionOption.GenerateUniqueName);

        using (var outStream = await file.OpenAsync(FileAccessMode.ReadWrite))
        {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outStream);
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync();
        }

        return file.Path;
    }
}
