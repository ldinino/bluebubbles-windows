using BlueBubbles.Windows.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using global::Windows.ApplicationModel.DataTransfer;
using global::Windows.Storage;
using global::Windows.Storage.Pickers;
using global::Windows.Storage.Streams;

namespace BlueBubbles.Windows.Views;

public sealed partial class FullscreenMediaPage : Page
{
    private AttachmentViewModel? _attachment;
    private string? _localPath;

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
        if (_localPath is not null)
        {
            // Cap the decode width so a multi-megapixel phone photo doesn't sit
            // in memory at full resolution. 2560px still leaves headroom for the
            // 5x context-menu zoom without being presumptuous about hardware.
            FullImage.Source = await Helpers.ImageLoader.FromFileAsync(_localPath, 2560);
        }
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
