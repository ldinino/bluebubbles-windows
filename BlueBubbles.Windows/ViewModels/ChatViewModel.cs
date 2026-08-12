using System.Collections.ObjectModel;
using System.Text.Json;
using BlueBubbles.Core.Configuration;
using BlueBubbles.Core.Data.Entities;
using BlueBubbles.Core.Models;
using BlueBubbles.Core.Services;
using BlueBubbles.Core.Utils;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlueBubbles.Windows.ViewModels;

public partial class ChatViewModel : ObservableObject
{
    private readonly IMessagesService _messagesService;
    private readonly IContactResolverService _contacts;
    private readonly IActionHandler _actionHandler;
    private readonly IOutgoingMessageService _outgoingService;
    private readonly IChatsService _chatsService;
    private readonly ISocketService _socketService;
    private readonly IAttachmentCacheService _attachmentCache;
    private readonly ISyncService _syncService;
    private readonly IScheduledMessageService _scheduledMessages;
    private readonly ILinkPreviewService? _linkPreview;
    private readonly AppSettings _settings;

    // Primary identity of the open conversation (the merged tile's key / phone-preferred chat).
    private string _chatGuid = string.Empty;
    // A conversation can fold several underlying chats together ("sticky bifurcation"): these span all
    // constituents so loading, socket routing, and reconcile cover the whole interleaved history.
    private List<int> _chatIds = [];
    private List<string> _constituentGuids = [];
    private readonly HashSet<string> _chatGuids = new(StringComparer.OrdinalIgnoreCase);
    private bool _isMerged;
    private string _primaryAddress = string.Empty;
    // Outgoing target: the most-recently-active constituent (recomputed on load and on new messages).
    private string _sendGuid = string.Empty;
    private bool _isGroup;
    private IReadOnlyList<string> _participantAddresses = [];
    private string? _chatDisplayNameRaw;
    private long? _oldestMessageDate;
    // Set when a sync finishes while the thread is still opening; LoadChatAsync runs the deferred
    // reconcile once it's done so the sync signal isn't dropped to the IsLoading guard.
    private bool _reconcilePending;
    // Set when a contact import/reset lands while the thread is still loading; the re-merge is deferred
    // to the load's completion so it never mutates the message list concurrently.
    private bool _reapplyMergePending;
    private readonly HashSet<string> _pendingTempGuids = [];
    private Timer? _typingDebounce;   // send-side: throttles our "started/stopped-typing" emits
    private Timer? _typingExpiry;     // receive-side: auto-clears a stuck "… is typing" bubble

    private const int PageSize = 50;

    // The server is edge-triggered (one event when the other party starts typing, one when
    // they stop). The stop event is occasionally lost (socket hiccup / Private API), which
    // would otherwise leave the bubble up forever — so we clear it ourselves after this long
    // with no further activity. Apple's own clients use a comparable local timeout.
    private static readonly TimeSpan TypingTimeout = TimeSpan.FromSeconds(30);

    public ObservableCollection<object> Items { get; } = [];
    public ObservableCollection<StagedAttachment> StagedAttachments { get; } = [];

    /// <summary>This chat's pending (or errored) server-side scheduled messages, shown as
    /// outlined bubbles pinned at the bottom of the thread until the server sends them.</summary>
    public ObservableCollection<ScheduledMessageItem> ScheduledItems { get; } = [];

    [ObservableProperty] public partial string ChatDisplayName { get; set; }
    [ObservableProperty] public partial string ParticipantSummary { get; set; }
    [ObservableProperty] public partial string Initials { get; set; }
    [ObservableProperty] public partial byte[]? AvatarBytes { get; set; }
    [ObservableProperty] public partial bool IsGroupChat { get; set; }
    [ObservableProperty] public partial string GroupInitials1 { get; set; }
    [ObservableProperty] public partial string GroupInitials2 { get; set; }
    [ObservableProperty] public partial byte[]? GroupAvatarBytes1 { get; set; }
    [ObservableProperty] public partial byte[]? GroupAvatarBytes2 { get; set; }
    [ObservableProperty] public partial bool IsTyping { get; set; }
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial bool HasMoreMessages { get; set; }
    [ObservableProperty] public partial bool ShowScrollToBottom { get; set; }
    [ObservableProperty] public partial string MessageText { get; set; }
    [ObservableProperty] public partial bool CanSend { get; set; }

    /// <summary>Composer placeholder reflecting the chat's transport: "iMessage", or a neutral
    /// "Text Message" for SMS/RCS-forwarded chats (the server doesn't distinguish SMS from RCS).</summary>
    [ObservableProperty] public partial string ComposerPlaceholder { get; set; }

    /// <summary>The message currently being replied to, or null when not in reply mode.</summary>
    [ObservableProperty] public partial ReplyDraft? ReplyingTo { get; set; }

    /// <summary>The message currently being edited, or null when not in edit mode.</summary>
    [ObservableProperty] public partial EditDraft? EditingMessage { get; set; }

    public event EventHandler? MessagesLoaded;
    public event EventHandler? NewMessageAppended;
    public event EventHandler<string>? ScrollToMessageRequested;

    /// <summary>Raised with a user-facing message when a delete couldn't reach the server, so the
    /// page can surface it (the bubble/local row is intentionally left untouched on failure).</summary>
    public event EventHandler<string>? DeleteMessageFailed;

    public ChatViewModel(
        IMessagesService messagesService,
        IContactResolverService contacts,
        IActionHandler actionHandler,
        IOutgoingMessageService outgoingService,
        IChatsService chatsService,
        ISocketService socketService,
        IAttachmentCacheService attachmentCache,
        ISyncService syncService,
        IScheduledMessageService scheduledMessages,
        AppSettings settings,
        ILinkPreviewService? linkPreview = null)
    {
        _messagesService = messagesService;
        _contacts = contacts;
        _actionHandler = actionHandler;
        _outgoingService = outgoingService;
        _chatsService = chatsService;
        _socketService = socketService;
        _attachmentCache = attachmentCache;
        _syncService = syncService;
        _scheduledMessages = scheduledMessages;
        _settings = settings;
        _linkPreview = linkPreview;

        ChatDisplayName = string.Empty;
        ParticipantSummary = string.Empty;
        Initials = string.Empty;
        GroupInitials1 = string.Empty;
        GroupInitials2 = string.Empty;
        MessageText = string.Empty;
        ComposerPlaceholder = "iMessage";

        _actionHandler.NewMessageReceived += (s, e) => RunOnUI(() => OnNewMessageReceived(s, e));
        _actionHandler.MessageUpdated += (s, e) => RunOnUI(() => OnMessageUpdated(s, e));
        _actionHandler.ReactionReceived += (s, e) => RunOnUI(() => OnReactionReceived(s, e));
        _actionHandler.TypingIndicatorChanged += (s, e) => RunOnUI(() => OnTypingIndicatorChanged(s, e));
        _actionHandler.ScheduledMessagesChanged += (s, e) => RunOnUI(() => OnScheduledMessagesChanged(e));
        _outgoingService.MessageStateChanged += (s, e) => RunOnUI(() => OnOutgoingMessageStateChanged(s, e));
        _contacts.ContactsChanged += (s, e) => RunOnUI(() => _ = OnContactsChangedAsync());

        // A background delta sync (after sleep / reconnect) writes missed messages straight to the
        // DB — the socket never pushes them — so an already-open thread wouldn't see them until it
        // was reloaded. Catch the open chat up from the DB whenever a sync finishes.
        _syncService.SyncStateChanged += (s, syncing) =>
        {
            if (!syncing) RunOnUI(() => _ = RunReconcileAsync());
        };

        // Direct consequence of the database gaining rows for a chat (socket save or per-batch delta
        // write): if it's the open thread, append the new rows from the DB. This keeps the view in
        // step with the DB on every persist, not just on the single end-of-sync pulse above.
        _chatsService.MessagesPersisted += (s, guid) =>
        {
            if (_chatGuids.Contains(guid)) RunOnUI(() => _ = AppendPersistedMessagesAsync(guid));
        };
    }

    /// <summary>Reacts to a contact set change (vCard import/reset) for the open conversation. A contact
    /// card can newly link this thread with another (sticky bifurcation) or, on reset, split a merged
    /// conversation apart — so we re-derive the merge. When the underlying thread set actually changed we
    /// reload to re-interleave the history live; otherwise we only re-resolve the header, preserving the
    /// scroll position.</summary>
    private async Task OnContactsChangedAsync()
    {
        if (string.IsNullOrEmpty(_chatGuid)) return;

        // Don't mutate the message list while a load is in flight — defer the re-merge to its completion.
        if (IsLoading)
        {
            _reapplyMergePending = true;
            return;
        }

        var tile = BuildOpenConversationTile();
        if (tile is null)
        {
            RefreshContactInfo();
            return;
        }

        var newGuids = tile.ConstituentGuids;
        var sameThreadSet = newGuids.Count == _chatGuids.Count && newGuids.All(_chatGuids.Contains);
        if (sameThreadSet)
        {
            RefreshContactInfo();
            return;
        }

        // The conversation gained or lost a constituent thread — reload to interleave (or un-interleave)
        // the full history in place.
        await LoadConversationAsync(tile);
    }

    /// <summary>Rebuilds the tile for the currently-open conversation from the latest chats and contacts,
    /// so a contact change re-runs the merge for this thread. Returns null when the open chat can't be
    /// found (e.g. it was deleted).</summary>
    private ConversationTileViewModel? BuildOpenConversationTile()
    {
        var m = FindOpenConversation(_chatsService.Chats)
            ?? FindOpenConversation(_chatsService.ArchivedChats);
        return m is null ? null : new ConversationTileViewModel(m, _contacts);
    }

    private MergedConversation? FindOpenConversation(IReadOnlyList<ChatWithParticipants> source)
    {
        var merged = ConversationMerger.Merge(source, _contacts);
        // Prefer the group still anchored on the current primary GUID; otherwise any group that overlaps
        // the current constituent set (covers the primary changing when a merge forms).
        return merged.FirstOrDefault(x => x.ConstituentGuids.Contains(_chatGuid, StringComparer.OrdinalIgnoreCase))
            ?? merged.FirstOrDefault(x => x.ConstituentGuids.Any(_chatGuids.Contains));
    }

    /// <summary>Re-resolves the open chat's header (name, initials, avatars) after the contact set
    /// changes — e.g. a vCard import — so it doesn't stay on the raw address.</summary>
    private void RefreshContactInfo()
    {
        if (string.IsNullOrEmpty(_chatGuid)) return;

        if (_isMerged)
        {
            // One person with several addresses — resolve as the single contact, info bar shows the phone.
            ChatDisplayName = _contacts.GetDisplayName(_primaryAddress);
            Initials = _contacts.GetAvatarInitials(_primaryAddress);
            AvatarBytes = _contacts.GetAvatar(_primaryAddress);
            ParticipantSummary = ContactResolverService.FormatAddress(_primaryAddress);
            return;
        }

        ChatDisplayName = _contacts.GetChatDisplayName(_participantAddresses, _chatDisplayNameRaw);
        Initials = _contacts.GetChatInitials(_participantAddresses, _chatDisplayNameRaw);
        AvatarBytes = _participantAddresses.Count == 1
            ? _contacts.GetAvatar(_participantAddresses[0])
            : null;
        if (_isGroup)
            RefreshGroupAvatars();
        else
            ParticipantSummary = _participantAddresses.FirstOrDefault() ?? string.Empty;
    }

    /// <summary>Re-resolves the two stacked group sub-avatars from the latest chat data, picking the
    /// same recent-sender faces the list tile uses so the header mirrors the list.</summary>
    private void RefreshGroupAvatars()
    {
        var data = _chatsService.Chats.FirstOrDefault(c => c.Chat.Guid == _chatGuid);
        if (data is null) return;
        var group = GroupAvatarResolver.Resolve(data, _contacts);
        GroupInitials1 = group.Initials1;
        GroupInitials2 = group.Initials2;
        GroupAvatarBytes1 = group.Bytes1;
        GroupAvatarBytes2 = group.Bytes2;
    }

    public Task LoadChatAsync(ConversationTileViewModel tile)
    {
        if (_chatGuid == tile.ChatGuid) return Task.CompletedTask;
        return LoadConversationAsync(tile);
    }

    /// <summary>Loads (or reloads) the conversation a tile represents — clearing the thread and pulling
    /// the interleaved message history for all its constituent chats. Unlike <see cref="LoadChatAsync"/>
    /// it doesn't short-circuit on the same primary GUID, so it also drives a live re-interleave when a
    /// contact import/reset changes which underlying chats this conversation folds together.</summary>
    private async Task LoadConversationAsync(ConversationTileViewModel tile)
    {
        _chatGuid = tile.ChatGuid;
        _isGroup = tile.IsGroup;
        _isMerged = tile.IsMerged;
        _primaryAddress = tile.PrimaryAddress;
        _chatIds = tile.ConstituentChatIds.ToList();
        _constituentGuids = tile.ConstituentGuids.ToList();
        _chatGuids.Clear();
        foreach (var g in _constituentGuids) _chatGuids.Add(g);
        _sendGuid = _chatGuid;
        RecomputeSendTarget();
        _participantAddresses = tile.Participants.Select(p => p.Address).ToList();
        _chatDisplayNameRaw = tile.Chat.DisplayName;
        ChatDisplayName = tile.DisplayName;
        Initials = tile.Initials;
        AvatarBytes = tile.AvatarBytes;
        IsGroupChat = tile.IsGroup;
        // Mirror the list tile's two-face group avatar in the header (it would otherwise show the
        // AvatarControl's empty placeholder for groups).
        GroupInitials1 = tile.GroupInitials1;
        GroupInitials2 = tile.GroupInitials2;
        GroupAvatarBytes1 = tile.GroupAvatarBytes1;
        GroupAvatarBytes2 = tile.GroupAvatarBytes2;
        // Unknown/missing service is treated as iMessage (the overwhelmingly common case).
        ComposerPlaceholder = string.IsNullOrEmpty(tile.Chat.Service) || tile.Chat.Service == "iMessage"
            ? "iMessage"
            : "Text Message";
        SetTypingBubble(false);

        ParticipantSummary = _isGroup
            ? $"{tile.Participants.Count} participants"
            : _isMerged
                ? ContactResolverService.FormatAddress(_primaryAddress)
                : tile.Participants.FirstOrDefault()?.Address ?? string.Empty;

        Items.Clear();
        ScheduledItems.Clear();
        _oldestMessageDate = null;
        _reconcilePending = false;
        _reapplyMergePending = false;
        _pendingTempGuids.Clear();
        HasMoreMessages = true;
        MessageText = string.Empty;
        StagedAttachments.Clear();
        StopTypingIndicator();

        _ = RefreshScheduledMessagesAsync();

        IsLoading = true;
        try
        {
            var messages = await _messagesService.LoadMessagesAsync(_chatIds, PageSize);

            // Safety net: a chat left empty by a missed/incomplete sync would otherwise show a
            // permanently-blank thread. Pull each constituent's newest page from the server on open, once.
            if (messages.Count == 0)
            {
                var hydrated = false;
                for (var i = 0; i < _chatIds.Count; i++)
                    hydrated |= await _messagesService.EnsureChatHydratedAsync(_chatIds[i], _constituentGuids[i], PageSize);
                if (hydrated)
                    messages = await _messagesService.LoadMessagesAsync(_chatIds, PageSize);
            }

            if (messages.Count > 0) _oldestMessageDate = messages[0].DateCreated;
            BuildMessageList(messages);
            await LoadAndApplyReactionsAsync(messages);
            await ResolveReplySnippetsAsync();
        }
        finally
        {
            IsLoading = false;
        }

        MessagesLoaded?.Invoke(this, EventArgs.Empty);

        // A sync that completed mid-load deferred its reconcile rather than dropping it; run it now
        // that the freshly-loaded page is in place.
        if (_reconcilePending)
        {
            _reconcilePending = false;
            await RunReconcileAsync();
        }

        // A contact import/reset that landed mid-load deferred its re-merge; run it now that the load is
        // done (it re-checks the thread set and only reloads again if it actually changed).
        if (_reapplyMergePending)
        {
            _reapplyMergePending = false;
            await OnContactsChangedAsync();
        }
    }

    [RelayCommand]
    private async Task LoadMoreMessagesAsync()
    {
        if (!HasMoreMessages || IsLoading) return;
        IsLoading = true;
        try
        {
            var messages = await _messagesService.LoadMessagesAsync(_chatIds, PageSize, _oldestMessageDate);

            if (messages.Count == 0)
            {
                // Cache exhausted across the union — pull an older page for each constituent from the
                // server, then re-query the interleaved older window.
                var fetchedAny = false;
                for (var i = 0; i < _chatIds.Count; i++)
                {
                    var older = await _messagesService.FetchOlderMessagesFromServerAsync(
                        _chatIds[i], _constituentGuids[i], 25);
                    fetchedAny |= older.Count > 0;
                }
                if (fetchedAny)
                    messages = await _messagesService.LoadMessagesAsync(_chatIds, PageSize, _oldestMessageDate);
            }

            if (messages.Count == 0)
            {
                HasMoreMessages = false;
            }
            else
            {
                _oldestMessageDate = messages[0].DateCreated;
                PrependMessages(messages);
                await LoadAndApplyReactionsAsync(messages);
                await ResolveReplySnippetsAsync();
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnMessageTextChanged(string value)
    {
        CanSend = !string.IsNullOrWhiteSpace(value) || StagedAttachments.Count > 0;
        EmitTypingIndicator();
    }

    /// <summary>Picks the outgoing target for a merged conversation: the constituent chat with the most
    /// recent message (per the merged-thread design). A no-op for a single chat. Recomputed before each
    /// send and when a new message lands, so replies follow whichever address is currently active.</summary>
    private void RecomputeSendTarget()
    {
        if (_constituentGuids.Count <= 1)
        {
            _sendGuid = _chatGuid;
            return;
        }

        var chats = _chatsService.Chats;
        var bestGuid = _chatGuid;
        var bestDate = long.MinValue;
        foreach (var guid in _constituentGuids)
        {
            var date = chats.FirstOrDefault(c => c.Chat.Guid == guid)?.Chat.LatestMessageDate ?? 0;
            if (date >= bestDate) { bestDate = date; bestGuid = guid; }
        }
        _sendGuid = bestGuid;
    }

    [RelayCommand]
    private void SendMessage()
    {
        if (string.IsNullOrEmpty(_chatGuid)) return;

        // In edit mode the composer's send button commits the edit instead of sending a new message.
        if (EditingMessage is not null)
        {
            CommitEdit();
            return;
        }

        var text = MessageText?.Trim();
        var hasText = !string.IsNullOrEmpty(text);
        var hasAttachments = StagedAttachments.Count > 0;
        if (!hasText && !hasAttachments) return;

        RecomputeSendTarget();
        StopTypingIndicator();

        string? previewText = null;

        // A reply targets a single message. Apply it to the text, or the first attachment if text-free.
        var reply = ReplyingTo;
        var replyConsumed = false;

        if (hasText)
        {
            var tempGuid = _outgoingService.EnqueueText(_sendGuid, text!,
                selectedMessageGuid: reply?.MessageGuid, partIndex: reply?.PartIndex);
            InsertOptimisticMessage(tempGuid, text!, reply);
            replyConsumed = reply is not null;
            previewText = text;
        }

        foreach (var attachment in StagedAttachments.ToList())
        {
            var attReply = replyConsumed ? null : reply;
            var tempGuid = _outgoingService.EnqueueAttachment(_sendGuid, attachment.FilePath,
                selectedMessageGuid: attReply?.MessageGuid, partIndex: attReply?.PartIndex);
            InsertOptimisticAttachment(tempGuid, attachment, attReply);
            replyConsumed = replyConsumed || attReply is not null;
            previewText ??= attachment.FileName;
        }

        MessageText = string.Empty;
        StagedAttachments.Clear();
        ReplyingTo = null;
        CanSend = false;

        _ = _chatsService.HandleNewMessageAsync(
            _sendGuid, previewText,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            isFromMe: true);
    }

    /// <summary>GUID of the open chat, or empty when no chat is loaded. Used by the page to
    /// scope the scheduled-messages dialog.</summary>
    public string ChatGuid => _chatGuid;

    /// <summary>Whether "Send later" is currently possible: a loaded chat, non-empty text, and
    /// no staged attachments / reply / edit (the server's scheduled payload is text-only, and a
    /// scheduled reply is a deliberate v1 scope cut).</summary>
    public bool CanScheduleSend =>
        !string.IsNullOrEmpty(_chatGuid) &&
        !string.IsNullOrWhiteSpace(MessageText) &&
        StagedAttachments.Count == 0 &&
        EditingMessage is null &&
        ReplyingTo is null;

    /// <summary>
    /// Schedules the composer text for a future server-side send and clears the composer.
    /// Returns null on success, or a user-facing error message. The message shows up in
    /// <see cref="ScheduledItems"/> (outlined pending bubble) — it only enters the thread
    /// proper when the server sends it at fire time, via the normal new-message socket path.
    /// </summary>
    public async Task<string?> ScheduleCurrentMessageAsync(DateTimeOffset sendAt)
    {
        if (!CanScheduleSend)
            return "This message can't be scheduled.";

        RecomputeSendTarget();
        StopTypingIndicator();

        var response = await _scheduledMessages.CreateAsync(
            _sendGuid, MessageText.Trim(), sendAt.ToUnixTimeMilliseconds());
        if (response.Status is < 200 or >= 300)
            return response.Error?.ErrorMessage ?? response.Message;

        MessageText = string.Empty;
        CanSend = false;
        await RefreshScheduledMessagesAsync();
        return null;
    }

    /// <summary>Cancels a pending scheduled message server-side and drops its bubble.
    /// Returns null on success, or a user-facing error message.</summary>
    public async Task<string?> CancelScheduledAsync(ScheduledMessageItem item)
    {
        var response = await _scheduledMessages.DeleteAsync(item.Id);
        if (response.Status is < 200 or >= 300)
            return response.Error?.ErrorMessage ?? response.Message;

        ScheduledItems.Remove(item);
        return null;
    }

    /// <summary>Reschedules (text and/or time) a pending scheduled message.
    /// Returns null on success, or a user-facing error message.</summary>
    public async Task<string?> UpdateScheduledAsync(ScheduledMessageItem item, string text, DateTimeOffset sendAt)
    {
        var response = await _scheduledMessages.UpdateAsync(
            item.Id, _chatGuid, text, sendAt.ToUnixTimeMilliseconds());
        if (response.Status is < 200 or >= 300)
            return response.Error?.ErrorMessage ?? response.Message;

        await RefreshScheduledMessagesAsync();
        return null;
    }

    /// <summary>Reloads this chat's pending/errored scheduled messages from the server. The
    /// guid is captured up front so a reply landing after a chat switch is discarded.</summary>
    private async Task RefreshScheduledMessagesAsync()
    {
        var guid = _chatGuid;
        if (string.IsNullOrEmpty(guid)) return;

        var response = await _scheduledMessages.GetAllAsync();
        if (response.Status is < 200 or >= 300 || response.Data is null) return;

        var items = response.Data
            .Where(m => m.Payload?.ChatGuid is { } g && _chatGuids.Contains(g) &&
                        m.Status is ScheduledMessageStatus.Pending or ScheduledMessageStatus.Error)
            .OrderBy(m => m.ScheduledForLocal ?? DateTimeOffset.MaxValue)
            .Select(ScheduledMessageItem.From)
            .ToList();

        RunOnUI(() =>
        {
            if (_chatGuid != guid) return;
            ScheduledItems.Clear();
            foreach (var item in items)
                ScheduledItems.Add(item);
        });
    }

    /// <summary>Socket push for any scheduled-message change (created/updated/deleted/sent/
    /// error): if it touches the open chat, re-pull the pending list. A 'sent' event clears
    /// the outlined bubble; the real message then arrives via the new-message path.</summary>
    private void OnScheduledMessagesChanged(ScheduledMessagesEventArgs e)
    {
        if (string.IsNullOrEmpty(_chatGuid)) return;
        if (!e.Messages.Any(m => m.Payload?.ChatGuid is { } g && _chatGuids.Contains(g))) return;
        _ = RefreshScheduledMessagesAsync();
    }

    public void AddStagedAttachment(string filePath)
    {
        StagedAttachments.Add(new StagedAttachment(filePath));
        CanSend = true;
    }

    public void RemoveStagedAttachment(StagedAttachment attachment)
    {
        StagedAttachments.Remove(attachment);
        CanSend = !string.IsNullOrWhiteSpace(MessageText) || StagedAttachments.Count > 0;
    }

    private void InsertOptimisticMessage(string tempGuid, string text, ReplyDraft? reply = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var entity = new MessageEntity
        {
            Guid = tempGuid,
            Text = text,
            IsFromMe = true,
            DateCreated = now,
            ThreadOriginatorGuid = reply?.MessageGuid
        };
        var bubble = CreateBubbles(entity, cache: null)[0];
        InsertOptimisticBubble(bubble, tempGuid, now, reply);
    }

    /// <summary>Optimistic bubble for a just-picked local attachment — renders the image/thumbnail
    /// immediately rather than the file name.</summary>
    private void InsertOptimisticAttachment(string tempGuid, StagedAttachment staged, ReplyDraft? reply = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var localAttachment = AttachmentViewModel.CreateLocal(staged.FilePath);
        var bubble = MessageBubbleViewModel.CreateOptimisticAttachment(
            tempGuid, now, [localAttachment], reply?.MessageGuid);
        WireBubble(bubble);
        InsertOptimisticBubble(bubble, tempGuid, now, reply);
    }

    /// <summary>Shared optimistic-insert: date separator, tail fixup, sending/delay state, append.</summary>
    private void InsertOptimisticBubble(MessageBubbleViewModel bubble, string tempGuid, long now, ReplyDraft? reply)
    {
        _pendingTempGuids.Add(tempGuid);

        var msgDate = DateTimeOffset.FromUnixTimeMilliseconds(now).LocalDateTime.Date;
        var newestBubble = Items.OfType<MessageBubbleViewModel>().LastOrDefault();
        var newestBubbleDate = newestBubble?.DateCreated > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(newestBubble.DateCreated).LocalDateTime.Date
            : (DateTime?)null;

        if (newestBubbleDate != msgDate)
            Items.Add(new DateSeparatorViewModel(DateTimeOffset.FromUnixTimeMilliseconds(now)));

        if (newestBubble is not null)
            newestBubble.ShowTail = newestBubble.IsFromMe != bubble.IsFromMe;

        var delayed = _settings.SendDelay > 0;
        bubble.Status = DeliveryStatus.Sending;
        bubble.IsDelayed = delayed;
        bubble.ShowTail = true;

        if (reply is not null)
            bubble.SetReplyContext(reply.SenderLabel, reply.Preview);

        if (delayed)
            bubble.CancelAction = () => CancelDelayedMessage(tempGuid);

        Items.Add(bubble);
        NewMessageAppended?.Invoke(this, EventArgs.Empty);
    }

    private void CancelDelayedMessage(string tempGuid)
    {
        _outgoingService.CancelPending(tempGuid);
    }

    private void EmitTypingIndicator()
    {
        if (EditingMessage is not null) return;   // editing pre-fills the composer; that isn't "typing"
        if (!_settings.PrivateSendTypingIndicators) return;
        if (string.IsNullOrEmpty(_chatGuid)) return;

        if (_typingDebounce is null)
        {
            _ = _socketService.SendMessageAsync("started-typing",
                new Dictionary<string, object?> { ["chatGuid"] = _chatGuid });
        }

        _typingDebounce?.Dispose();
        _typingDebounce = new Timer(_ =>
        {
            _ = _socketService.SendMessageAsync("stopped-typing",
                new Dictionary<string, object?> { ["chatGuid"] = _chatGuid });
            _typingDebounce = null;
        }, null, 3000, Timeout.Infinite);
    }

    private void StopTypingIndicator()
    {
        if (_typingDebounce is not null)
        {
            _typingDebounce.Dispose();
            _typingDebounce = null;
            _ = _socketService.SendMessageAsync("stopped-typing",
                new Dictionary<string, object?> { ["chatGuid"] = _chatGuid });
        }
    }

    private void BuildMessageList(List<MessageEntity> messages)
    {
        Items.Clear();
        DateTime? lastDate = null;

        foreach (var msg in messages)
        {
            var msgDate = msg.DateCreated.HasValue
                ? DateTimeOffset.FromUnixTimeMilliseconds(msg.DateCreated.Value).LocalDateTime.Date
                : (DateTime?)null;

            if (msgDate.HasValue && msgDate != lastDate)
            {
                Items.Add(new DateSeparatorViewModel(
                    DateTimeOffset.FromUnixTimeMilliseconds(msg.DateCreated!.Value)));
                lastDate = msgDate;
            }

            foreach (var bubble in CreateBubbles(msg, _attachmentCache))
            {
                Items.Add(bubble);
            }
        }

        UpdateTails();
    }

    private void PrependMessages(List<MessageEntity> messages)
    {
        var olderItems = new List<object>();
        DateTime? lastDate = null;

        foreach (var msg in messages)
        {
            var msgDate = msg.DateCreated.HasValue
                ? DateTimeOffset.FromUnixTimeMilliseconds(msg.DateCreated.Value).LocalDateTime.Date
                : (DateTime?)null;

            if (msgDate.HasValue && msgDate != lastDate)
            {
                olderItems.Add(new DateSeparatorViewModel(
                    DateTimeOffset.FromUnixTimeMilliseconds(msg.DateCreated!.Value)));
                lastDate = msgDate;
            }

            olderItems.AddRange(CreateBubbles(msg, _attachmentCache));
        }

        // Remove duplicate date separator at the beginning of existing items
        if (Items.Count > 0 && Items[0] is DateSeparatorViewModel && lastDate.HasValue)
        {
            var oldestExistingBubble = Items.OfType<MessageBubbleViewModel>().FirstOrDefault();
            if (oldestExistingBubble is not null)
            {
                var existingDate = DateTimeOffset.FromUnixTimeMilliseconds(oldestExistingBubble.DateCreated)
                    .LocalDateTime.Date;
                if (existingDate == lastDate)
                    Items.RemoveAt(0);
            }
        }

        for (var i = 0; i < olderItems.Count; i++)
            Items.Insert(i, olderItems[i]);

        UpdateTails();
    }

    private void UpdateTails()
    {
        MessageBubbleViewModel? prev = null;
        for (var i = 0; i < Items.Count; i++)
        {
            if (Items[i] is MessageBubbleViewModel bubble)
            {
                if (prev is not null)
                    prev.ShowTail = prev.IsFromMe != bubble.IsFromMe;
                prev = bubble;
            }
            else if (Items[i] is DateSeparatorViewModel)
            {
                if (prev is not null) prev.ShowTail = true;
                prev = null;
            }
        }
        if (prev is not null) prev.ShowTail = true;
    }

    private void OnOutgoingMessageStateChanged(object? sender, OutgoingMessageEvent e)
    {
        if (e.ChatGuid != _chatGuid) return;

        var bubble = Items.OfType<MessageBubbleViewModel>()
            .FirstOrDefault(b => b.MessageGuid == e.TempGuid || b.TempGuid == e.TempGuid);
        if (bubble is null) return;

        switch (e.State)
        {
            case OutgoingMessageState.Sending:
                bubble.IsDelayed = false;
                bubble.CancelAction = null;
                break;

            case OutgoingMessageState.Sent when e.ServerMessage is not null:
                bubble.IsDelayed = false;
                bubble.CancelAction = null;
                bubble.ConfirmSent(e.ServerMessage.Guid);
                _pendingTempGuids.Remove(e.TempGuid);
                if (e.ServerMessage.DateDelivered is not null || e.ServerMessage.DateRead is not null)
                {
                    bubble.UpdateDeliveryStatus(new MessageEntity
                    {
                        IsFromMe = true,
                        DateDelivered = e.ServerMessage.DateDelivered,
                        DateRead = e.ServerMessage.DateRead
                    });
                }
                _ = SaveSentMessageAsync(e.ChatGuid, e.ServerMessage);
                break;

            case OutgoingMessageState.Failed:
                bubble.IsDelayed = false;
                bubble.CancelAction = null;
                bubble.MarkFailed(e.ErrorMessage);
                _pendingTempGuids.Remove(e.TempGuid);
                break;

            case OutgoingMessageState.Cancelled:
                _pendingTempGuids.Remove(e.TempGuid);
                Items.Remove(bubble);
                UpdateTails();
                break;
        }
    }

    private async Task SaveSentMessageAsync(string chatGuid, Message serverMessage)
    {
        try { await _messagesService.SaveIncomingMessageAsync(chatGuid, serverMessage); }
        catch { }
    }

    private void OnNewMessageReceived(object? sender, MessageEventArgs e)
    {
        var chatGuid = e.Message.Chats?.FirstOrDefault()?.Guid;
        if (chatGuid is null || !_chatGuids.Contains(chatGuid)) return;

        if (e.Message.AssociatedMessageGuid is not null) return;

        // Dedup: socket echo of a message we sent optimistically
        if (e.TempGuid is not null && _pendingTempGuids.Contains(e.TempGuid))
        {
            var existing = Items.OfType<MessageBubbleViewModel>()
                .FirstOrDefault(b => b.TempGuid == e.TempGuid || b.MessageGuid == e.TempGuid);
            if (existing is not null)
            {
                existing.ConfirmSent(e.Message.Guid);
                existing.UpdateDeliveryStatus(new MessageEntity
                {
                    IsFromMe = e.Message.IsFromMe,
                    DateRead = e.Message.DateRead,
                    DateDelivered = e.Message.DateDelivered
                });
                _pendingTempGuids.Remove(e.TempGuid);
            }
            return;
        }

        // Dedup: message GUID already shown (out-of-order socket event after API response)
        var existingByGuid = Items.OfType<MessageBubbleViewModel>()
            .FirstOrDefault(b => b.MessageGuid == e.Message.Guid);
        if (existingByGuid is not null)
        {
            existingByGuid.UpdateDeliveryStatus(new MessageEntity
            {
                IsFromMe = e.Message.IsFromMe,
                DateRead = e.Message.DateRead,
                DateDelivered = e.Message.DateDelivered
            });
            return;
        }

        var entity = new MessageEntity
        {
            Guid = e.Message.Guid,
            Text = e.Message.Text,
            Subject = e.Message.Subject,
            IsFromMe = e.Message.IsFromMe,
            DateCreated = e.Message.DateCreated,
            DateDelivered = e.Message.DateDelivered,
            DateRead = e.Message.DateRead,
            IsDelivered = e.Message.IsDelivered,
            HasAttachments = e.Message.HasAttachments,
            BalloonBundleId = e.Message.BalloonBundleId,
            HasApplePayloadData = e.Message.HasApplePayloadData,
            PayloadDataJson = e.Message.PayloadData is not null
                ? JsonSerializer.Serialize(e.Message.PayloadData, JsonDefaults.Options) : null,
            Handle = e.Message.Handle is not null
                ? new HandleEntity { Address = e.Message.Handle.Address, Service = e.Message.Handle.Service }
                : null,
            Attachments = e.Message.Attachments?
                .Select(a => new AttachmentEntity
                {
                    Guid = a.Guid,
                    Uti = a.Uti,
                    MimeType = a.MimeType,
                    TransferName = a.TransferName,
                    TotalBytes = a.TotalBytes,
                    Width = a.Width,
                    Height = a.Height,
                    HasLivePhoto = a.HasLivePhoto
                }).ToList<AttachmentEntity>()
                ?? []
        };

        var bubbles = AppendMessageBubbles(entity);

        if (bubbles.Any(b => b.IsReply && !b.ReplyContextReady))
            _ = ResolveReplySnippetsAsync();

        SetTypingBubble(false);   // the other party sent — they've stopped typing
        // The most-recent constituent may have changed, so a reply follows the now-active address.
        RecomputeSendTarget();
        NewMessageAppended?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Appends one message's bubble(s) to the end of the list, inserting a day separator and
    /// fixing the previous bubble's tail. Shared by the live-socket and post-sync catch-up paths;
    /// callers own typing-bubble / reply-resolution / scroll side effects.</summary>
    private List<MessageBubbleViewModel> AppendMessageBubbles(MessageEntity entity)
    {
        var msgDate = entity.DateCreated.HasValue
            ? DateTimeOffset.FromUnixTimeMilliseconds(entity.DateCreated.Value).LocalDateTime.Date
            : (DateTime?)null;

        var newestBubble = Items.OfType<MessageBubbleViewModel>().LastOrDefault();
        var newestBubbleDate = newestBubble?.DateCreated > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(newestBubble.DateCreated).LocalDateTime.Date
            : (DateTime?)null;

        if (msgDate.HasValue && msgDate != newestBubbleDate)
            Items.Add(new DateSeparatorViewModel(
                DateTimeOffset.FromUnixTimeMilliseconds(entity.DateCreated!.Value)));

        if (newestBubble is not null)
            newestBubble.ShowTail = newestBubble.IsFromMe != entity.IsFromMe;

        var bubbles = CreateBubbles(entity, _attachmentCache);
        foreach (var bubble in bubbles)
        {
            bubble.ShowTail = true;
            Items.Add(bubble);
            TriggerAutoDownload(bubble);
        }
        return bubbles;
    }

    /// <summary>Brings the open thread fully up to date after a background delta sync, without rebuilding
    /// the list (so the user's scroll position is kept). The ROWID-watermark delta only sees brand-new
    /// rows, and even those arrive in the DB rather than via the socket's live event — so we (1) re-fetch
    /// the newest page from the server and upsert it, recovering in-place edits / unsends / read receipts
    /// the delta structurally can't; (2) append any messages newer than the last visible bubble; and
    /// (3) reconcile already-visible bubbles' status, edits, unsends, and reactions from the DB.</summary>
    /// <summary>Runs the post-sync reconcile, surfacing failures to the log instead of letting the
    /// fire-and-forget task swallow them. If the thread is still opening, defers the reconcile to
    /// <see cref="LoadChatAsync"/>'s completion rather than dropping the only sync signal.</summary>
    private async Task RunReconcileAsync()
    {
        if (IsLoading)
        {
            _reconcilePending = true;
            return;
        }

        try
        {
            await ReconcileAfterSyncAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogCategory.Sync, $"Post-sync reconcile failed for {_chatGuid}: {ex.Message}");
        }
    }

    /// <summary>Appends rows just persisted for the open chat (socket save or delta batch). Mirrors
    /// the catch-up step of the post-sync reconcile but skips the server round-trip — the rows are
    /// already in the DB. Deduped by GUID, so it's a no-op when the live socket path already showed
    /// the message.</summary>
    private async Task AppendPersistedMessagesAsync(string chatGuid)
    {
        if (!_chatGuids.Contains(chatGuid) || IsLoading) return;

        try
        {
            await AppendNewerMessagesFromDbAsync(_chatIds, _chatGuid);
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogCategory.Sync, $"Append after persist failed for {chatGuid}: {ex.Message}");
        }
    }

    private async Task ReconcileAfterSyncAsync()
    {
        if (string.IsNullOrEmpty(_chatGuid) || IsLoading) return;

        var chatGuid = _chatGuid;
        var chatIds = _chatIds;

        // 1. Re-pull the newest page of each constituent from the server (best-effort) so in-place
        //    mutations are reconciled.
        for (var i = 0; i < _chatIds.Count; i++)
            await _messagesService.RefreshLatestFromServerAsync(_chatIds[i], _constituentGuids[i], PageSize);
        if (_chatGuid != chatGuid) return;   // user navigated away while we were fetching

        // 2. Append messages now in the DB that are newer than the last visible bubble.
        await AppendNewerMessagesFromDbAsync(chatIds, chatGuid);
        if (_chatGuid != chatGuid) return;

        // 3. Reconcile the already-visible bubbles in place.
        await ReconcileVisibleBubblesAsync(chatGuid);
    }

    private async Task AppendNewerMessagesFromDbAsync(IReadOnlyList<int> chatIds, string chatGuid)
    {
        var newest = Items.OfType<MessageBubbleViewModel>()
            .Select(b => b.DateCreated)
            .DefaultIfEmpty(0)
            .Max();
        if (newest <= 0) return;   // nothing loaded yet — open/hydrate paths cover the empty case

        var newer = await _messagesService.LoadMessagesAfterAsync(chatIds, newest);
        if (newer.Count == 0 || _chatGuid != chatGuid) return;

        var shownGuids = Items.OfType<MessageBubbleViewModel>()
            .Select(b => b.MessageGuid)
            .ToHashSet();

        var appended = false;
        foreach (var msg in newer)
        {
            if (shownGuids.Contains(msg.Guid)) continue;
            AppendMessageBubbles(msg);
            appended = true;
        }
        if (!appended) return;

        await ResolveReplySnippetsAsync();
        NewMessageAppended?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Re-reconciles every visible message bubble against the DB's authoritative state: read /
    /// delivery receipts, edits, unsends, and the full reaction set (including reactions removed while
    /// offline). Skips optimistic (temp-GUID) bubbles so an in-flight send isn't clobbered.</summary>
    private async Task ReconcileVisibleBubblesAsync(string chatGuid)
    {
        var guids = Items.OfType<MessageBubbleViewModel>()
            .Select(b => b.MessageGuid)
            .Where(g => !string.IsNullOrEmpty(g) && !g.StartsWith("temp-", StringComparison.Ordinal))
            .ToHashSet();
        if (guids.Count == 0) return;

        var entities = await _messagesService.GetMessagesByGuidsAsync(guids);
        var reactions = await _messagesService.LoadReactionsAsync(guids);
        if (_chatGuid != chatGuid) return;

        var byGuid = entities.ToDictionary(m => m.Guid);

        // A message deleted on the server during the gap (applied to the cache by the window
        // reconcile in RefreshLatestFromServerAsync) must also disappear from the open thread, not
        // just from future loads. GetMessagesByGuidsAsync returns soft-deleted rows, so we can spot them.
        var deletedGuids = byGuid.Values
            .Where(m => m.DateDeleted != null)
            .Select(m => m.Guid)
            .ToHashSet();
        if (deletedGuids.Count > 0)
        {
            foreach (var b in Items.OfType<MessageBubbleViewModel>()
                         .Where(b => deletedGuids.Contains(b.MessageGuid)).ToList())
                Items.Remove(b);
            PruneOrphanDateSeparators();
            UpdateTails();
        }

        foreach (var bubble in Items.OfType<MessageBubbleViewModel>().ToList())
        {
            if (!byGuid.TryGetValue(bubble.MessageGuid, out var entity)) continue;
            if (entity.DateDeleted != null) continue;   // removed above

            if (MessageEdits.IsPartRetracted(entity.MessageSummaryInfoJson, 0))
            {
                if (!bubble.IsUnsent) bubble.ApplyUnsend();
                continue;
            }

            // Only the text-bearing bubble of a text+attachment pair carries the edit.
            if (entity.DateEdited is > 0 && entity.DateEdited != bubble.DateEdited
                && !string.IsNullOrEmpty(bubble.Text))
                bubble.ApplyEdit(entity.Text, entity.DateEdited);

            bubble.UpdateDeliveryStatus(entity);
        }

        // Reactions live on the last bubble for a GUID. Replace the whole set (empty when a reaction was
        // removed while offline) so badges added or cleared during the gap both reconcile.
        var byParent = reactions.GroupBy(r => r.AssociatedMessageGuid!)
            .ToDictionary(g => g.Key, g => g.ToList());
        foreach (var guid in guids)
        {
            var host = LastBubbleForGuid(guid);
            host?.SetReactions(byParent.TryGetValue(guid, out var rs)
                ? rs.Select(ToReactionRecord)
                : []);
        }
    }

    private void OnMessageUpdated(object? sender, MessageEventArgs e)
    {
        // A text+attachment message renders as two bubbles sharing one GUID — update all of them.
        var bubbles = Items.OfType<MessageBubbleViewModel>()
            .Where(b => b.MessageGuid == e.Message.Guid)
            .ToList();
        if (bubbles.Count == 0) return;

        // Unsend: a retracted part → show the placeholder. (No further status reconciliation needed.)
        if (MessageEdits.IsPartRetracted(e.Message.MessageSummaryInfo, 0))
        {
            foreach (var b in bubbles) b.ApplyUnsend();
            return;
        }

        // Edit: a (new) dateEdited → rewrite the text bubble and surface the "Edited" label.
        if (e.Message.DateEdited is > 0)
        {
            var host = bubbles.FirstOrDefault(b => !string.IsNullOrEmpty(b.Text)) ?? bubbles[0];
            host.ApplyEdit(e.Message.Text, e.Message.DateEdited);
        }

        // Delivery status (delivered / read) — always reconcile against the server's current state.
        var pseudoEntity = new MessageEntity
        {
            IsFromMe = e.Message.IsFromMe,
            DateRead = e.Message.DateRead,
            DateDelivered = e.Message.DateDelivered
        };
        foreach (var b in bubbles) b.UpdateDeliveryStatus(pseudoEntity);
    }

    private void OnTypingIndicatorChanged(object? sender, TypingIndicatorPayload e)
    {
        // The event is for the open chat only; a merged conversation matches any constituent GUID
        // (the set comparer is case-insensitive, so GUID casing differences are tolerated).
        if (e.Guid is not null && _chatGuids.Contains(e.Guid))
            SetTypingBubble(e.Display);
    }

    /// <summary>Shows/hides the incoming "… is typing" bubble and arms a safety timeout so a
    /// missed stop event can't leave it stuck on. Must be called on the UI thread.</summary>
    private void SetTypingBubble(bool typing)
    {
        IsTyping = typing;

        _typingExpiry?.Dispose();
        _typingExpiry = null;

        if (typing)
        {
            _typingExpiry = new Timer(_ => RunOnUI(() =>
            {
                IsTyping = false;
                _typingExpiry?.Dispose();
                _typingExpiry = null;
            }), null, TypingTimeout, Timeout.InfiniteTimeSpan);
        }
    }

    // ── Reactions (tapbacks) ──

    /// <summary>Creates bubbles for a message and wires each one's reaction callback.</summary>
    private List<MessageBubbleViewModel> CreateBubbles(MessageEntity entity, IAttachmentCacheService? cache)
    {
        var bubbles = MessageBubbleViewModel.CreateFromEntity(entity, _contacts, _isGroup, cache);
        foreach (var bubble in bubbles)
            WireBubble(bubble);
        return bubbles;
    }

    /// <summary>Attaches the per-bubble reaction/reply/scroll/edit/unsend/delete callbacks.</summary>
    private void WireBubble(MessageBubbleViewModel bubble)
    {
        bubble.SendReactionAction = type => SendReaction(bubble, type);
        bubble.StartReplyAction = () => StartReply(bubble);
        bubble.ScrollToMessageAction = guid => ScrollToMessageRequested?.Invoke(this, guid);
        bubble.StartEditAction = () => StartEdit(bubble);
        bubble.UnsendAction = () => Unsend(bubble);
        bubble.DeleteAction = () => _ = DeleteMessageAsync(bubble);
        if (bubble.UrlPreview is not null && _linkPreview is not null)
            bubble.UrlPreview.Fetcher = _linkPreview.FetchAsync;
    }

    /// <summary>Loads stored reactions for the given messages and applies them to their bubbles.</summary>
    private async Task LoadAndApplyReactionsAsync(IReadOnlyCollection<MessageEntity> messages)
    {
        if (messages.Count == 0) return;

        var parentGuids = messages.Select(m => m.Guid).ToList();
        var reactions = await _messagesService.LoadReactionsAsync(parentGuids);
        if (reactions.Count == 0) return;

        foreach (var group in reactions.GroupBy(r => r.AssociatedMessageGuid!))
        {
            var host = LastBubbleForGuid(group.Key);
            host?.SetReactions(group.Select(ToReactionRecord));
        }
    }

    private void OnReactionReceived(object? sender, ReactionEventArgs e)
    {
        if (e.Reaction.Chats?.FirstOrDefault()?.Guid != _chatGuid) return;

        var host = LastBubbleForGuid(e.ParentGuid);
        host?.AddReaction(new ReactionRecord(
            e.Reaction.Guid,
            e.Reaction.AssociatedMessageType ?? string.Empty,
            e.Reaction.IsFromMe,
            e.Reaction.Handle?.Address,
            e.Reaction.DateCreated ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    }

    /// <summary>The user picked <paramref name="reactionType"/> for a message. Tapping the type already
    /// applied removes it; otherwise it (re)applies. Updates optimistically, then calls the server.</summary>
    private void SendReaction(MessageBubbleViewModel bubble, string reactionType)
    {
        if (string.IsNullOrEmpty(_chatGuid)) return;

        var targetGuid = bubble.MessageGuid;
        if (string.IsNullOrEmpty(targetGuid) ||
            targetGuid.StartsWith("temp-", StringComparison.Ordinal))
            return; // cannot react to a message that hasn't been sent yet

        var send = bubble.SelfReactionType == reactionType ? $"-{reactionType}" : reactionType;

        bubble.AddReaction(new ReactionRecord(
            OutgoingMessageService.GenerateTempGuid(), send,
            IsFromMe: true, ReactorAddress: null,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

        _ = SendReactionApiAsync(targetGuid, bubble.Text ?? string.Empty, send);
    }

    private async Task SendReactionApiAsync(string targetGuid, string selectedText, string reaction)
    {
        try
        {
            var response = await _outgoingService.SendTapbackAsync(
                _chatGuid, selectedText, targetGuid, reaction, partIndex: 0);

            // Persist the authoritative reaction so it survives a reload even if the
            // socket echo is missed. The echo (if any) de-dupes by GUID. Mirrors SaveSentMessageAsync.
            // Guarded so a response without the association can't land as a normal bubble.
            if (response.Status == 200 && response.Data is { AssociatedMessageGuid: not null } data
                && ReactionTypes.IsReaction(data.AssociatedMessageType))
                await _messagesService.SaveReactionAsync(_chatGuid, data);
        }
        catch
        {
            // The socket echo reconciles the authoritative state; nothing to roll back here.
        }
    }

    private MessageBubbleViewModel? LastBubbleForGuid(string guid)
        => Items.OfType<MessageBubbleViewModel>().LastOrDefault(b => b.MessageGuid == guid);

    private static ReactionRecord ToReactionRecord(MessageEntity reaction)
        => new(
            reaction.Guid,
            reaction.AssociatedMessageType ?? string.Empty,
            reaction.IsFromMe,
            reaction.Handle?.Address,
            reaction.DateCreated ?? 0);

    // ── Replies (threads) ──

    /// <summary>Enters reply mode targeting <paramref name="bubble"/>'s message.</summary>
    public void StartReply(MessageBubbleViewModel bubble)
    {
        if (string.IsNullOrEmpty(bubble.MessageGuid) ||
            bubble.MessageGuid.StartsWith("temp-", StringComparison.Ordinal))
            return; // can't reply to a message that hasn't been sent yet

        var sender = bubble.IsFromMe ? "You" : (bubble.SenderName ?? ChatDisplayName);
        if (EditingMessage is not null)
        {
            // Reply and edit are mutually exclusive; leaving edit mode drops its pre-filled text.
            EditingMessage = null;
            MessageText = string.Empty;
        }
        ReplyingTo = new ReplyDraft(bubble.MessageGuid, 0, sender, BubblePreview(bubble));
    }

    [RelayCommand]
    private void CancelReply() => ReplyingTo = null;

    /// <summary>Fills in the quoted snippet/sender for any reply bubbles not yet resolved, preferring
    /// already-loaded messages and falling back to the database.</summary>
    private async Task ResolveReplySnippetsAsync()
    {
        var unresolved = Items.OfType<MessageBubbleViewModel>()
            .Where(b => b.IsReply && !b.ReplyContextReady)
            .ToList();
        if (unresolved.Count == 0) return;

        var loaded = Items.OfType<MessageBubbleViewModel>()
            .GroupBy(b => b.MessageGuid)
            .ToDictionary(g => g.Key, g => g.First());

        var misses = new HashSet<string>();
        foreach (var host in unresolved)
        {
            if (loaded.TryGetValue(host.ThreadOriginatorGuid!, out var origin))
                host.SetReplyContext(
                    origin.IsFromMe ? "You" : (origin.SenderName ?? ChatDisplayName),
                    BubblePreview(origin));
            else
                misses.Add(host.ThreadOriginatorGuid!);
        }

        if (misses.Count == 0) return;

        var originals = await _messagesService.GetMessagesByGuidsAsync(misses);
        var byGuid = originals.ToDictionary(m => m.Guid);

        foreach (var host in unresolved.Where(h => !h.ReplyContextReady))
        {
            if (byGuid.TryGetValue(host.ThreadOriginatorGuid!, out var origin))
                host.SetReplyContext(EntitySenderLabel(origin), EntityPreview(origin));
        }
    }

    private static string BubblePreview(MessageBubbleViewModel b)
    {
        if (!string.IsNullOrWhiteSpace(b.Text)) return b.Text!;
        return b.HasAttachments ? "Attachment" : "Message";
    }

    private string EntitySenderLabel(MessageEntity m)
        => m.IsFromMe ? "You"
            : (m.Handle?.Address is { } addr ? _contacts.GetDisplayName(addr) : ChatDisplayName);

    private static string EntityPreview(MessageEntity m)
    {
        var text = m.Text?.Replace("￼", string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(text)) return text!;
        return m.HasAttachments ? "Attachment" : "Message";
    }

    // ── Message actions: edit, unsend, delete (Phase 15) ──

    /// <summary>Enters edit mode for an own text message: pre-fills the composer with its text.</summary>
    public void StartEdit(MessageBubbleViewModel bubble)
    {
        if (!bubble.IsFromMe || bubble.IsUnsent) return;
        if (string.IsNullOrEmpty(bubble.MessageGuid) ||
            bubble.MessageGuid.StartsWith("temp-", StringComparison.Ordinal))
            return; // can't edit a message that hasn't been sent yet

        var text = bubble.Text;
        if (string.IsNullOrEmpty(text)) return; // only text parts are editable

        ReplyingTo = null;   // edit and reply are mutually exclusive composer modes
        EditingMessage = new EditDraft(bubble.MessageGuid, 0, text);
        MessageText = text;
        CanSend = true;
    }

    [RelayCommand]
    private void CancelEdit()
    {
        // Clear the draft while still flagged as editing so the text change doesn't emit "typing".
        MessageText = string.Empty;
        EditingMessage = null;
        CanSend = false;
    }

    /// <summary>Commits the in-progress edit: optimistic bubble update, then the Private API edit call.</summary>
    private void CommitEdit()
    {
        var edit = EditingMessage;
        if (edit is null) return;

        var newText = MessageText?.Trim() ?? string.Empty;

        // Leave edit mode regardless of outcome so the composer returns to normal. Clear the draft
        // while still flagged as editing so the text change doesn't emit a "typing" indicator.
        MessageText = string.Empty;
        EditingMessage = null;
        CanSend = false;

        // Empty or unchanged → nothing to send (editing to empty would be an unsend, not an edit).
        if (string.IsNullOrEmpty(newText) || newText == edit.OriginalText) return;

        var bubble = Items.OfType<MessageBubbleViewModel>()
            .FirstOrDefault(b => b.MessageGuid == edit.MessageGuid && !string.IsNullOrEmpty(b.Text));
        bubble?.ApplyEdit(newText, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        _ = SendEditApiAsync(edit.MessageGuid, newText, edit.PartIndex);
    }

    private async Task SendEditApiAsync(string messageGuid, string newText, int partIndex)
    {
        try
        {
            var backwardsCompat = MessageEdits.BuildBackwardsCompatText(newText);
            var response = await _outgoingService.SendEditAsync(
                messageGuid, newText, backwardsCompat, partIndex);

            // Persist the authoritative edited text + history; the socket echo (if any) re-applies idempotently.
            if (response.Status == 200 && response.Data is not null)
                await _messagesService.UpdateMessageAsync(response.Data);
        }
        catch
        {
            // The socket updated-message echo reconciles authoritative state; nothing to roll back.
        }
    }

    /// <summary>Unsends (retracts) an own message via the Private API, showing the placeholder optimistically.</summary>
    private void Unsend(MessageBubbleViewModel bubble)
    {
        if (string.IsNullOrEmpty(_chatGuid)) return;
        if (!bubble.IsFromMe || bubble.IsUnsent) return;

        var guid = bubble.MessageGuid;
        if (string.IsNullOrEmpty(guid) || guid.StartsWith("temp-", StringComparison.Ordinal))
            return; // can't unsend a message that hasn't been sent yet

        bubble.ApplyUnsend();
        _ = SendUnsendApiAsync(guid, 0);
    }

    private async Task SendUnsendApiAsync(string messageGuid, int partIndex)
    {
        try
        {
            var response = await _outgoingService.SendUnsendAsync(messageGuid, partIndex);
            if (response.Status == 200 && response.Data is not null)
                await _messagesService.UpdateMessageAsync(response.Data);
        }
        catch
        {
            // The socket updated-message echo reconciles authoritative state.
        }
    }

    /// <summary>Deletes a message: server first — a local-only delete would be resurrected by the
    /// next sync — then removes its bubble(s) and soft-deletes the local row. Unlike unsend, this
    /// does not retract the message on the recipient's side. Never-sent drafts (temp- GUIDs) don't
    /// exist server-side and are removed locally only. On server failure nothing is removed and
    /// <see cref="DeleteMessageFailed"/> is raised instead.</summary>
    private async Task DeleteMessageAsync(MessageBubbleViewModel bubble)
    {
        var guid = bubble.MessageGuid;

        if (!string.IsNullOrEmpty(guid) && !guid.StartsWith("temp-", StringComparison.Ordinal))
        {
            if (string.IsNullOrEmpty(_chatGuid)) return;
            if (!await _messagesService.DeleteMessageAsync(_chatGuid, guid))
            {
                DeleteMessageFailed?.Invoke(this,
                    "The message couldn't be deleted. Check the server connection and try again.");
                return;
            }
        }

        // A text+attachment message renders as two bubbles sharing one GUID — remove both.
        foreach (var b in Items.OfType<MessageBubbleViewModel>()
                     .Where(b => b.MessageGuid == guid).ToList())
            Items.Remove(b);

        PruneOrphanDateSeparators();
        UpdateTails();
    }

    /// <summary>Removes any date separator left without a following message bubble (e.g. after a delete).</summary>
    private void PruneOrphanDateSeparators()
    {
        for (var i = Items.Count - 1; i >= 0; i--)
        {
            if (Items[i] is not DateSeparatorViewModel) continue;
            var hasBubbleAfter = i + 1 < Items.Count && Items[i + 1] is MessageBubbleViewModel;
            if (!hasBubbleAfter) Items.RemoveAt(i);
        }
    }

    /// <summary>Auto-downloads a newly arrived message's attachments. The initial page does NOT go
    /// through here: those download when their bubble is realized (see <see cref="Controls.AttachmentHolder"/>),
    /// so the images the user is actually looking at aren't queued behind the whole loaded window.</summary>
    private void TriggerAutoDownload(MessageBubbleViewModel bubble)
    {
        if (!_settings.AutoDownload || !bubble.HasAttachments) return;
        foreach (var att in bubble.Attachments!)
        {
            if (att.State != AttachmentState.NotDownloaded) continue;
            _ = att.DownloadAsync();
        }
    }

    private static void RunOnUI(Action action)
    {
        var dispatcher = App.MainWindow?.DispatcherQueue;
        if (dispatcher is not null)
            dispatcher.TryEnqueue(() => action());
        else
            action();
    }
}

/// <summary>The message being replied to while the composer is in reply mode.</summary>
public record ReplyDraft(string MessageGuid, int PartIndex, string SenderLabel, string Preview);

/// <summary>The message being edited while the composer is in edit mode. <see cref="OriginalText"/> is
/// the pre-edit text, used to suppress no-op edits.</summary>
public record EditDraft(string MessageGuid, int PartIndex, string OriginalText);

/// <summary>A pending (or errored) server-side scheduled message, rendered as an outlined
/// bubble pinned at the bottom of the thread until the server sends it.</summary>
public record ScheduledMessageItem(
    int Id,
    string Text,
    DateTimeOffset? ScheduledForLocal,
    string DisplayTime,
    bool HasError,
    string? Error)
{
    public static ScheduledMessageItem From(ScheduledMessage m) => new(
        m.Id,
        m.Payload?.MessageText ?? string.Empty,
        m.ScheduledForLocal,
        m.ScheduledForLocal is { } t
            ? $"Send later — {t.LocalDateTime:ddd, MMM d 'at' h:mm tt}"
            : "Send later",
        m.Status == ScheduledMessageStatus.Error,
        m.Error);
}
