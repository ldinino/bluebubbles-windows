using System.IO;
using BlueBubbles.Core.Configuration;
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

    public AvatarControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Subscribe once so "Colorful avatars" / "Avatar size" changes re-render live.
        if (_settings is null)
        {
            _settings = App.Services.GetService<AppSettings>();
            if (_settings is not null)
                _settings.PropertyChanged += OnSettingsChanged;
        }
        RefreshLayout();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_settings is not null)
        {
            _settings.PropertyChanged -= OnSettingsChanged;
            _settings = null;
        }
    }

    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppSettings.ColorfulAvatars) or nameof(AppSettings.AvatarScale))
            DispatcherQueue.TryEnqueue(RefreshLayout);
    }

    private static void OnPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AvatarControl ctrl) ctrl.RefreshLayout();
    }

    private void RefreshLayout()
    {
        var generation = ++_loadGeneration;

        // "Avatar size" scales the requested Size; "Colorful avatars" toggles the tinted fallback.
        var settings = _settings ??= App.Services.GetService<AppSettings>();
        var size = Size * (settings?.AvatarScale ?? 1.0);
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
            PersonPic.ProfilePicture = null;
            InitialsCircle.Visibility = Visibility.Collapsed;
            _ = SetPersonPicImageAsync(AvatarImage, generation);
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
        try
        {
            var bitmap = new BitmapImage();
            using var stream = new MemoryStream(imageData);
            var ras = stream.AsRandomAccessStream();
            await bitmap.SetSourceAsync(ras);
            if (_loadGeneration == generation)
                PersonPic.ProfilePicture = bitmap;
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
            _ = SetEllipseImageAsync(imageEllipse, imageBytes, generation);
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

    private async Task SetEllipseImageAsync(Ellipse ellipse, byte[] imageData, int generation)
    {
        try
        {
            var bitmap = new BitmapImage();
            using var stream = new MemoryStream(imageData);
            var ras = stream.AsRandomAccessStream();
            await bitmap.SetSourceAsync(ras);
            if (_loadGeneration == generation)
            {
                ellipse.Fill = new ImageBrush
                {
                    ImageSource = bitmap,
                    Stretch = Stretch.UniformToFill
                };
            }
        }
        catch { }
    }
}
