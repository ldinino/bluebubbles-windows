using System.ComponentModel;
using BlueBubbles.Windows.ViewModels;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace BlueBubbles.Windows.Controls;

/// <summary>
/// A rich link preview card with four states (rich / show-preview / loading / generic). Tapping the
/// card opens the link; "Show preview" triggers an on-demand metadata fetch on the view-model. The
/// hero image may be a local attachment (Apple rich preview) or a remote URL (fetched preview); the
/// local path uses a generation guard so a stale async decode can't land on a recycled card.
/// </summary>
public sealed partial class UrlPreview : UserControl
{
    private UrlPreviewViewModel? _vm;
    private AttachmentViewModel? _image;
    private long _generation;

    public UrlPreview()
    {
        InitializeComponent();
        // The whole card is clickable (a tap opens the link), so show the hand cursor on hover.
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => DetachVm();
    }

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        DetachVm();
        if (args.NewValue is UrlPreviewViewModel vm)
        {
            _vm = vm;
            vm.PropertyChanged += OnVmPropertyChanged;
            Render(vm);
        }
    }

    private void DetachVm()
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = null;
        DetachImage();
    }

    private void DetachImage()
    {
        if (_image is not null)
        {
            _image.PropertyChanged -= OnImagePropertyChanged;
            _image = null;
        }
        Interlocked.Increment(ref _generation);
        HeroImage.Source = null;
        HeroContainer.Visibility = Visibility.Collapsed;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is null) return;
        if (e.PropertyName is nameof(UrlPreviewViewModel.State)
            or nameof(UrlPreviewViewModel.Title)
            or nameof(UrlPreviewViewModel.Summary)
            or nameof(UrlPreviewViewModel.SiteName)
            or nameof(UrlPreviewViewModel.ImageUri))
        {
            DispatcherQueue.TryEnqueue(() => Render(_vm));
        }
    }

    private void Render(UrlPreviewViewModel vm)
    {
        TitleText.Text = vm.DisplayTitle;

        var summary = vm.State == UrlPreviewState.Generic ? vm.Url : vm.Summary;
        SummaryText.Text = summary ?? string.Empty;
        SummaryText.Visibility = string.IsNullOrWhiteSpace(summary) ? Visibility.Collapsed : Visibility.Visible;

        var site = string.IsNullOrWhiteSpace(vm.SiteName) ? vm.Host : vm.SiteName!;
        SiteText.Text = site;
        SiteText.Visibility = (vm.State == UrlPreviewState.Rich
                               && !string.IsNullOrWhiteSpace(site)
                               && !string.Equals(site, vm.DisplayTitle, StringComparison.OrdinalIgnoreCase))
            ? Visibility.Visible : Visibility.Collapsed;

        var loading = vm.State == UrlPreviewState.Loading;
        var needs = vm.State == UrlPreviewState.NeedsPreview;
        ActionRow.Visibility = (loading || needs) ? Visibility.Visible : Visibility.Collapsed;
        ShowPreviewButton.Visibility = needs ? Visibility.Visible : Visibility.Collapsed;
        LoadingRing.IsActive = loading;
        LoadingRing.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        LoadingText.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;

        // Hero only in the rich state.
        DetachImage();
        if (vm.State != UrlPreviewState.Rich) return;

        if (!string.IsNullOrEmpty(vm.ImageUri))
        {
            LoadRemoteHero(vm.ImageUri!);
        }
        else if (vm.Image is { } image)
        {
            _image = image;
            image.PropertyChanged += OnImagePropertyChanged;
            if (image.State == AttachmentState.Cached) LoadLocalHero(image);
            else if (image.State == AttachmentState.NotDownloaded) _ = image.DownloadAsync();
        }
    }

    private void OnImagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_image is { State: AttachmentState.Cached } image)
            DispatcherQueue.TryEnqueue(() => LoadLocalHero(image));
    }

    private void LoadRemoteHero(string uriString)
    {
        if (!Uri.TryCreate(uriString, UriKind.Absolute, out var uri)) return;
        var bitmap = new BitmapImage();
        bitmap.ImageOpened += (_, _) => HeroContainer.Visibility = Visibility.Visible;
        bitmap.ImageFailed += (_, _) => HeroContainer.Visibility = Visibility.Collapsed;
        bitmap.UriSource = uri;
        HeroImage.Source = bitmap;
    }

    private void LoadLocalHero(AttachmentViewModel image)
    {
        var generation = Interlocked.Increment(ref _generation);
        if (image.LocalPath is null) return;
        _ = LoadLocalHeroAsync(image.LocalPath, generation);
    }

    private async Task LoadLocalHeroAsync(string path, long generation)
    {
        var bitmap = await Helpers.ImageLoader.FromFileAsync(path);
        if (Interlocked.Read(ref _generation) != generation || bitmap is null) return;
        HeroImage.Source = bitmap;
        HeroContainer.Visibility = Visibility.Visible;
    }

    private void OnShowPreviewClick(object sender, RoutedEventArgs e)
    {
        if (_vm?.LoadPreviewCommand.CanExecute(null) == true)
            _vm.LoadPreviewCommand.Execute(null);
    }

    private async void OnTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (_vm is null) return;

        // A tap on the "Show preview" affordance must only kick off the metadata fetch (its own Click
        // handler) — not also open the link. The Tapped event bubbles from the button up to this card,
        // so ignore taps that originate within it.
        if (e.OriginalSource is DependencyObject source && IsWithin(source, ShowPreviewButton))
            return;

        if (Uri.TryCreate(_vm.Url, UriKind.Absolute, out var uri))
        {
            try { await global::Windows.System.Launcher.LaunchUriAsync(uri); }
            catch { }
        }
    }

    // True if <paramref name="node"/> is <paramref name="ancestor"/> or one of its visual-tree descendants.
    private static bool IsWithin(DependencyObject? node, DependencyObject ancestor)
    {
        while (node is not null)
        {
            if (ReferenceEquals(node, ancestor)) return true;
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }
}
