using BlueBubbles.Windows.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace BlueBubbles.Windows.Controls;

public sealed partial class AttachmentHolder : UserControl
{
    private AttachmentViewModel? _vm;
    private long _bindGeneration;
    private string? _loadedImagePath;
    private string? _loadedVideoPath;

    public event EventHandler<AttachmentViewModel>? ImageClicked;

    public AttachmentHolder()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        Detach();

        if (args.NewValue is AttachmentViewModel vm)
        {
            _vm = vm;
            vm.PropertyChanged += OnVmPropertyChanged;
            ApplyState(vm);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // A genuine unload detached us without a DataContext change, so DataContextChanged
        // won't re-fire when the same container re-enters the tree — re-attach from the
        // current DataContext or the bubble stays torn down (blank) forever.
        //
        // Deferred one dispatcher tick: Loaded can fire BEFORE a recycled container is
        // re-linked to its new item, so DataContext may still hold the previous thread's
        // attachment here. Re-attaching synchronously painted that stale image into the
        // fresh bubble (flicker, and duplicated images when the re-link raced wrong). By the
        // time this callback runs, a re-link has fired DataContextChanged and set _vm (no-op
        // below); only a genuinely unchanged DataContext still needs the re-attach.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsLoaded || _vm is not null) return;
            if (DataContext is AttachmentViewModel vm)
            {
                _vm = vm;
                vm.PropertyChanged += OnVmPropertyChanged;
                ApplyState(vm);
            }
        });
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Unloaded dispatches asynchronously, so a recycled container can already be back in
        // the tree — re-bound to a new attachment — when the old removal's Unloaded finally
        // fires. Tearing down then wipes the fresh image (a blank bubble that never recovers
        // until the next rebind). Only detach when we are genuinely out of the tree.
        if (IsLoaded) return;
        Detach();
    }

    private void Detach()
    {
        // Strand any in-flight decode from this binding so it can't land on the next one.
        Interlocked.Increment(ref _bindGeneration);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm = null;
        }
        ImageContent.Source = null;
        VideoThumbnail.Source = null;
        _loadedImagePath = null;
        _loadedVideoPath = null;
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not AttachmentViewModel vm) return;
        DispatcherQueue.TryEnqueue(() => ApplyState(vm));
    }

    private void ApplyState(AttachmentViewModel vm)
    {
        HideAll();

        switch (vm.State)
        {
            case AttachmentState.Cached when vm.Category == AttachmentCategory.Image:
                ShowCachedImage(vm);
                break;

            case AttachmentState.Cached when vm.Category == AttachmentCategory.Video:
                ShowCachedVideo(vm);
                break;

            case AttachmentState.Cached when vm.Category == AttachmentCategory.Audio:
                ShowAudio(vm);
                break;

            case AttachmentState.Cached:
                ShowFile(vm);
                break;

            case AttachmentState.NotDownloaded:
                ShowNotDownloaded(vm);
                break;

            case AttachmentState.Downloading:
                ShowDownloading(vm);
                break;

            case AttachmentState.Error:
                ShowError(vm);
                break;
        }
    }

    // Must match the RootGrid MaxWidth/MaxHeight in AttachmentHolder.xaml — these bound the
    // displayed footprint and the decode target.
    private const double MaxImageWidth = 360;
    private const double MaxImageHeight = 320;

    private void ShowCachedImage(AttachmentViewModel vm)
    {
        ImageContent.Visibility = Visibility.Visible;

        // Cached images are clickable → fullscreen. (ShowFile wires this for other
        // types, but images take this branch, so wire it here too.)
        RootGrid.Tapped -= OnTappedOpenFile;
        RootGrid.Tapped += OnTappedOpenFile;

        if (vm.LocalPath is null) return;

        // Pre-size the Image from the known dimensions so the bubble reserves its final
        // footprint immediately. The bitmap then fills that box without a layout reflow —
        // the reflow-on-arrival (bubble pops from MinHeight to full height) is what reads as
        // a flicker, and it's worst on big images that decode slowly. Always set explicitly
        // (value or NaN) since list containers recycle and could carry a prior image's size.
        var (dispW, dispH) = FitDisplaySize(vm.Width, vm.Height);
        ImageContent.Width = dispW;
        ImageContent.Height = dispH;

        // Already showing this exact image — don't re-decode (and don't blink it).
        if (vm.LocalPath == _loadedImagePath && ImageContent.Source is not null) return;

        var generation = Interlocked.Increment(ref _bindGeneration);
        _loadedImagePath = vm.LocalPath;

        // Decode at the display size in logical pixels: crisp at any DPI, and a high-res
        // photo decodes straight to its ~360px footprint instead of full-res-then-downscale.
        var decodeWidth = double.IsNaN(dispW) ? (int)MaxImageWidth : (int)Math.Ceiling(dispW);

        // Recycle fast-path: if this image is already decoded, assign it synchronously so a
        // scrolled-back-into-view bubble never flashes a blank box waiting on a disk decode.
        var cached = Helpers.ImageLoader.TryGetCached(vm.LocalPath, decodeWidth);
        if (cached is not null)
        {
            ImageContent.Source = cached;
            return;
        }

        _ = LoadImageAsync(vm.LocalPath, generation, decodeWidth);
    }

    private async Task LoadImageAsync(string path, long generation, int decodeWidth)
    {
        var bitmap = await Helpers.ImageLoader.FromFileAsync(path, decodeWidth, decodeLogical: true, cache: true);
        if (Interlocked.Read(ref _bindGeneration) != generation) return;
        ImageContent.Source = bitmap;
    }

    /// <summary>Uniform-fits the source dimensions into the bubble's max box. Doesn't upscale
    /// (matches the prior natural-size behavior for small images). Returns NaN when the
    /// dimensions are unknown, so the Image auto-sizes to the decoded bitmap as before.</summary>
    private static (double Width, double Height) FitDisplaySize(int? width, int? height)
    {
        if (width is not > 0 || height is not > 0)
            return (double.NaN, double.NaN);

        var scale = Math.Min(MaxImageWidth / width.Value, MaxImageHeight / height.Value);
        scale = Math.Min(scale, 1.0);
        return (width.Value * scale, height.Value * scale);
    }

    private void ShowCachedVideo(AttachmentViewModel vm)
    {
        VideoContent.Visibility = Visibility.Visible;

        // Tapping a video opens the in-app fullscreen player (same flow as images).
        RootGrid.Tapped -= OnTappedOpenFile;
        RootGrid.Tapped += OnTappedOpenFile;

        if (vm.LocalPath is null) return;

        // Reserve the final footprint up front (same reasoning as images) so the poster
        // doesn't reflow the bubble when it arrives.
        var (dispW, dispH) = FitDisplaySize(vm.Width, vm.Height);
        VideoThumbnail.Width = dispW;
        VideoThumbnail.Height = dispH;

        // Already showing this video's poster — don't re-extract it.
        if (vm.LocalPath == _loadedVideoPath && VideoThumbnail.Source is not null) return;

        var generation = Interlocked.Increment(ref _bindGeneration);
        _loadedVideoPath = vm.LocalPath;
        _ = LoadVideoThumbnailAsync(vm.LocalPath, generation);
    }

    private async Task LoadVideoThumbnailAsync(string path, long generation)
    {
        // Decode a real frame through Windows' media pipeline so the bubble shows the video's
        // actual first frame (like iMessage), not a placeholder. Fall back to the shell thumbnail
        // only when the codec can't be decoded — and even then reject the shell's generic icon
        // (imageOnly) so we never blow a media-file glyph up to bubble size.
        var poster = await Helpers.ImageLoader.VideoFrameAsync(path, (uint)MaxImageWidth)
                     ?? await Helpers.ImageLoader.ThumbnailAsync(path, (uint)MaxImageWidth, imageOnly: true);
        if (Interlocked.Read(ref _bindGeneration) != generation) return;
        VideoThumbnail.Source = poster;
    }

    private void ShowAudio(AttachmentViewModel vm)
    {
        AudioContent.Visibility = Visibility.Visible;
        AudioDuration.Text = vm.FormattedSize;
    }

    private void ShowFile(AttachmentViewModel vm)
    {
        FileContent.Visibility = Visibility.Visible;
        FileName.Text = vm.DisplayName;
        FileSize.Text = vm.FormattedSize;
        FileIcon.Glyph = GetFileGlyph(vm.Category);

        if (vm.State == AttachmentState.Cached)
        {
            RootGrid.Tapped -= OnTappedOpenFile;
            RootGrid.Tapped += OnTappedOpenFile;
        }
    }

    private void ShowNotDownloaded(AttachmentViewModel vm)
    {
        ShowFilePreview(vm);
        DownloadOverlay.Visibility = Visibility.Visible;
        DownloadSizeText.Text = vm.FormattedSize;
    }

    private void ShowDownloading(AttachmentViewModel vm)
    {
        ShowFilePreview(vm);
        ProgressOverlay.Visibility = Visibility.Visible;
        ProgressText.Text = $"{vm.Progress:F0}%";
    }

    private void ShowError(AttachmentViewModel vm)
    {
        ShowFilePreview(vm);
        ErrorOverlay.Visibility = Visibility.Visible;
    }

    private void ShowFilePreview(AttachmentViewModel vm)
    {
        FileContent.Visibility = Visibility.Visible;
        FileName.Text = vm.DisplayName;
        FileSize.Text = vm.FormattedSize;
        FileIcon.Glyph = GetFileGlyph(vm.Category);
    }

    private void HideAll()
    {
        ImageContent.Visibility = Visibility.Collapsed;
        VideoContent.Visibility = Visibility.Collapsed;
        AudioContent.Visibility = Visibility.Collapsed;
        FileContent.Visibility = Visibility.Collapsed;
        DownloadOverlay.Visibility = Visibility.Collapsed;
        ProgressOverlay.Visibility = Visibility.Collapsed;
        ErrorOverlay.Visibility = Visibility.Collapsed;
        // NOTE: deliberately does NOT null ImageContent.Source — keeping the loaded bitmap means a
        // redundant state re-apply for the same image doesn't blink it. Detach() clears it on recycle.
        RootGrid.Tapped -= OnTappedOpenFile;
    }

    private void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        if (_vm is not null)
            _ = _vm.DownloadAsync();
    }

    private void OnAudioPlayClick(object sender, RoutedEventArgs e)
    {
        if (_vm?.LocalPath is null) return;
        _ = global::Windows.System.Launcher.LaunchUriAsync(new Uri(_vm.LocalPath));
    }

    private async void OnTappedOpenFile(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (_vm is null) return;

        // Images and videos open in the in-app fullscreen viewer/player. The video
        // player itself falls back to the external player if Windows can't decode it.
        if (_vm.Category is AttachmentCategory.Image or AttachmentCategory.Video)
        {
            if (_vm.Category == AttachmentCategory.Image)
                ImageClicked?.Invoke(this, _vm);

            var frame = FindParentFrame();
            if (frame is not null)
                frame.Navigate(typeof(Views.FullscreenMediaPage), _vm);
            return;
        }

        if (_vm.LocalPath is not null)
        {
            try
            {
                var file = await global::Windows.Storage.StorageFile.GetFileFromPathAsync(_vm.LocalPath);
                await global::Windows.System.Launcher.LaunchFileAsync(file);
            }
            catch { }
        }
    }

    private Microsoft.UI.Xaml.Controls.Frame? FindParentFrame()
    {
        DependencyObject? current = this;
        while (current is not null)
        {
            if (current is Microsoft.UI.Xaml.Controls.Frame frame)
                return frame;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private static string GetFileGlyph(AttachmentCategory category) => category switch
    {
        AttachmentCategory.Image => "",
        AttachmentCategory.Video => "",
        AttachmentCategory.Audio => "",
        _ => ""
    };
}
