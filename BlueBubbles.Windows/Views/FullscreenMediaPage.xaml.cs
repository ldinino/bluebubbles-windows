using BlueBubbles.Windows.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using global::Windows.ApplicationModel.DataTransfer;
using global::Windows.Media.Core;
using global::Windows.Media.Playback;
using global::Windows.Storage;
using global::Windows.Storage.Pickers;
using global::Windows.Storage.Streams;

namespace BlueBubbles.Windows.Views;

public sealed partial class FullscreenMediaPage : Page
{
    private AttachmentViewModel? _attachment;
    private string? _localPath;
    private MediaPlayer? _player;
    private bool _fellBackToExternal;

    public FullscreenMediaPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not AttachmentViewModel vm) return;

        _attachment = vm;
        FileNameText.Text = vm.DisplayName;

        // May be opened straight from the media gallery before it's cached.
        if (vm.LocalPath is null && vm.State != AttachmentState.Cached)
            await vm.DownloadAsync();

        _localPath = vm.LocalPath;
        if (_localPath is null) return;

        if (vm.Category == AttachmentCategory.Video)
            await ShowVideoAsync(_localPath);
        else
            // Cap the decode width so a multi-megapixel phone photo doesn't sit
            // in memory at full resolution. 2560px still leaves headroom for the
            // 5x context-menu zoom without being presumptuous about hardware.
            FullImage.Source = await Helpers.ImageLoader.FromFileAsync(_localPath, 2560);
    }

    // Best-effort inline playback through Windows' built-in codecs. We don't probe the
    // codec up front (there's no cheap way to); instead we hand the file to MediaPlayer
    // and let it tell us via MediaFailed if it can't decode, then hand off to the external
    // player. No ffmpeg / bundled decoders — see PUNCHLIST AT2.
    private async Task ShowVideoAsync(string path)
    {
        ImageScroller.Visibility = Visibility.Collapsed;
        VideoPlayer.Visibility = Visibility.Visible;

        StorageFile file;
        try { file = await StorageFile.GetFileFromPathAsync(path); }
        catch { return; }

        _player = new MediaPlayer { AutoPlay = true };
        _player.MediaFailed += OnVideoMediaFailed;
        _player.Source = MediaSource.CreateFromStorageFile(file);
        VideoPlayer.SetMediaPlayer(_player);
    }

    private void OnVideoMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        // Unsupported codec (or a decode error): fall back to the external player once.
        if (_fellBackToExternal || _localPath is null) return;
        _fellBackToExternal = true;

        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(_localPath);
                await global::Windows.System.Launcher.LaunchFileAsync(file);
            }
            catch { }

            if (Frame.CanGoBack) Frame.GoBack();
        });
    }

    // Tear the player down on the way out so the file handle is released and audio
    // stops the moment the page is left (back button, navigation, or window close).
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        if (_player is null) return;

        _player.MediaFailed -= OnVideoMediaFailed;
        VideoPlayer.SetMediaPlayer(null);
        _player.Dispose();
        _player = null;
    }

    // Cap the image to the visible viewport so Stretch="Uniform" fits it on
    // screen at zoom 1.0. The ScrollViewer otherwise measures content with
    // infinite space, letting the Image expand to its full pixel resolution.
    private void OnScrollerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        FullImage.MaxWidth = e.NewSize.Width;
        FullImage.MaxHeight = e.NewSize.Height;
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack) Frame.GoBack();
    }

    private void OnZoomInClick(object sender, RoutedEventArgs e) => Zoom(1.4f);

    private void OnZoomOutClick(object sender, RoutedEventArgs e) => Zoom(1f / 1.4f);

    private void OnZoomResetClick(object sender, RoutedEventArgs e)
    {
        ImageScroller.ChangeView(null, null, 1.0f);
    }

    // Step the zoom factor while keeping the current viewport center fixed.
    // Offsets are in zoomed-content pixels, so we map the center back to
    // unzoomed content coordinates and re-project it at the target factor.
    private void Zoom(float multiplier)
    {
        var current = ImageScroller.ZoomFactor;
        var target = Math.Clamp(current * multiplier,
            ImageScroller.MinZoomFactor, ImageScroller.MaxZoomFactor);
        if (Math.Abs(target - current) < 0.001f) return;

        var centerX = (ImageScroller.HorizontalOffset + ImageScroller.ViewportWidth / 2) / current;
        var centerY = (ImageScroller.VerticalOffset + ImageScroller.ViewportHeight / 2) / current;
        var newH = centerX * target - ImageScroller.ViewportWidth / 2;
        var newV = centerY * target - ImageScroller.ViewportHeight / 2;
        ImageScroller.ChangeView(newH, newV, target);
    }

    private async void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (_localPath is null) return;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(_localPath);
            var data = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            data.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
            data.SetStorageItems([file]);
            Clipboard.SetContent(data);
        }
        catch { }
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_localPath is null) return;

        var picker = new FileSavePicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var ext = Path.GetExtension(_localPath);
        picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
        picker.SuggestedFileName = _attachment?.DisplayName ?? "image";
        picker.FileTypeChoices.Add("File", [string.IsNullOrEmpty(ext) ? ".png" : ext]);

        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        try
        {
            var bytes = await System.IO.File.ReadAllBytesAsync(_localPath);
            await FileIO.WriteBytesAsync(file, bytes);
        }
        catch { }
    }
}
