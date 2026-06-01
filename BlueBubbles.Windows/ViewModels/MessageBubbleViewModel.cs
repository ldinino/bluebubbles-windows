using System.Text.Json;
using BlueBubbles.Core.Configuration;
using BlueBubbles.Core.Data.Entities;
using BlueBubbles.Core.Models;
using BlueBubbles.Core.Services;
using BlueBubbles.Core.Utils;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;

namespace BlueBubbles.Windows.ViewModels;

public partial class MessageBubbleViewModel : ObservableObject
{
    public string MessageGuid { get; private set; }
    public string? TempGuid { get; }

    /// <summary>The bubble's text. Mutable because an edit (local or remote) rewrites it in place.</summary>
    [ObservableProperty] public partial string? Text { get; set; }

    public string? Subject { get; }
    public bool IsFromMe { get; }
    public string? SenderName { get; }
    public string? SenderInitials { get; }

    /// <summary>Stable per-contact key for "Colorful bubbles" tinting (incoming only): the sender's
    /// address. Lets a contact's bubbles read the same color as their avatar.</summary>
    public string? SenderColorKey { get; }
    public long DateCreated { get; }
    public bool IsEmojiOnly { get; }
    public List<AttachmentViewModel>? Attachments { get; }
    public bool HasAttachments => Attachments is { Count: > 0 };

    /// <summary>Rich link (URL) preview card for this bubble, or null. When set, the bubble renders
    /// the card instead of the raw URL text and the iMessage payload "attachment".</summary>
    public UrlPreviewViewModel? UrlPreview { get; }
    public bool IsUrlPreview => UrlPreview is not null;

    public string FormattedTime => DateCreated > 0
        ? DateTimeOffset.FromUnixTimeMilliseconds(DateCreated).LocalDateTime
            .ToString(App.Services.GetService<AppSettings>()?.Use24HrFormat == true ? "HH:mm" : "h:mm tt")
        : string.Empty;

    public string DeliveryStatusText => Status switch
    {
        DeliveryStatus.Sending when IsDelayed => "Scheduled",
        DeliveryStatus.Sending => "Sending…",
        DeliveryStatus.Sent => "Sent",
        DeliveryStatus.Delivered => "Delivered",
        DeliveryStatus.Read => "Read",
        DeliveryStatus.Error => "Failed",
        _ => string.Empty
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeliveryStatusText))]
    public partial DeliveryStatus Status { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeliveryStatusText))]
    public partial bool IsDelayed { get; set; }

    [ObservableProperty] public partial bool ShowTail { get; set; }
    [ObservableProperty] public partial string? ErrorMessage { get; set; }

    public Action? CancelAction { get; set; }

    // ── Edits & Unsend (Phase 15) ──

    /// <summary>Timestamp of the latest edit, or null. Drives the "Edited" label (incoming or outgoing).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEdited))]
    public partial long? DateEdited { get; set; }

    public bool IsEdited => DateEdited is > 0;

    /// <summary>True when this message part was unsent (retracted). Replaces the content with a placeholder.</summary>
    [ObservableProperty]
    public partial bool IsUnsent { get; set; }

    /// <summary>Invoked when the user chooses "Edit" on this (own) message.</summary>
    public Action? StartEditAction { get; set; }

    /// <summary>Invoked when the user chooses "Undo Send" on this (own) message.</summary>
    public Action? UnsendAction { get; set; }

    /// <summary>Invoked when the user confirms "Delete" on this message.</summary>
    public Action? DeleteAction { get; set; }

    /// <summary>Applies an edit (local optimistic or remote): rewrites the text and shows "Edited".</summary>
    public void ApplyEdit(string? newText, long? dateEdited)
    {
        Text = newText;
        DateEdited = dateEdited ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        IsUnsent = false;
    }

    /// <summary>Marks the message as unsent so the bubble renders the retracted placeholder. The text is
    /// dropped so the retracted content can't be copied or previewed.</summary>
    public void ApplyUnsend()
    {
        IsUnsent = true;
        Text = null;
    }

    // ── Reactions (tapbacks) ──

    private readonly List<ReactionRecord> _reactionRecords = [];

    /// <summary>Grouped reaction badges shown beneath this bubble, in canonical order.</summary>
    public IReadOnlyList<ReactionBadgeViewModel> Reactions { get; private set; } = [];

    public bool HasReactions => Reactions.Count > 0;

    /// <summary>The local user's active reaction type on this message, or null. Drives toggle behaviour.</summary>
    public string? SelfReactionType { get; private set; }

    /// <summary>Bumped whenever <see cref="Reactions"/> is recomputed so the view rebuilds the pill.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReactions))]
    public partial int ReactionRevision { get; set; }

    /// <summary>Invoked when the user picks a reaction type for this message (toggle resolved upstream).</summary>
    public Action<string>? SendReactionAction { get; set; }

    /// <summary>Replaces the full set of reactions (used on initial load).</summary>
    public void SetReactions(IEnumerable<ReactionRecord> records)
    {
        _reactionRecords.Clear();
        _reactionRecords.AddRange(records);
        RecomputeReactions();
    }

    /// <summary>Adds or replaces a single reaction (used for live socket updates and optimistic sends).</summary>
    public void AddReaction(ReactionRecord record)
    {
        _reactionRecords.RemoveAll(r => r.Guid == record.Guid);
        _reactionRecords.Add(record);
        RecomputeReactions();
    }

    private void RecomputeReactions()
    {
        Reactions = ReactionSummarizer.Summarize(_reactionRecords)
            .Select(s => new ReactionBadgeViewModel(s.ReactionType, s.Emoji, s.Count, s.IncludesMe))
            .ToList();
        SelfReactionType = ReactionSummarizer.SelfReaction(_reactionRecords);
        ReactionRevision++;
    }

    // ── Replies (threads) ──

    /// <summary>GUID of the message this one replies to, or null. Set only on the reply-host bubble.</summary>
    public string? ThreadOriginatorGuid { get; private set; }

    public bool IsReply => ThreadOriginatorGuid is not null;

    /// <summary>Snippet + sender of the replied-to message, resolved asynchronously after load.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReplyContextReady))]
    public partial string? ReplyPreviewText { get; set; }

    [ObservableProperty] public partial string? ReplySenderLabel { get; set; }

    public bool ReplyContextReady => !string.IsNullOrEmpty(ReplyPreviewText);

    /// <summary>Invoked when the user taps the reply indicator, to jump to the original message.</summary>
    public Action<string>? ScrollToMessageAction { get; set; }

    /// <summary>Invoked when the user chooses "Reply" on this message.</summary>
    public Action? StartReplyAction { get; set; }

    public void SetReplyContext(string senderLabel, string previewText)
    {
        ReplySenderLabel = senderLabel;
        ReplyPreviewText = previewText;
    }

    private MessageBubbleViewModel(MessageEntity message, IContactResolverService contacts,
        bool isGroup, IAttachmentCacheService? attachmentCache,
        bool includeText, bool includeAttachments, bool includeReply, string? displayText)
    {
        if (includeReply)
            ThreadOriginatorGuid = message.ThreadOriginatorGuid;

        MessageGuid = message.Guid;
        TempGuid = message.Guid.StartsWith("temp-", StringComparison.Ordinal) ? message.Guid : null;
        Text = includeText ? displayText : null;
        Subject = includeText ? message.Subject : null;
        DateEdited = message.DateEdited;
        IsUnsent = MessageEdits.IsPartRetracted(message.MessageSummaryInfoJson, 0);
        if (IsUnsent) Text = null;   // retracted content is never shown or copyable
        IsFromMe = message.IsFromMe;
        DateCreated = message.DateCreated ?? 0;
        ShowTail = true;

        if (!message.IsFromMe && isGroup && message.Handle is not null)
        {
            SenderName = contacts.GetDisplayName(message.Handle.Address);
            SenderInitials = contacts.GetInitials(SenderName);
        }

        SenderColorKey = message.IsFromMe ? null : message.Handle?.Address;

        if (includeAttachments && attachmentCache is not null && message.Attachments.Count > 0)
        {
            Attachments = message.Attachments
                .Select(a => new AttachmentViewModel(a, attachmentCache))
                .ToList();
        }

        IsEmojiOnly = !HasAttachments && CheckEmojiOnly(Text);

        UrlPreview = TryBuildUrlPreview(message, Attachments);

        Status = message.IsFromMe
            ? (message.DateRead is not null ? DeliveryStatus.Read
                : message.DateDelivered is not null ? DeliveryStatus.Delivered
                : DeliveryStatus.Sent)
            : DeliveryStatus.None;
    }

    /// <summary>Builds an optimistic outgoing bubble for just-picked local attachments
    /// (no server message exists yet). The attachments are pre-built local
    /// <see cref="AttachmentViewModel"/>s so the image renders immediately.</summary>
    private MessageBubbleViewModel(string guid, long dateCreated,
        List<AttachmentViewModel> attachments, string? threadOriginatorGuid)
    {
        MessageGuid = guid;
        TempGuid = guid.StartsWith("temp-", StringComparison.Ordinal) ? guid : null;
        IsFromMe = true;
        DateCreated = dateCreated;
        ShowTail = true;
        Attachments = attachments;
        ThreadOriginatorGuid = threadOriginatorGuid;
        Status = DeliveryStatus.Sent;
    }

    public static MessageBubbleViewModel CreateOptimisticAttachment(
        string guid, long dateCreated, List<AttachmentViewModel> attachments,
        string? threadOriginatorGuid = null)
        => new(guid, dateCreated, attachments, threadOriginatorGuid);

    // U+FFFC — iMessage places this in the text at the position where an attachment belongs.
    private const char ObjectReplacementChar = '￼';

    /// <summary>
    /// Creates one or two bubbles from a message. When a message has both text and
    /// attachments, they are split into separate bubbles. The order is determined by
    /// the position of U+FFFC in the message text — the same marker iMessage uses to
    /// indicate where attachments sit relative to text.
    /// </summary>
    public static List<MessageBubbleViewModel> CreateFromEntity(
        MessageEntity message, IContactResolverService contacts,
        bool isGroup, IAttachmentCacheService? attachmentCache = null)
    {
        var rawText = message.Text;
        var cleanText = StripReplacementChars(rawText);

        // A rich link preview is one logical bubble — a card — never split into URL text + a payload
        // "attachment". Detect it before the text/attachment split below.
        if (LooksLikeUrlPreview(message))
        {
            return [new(message, contacts, isGroup, attachmentCache,
                includeText: true, includeAttachments: true, includeReply: true,
                displayText: cleanText ?? rawText)];
        }

        var hasText = !string.IsNullOrWhiteSpace(cleanText);
        var hasAttachments = attachmentCache is not null && message.Attachments.Count > 0;

        if (hasText && hasAttachments)
        {
            var textFirst = AttachmentComesAfterText(rawText);

            // The reply indicator renders above the message, so it lives on whichever bubble is first.
            var textBubble = new MessageBubbleViewModel(message, contacts, isGroup, attachmentCache,
                includeText: true, includeAttachments: false, includeReply: textFirst, displayText: cleanText);
            var attachBubble = new MessageBubbleViewModel(message, contacts, isGroup, attachmentCache,
                includeText: false, includeAttachments: true, includeReply: !textFirst, displayText: null);

            return textFirst ? [textBubble, attachBubble] : [attachBubble, textBubble];
        }

        return [new(message, contacts, isGroup, attachmentCache,
            includeText: true, includeAttachments: true, includeReply: true, displayText: cleanText ?? rawText)];
    }

    private static string? StripReplacementChars(string? text)
    {
        if (text is null) return null;
        var stripped = text.Replace(ObjectReplacementChar.ToString(), "").Trim();
        return stripped.Length > 0 ? stripped : null;
    }

    private static bool AttachmentComesAfterText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return true;
        var idx = text.IndexOf(ObjectReplacementChar);
        if (idx < 0) return true;
        // If there's any real text before the first ￼, text came first.
        var before = text[..idx];
        return !string.IsNullOrWhiteSpace(before);
    }

    // ── URL (rich link) previews ──

    private const string UrlBalloonBundleId = "com.apple.messages.URLBalloonProvider";

    /// <summary>Cheap predicate (no UI build) for whether a message is a rich link preview: the
    /// iMessage URL-balloon marker, or a URL-type payload with data.</summary>
    private static bool LooksLikeUrlPreview(MessageEntity message)
    {
        if (message.BalloonBundleId == UrlBalloonBundleId) return true;
        var payload = ParsePayload(message.PayloadDataJson);
        return payload?.Type == PayloadType.Url && payload.UrlData is { Count: > 0 };
    }

    /// <summary>Builds the preview card data from the message's payload, resolving the destination
    /// URL and the hero image (the iMessage <c>pluginPayloadAttachment</c>). Returns null when the
    /// message isn't a link preview or no URL can be resolved.</summary>
    private static UrlPreviewViewModel? TryBuildUrlPreview(MessageEntity message,
        List<AttachmentViewModel>? attachments)
    {
        var payload = ParsePayload(message.PayloadDataJson);
        var data = payload?.Type == PayloadType.Url ? payload.UrlData?.FirstOrDefault() : null;
        var isBalloon = message.BalloonBundleId == UrlBalloonBundleId;
        var isSingleUrl = UrlDetector.IsSingleUrl(StripReplacementChars(message.Text));
        if (data is null && !isBalloon && !isSingleUrl) return null;

        var target = ResolveUrl(data) ?? UrlDetector.FirstUrl(message.Text);
        if (string.IsNullOrEmpty(target)) return null;

        // The server preview image is delivered as the message's pluginPayloadAttachment; fall back to
        // any image attachment. (Apple's imageMetadata URLs are internal and not directly loadable.)
        var hero = attachments?.FirstOrDefault(a =>
                       a.TransferName?.Contains("pluginPayloadAttachment", StringComparison.OrdinalIgnoreCase) == true)
                   ?? attachments?.FirstOrDefault(a => a.Category == AttachmentCategory.Image);

        var host = Uri.TryCreate(target, UriKind.Absolute, out var uri) ? uri.Host : null;
        var site = !string.IsNullOrWhiteSpace(data?.SiteName) ? data!.SiteName : host;

        // Rich when the server already gave us a title or image; otherwise show a "Show preview"
        // affordance the user can tap to fetch metadata on demand.
        var hasServerPreview = !string.IsNullOrWhiteSpace(data?.Title) || hero is not null;
        var state = hasServerPreview ? UrlPreviewState.Rich : UrlPreviewState.NeedsPreview;
        return new UrlPreviewViewModel(target, data?.Title, data?.Summary, site, hero, state);
    }

    private static PayloadData? ParsePayload(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<PayloadData>(json, JsonDefaults.Options); }
        catch { return null; }
    }

    private static string? ResolveUrl(UrlPreviewData? data)
        => data is null ? null : PickUrl(data.Url) ?? PickUrl(data.OriginalUrl);

    // The payload stores URLs as { "NS.relative": "https://..." }.
    private static string? PickUrl(Dictionary<string, string?>? map)
    {
        if (map is null) return null;
        if (map.TryGetValue("NS.relative", out var rel) && !string.IsNullOrEmpty(rel)) return rel;
        return map.Values.FirstOrDefault(v => !string.IsNullOrEmpty(v));
    }

    public void ConfirmSent(string serverGuid)
    {
        MessageGuid = serverGuid;
        Status = DeliveryStatus.Sent;
    }

    public void MarkFailed(string? errorMessage = null)
    {
        Status = DeliveryStatus.Error;
        ErrorMessage = errorMessage;
    }

    public void UpdateDeliveryStatus(MessageEntity updated)
    {
        Status = updated.IsFromMe
            ? (updated.DateRead is not null ? DeliveryStatus.Read
                : updated.DateDelivered is not null ? DeliveryStatus.Delivered
                : DeliveryStatus.Sent)
            : DeliveryStatus.None;
    }

    private static bool CheckEmojiOnly(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var stripped = text.Trim();
        if (stripped.Length == 0 || stripped.Length > 20) return false;

        foreach (var c in stripped)
        {
            if (char.IsAsciiLetterOrDigit(c)) return false;
            if (char.IsWhiteSpace(c)) continue;
            if (char.IsPunctuation(c) && c != '‍') return false;
        }
        return true;
    }
}

public enum DeliveryStatus
{
    None,
    Sending,
    Sent,
    Delivered,
    Read,
    Error
}

/// <summary>A single reaction pill: emoji, how many reactors, and whether the local user is one of them.</summary>
public sealed record ReactionBadgeViewModel(string ReactionType, string Emoji, int Count, bool IncludesMe)
{
    public string CountText => Count > 1 ? Count.ToString() : string.Empty;
    public bool ShowCount => Count > 1;
}
