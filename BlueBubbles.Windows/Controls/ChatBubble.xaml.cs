using BlueBubbles.Core.Configuration;
using BlueBubbles.Core.Utils;
using BlueBubbles.Windows.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace BlueBubbles.Windows.Controls;

public sealed partial class ChatBubble : UserControl
{
    private MessageBubbleViewModel? _currentVm;
    private MessageBubbleViewModel? _renderedContentForVm;
    private AppSettings? _settings;
    private Flyout? _glyphFlyout;
    private MenuFlyout? _textSelectionFlyout;
    private readonly List<Button> _reactionButtons = [];
    private Button? _copyItem;
    private Button? _editItem;        // message edit — own text messages only
    private Button? _undoItem;        // unsend ("Undo Send") — own messages only
    private Button? _deleteItem;      // local message delete
    private Border? _editSeparator;   // divider above the Edit/Undo Send group; hidden for incoming messages

    public event EventHandler<ViewModels.AttachmentViewModel>? AttachmentImageClicked;

    public ChatBubble()
    {
        InitializeComponent();
        BuildGlyphFlyout();
        // Drive the right-click menu through ContextFlyout, not ContextRequested. Assigning a
        // ContextFlyout makes WinUI show it on right-click AND suppresses the TextBlock's
        // built-in Copy/Select-All menu — so there's no event-timing fight, no manual ShowAt,
        // and no thread stall. The whole bubble (and the text) shows the glyph menu; the text
        // swaps to a simple Copy / Select All menu only while the user has an active selection.
        RootGrid.ContextFlyout = _glyphFlyout;
        MessageText.ContextFlyout = _glyphFlyout;
        MessageText.SelectionChanged += OnTextSelectionChanged;
        Loaded += OnLoaded;
        DataContextChanged += (_, _) =>
        {
            if (_currentVm is not null)
                _currentVm.PropertyChanged -= OnVmPropertyChanged;

            if (DataContext is MessageBubbleViewModel vm)
            {
                _currentVm = vm;
                vm.PropertyChanged += OnVmPropertyChanged;
                ApplyStyle(vm);
            }
            else
            {
                _currentVm = null;
                _renderedContentForVm = null;
            }
        };
        Unloaded += (_, _) =>
        {
            // Unloaded dispatches asynchronously: a recycled bubble can already be re-bound and
            // back in the tree when the old removal's Unloaded fires — unsubscribing then leaves
            // the fresh bind deaf to VM updates. Only tear down when genuinely out of the tree.
            if (IsLoaded) return;
            if (_currentVm is not null)
            {
                _currentVm.PropertyChanged -= OnVmPropertyChanged;
                _currentVm = null;
            }
            if (_settings is not null)
            {
                _settings.PropertyChanged -= OnSettingsChanged;
                _settings = null;
            }
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Subscribe once so "Colorful bubbles" / "Show delivery timestamps" / "24-hour time"
        // changes re-render the visible bubbles live.
        if (_settings is null)
        {
            _settings = App.Services.GetService<AppSettings>();
            if (_settings is not null)
                _settings.PropertyChanged += OnSettingsChanged;
        }
        // A genuine unload detached the VM without a DataContext change; re-attach on re-entry
        // (DataContextChanged won't re-fire for an unchanged DataContext).
        if (_currentVm is null && DataContext is MessageBubbleViewModel vm)
        {
            _currentVm = vm;
            vm.PropertyChanged += OnVmPropertyChanged;
        }
        if (_currentVm is not null)
            ApplyStyle(_currentVm);
    }

    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_currentVm is { } vm
            && e.PropertyName is nameof(AppSettings.ColorfulBubbles)
                or nameof(AppSettings.ShowDeliveryTimestamps)
                or nameof(AppSettings.Use24HrFormat))
        {
            DispatcherQueue.TryEnqueue(() => ApplyStyle(vm));
        }
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not MessageBubbleViewModel vm) return;
        if (e.PropertyName is nameof(MessageBubbleViewModel.Status)
            or nameof(MessageBubbleViewModel.ShowTail)
            or nameof(MessageBubbleViewModel.IsDelayed))
        {
            DispatcherQueue.TryEnqueue(() => ApplyStyle(vm));
        }
        else if (e.PropertyName is nameof(MessageBubbleViewModel.ReactionRevision))
        {
            DispatcherQueue.TryEnqueue(() => BuildReactionsPanel(vm));
        }
        else if (e.PropertyName is nameof(MessageBubbleViewModel.ReplyPreviewText)
            or nameof(MessageBubbleViewModel.ReplySenderLabel))
        {
            DispatcherQueue.TryEnqueue(() => UpdateReplyIndicator(vm));
        }
        else if (e.PropertyName is nameof(MessageBubbleViewModel.Text)
            or nameof(MessageBubbleViewModel.IsUnsent)
            or nameof(MessageBubbleViewModel.DateEdited))
        {
            // An edit rewrites the text / sets DateEdited; an unsend flips IsUnsent — re-render the bubble.
            DispatcherQueue.TryEnqueue(() => ApplyStyle(vm));
        }
    }

    private void ApplyStyle(MessageBubbleViewModel vm)
    {
        // An unsent (retracted) message hides its content and shows a neutral placeholder instead.
        if (vm.IsUnsent)
        {
            RenderUnsent(vm);
            return;
        }

        // Attachments / URL preview are built once per message VM — NOT on every style re-apply
        // (Status / ShowTail / Text changes), which previously destroyed the loaded AttachmentHolder
        // and re-loaded its image, causing the "appear, disappear, appear" flicker on thread load.
        if (_renderedContentForVm != vm)
        {
            BuildContent(vm);
            _renderedContentForVm = vm;
        }

        // Reaction pills
        BuildReactionsPanel(vm);

        // Reply indicator
        UpdateReplyIndicator(vm);

        // Text content. URLs render as clickable links; a pure-URL rich-link message shows only its
        // card, so its redundant raw-URL text is hidden. (Also hidden for attachment-only bubbles.)
        var linkBrush = vm.IsFromMe
            ? GetBrush("TextOnAccentFillColorPrimaryBrush")
            : GetBrush("AccentTextFillColorPrimaryBrush");
        var showText = !string.IsNullOrEmpty(vm.Text)
            && !(vm.IsUrlPreview && UrlDetector.IsSingleUrl(vm.Text));
        MessageText.Visibility = showText ? Visibility.Visible : Visibility.Collapsed;
        SetMessageInlines(showText ? vm.Text : null, linkBrush);
        MessageText.FontStyle = global::Windows.UI.Text.FontStyle.Normal;
        TimeText.Text = vm.FormattedTime;
        TimeText.Visibility = (_settings?.ShowDeliveryTimestamps ?? true)
            ? Visibility.Visible : Visibility.Collapsed;
        EditedText.Visibility = vm.IsEdited ? Visibility.Visible : Visibility.Collapsed;

        // Subject
        if (!string.IsNullOrEmpty(vm.Subject))
        {
            SubjectText.Text = vm.Subject;
            SubjectText.Visibility = Visibility.Visible;
        }
        else
        {
            SubjectText.Visibility = Visibility.Collapsed;
        }

        // Sender name (group incoming only)
        if (vm.SenderName is not null)
        {
            SenderText.Text = vm.SenderName;
            SenderText.Visibility = Visibility.Visible;
        }
        else
        {
            SenderText.Visibility = Visibility.Collapsed;
        }

        // Delivery status (outgoing only)
        if (vm.IsFromMe && vm.DeliveryStatusText.Length > 0)
        {
            StatusText.Text = $"· {vm.DeliveryStatusText}";
            StatusText.Visibility = Visibility.Visible;

            if (vm.Status == ViewModels.DeliveryStatus.Error)
            {
                var errorBrush = new SolidColorBrush(Colors.Red);
                StatusText.Foreground = errorBrush;
                StatusText.Opacity = 1.0;
            }
        }
        else
        {
            StatusText.Visibility = Visibility.Collapsed;
        }

        // Cancel link for delayed messages
        CancelLink.Visibility = vm.IsDelayed ? Visibility.Visible : Visibility.Collapsed;

        // Sending state: slightly translucent bubble
        if (vm.Status == ViewModels.DeliveryStatus.Sending)
        {
            BubbleBorder.Opacity = 0.7;
        }
        else
        {
            BubbleBorder.Opacity = 1.0;
        }

        // Emoji-only: large text, no bubble background
        if (vm.IsEmojiOnly)
        {
            BubbleBorder.Background = null;
            BubbleBorder.Padding = new Thickness(4, 2, 4, 2);
            MessageText.FontSize = 40;
            BubbleBorder.HorizontalAlignment = vm.IsFromMe
                ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            var textBrush = GetBrush("TextFillColorPrimaryBrush");
            var secondaryBrush = GetBrush("TextFillColorSecondaryBrush");
            MessageText.Foreground = textBrush;
            SenderText.Foreground = secondaryBrush;
            TimeText.Foreground = secondaryBrush;
            StatusText.Foreground = secondaryBrush;
            EditedText.Foreground = secondaryBrush;
            BubbleBorder.CornerRadius = new CornerRadius(0);
            return;
        }

        // Bubble styling
        MessageText.FontSize = 14;
        BubbleBorder.Padding = new Thickness(12, 8, 12, 8);

        if (vm.IsFromMe)
        {
            BubbleBorder.HorizontalAlignment = HorizontalAlignment.Right;
            BubbleBorder.Background = GetBrush("AccentFillColorDefaultBrush");
            var onAccent = GetBrush("TextOnAccentFillColorPrimaryBrush");
            MessageText.Foreground = onAccent;
            SenderText.Foreground = onAccent;
            SubjectText.Foreground = onAccent;
            TimeText.Foreground = onAccent;
            StatusText.Foreground = onAccent;
            EditedText.Foreground = onAccent;
        }
        else
        {
            BubbleBorder.HorizontalAlignment = HorizontalAlignment.Left;
            // "Colorful bubbles": tint incoming bubbles with the sender's per-contact color.
            BubbleBorder.Background = (_settings?.ColorfulBubbles ?? false)
                ? new SolidColorBrush(Helpers.ContactColors.TintForKey(vm.SenderColorKey))
                : GetBrush("ControlFillColorDefaultBrush");
            var textBrush = GetBrush("TextFillColorPrimaryBrush");
            var secondaryBrush = GetBrush("TextFillColorSecondaryBrush");
            MessageText.Foreground = textBrush;
            SenderText.Foreground = secondaryBrush;
            SubjectText.Foreground = textBrush;
            TimeText.Foreground = secondaryBrush;
            StatusText.Foreground = secondaryBrush;
            EditedText.Foreground = secondaryBrush;
        }

        // Corner radius: tail on the sender's side
        BubbleBorder.CornerRadius = vm.ShowTail
            ? (vm.IsFromMe ? new CornerRadius(18, 18, 4, 18) : new CornerRadius(18, 18, 18, 4))
            : new CornerRadius(18);

        // A rich-link card is its own surface — don't wrap it in the coloured message bubble.
        // Strip the bubble's fill/padding/corners so the card stands alone, and give the meta row
        // (time/status) neutral colours since it now sits on the page background, not an accent fill.
        if (vm.IsUrlPreview)
        {
            BubbleBorder.Background = null;
            BubbleBorder.Padding = new Thickness(0);
            BubbleBorder.CornerRadius = new CornerRadius(0);
            var metaBrush = GetBrush("TextFillColorSecondaryBrush");
            TimeText.Foreground = metaBrush;
            StatusText.Foreground = metaBrush;
            EditedText.Foreground = metaBrush;
            MetaPanel.Margin = new Thickness(0, 2, 4, 0);
        }
        else
        {
            MetaPanel.Margin = new Thickness(0, 4, 0, 0);
        }
    }

    // Builds the attachment chips / rich-link card for a bubble. Called once per message VM (guarded
    // in ApplyStyle) so the AttachmentHolders — and their loaded images — survive style re-applies.
    private void BuildContent(MessageBubbleViewModel vm)
    {
        AttachmentsPanel.Children.Clear();
        UrlPreviewPanel.Children.Clear();
        if (vm.IsUrlPreview)
        {
            AttachmentsPanel.Visibility = Visibility.Collapsed;
            UrlPreviewPanel.Visibility = Visibility.Visible;
            UrlPreviewPanel.Children.Add(new UrlPreview { DataContext = vm.UrlPreview });
        }
        else
        {
            UrlPreviewPanel.Visibility = Visibility.Collapsed;
            if (vm.HasAttachments)
            {
                AttachmentsPanel.Visibility = Visibility.Visible;
                foreach (var att in vm.Attachments!)
                {
                    var holder = new AttachmentHolder { DataContext = att };
                    holder.ImageClicked += OnAttachmentImageClicked;
                    AttachmentsPanel.Children.Add(holder);
                }
            }
            else
            {
                AttachmentsPanel.Visibility = Visibility.Collapsed;
            }
        }
    }

    // Builds the message body as inline runs, turning URLs into clickable hyperlinks. The link colour
    // matches the bubble's text palette (so it stays legible on the accent background) and is underlined
    // to read as a link. Non-link spans inherit MessageText.Foreground.
    private void SetMessageInlines(string? text, Brush? linkBrush)
    {
        MessageText.Inlines.Clear();
        if (string.IsNullOrEmpty(text)) return;

        var tokens = UrlDetector.Find(text);
        if (tokens.Count == 0)
        {
            MessageText.Inlines.Add(new Run { Text = text });
            return;
        }

        var pos = 0;
        foreach (var token in tokens)
        {
            if (token.Start > pos)
                MessageText.Inlines.Add(new Run { Text = text[pos..token.Start] });

            var link = new Hyperlink { UnderlineStyle = UnderlineStyle.Single };
            if (linkBrush is not null) link.Foreground = linkBrush;
            link.Inlines.Add(new Run { Text = token.Text });
            if (Uri.TryCreate(token.Url, UriKind.Absolute, out var uri)) link.NavigateUri = uri;
            MessageText.Inlines.Add(link);

            pos = token.Start + token.Length;
        }

        if (pos < text.Length)
            MessageText.Inlines.Add(new Run { Text = text[pos..] });
    }

    // Renders the retracted ("unsent") placeholder in place of the message content. The bubble drops
    // its accent colour and shows muted italic text on the sender's side.
    private void RenderUnsent(MessageBubbleViewModel vm)
    {
        AttachmentsPanel.Children.Clear();
        AttachmentsPanel.Visibility = Visibility.Collapsed;
        ReactionsPanel.Children.Clear();
        ReactionsPanel.Visibility = Visibility.Collapsed;
        ReplyIndicator.Visibility = Visibility.Collapsed;
        SubjectText.Visibility = Visibility.Collapsed;
        StatusText.Visibility = Visibility.Collapsed;
        CancelLink.Visibility = Visibility.Collapsed;
        EditedText.Visibility = Visibility.Collapsed;

        SenderText.Visibility = vm.SenderName is not null ? Visibility.Visible : Visibility.Collapsed;
        if (vm.SenderName is not null) SenderText.Text = vm.SenderName;

        MessageText.Visibility = Visibility.Visible;
        MessageText.Text = "This message was unsent";
        MessageText.FontStyle = global::Windows.UI.Text.FontStyle.Italic;
        MessageText.FontSize = 14;

        TimeText.Text = vm.FormattedTime;
        TimeText.Visibility = (_settings?.ShowDeliveryTimestamps ?? true)
            ? Visibility.Visible : Visibility.Collapsed;

        BubbleBorder.Opacity = 1.0;
        BubbleBorder.Padding = new Thickness(12, 8, 12, 8);
        BubbleBorder.HorizontalAlignment = vm.IsFromMe
            ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        BubbleBorder.Background = GetBrush("ControlFillColorDefaultBrush");

        var secondaryBrush = GetBrush("TextFillColorSecondaryBrush");
        MessageText.Foreground = secondaryBrush;
        SenderText.Foreground = secondaryBrush;
        TimeText.Foreground = secondaryBrush;

        BubbleBorder.CornerRadius = vm.ShowTail
            ? (vm.IsFromMe ? new CornerRadius(18, 18, 4, 18) : new CornerRadius(18, 18, 18, 4))
            : new CornerRadius(18);
    }

    // Replies (threads)

    private void UpdateReplyIndicator(MessageBubbleViewModel vm)
    {
        if (!vm.IsReply)
        {
            ReplyIndicator.Visibility = Visibility.Collapsed;
            return;
        }

        ReplyIndicator.Visibility = Visibility.Visible;
        ReplySenderText.Text = vm.ReplySenderLabel ?? string.Empty;
        ReplySnippetText.Text = vm.ReplyPreviewText ?? "..."; // placeholder until resolved

        // Match the bubble's palette: on-accent for outgoing, accent cue for incoming.
        if (vm.IsFromMe)
        {
            var onAccent = GetBrush("TextOnAccentFillColorPrimaryBrush");
            ReplyAccentBar.Fill = onAccent;
            ReplySenderText.Foreground = onAccent;
            ReplySnippetText.Foreground = GetBrush("TextOnAccentFillColorSecondaryBrush") ?? onAccent;
        }
        else
        {
            var accent = GetBrush("AccentFillColorDefaultBrush");
            ReplyAccentBar.Fill = accent;
            ReplySenderText.Foreground = GetBrush("AccentTextFillColorPrimaryBrush") ?? accent;
            ReplySnippetText.Foreground = GetBrush("TextFillColorSecondaryBrush");
        }
    }

    private void OnReplyIndicatorTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_currentVm?.ThreadOriginatorGuid is { } guid)
            _currentVm.ScrollToMessageAction?.Invoke(guid);
    }

    // Reactions (tapbacks)

    private void BuildReactionsPanel(MessageBubbleViewModel vm)
    {
        ReactionsPanel.Children.Clear();

        if (!vm.HasReactions)
        {
            ReactionsPanel.Visibility = Visibility.Collapsed;
            return;
        }

        ReactionsPanel.Visibility = Visibility.Visible;
        ReactionsPanel.HorizontalAlignment = vm.IsFromMe
            ? HorizontalAlignment.Right : HorizontalAlignment.Left;

        foreach (var badge in vm.Reactions)
            ReactionsPanel.Children.Add(CreateReactionPill(badge));
    }

    private Button CreateReactionPill(ReactionBadgeViewModel badge)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        content.Children.Add(new TextBlock
        {
            Text = badge.Emoji,
            FontFamily = new FontFamily("Segoe UI Emoji"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        });

        var countBrush = badge.IncludesMe
            ? GetBrush("TextOnAccentFillColorPrimaryBrush")
            : GetBrush("TextFillColorPrimaryBrush");

        if (badge.ShowCount)
        {
            content.Children.Add(new TextBlock
            {
                Text = badge.CountText,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = countBrush
            });
        }

        var pill = new Button
        {
            Content = content,
            Tag = badge.ReactionType,
            Padding = new Thickness(7, 1, 7, 1),
            MinWidth = 0,
            MinHeight = 0,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Background = badge.IncludesMe
                ? GetBrush("AccentFillColorDefaultBrush")
                : GetBrush("CardBackgroundFillColorDefaultBrush"),
            BorderBrush = GetBrush("CardStrokeColorDefaultBrush")
        };
        pill.Click += OnReactionBadgeClick;
        AutomationProperties.SetName(pill,
            $"{ReactionTypes.ToVerb(badge.ReactionType)}{(badge.Count > 1 ? $", {badge.Count}" : "")}");
        return pill;
    }

    private void OnReactionBadgeClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string type })
            _currentVm?.SendReactionAction?.Invoke(type);
    }

    // Glyph-style bubble menu, top to bottom: a tapback reactions row, Reply, a grouped
    // Edit / Undo Send pair (own messages only — shown/hidden per message in OnGlyphFlyoutOpening),
    // Copy, then Delete. Built as a Flyout (not a MenuFlyout) so the reactions can sit in a
    // horizontal row above the action items.
    private void BuildGlyphFlyout()
    {
        var root = new StackPanel { MinWidth = 240 };

        var reactionsBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Padding = new Thickness(4, 4, 4, 6),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        foreach (var type in ReactionTypes.All)
        {
            var btn = new Button
            {
                Tag = type,
                Width = 40,
                Height = 40,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(20),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Content = new TextBlock
                {
                    Text = ReactionTypes.ToEmoji(type),
                    FontFamily = new FontFamily("Segoe UI Emoji"),
                    FontSize = 20,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            AutomationProperties.SetName(btn, ReactionTypes.ToVerb(type));
            btn.Click += OnReactionButtonClick;
            _reactionButtons.Add(btn);
            reactionsBar.Children.Add(btn);
        }
        root.Children.Add(reactionsBar);

        root.Children.Add(MakeSeparator());
        root.Children.Add(MakeMenuItem("", "Reply…", OnReplyMenuClick));

        // Edit + Undo Send are grouped together (no divider between them) and apply to your own
        // messages only. The whole group — including this divider — is hidden for incoming messages.
        _editSeparator = MakeSeparator();
        root.Children.Add(_editSeparator);
        _editItem = MakeMenuItem("", "Edit", null);
        _editItem.Click += OnEditMenuClick;
        root.Children.Add(_editItem);
        _undoItem = MakeMenuItem("", "Undo Send", null);
        _undoItem.Click += OnUndoSendMenuClick;
        root.Children.Add(_undoItem);

        root.Children.Add(MakeSeparator());
        _copyItem = MakeMenuItem("", "Copy", OnCopyAllClick);
        root.Children.Add(_copyItem);

        // Delete removes the message from this client (soft-delete) after a confirmation prompt.
        root.Children.Add(MakeSeparator());
        _deleteItem = MakeMenuItem("", "Delete…", null);
        _deleteItem.Click += OnDeleteMenuClick;
        root.Children.Add(_deleteItem);

        var presenterStyle = new Style { TargetType = typeof(FlyoutPresenter) };
        presenterStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(4)));
        presenterStyle.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(8)));
        presenterStyle.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 0.0));

        _glyphFlyout = new Flyout { Content = root, FlyoutPresenterStyle = presenterStyle };
        _glyphFlyout.Opening += OnGlyphFlyoutOpening;
    }

    private static Border MakeSeparator() => new()
    {
        Height = 1,
        Margin = new Thickness(8, 5, 8, 5),
        Background = GetBrush("DividerStrokeColorDefaultBrush")
    };

    private static Button MakeMenuItem(string glyph, string text, RoutedEventHandler? onClick)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        content.Children.Add(new FontIcon
        {
            Glyph = glyph,
            FontSize = 16,
            Width = 20,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });

        var btn = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(12, 8, 16, 8),
            CornerRadius = new CornerRadius(4)
        };
        if (onClick is not null) btn.Click += onClick;
        return btn;
    }

    private void OnGlyphFlyoutOpening(object? sender, object e)
    {
        var self = _currentVm?.SelfReactionType;
        foreach (var btn in _reactionButtons)
        {
            var active = btn.Tag is string t && t == self;
            btn.Background = active
                ? GetBrush("AccentFillColorDefaultBrush")
                : new SolidColorBrush(Colors.Transparent);
        }
        if (_copyItem is not null)
            _copyItem.IsEnabled = !string.IsNullOrEmpty(_currentVm?.Text);

        // Edit / Undo Send: own, already-sent (non-temp), not-yet-unsent messages only. Edit additionally
        // needs editable text. The shared divider is hidden when neither item shows (incoming messages).
        var vm = _currentVm;
        var own = vm?.IsFromMe == true;
        var sent = vm is not null && !vm.MessageGuid.StartsWith("temp-", StringComparison.Ordinal);
        var notUnsent = vm?.IsUnsent != true;
        var canEdit = own && sent && notUnsent && !string.IsNullOrEmpty(vm!.Text);
        var canUnsend = own && sent && notUnsent;

        if (_editItem is not null)
            _editItem.Visibility = canEdit ? Visibility.Visible : Visibility.Collapsed;
        if (_undoItem is not null)
            _undoItem.Visibility = canUnsend ? Visibility.Visible : Visibility.Collapsed;
        if (_editSeparator is not null)
            _editSeparator.Visibility = (canEdit || canUnsend) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnReactionButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string type })
            _currentVm?.SendReactionAction?.Invoke(type);
        _glyphFlyout?.Hide();
    }

    private void OnReplyMenuClick(object sender, RoutedEventArgs e)
    {
        _currentVm?.StartReplyAction?.Invoke();
        _glyphFlyout?.Hide();
    }

    private void OnEditMenuClick(object sender, RoutedEventArgs e)
    {
        _currentVm?.StartEditAction?.Invoke();
        _glyphFlyout?.Hide();
    }

    private void OnUndoSendMenuClick(object sender, RoutedEventArgs e)
    {
        _currentVm?.UnsendAction?.Invoke();
        _glyphFlyout?.Hide();
    }

    private async void OnDeleteMenuClick(object sender, RoutedEventArgs e)
    {
        // Capture before awaiting — container recycling can swap _currentVm out from under us.
        var vm = _currentVm;
        _glyphFlyout?.Hide();
        if (vm is null || XamlRoot is null) return;

        var dialog = new ContentDialog
        {
            Title = "Delete Message",
            Content = "This message will be deleted from your devices. This can't be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            vm.DeleteAction?.Invoke();
    }

    private void OnCopyAllClick(object sender, RoutedEventArgs e)
    {
        var text = _currentVm?.Text;
        if (!string.IsNullOrEmpty(text))
        {
            var dp = new DataPackage();
            dp.SetText(text);
            Clipboard.SetContent(dp);
        }
        _glyphFlyout?.Hide();
    }

    // Selection drives which menu the text shows on right-click: the simple Copy / Select All
    // menu while text is highlighted, otherwise the same glyph menu as the rest of the bubble.
    private void OnTextSelectionChanged(object sender, RoutedEventArgs e)
    {
        MessageText.ContextFlyout = MessageText.SelectedText.Length > 0
            ? (_textSelectionFlyout ??= BuildTextSelectionFlyout())
            : _glyphFlyout;
    }

    private MenuFlyout BuildTextSelectionFlyout()
    {
        var flyout = new MenuFlyout();

        var copy = new MenuFlyoutItem { Text = "Copy" };
        copy.Click += (_, _) =>
        {
            var selected = MessageText.SelectedText;
            if (string.IsNullOrEmpty(selected)) return;
            var dp = new DataPackage();
            dp.SetText(selected);
            Clipboard.SetContent(dp);
        };

        var selectAll = new MenuFlyoutItem { Text = "Select All" };
        selectAll.Click += (_, _) => MessageText.SelectAll();

        flyout.Items.Add(copy);
        flyout.Items.Add(selectAll);
        return flyout;
    }

    private void OnAttachmentImageClicked(object? sender, ViewModels.AttachmentViewModel e)
    {
        AttachmentImageClicked?.Invoke(this, e);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        _currentVm?.CancelAction?.Invoke();
    }

    private static Brush? GetBrush(string key)
    {
        return Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush
            : null;
    }
}
