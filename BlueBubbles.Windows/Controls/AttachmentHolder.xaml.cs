using BlueBubbles.Windows.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace BlueBubbles.Windows.Controls;

/// <summary>How a media attachment sizes itself.</summary>
public enum MediaDisplayMode
{
    /// <summary>Aspect-fit inside the max media box — a lone photo keeps its shape.</summary>
    Natural,
    /// <summary>Fixed square, centre-cropped — used for the multi-image collage.</summary>
    Tile
}

/// <summary>
/// Renders one attachment. Images and videos present bare (rounded media, no bubble chrome);
/// audio and other files keep their chip layout.
/// <para>Lifetime model, deliberately simple: <see cref="Render"/> is idempotent and is the only
/// thing that touches visual state. Subscription follows Loaded/Unloaded, the bound view model
/// follows DataContextChanged, and neither ever tears down the displayed bitmap — the previous
/// design cleared the image on Unloaded, which (because Unloaded dispatches asynchronously and can
/// land after a recycled container is already re-bound) wiped freshly-loaded images and left blank
/// bubbles that never recovered.</para>
/// </summary>
public sealed partial class AttachmentHolder : UserControl
{
    // Bounds for a lone photo, sized to sit alongside text bubbles (which cap at 500 wide) rather
    // than dominate the thread. Portrait-friendly: phone photos are usually 3:4, so the height cap
    // is what governs them. (Tunable.)
    public const double MaxMediaWidth = 300;
    public const double MaxMediaHeight = 360;

    // Collage tile: two per row across MaxMediaWidth, with the 4px gap ChatBubble uses.
    public const double TileSize = (MaxMediaWidth - 4) / 2;

    private AttachmentViewModel? _vm;
    private long _bindGeneration;
    private string? _renderedMediaPath;
    private bool _subscribed;

    public event EventHandler<AttachmentViewModel>? ImageClicked;

    /// <summary>Set before the control is bound; changing it re-renders.</summary>
    public MediaDisplayMode DisplayMode { get; set; } = MediaDisplayMode.Natural;

    public AttachmentHolder()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        RootGrid.Tapped += OnTapped;
    }

    // ── Binding / lifetime ──────────────────────────────────────────────────────────────────

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        var next = args.NewValue as AttachmentViewModel;
        if (ReferenceEquals(next, _vm)) return;   // redundant re-link: nothing to do

        Unsubscribe();

        // Strand any in-flight decode from the previous binding so it can't land on this one.
        Interlocked.Increment(ref _bindGeneration);
        _renderedMediaPath = null;
        MediaImage.Source = null;

        _vm = next;
        Subscribe();
        Render();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Subscribe();
        Render();
    }

    // Only drop the subscription — the container is very likely being recycled, and clearing
    // visual state here is what used to blank images.
    private void OnUnloaded(object sender, RoutedEventArgs e) => Unsubscribe();

    private void Subscribe()
    {
        if (_subscribed || _vm is null) return;
        _vm.PropertyChanged += OnVmPropertyChanged;
        _subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!_subscribed || _vm is null) return;
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _subscribed = false;
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        => DispatcherQueue.TryEnqueue(Render);

    // ── Rendering ───────────────────────────────────────────────────────────────────────────

    private void Render()
    {
        var vm = _vm;
        if (vm is null) return;

        var isMedia = vm.Category is AttachmentCategory.Image or AttachmentCategory.Video;

        MediaSurface.Visibility = isMedia ? Visibility.Visible : Visibility.Collapsed;
        AudioContent.Visibility = Visibility.Collapsed;
        FileContent.Visibility = Visibility.Collapsed;

        if (isMedia) SizeMediaSurface(vm);
        else RenderChip(vm);

        DownloadOverlay.Visibility = vm.State == AttachmentState.NotDownloaded
            ? Visibility.Visible : Visibility.Collapsed;
        ProgressOverlay.Visibility = vm.State == AttachmentState.Downloading
            ? Visibility.Visible : Visibility.Collapsed;
        ErrorOverlay.Visibility = vm.State == AttachmentState.Error
            ? Visibility.Visible : Visibility.Collapsed;

        switch (vm.State)
        {
            case AttachmentState.NotDownloaded:
                DownloadSizeText.Text = vm.FormattedSize;
                break;
            case AttachmentState.Downloading:
                ProgressText.Text = $"{vm.Progress:F0}%";
                break;
            case AttachmentState.Error:
                ErrorText.Text = vm.ErrorMessage ?? "Couldn't load this file.";
                // Whatever was showing is meaningless now and would sit behind the overlay.
                MediaImage.Source = null;
                _renderedMediaPath = null;
                break;
            case AttachmentState.Cached when isMedia:
                LoadMedia(vm);
                break;
        }

        // A video only earns its play badge once there's actually a poster behind it.
        PlayBadge.Visibility = vm.Category == AttachmentCategory.Video
            && vm.State == AttachmentState.Cached
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderChip(AttachmentViewModel vm)
    {
        if (vm.Category == AttachmentCategory.Audio && vm.State == AttachmentState.Cached)
        {
            AudioContent.Visibility = Visibility.Visible;
            AudioDuration.Text = vm.FormattedSize;
            return;
        }

        FileContent.Visibility = Visibility.Visible;
        FileName.Text = vm.DisplayName;
        FileSize.Text = vm.FormattedSize;
        FileIcon.Glyph = GetFileGlyph(vm.Category);
    }

    /// <summary>Reserves the media's final footprint before the bitmap arrives, so the thread
    /// doesn't reflow (and visibly jump) when a slow decode lands.</summary>
    private void SizeMediaSurface(AttachmentViewModel vm)
    {
        if (DisplayMode == MediaDisplayMode.Tile)
        {
            MediaSurface.MaxWidth = MediaSurface.Width = TileSize;
            MediaSurface.MaxHeight = MediaSurface.Height = TileSize;
            MediaImage.Stretch = Stretch.UniformToFill;
            return;
        }

        MediaImage.Stretch = Stretch.Uniform;

        // The cap is what actually bounds the picture, and it is set unconditionally on purpose.
        // When the server reports no dimensions we leave Width/Height as NaN so the bitmap sizes
        // itself — but an unconstrained Image sizes to PixelWidth interpreted as DIPs, and a
        // LOGICAL-pixel decode produces scale x more pixels than that. Without these caps every
        // dimensionless photo rendered at double size on a 200% display.
        MediaSurface.MaxWidth = MaxMediaWidth;
        MediaSurface.MaxHeight = MaxMediaHeight;

        var (width, height) = FitDisplaySize(vm.Width, vm.Height, vm.State == AttachmentState.Cached);
        MediaSurface.Width = width;
        MediaSurface.Height = height;
    }

    /// <summary>Uniform-fits the source dimensions into the media box, never upscaling. When the
    /// server reported no dimensions we can't reserve the right shape: an already-cached file is
    /// left to size itself to the decoded bitmap (NaN), while one still to be fetched gets a 4:3
    /// placeholder box so its download button has somewhere to sit.</summary>
    private static (double Width, double Height) FitDisplaySize(int? width, int? height, bool isCached)
    {
        if (width is not > 0 || height is not > 0)
            return isCached
                ? (double.NaN, double.NaN)
                : (MaxMediaWidth, MaxMediaWidth * 0.75);

        var scale = Math.Min(MaxMediaWidth / width.Value, MaxMediaHeight / height.Value);
        scale = Math.Min(scale, 1.0);
        return (width.Value * scale, height.Value * scale);
    }

    private void LoadMedia(AttachmentViewModel vm)
    {
        if (vm.LocalPath is null) return;

        // Already showing this exact file — don't re-decode (and don't blink it).
        if (vm.LocalPath == _renderedMediaPath && MediaImage.Source is not null) return;

        var generation = Interlocked.Increment(ref _bindGeneration);
        _renderedMediaPath = vm.LocalPath;

        // Decode at the display size in logical pixels: crisp at any DPI, and a high-res photo
        // decodes straight to its on-screen footprint instead of full-res-then-downscale.
        var surfaceWidth = MediaSurface.Width;
        var decodeWidth = (int)Math.Ceiling(DisplayMode == MediaDisplayMode.Tile
            ? TileSize
            : (double.IsNaN(surfaceWidth) || surfaceWidth <= 0 ? MaxMediaWidth : surfaceWidth));

        if (vm.Category == AttachmentCategory.Image)
        {
            // Recycle fast-path: an already-decoded bitmap is assigned synchronously, so a bubble
            // scrolled back into view never flashes an empty box waiting on a disk read.
            var cached = Helpers.ImageLoader.TryGetCached(vm.LocalPath, decodeWidth);
            if (cached is not null)
            {
                MediaImage.Source = cached;
                return;
            }
        }

        _ = LoadMediaAsync(vm, vm.LocalPath, decodeWidth, generation);
    }

    private async Task LoadMediaAsync(AttachmentViewModel vm, string path, int decodeWidth, long generation)
    {
        var bitmap = vm.Category == AttachmentCategory.Video
            // Decode a real frame through Windows' media pipeline so a video shows its actual
            // first frame, falling back to the shell thumbnail (never its generic file glyph).
            ? await Helpers.ImageLoader.VideoFrameAsync(path, (uint)decodeWidth)
              ?? await Helpers.ImageLoader.ThumbnailAsync(path, (uint)decodeWidth, imageOnly: true)
            : await Helpers.ImageLoader.FromFileAsync(path, decodeWidth, decodeLogical: true, cache: true);

        if (Interlocked.Read(ref _bindGeneration) != generation)
            return;

        if (bitmap is null)
        {
            ReportUnreadable(vm, path);
            return;
        }

        MediaImage.Source = bitmap;
    }

    // Decode failed even though the file is present: a truncated download, or a codec this machine
    // doesn't have (HEIC without the HEIF extension is the usual culprit). Surface it as a
    // retryable error rather than the silent blank frame this used to produce.
    private void OnMediaImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (_vm is null || _renderedMediaPath is null) return;
        ReportUnreadable(_vm, _renderedMediaPath);
    }

    private void ReportUnreadable(AttachmentViewModel vm, string path)
    {
        Helpers.ImageLoader.Invalidate(path);
        _renderedMediaPath = null;
        vm.MarkUnreadable("Couldn't display this file. It may have downloaded incompletely, "
                          + "or be in a format Windows can't open.");
    }

    // ── Interaction ─────────────────────────────────────────────────────────────────────────

    private void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) _ = _vm.DownloadAsync();
    }

    private void OnRetryClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        Interlocked.Increment(ref _bindGeneration);
        _renderedMediaPath = null;
        MediaImage.Source = null;
        _ = _vm.RetryAsync();
    }

    private void OnAudioPlayClick(object sender, RoutedEventArgs e)
    {
        if (_vm?.LocalPath is null) return;
        _ = global::Windows.System.Launcher.LaunchUriAsync(new Uri(_vm.LocalPath));
    }

    private async void OnTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        // Only a usable, downloaded attachment opens; otherwise the overlay buttons own the tap.
        if (_vm is null || _vm.State != AttachmentState.Cached || _vm.LocalPath is null) return;

        // Images and videos open in the in-app fullscreen viewer/player. The player itself falls
        // back to the external app if Windows can't decode the codec.
        if (_vm.Category is AttachmentCategory.Image or AttachmentCategory.Video)
        {
            if (_vm.Category == AttachmentCategory.Image)
                ImageClicked?.Invoke(this, _vm);

            FindParentFrame()?.Navigate(typeof(Views.FullscreenMediaPage), _vm);
            return;
        }

        try
        {
            var file = await global::Windows.Storage.StorageFile.GetFileFromPathAsync(_vm.LocalPath);
            await global::Windows.System.Launcher.LaunchFileAsync(file);
        }
        catch { }
    }

    private Frame? FindParentFrame()
    {
        DependencyObject? current = this;
        while (current is not null)
        {
            if (current is Frame frame) return frame;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private static string GetFileGlyph(AttachmentCategory category) => category switch
    {
        AttachmentCategory.Image => "\uEB9F",
        AttachmentCategory.Video => "\uE714",
        AttachmentCategory.Audio => "\uE8D6",
        _ => "\uE7C3"
    };
}
