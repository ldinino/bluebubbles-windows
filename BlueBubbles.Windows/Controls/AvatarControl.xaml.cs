using System.IO;
using System.Runtime.CompilerServices;
using BlueBubbles.Core.Configuration;
using BlueBubbles.Core.Diagnostics;
using BlueBubbles.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace BlueBubbles.Windows.Controls;

public sealed partial class AvatarControl : UserControl
{
    // Fallback fill when "Colorful avatars" is off — a medium grey that keeps the white
    // initials legible in both light and dark themes.
    private static readonly Color NeutralAvatarColor = Color.FromArgb(255, 0x75, 0x75, 0x75);

    // Decoded-bitmap cache keyed on the source byte[] reference. ContactResolver hands out the same
    // cached array for a given contact, so the same photo decodes once and every avatar (list tile,
    // pinned grid, header, details) reuses the BitmapImage. A BitmapImage can back multiple targets,
    // so sharing is safe. ConditionalWeakTable lets entries evict when the underlying bytes are dropped
    // (e.g. on a contacts reload). Accessed only from the UI thread.
    private static readonly ConditionalWeakTable<byte[], BitmapImage> BitmapCache = new();

    private static BitmapImage? TryGetCachedBitmap(byte[] imageData) =>
        BitmapCache.TryGetValue(imageData, out var bitmap) ? bitmap : null;

    public static readonly DependencyProperty SizeProperty =
        DependencyProperty.Register(nameof(Size), typeof(double), typeof(AvatarControl),
            new PropertyMetadata(40.0, OnPropertyChanged));

    public static readonly DependencyProperty InitialsProperty =
        DependencyProperty.Register(nameof(Initials), typeof(string), typeof(AvatarControl),
            new PropertyMetadata("", OnPropertyChanged));

    public static readonly DependencyProperty AvatarImageProperty =
        DependencyProperty.Register(nameof(AvatarImage), typeof(byte[]), typeof(AvatarControl),
            new PropertyMetadata(null, OnPropertyChanged));

    public static readonly DependencyProperty IsGroupProperty =
        DependencyProperty.Register(nameof(IsGroup), typeof(bool), typeof(AvatarControl),
            new PropertyMetadata(false, OnPropertyChanged));

    public static readonly DependencyProperty GroupInitials1Property =
        DependencyProperty.Register(nameof(GroupInitials1), typeof(string), typeof(AvatarControl),
            new PropertyMetadata("", OnPropertyChanged));

    public static readonly DependencyProperty GroupInitials2Property =
        DependencyProperty.Register(nameof(GroupInitials2), typeof(string), typeof(AvatarControl),
            new PropertyMetadata("", OnPropertyChanged));

    public static readonly DependencyProperty GroupAvatarImage1Property =
        DependencyProperty.Register(nameof(GroupAvatarImage1), typeof(byte[]), typeof(AvatarControl),
            new PropertyMetadata(null, OnPropertyChanged));

    public static readonly DependencyProperty GroupAvatarImage2Property =
        DependencyProperty.Register(nameof(GroupAvatarImage2), typeof(byte[]), typeof(AvatarControl),
            new PropertyMetadata(null, OnPropertyChanged));

    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public string Initials { get => (string)GetValue(InitialsProperty); set => SetValue(InitialsProperty, value); }
    public byte[]? AvatarImage { get => (byte[]?)GetValue(AvatarImageProperty); set => SetValue(AvatarImageProperty, value); }
    public bool IsGroup { get => (bool)GetValue(IsGroupProperty); set => SetValue(IsGroupProperty, value); }
    public string GroupInitials1 { get => (string)GetValue(GroupInitials1Property); set => SetValue(GroupInitials1Property, value); }
    public string GroupInitials2 { get => (string)GetValue(GroupInitials2Property); set => SetValue(GroupInitials2Property, value); }
    public byte[]? GroupAvatarImage1 { get => (byte[]?)GetValue(GroupAvatarImage1Property); set => SetValue(GroupAvatarImage1Property, value); }
    public byte[]? GroupAvatarImage2 { get => (byte[]?)GetValue(GroupAvatarImage2Property); set => SetValue(GroupAvatarImage2Property, value); }

    private int _loadGeneration;
    private AppSettings? _settings;
    private bool _relayoutQueued;

    // What asked for the pending relayout. Populated only while verbose logging is on (B2b): B2k
    // needs to know *which* dependency property keeps republishing on an idle control, and the
    // coalescing window means a single relayout can have several causes.
    private readonly HashSet<string> _pendingTriggers = new(StringComparer.Ordinal);

    // Stable per-control id so the (Debug-only) flicker diagnostics can correlate log lines across a
    // recycled container's lifetime: which generation cleared the source, which async decode landed,
    // and which got dropped as stale. Silent unless log verbosity is raised to Debug (B3).
    private static int _nextInstanceId;
    private readonly int _instanceId = System.Threading.Interlocked.Increment(ref _nextInstanceId);

    public AvatarControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Subscribe once so "Colorful avatars" changes re-render live.
        if (_settings is null)
        {
            _settings = App.Services.GetService<AppSettings>();
            if (_settings is not null)
                _settings.PropertyChanged += OnSettingsChanged;
        }
        // Queued, not direct: the binding's property sets have usually already queued a relayout
        // that has not run yet. Calling RefreshLayout here instead would run it twice on one bind,
        // and the second run would re-decode the avatar because the first decode is still in flight.
        QueueRelayout("Loaded");
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Unloaded dispatches asynchronously: a recycled container can already be back in the
        // tree (OnLoaded re-subscribed) when the old removal's Unloaded fires. Only unsubscribe
        // when genuinely out of the tree.
        if (IsLoaded) return;
        if (_settings is not null)
        {
            _settings.PropertyChanged -= OnSettingsChanged;
            _settings = null;
        }
    }

    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppSettings.ColorfulAvatars))
            DispatcherQueue.TryEnqueue(() =>
            {
                NoteTrigger("ColorfulAvatarsSetting");
                RefreshLayout();
            });
    }

    private static void OnPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AvatarControl ctrl) ctrl.QueueRelayout(PerfStats.IsEnabled ? TriggerName(e.Property) : null);
    }

    private static string TriggerName(DependencyProperty property) =>
        property == AvatarImageProperty ? nameof(AvatarImage)
        : property == InitialsProperty ? nameof(Initials)
        : property == SizeProperty ? nameof(Size)
        : property == IsGroupProperty ? nameof(IsGroup)
        : property == GroupInitials1Property ? nameof(GroupInitials1)
        : property == GroupInitials2Property ? nameof(GroupInitials2)
        : property == GroupAvatarImage1Property ? nameof(GroupAvatarImage1)
        : property == GroupAvatarImage2Property ? nameof(GroupAvatarImage2)
        : "Unknown";

    private void NoteTrigger(string? trigger)
    {
        if (trigger is null || !PerfStats.IsEnabled) return;
        _pendingTriggers.Add(trigger);
    }

    // A single Refresh on the bound tile flips several dependency properties (initials, image, group
    // faces, …); without coalescing each one would run a full RefreshLayout. Collapse them into one
    // relayout per frame.
    private void QueueRelayout(string? trigger = null)
    {
        NoteTrigger(trigger);

        if (_relayoutQueued) return;
        _relayoutQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                _relayoutQueued = false;
                RefreshLayout();
            }))
        {
            _relayoutQueued = false;
            RefreshLayout();
        }
    }

    private void RefreshLayout()
    {
        // Measurement wrapper (B2b). Free when verbose logging is off: Timestamp() returns the 0
        // sentinel and every recorder below returns before touching the session.
        var startedAt = PerfStats.Timestamp();
        var triggers = _pendingTriggers.Count == 0 ? null : string.Join("+", _pendingTriggers.OrderBy(t => t, StringComparer.Ordinal));
        _pendingTriggers.Clear();

        try
        {
            RefreshLayoutCore(triggers);
        }
        finally
        {
            PerfStats.Duration("avatar.relayout", startedAt);
            PerfStats.Count("avatar.relayout");
            if (triggers is not null) PerfStats.Count($"avatar.relayout.by:{triggers}");
        }
    }

    private void RefreshLayoutCore(string? triggers)
    {
        var generation = ++_loadGeneration;

        // Flicker tracing (B3). Dormant by default — the "Verbose logging" toggle is hidden and the
        // log sits at Info, so IsEnabled(Debug) is false and these messages are never built on this
        // hot path. Re-light it (un-hide the toggle) when debugging avatar issues again.
        if (AppLog.IsEnabled(LogLevel.Debug))
            AppLog.Debug(LogCategory.Ui,
                $"Avatar[{_instanceId}] RefreshLayout gen={generation} by={triggers ?? "unattributed"} group={IsGroup} " +
                $"img={(AvatarImage?.Length ?? 0)}B initials='{Initials}'");

        // "Colorful avatars" toggles the tinted fallback.
        var settings = _settings ??= App.Services.GetService<AppSettings>();
        var size = Size;
        var colorful = settings?.ColorfulAvatars ?? true;

        RootGrid.Width = size;
        RootGrid.Height = size;

        if (IsGroup)
        {
            SingleAvatar.Visibility = Visibility.Collapsed;
            GroupAvatar.Visibility = Visibility.Visible;

            var subSize = size * 0.65;
            GroupFront.Width = GroupFront.Height = subSize;
            GroupBack.Width = GroupBack.Height = subSize;

            ConfigureGroupCircle(
                GroupFrontEllipse, GroupFrontInitials, GroupFrontGlyph, GroupFrontImage,
                GroupInitials1, GroupAvatarImage1, subSize, generation, colorful);
            ConfigureGroupCircle(
                GroupBackEllipse, GroupBackInitials, GroupBackGlyph, GroupBackImage,
                GroupInitials2, GroupAvatarImage2, subSize, generation, colorful);
            return;
        }

        SingleAvatar.Visibility = Visibility.Visible;
        GroupAvatar.Visibility = Visibility.Collapsed;

        if (AvatarImage is { Length: > 0 })
        {
            PersonPic.Visibility = Visibility.Visible;
            PersonPic.Width = PersonPic.Height = size;
            InitialsCircle.Visibility = Visibility.Collapsed;

            // Reuse the decoded bitmap when we've seen these bytes before — no async round-trip, no
            // flash through the empty placeholder on container recycle.
            var cached = TryGetCachedBitmap(AvatarImage);
            if (cached is not null)
            {
                if (AppLog.IsEnabled(LogLevel.Debug)) AppLog.Debug(LogCategory.Ui, $"Avatar[{_instanceId}] gen={generation} single cache HIT (sync set)");
                PersonPic.ProfilePicture = cached;
            }
            else
            {
                if (AppLog.IsEnabled(LogLevel.Debug)) AppLog.Debug(LogCategory.Ui, $"Avatar[{_instanceId}] gen={generation} single cache MISS -> clear+decode");
                PersonPic.ProfilePicture = null;
                _ = SetPersonPicImageAsync(AvatarImage, generation);
            }
        }
        else
        {
            PersonPic.ProfilePicture = null;
            PersonPic.Visibility = Visibility.Collapsed;
            InitialsCircle.Visibility = Visibility.Visible;
            InitialsCircle.Width = InitialsCircle.Height = size;

            if (string.IsNullOrWhiteSpace(Initials))
            {
                // No contact name → generic person glyph on a neutral circle (Microsoft-style
                // default avatar), rather than punctuation from a raw phone number/email.
                InitialsText.Visibility = Visibility.Collapsed;
                PersonGlyph.Visibility = Visibility.Visible;
                PersonGlyph.FontSize = size * 0.5;
                InitialsEllipse.Fill = new SolidColorBrush(NeutralAvatarColor);
            }
            else
            {
                PersonGlyph.Visibility = Visibility.Collapsed;
                InitialsText.Visibility = Visibility.Visible;
                InitialsText.Text = Initials;
                InitialsText.FontSize = size * 0.4;
                InitialsEllipse.Fill = new SolidColorBrush(colorful ? GetColorForText(Initials) : NeutralAvatarColor);
            }
        }
    }

    private static Color GetColorForText(string? text) => Helpers.ContactColors.ForKey(text);

    private async Task SetPersonPicImageAsync(byte[] imageData, int generation)
    {
        var startedAt = PerfStats.Timestamp();
        try
        {
            var bitmap = await DecodeAndCacheAsync(imageData);
            PerfStats.Duration("avatar.decode", startedAt);
            var current = _loadGeneration;
            if (current == generation)
            {
                if (AppLog.IsEnabled(LogLevel.Debug)) AppLog.Debug(LogCategory.Ui, $"Avatar[{_instanceId}] gen={generation} single decode landed -> assign");
                PersonPic.ProfilePicture = bitmap;
            }
            else
            {
                PerfStats.Count("avatar.decode.stale");
                if (AppLog.IsEnabled(LogLevel.Debug)) AppLog.Debug(LogCategory.Ui, $"Avatar[{_instanceId}] gen={generation} single decode STALE (now {current}) -> drop");
            }
        }
        catch { }
    }

    private void ConfigureGroupCircle(
        Ellipse ellipse, TextBlock initialsText, FontIcon glyph, Ellipse imageEllipse,
        string initials, byte[]? imageBytes, double size, int generation, bool colorful)
    {
        if (imageBytes is { Length: > 0 })
        {
            ellipse.Visibility = Visibility.Collapsed;
            initialsText.Visibility = Visibility.Collapsed;
            glyph.Visibility = Visibility.Collapsed;
            imageEllipse.Visibility = Visibility.Visible;
            imageEllipse.Width = imageEllipse.Height = size;

            var cached = TryGetCachedBitmap(imageBytes);
            if (cached is not null)
            {
                if (AppLog.IsEnabled(LogLevel.Debug)) AppLog.Debug(LogCategory.Ui, $"Avatar[{_instanceId}] gen={generation} group face '{initials}' cache HIT (sync set)");
                SetEllipseBitmap(imageEllipse, cached);
            }
            else
            {
                if (AppLog.IsEnabled(LogLevel.Debug)) AppLog.Debug(LogCategory.Ui, $"Avatar[{_instanceId}] gen={generation} group face '{initials}' cache MISS -> clear+decode");
                // Drop any fill left over from a recycled container so the previous chat's face
                // isn't shown while the new one decodes (mirrors the single path clearing
                // PersonPic.ProfilePicture before its async load).
                imageEllipse.Fill = null;
                _ = SetEllipseImageAsync(imageEllipse, imageBytes, generation);
            }
            return;
        }

        imageEllipse.Visibility = Visibility.Collapsed;
        ellipse.Visibility = Visibility.Visible;

        if (string.IsNullOrWhiteSpace(initials))
        {
            // Unknown participant → generic person glyph on a neutral circle.
            initialsText.Visibility = Visibility.Collapsed;
            glyph.Visibility = Visibility.Visible;
            glyph.FontSize = size * 0.5;
            ellipse.Fill = new SolidColorBrush(NeutralAvatarColor);
        }
        else
        {
            glyph.Visibility = Visibility.Collapsed;
            initialsText.Visibility = Visibility.Visible;
            initialsText.Text = initials;
            initialsText.FontSize = size * 0.4;
            ellipse.Fill = new SolidColorBrush(colorful ? GetColorForText(initials) : NeutralAvatarColor);
        }
    }

    // Idempotent image fill for a group face: reuse the ImageBrush already on the ellipse and only
    // swap its source when the bitmap actually differs, so a redundant RefreshLayout on a warm cache
    // (e.g. navigating to Settings and back, which re-fires Loaded) doesn't rebuild the brush and
    // flash the face. Mirrors the single-avatar path's PersonPic.ProfilePicture reuse.
    private static void SetEllipseBitmap(Ellipse ellipse, BitmapImage bitmap)
    {
        if (ellipse.Fill is ImageBrush brush)
        {
            if (!ReferenceEquals(brush.ImageSource, bitmap))
                brush.ImageSource = bitmap;
        }
        else
        {
            ellipse.Fill = new ImageBrush { ImageSource = bitmap, Stretch = Stretch.UniformToFill };
        }
    }

    private async Task SetEllipseImageAsync(Ellipse ellipse, byte[] imageData, int generation)
    {
        try
        {
            var bitmap = await DecodeAndCacheAsync(imageData);
            var current = _loadGeneration;
            if (current == generation)
            {
                if (AppLog.IsEnabled(LogLevel.Debug)) AppLog.Debug(LogCategory.Ui, $"Avatar[{_instanceId}] gen={generation} group decode landed -> assign");
                SetEllipseBitmap(ellipse, bitmap);
            }
            else
            {
                if (AppLog.IsEnabled(LogLevel.Debug)) AppLog.Debug(LogCategory.Ui, $"Avatar[{_instanceId}] gen={generation} group decode STALE (now {current}) -> drop");
            }
        }
        catch { }
    }

    // Decodes the bytes into a BitmapImage and caches it on the array reference so subsequent realizations
    // (and other avatars sharing the same contact photo) reuse it. If another caller decoded the same bytes
    // while we awaited, prefer the cached instance so all targets share one bitmap.
    private static async Task<BitmapImage> DecodeAndCacheAsync(byte[] imageData)
    {
        var bitmap = new BitmapImage();
        using var stream = new MemoryStream(imageData);
        var ras = stream.AsRandomAccessStream();
        await bitmap.SetSourceAsync(ras);

        if (BitmapCache.TryGetValue(imageData, out var existing))
            return existing;
        BitmapCache.Add(imageData, bitmap);
        return bitmap;
    }
}
