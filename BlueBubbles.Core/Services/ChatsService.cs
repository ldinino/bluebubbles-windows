using BlueBubbles.Core.Configuration;
using BlueBubbles.Core.Data;
using BlueBubbles.Core.Data.Entities;
using BlueBubbles.Core.Models;
using BlueBubbles.Core.Utils;
using Microsoft.EntityFrameworkCore;

namespace BlueBubbles.Core.Services;

public class ChatsService : IChatsService
{
    private readonly IDbContextFactory<BlueBubblesDbContext> _dbFactory;
    private readonly IBlueBubblesApiService _api;
    private readonly AppSettings _settings;
    private readonly List<ChatWithParticipants> _chats = [];
    private readonly List<ChatWithParticipants> _archivedChats = [];
    private readonly object _lock = new();

    public IReadOnlyList<ChatWithParticipants> Chats
    {
        get { lock (_lock) return _chats.ToList(); }
    }

    public IReadOnlyList<ChatWithParticipants> ArchivedChats
    {
        get { lock (_lock) return _archivedChats.ToList(); }
    }

    public event EventHandler? ChatsChanged;
    public event EventHandler<string>? ChatUpdated;
    public event EventHandler? ArchivedChatsChanged;
    public event EventHandler<string>? MessagesPersisted;

    public void NotifyMessagesPersisted(string chatGuid) =>
        MessagesPersisted?.Invoke(this, chatGuid);

    public ChatsService(
        IDbContextFactory<BlueBubblesDbContext> dbFactory,
        IBlueBubblesApiService api,
        AppSettings settings)
    {
        _dbFactory = dbFactory;
        _api = api;
        _settings = settings;
    }

    public async Task LoadChatsAsync()
    {
        var items = await LoadChatsInternalAsync(archived: false);

        lock (_lock)
        {
            _chats.Clear();
            _chats.AddRange(items);
        }

        ChatsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task LoadArchivedChatsAsync()
    {
        var items = await LoadChatsInternalAsync(archived: true);

        lock (_lock)
        {
            _archivedChats.Clear();
            _archivedChats.AddRange(items);
        }

        ArchivedChatsChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task<List<ChatWithParticipants>> LoadChatsInternalAsync(bool archived)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var chatEntities = await db.Chats
            .Include(c => c.ChatParticipants)
            .ThenInclude(cp => cp.Handle)
            .Where(c => c.IsArchived == archived && c.DateDeleted == null)
            .OrderByDescending(c => c.IsPinned)
            // Pinned chats keep the user's manual drag order (PinIndex asc); unpinned chats (PinIndex
            // null → sorts last and ties) fall through to most-recent-message order. Previously pins were
            // ordered by message date too, so a manual reorder was silently lost on the next reload.
            .ThenBy(c => c.PinIndex ?? int.MaxValue)
            .ThenByDescending(c => c.LatestMessageDate)
            .ToListAsync();

        var chatIds = chatEntities.Select(c => c.Id).ToList();

        var lastMessageIds = await db.Messages
            .Where(m => chatIds.Contains(m.ChatId) && m.DateDeleted == null && m.AssociatedMessageGuid == null)
            .GroupBy(m => m.ChatId)
            .Select(g => g.OrderByDescending(m => m.DateCreated).Select(m => m.Id).FirstOrDefault())
            .ToListAsync();

        var lastMessages = lastMessageIds.Count > 0
            ? await db.Messages
                .Where(m => lastMessageIds.Contains(m.Id))
                // Attachments feed the preview fallback for attachment-only messages (B14).
                .Include(m => m.Attachments)
                .ToDictionaryAsync(m => m.ChatId, m => m)
            : new Dictionary<int, MessageEntity>();

        var groupChatIds = chatEntities
            .Where(c => c.ChatParticipants.Count > 1)
            .Select(c => c.Id)
            .ToList();

        var recentSendersByChat = new Dictionary<int, List<HandleEntity>>();
        if (groupChatIds.Count > 0)
        {
            var senderData = await db.Messages
                .Where(m => groupChatIds.Contains(m.ChatId)
                    && !m.IsFromMe
                    && m.HandleId != null
                    && m.DateDeleted == null
                    && m.AssociatedMessageGuid == null)
                .GroupBy(m => new { m.ChatId, m.HandleId })
                .Select(g => new { g.Key.ChatId, g.Key.HandleId, LatestDate = g.Max(m => m.DateCreated) })
                .ToListAsync();

            var topSenderIds = senderData
                .GroupBy(s => s.ChatId)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderByDescending(s => s.LatestDate)
                          .Take(2)
                          .Select(s => s.HandleId!.Value)
                          .ToList());

            var allHandleIds = topSenderIds.Values.SelectMany(x => x).Distinct().ToList();
            var handleMap = allHandleIds.Count > 0
                ? await db.Handles.Where(h => allHandleIds.Contains(h.Id)).ToDictionaryAsync(h => h.Id)
                : new Dictionary<int, HandleEntity>();

            foreach (var (chatId, handleIds) in topSenderIds)
            {
                recentSendersByChat[chatId] = handleIds
                    .Where(handleMap.ContainsKey)
                    .Select(id => handleMap[id])
                    .ToList();
            }
        }

        var items = new List<ChatWithParticipants>(chatEntities.Count);
        foreach (var chat in chatEntities)
        {
            var participants = chat.ChatParticipants
                .Select(cp => cp.Handle)
                .ToList();

            lastMessages.TryGetValue(chat.Id, out var lastMsg);
            recentSendersByChat.TryGetValue(chat.Id, out var recentSenders);
            var preview = MessagePreview.Derive(
                lastMsg?.Text, lastMsg?.Attachments.Select(a => a.MimeType));
            items.Add(new ChatWithParticipants(chat, participants, preview, recentSenders,
                lastMsg?.IsFromMe ?? false, lastMsg?.DateDelivered, lastMsg?.DateRead));
        }

        return items;
    }

    public string? FindExistingChatGuid(IEnumerable<string> addresses)
    {
        var normalized = addresses
            .Select(a => ContactResolverService.NormalizeAddress(a))
            .OrderBy(a => a)
            .ToList();

        lock (_lock)
        {
            foreach (var chat in _chats)
            {
                var chatAddresses = chat.Participants
                    .Select(p => ContactResolverService.NormalizeAddress(p.Address))
                    .OrderBy(a => a)
                    .ToList();

                if (normalized.Count == chatAddresses.Count && normalized.SequenceEqual(chatAddresses))
                    return chat.Chat.Guid;
            }
        }

        return null;
    }

    public async Task EnsureChatInDatabaseAsync(Chat chat, string? messageText)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var entity = await db.Chats.FirstOrDefaultAsync(c => c.Guid == chat.Guid);
        if (entity is null)
        {
            entity = new ChatEntity { Guid = chat.Guid };
            db.Chats.Add(entity);
        }

        // Server-owned fields only; client-owned pin/mute/archive are preserved (the server has no
        // endpoint for them and returns defaults). See ChatFieldMerge.
        ChatFieldMerge.ApplyServerOwnedFields(entity, chat);
        entity.LatestMessageDate = chat.LastMessage?.DateCreated
            ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await db.SaveChangesAsync();

        if (chat.Participants is not null)
        {
            await HandlePersistenceHelper.LinkParticipantsAsync(db, entity.Id, chat.Participants);
            await db.SaveChangesAsync();
        }
    }

    public async Task EnsureChatExistsAsync(Chat chatData)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var entity = await db.Chats
            .Include(c => c.ChatParticipants)
            .FirstOrDefaultAsync(c => c.Guid == chatData.Guid);

        if (entity is not null)
        {
            // Existing chat: only backfill participants when none are stored (a chat first created
            // from a sparse payload can land empty → renders blank). Never touch its other metadata.
            if (entity.ChatParticipants.Count == 0)
            {
                var participants = await ResolveParticipantsAsync(chatData);
                if (participants is { Count: > 0 })
                {
                    await HandlePersistenceHelper.LinkParticipantsAsync(db, entity.Id, participants);
                    await db.SaveChangesAsync();
                }
            }
            return;
        }

        entity = new ChatEntity
        {
            Guid = chatData.Guid,
            ChatIdentifier = chatData.ChatIdentifier,
            DisplayName = chatData.DisplayName,
            Service = chatData.Service,
            Style = chatData.Style,
            HasUnreadMessage = true
        };
        db.Chats.Add(entity);
        await db.SaveChangesAsync();

        var newParticipants = await ResolveParticipantsAsync(chatData);
        if (newParticipants is { Count: > 0 })
        {
            await HandlePersistenceHelper.LinkParticipantsAsync(db, entity.Id, newParticipants);
            await db.SaveChangesAsync();
        }

        // The in-memory list is what the conversation list renders, so a row that exists only in the
        // DB is invisible. Reload (outside any lock) so the new chat is present, with its participants,
        // before the caller applies the message that created it.
        await LoadChatsAsync();
    }

    public async Task ApplyChatUpdateAsync(Chat chatData)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var entity = await db.Chats
            .Include(c => c.ChatParticipants)
            .ThenInclude(cp => cp.Handle)
            .FirstOrDefaultAsync(c => c.Guid == chatData.Guid);
        if (entity is null)
        {
            // Nothing to update: the chat isn't cached yet, and the payload for these events is not a
            // create path (EnsureChatExistsAsync owns that, off new-message).
            AppLog.Warn(LogCategory.Socket, $"Chat update for unknown chat {chatData.Guid}; ignoring");
            return;
        }

        // Server-owned fields only; client-owned pin/mute/archive are preserved. See ChatFieldMerge.
        ChatFieldMerge.ApplyServerOwnedFields(entity, chatData);

        if (chatData.Participants is { Count: > 0 })
        {
            await HandlePersistenceHelper.LinkParticipantsAsync(db, entity.Id, chatData.Participants);
            HandlePersistenceHelper.RemoveParticipantsMissingFrom(db, entity, chatData.Participants);
        }

        await db.SaveChangesAsync();

        // Reload rather than patch the cache: participants and display name both feed the tile, and
        // the list renders from this in-memory copy, so a DB-only write would be invisible.
        await LoadChatsAsync();
    }

    /// <summary>Returns the chat's participants, preferring those already on the payload. The live
    /// socket <c>new-message</c> event carries the chat but not its participants, so a chat created
    /// from it would render as "Unknown" — fetch the full participant list from the server in that
    /// case. Returns an empty list on failure (offline etc.); the next incremental sync backfills.</summary>
    private async Task<List<Handle>> ResolveParticipantsAsync(Chat chatData)
    {
        if (chatData.Participants is { Count: > 0 })
            return chatData.Participants;

        try
        {
            var response = await _api.GetChatAsync(chatData.Guid, withQuery: "participants");
            return response.Data?.Participants ?? [];
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogCategory.Api,
                $"Could not fetch participants for chat {chatData.Guid}: {ex.Message}");
            return [];
        }
    }

    /// <summary>Applies a just-persisted message to the chat's list-visible state (preview,
    /// timestamp, unread, ordering). Invariant: every exit path raises <see cref="ChatsChanged"/> —
    /// a silent return here is indistinguishable from "nothing happened" to the conversation list,
    /// which is what forced users to restart or hit "fetch latest" to see new messages.</summary>
    public async Task HandleNewMessageAsync(string chatGuid, string? messageText, long dateCreated, bool isFromMe, string? senderAddress = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var chat = await db.Chats.FirstOrDefaultAsync(c => c.Guid == chatGuid);
        if (chat is null)
        {
            // The row should have been created by EnsureChatExistsAsync; if it isn't here, either that
            // failed or another writer is mid-flight. Reload from the DB rather than no-op: it picks up
            // a concurrently-created row and always raises ChatsChanged so the list re-reads.
            AppLog.Warn(LogCategory.Socket,
                $"New message for unknown chat {chatGuid} — reloading chats from the database");
            await LoadChatsAsync();
            return;
        }

        // A message arriving for a soft-deleted chat (e.g. one pruned as empty during a prior
        // sync) means it's live again — undo the delete so it resurfaces.
        var wasResurrected = chat.DateDeleted is not null;
        if (wasResurrected) chat.DateDeleted = null;

        chat.LatestMessageDate = dateCreated;
        if (!isFromMe) chat.HasUnreadMessage = true;
        await db.SaveChangesAsync();

        // A resurrected chat — or one that simply isn't in the in-memory list yet — needs a full
        // reload so it appears with its participants rather than being silently dropped below.
        var inList = false;
        lock (_lock) { inList = _chats.Any(c => c.Chat.Guid == chatGuid); }
        if (wasResurrected || !inList)
        {
            await LoadChatsAsync();
            return;
        }

        lock (_lock)
        {
            var idx = _chats.FindIndex(c => c.Chat.Guid == chatGuid);
            if (idx >= 0)
            {
                var existing = _chats[idx];
                var recentSenders = existing.RecentSenders;

                if (!isFromMe && senderAddress is not null && existing.Participants.Count > 1)
                {
                    var senderHandle = existing.Participants
                        .FirstOrDefault(p => p.Address.Equals(senderAddress, StringComparison.OrdinalIgnoreCase));
                    if (senderHandle is not null)
                    {
                        var newSenders = new List<HandleEntity> { senderHandle };
                        if (recentSenders is not null)
                        {
                            foreach (var s in recentSenders)
                            {
                                if (s.Id != senderHandle.Id && newSenders.Count < 2)
                                    newSenders.Add(s);
                            }
                        }
                        recentSenders = newSenders;
                    }
                }

                var updated = existing with
                {
                    Chat = existing.Chat,
                    LastMessageText = messageText,
                    RecentSenders = recentSenders,
                    // A brand-new message isn't delivered/read yet, so it shows as "Sent" when it's ours.
                    LastMessageIsFromMe = isFromMe,
                    LastMessageDateDelivered = null,
                    LastMessageDateRead = null
                };
                updated.Chat.LatestMessageDate = dateCreated;
                if (!isFromMe) updated.Chat.HasUnreadMessage = true;

                _chats.RemoveAt(idx);
                var insertIdx = FindInsertIndex(updated.Chat);
                _chats.Insert(insertIdx, updated);
            }
        }

        ChatsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task MarkChatReadAsync(string chatGuid, bool read, bool notifyServer = true)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var chat = await db.Chats.FirstOrDefaultAsync(c => c.Guid == chatGuid);
        if (chat is null) return;

        chat.HasUnreadMessage = !read;
        var shouldNotify = notifyServer
            && (chat.AutoSendReadReceipts ?? _settings.PrivateMarkChatAsRead);
        await db.SaveChangesAsync();

        lock (_lock)
        {
            var item = _chats.FirstOrDefault(c => c.Chat.Guid == chatGuid);
            if (item is not null)
                item.Chat.HasUnreadMessage = !read;
        }

        ChatUpdated?.Invoke(this, chatGuid);

        if (shouldNotify)
        {
            try
            {
                if (read)
                    await _api.MarkChatReadAsync(chatGuid);
                else
                    await _api.MarkChatUnreadAsync(chatGuid);
            }
            catch (Exception ex)
            {
                // Local read state is already flipped; only the server-side receipt was lost, so
                // don't fail the operation — but leave a trace, or "read here / unread for the
                // sender" mismatches are undiagnosable.
                AppLog.Warn(LogCategory.Api,
                    $"Mark chat {(read ? "read" : "unread")} failed for {chatGuid}: {ex.Message}");
            }
        }
    }

    public async Task TogglePinAsync(string chatGuid)
    {
        bool isPinned;
        int? pinIndex;
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var chat = await db.Chats.FirstOrDefaultAsync(c => c.Guid == chatGuid);
            if (chat is null) return;

            chat.IsPinned = !chat.IsPinned;
            if (chat.IsPinned)
            {
                var maxPin = await db.Chats.Where(c => c.IsPinned).MaxAsync(c => (int?)c.PinIndex) ?? -1;
                chat.PinIndex = maxPin + 1;
            }
            else
            {
                chat.PinIndex = null;
            }
            await db.SaveChangesAsync();
            isPinned = chat.IsPinned;
            pinIndex = chat.PinIndex;
        }

        // Pinning is a single-row metadata flip: mutate the in-memory cache and re-sort rather than
        // re-querying every chat (with its participant/message joins). The list view then animates a
        // single tile moving between sections instead of churning. Fall back to a full reload only if
        // the chat isn't in the active cache (e.g. pinned from the archive view).
        if (!TryApplyPinState(chatGuid, isPinned, pinIndex))
            await LoadChatsAsync();
    }

    public async Task ReorderPinsAsync(List<string> chatGuids)
    {
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            for (var i = 0; i < chatGuids.Count; i++)
            {
                var chat = await db.Chats.FirstOrDefaultAsync(c => c.Guid == chatGuids[i]);
                if (chat is not null)
                    chat.PinIndex = i;
            }
            await db.SaveChangesAsync();
        }

        // Mirror the new pin order in the cache; the grid already reflects it visually, so the
        // resulting ChatsChanged reconciles to a no-op rather than a re-query.
        lock (_lock)
        {
            for (var i = 0; i < chatGuids.Count; i++)
            {
                var item = _chats.FirstOrDefault(c => c.Chat.Guid == chatGuids[i]);
                if (item is not null)
                    item.Chat.PinIndex = i;
            }
            SortChats(_chats);
        }
        ChatsChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool TryApplyPinState(string chatGuid, bool isPinned, int? pinIndex)
    {
        lock (_lock)
        {
            var item = _chats.FirstOrDefault(c => c.Chat.Guid == chatGuid);
            if (item is null) return false;
            item.Chat.IsPinned = isPinned;
            item.Chat.PinIndex = pinIndex;
            SortChats(_chats);
        }
        ChatsChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    // Same ordering the DB query produces (see LoadChatsInternalAsync): pinned first, pins by manual
    // PinIndex, everything else by most-recent message. Keeps the in-memory cache canonical so an
    // optimistic pin/reorder update matches what a reload would yield.
    private static void SortChats(List<ChatWithParticipants> chats)
    {
        chats.Sort((a, b) =>
        {
            var pinned = b.Chat.IsPinned.CompareTo(a.Chat.IsPinned);
            if (pinned != 0) return pinned;
            if (a.Chat.IsPinned)
                return (a.Chat.PinIndex ?? int.MaxValue).CompareTo(b.Chat.PinIndex ?? int.MaxValue);
            return (b.Chat.LatestMessageDate ?? 0).CompareTo(a.Chat.LatestMessageDate ?? 0);
        });
    }

    public async Task ArchiveChatAsync(string chatGuid)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var chat = await db.Chats.FirstOrDefaultAsync(c => c.Guid == chatGuid);
        if (chat is null) return;

        chat.IsArchived = true;
        await db.SaveChangesAsync();

        lock (_lock)
        {
            _chats.RemoveAll(c => c.Chat.Guid == chatGuid);
        }

        ChatsChanged?.Invoke(this, EventArgs.Empty);

        if (_archivedChats.Count > 0)
            await LoadArchivedChatsAsync();
    }

    public async Task UnarchiveChatAsync(string chatGuid)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var chat = await db.Chats.FirstOrDefaultAsync(c => c.Guid == chatGuid);
        if (chat is null) return;

        chat.IsArchived = false;
        await db.SaveChangesAsync();

        lock (_lock)
        {
            _archivedChats.RemoveAll(c => c.Chat.Guid == chatGuid);
        }

        ArchivedChatsChanged?.Invoke(this, EventArgs.Empty);
        await LoadChatsAsync();
    }

    public async Task<bool> DeleteChatAsync(string chatGuid)
    {
        // Server first: a local-only delete is undone by the next sync (the chat still exists
        // server-side and gets re-pulled), so only touch the cache once the server has deleted.
        try
        {
            var response = await _api.DeleteChatAsync(chatGuid);
            if (response.Status is < 200 or >= 300)
            {
                AppLog.Warn(LogCategory.Api,
                    $"Delete chat failed for {chatGuid}: server returned {response.Status}");
                return false;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogCategory.Api, $"Delete chat failed for {chatGuid}: {ex.Message}");
            return false;
        }

        await using var db = await _dbFactory.CreateDbContextAsync();
        var chat = await db.Chats.FirstOrDefaultAsync(c => c.Guid == chatGuid);
        if (chat is null) return true;

        db.Chats.Remove(chat);
        await db.SaveChangesAsync();

        lock (_lock)
        {
            _chats.RemoveAll(c => c.Chat.Guid == chatGuid);
        }

        ChatsChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public async Task<bool> RenameChatAsync(string chatGuid, string newName)
    {
        try
        {
            var response = await _api.UpdateChatAsync(chatGuid, newName);
            if (response.Data is null) return false;
        }
        catch { return false; }

        await using var db = await _dbFactory.CreateDbContextAsync();
        var chat = await db.Chats.FirstOrDefaultAsync(c => c.Guid == chatGuid);
        if (chat is not null)
        {
            chat.DisplayName = newName;
            await db.SaveChangesAsync();
        }

        lock (_lock)
        {
            var item = _chats.FirstOrDefault(c => c.Chat.Guid == chatGuid);
            if (item is not null)
                item.Chat.DisplayName = newName;
        }

        ChatUpdated?.Invoke(this, chatGuid);
        return true;
    }

    public async Task ToggleMuteAsync(string chatGuid)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var chat = await db.Chats.FirstOrDefaultAsync(c => c.Guid == chatGuid);
        if (chat is null) return;

        chat.MuteType = chat.MuteType is null ? "mute" : null;
        chat.MuteArgs = null;
        await db.SaveChangesAsync();

        lock (_lock)
        {
            var item = _chats.FirstOrDefault(c => c.Chat.Guid == chatGuid);
            if (item is not null)
            {
                item.Chat.MuteType = chat.MuteType;
                item.Chat.MuteArgs = null;
            }
        }

        ChatUpdated?.Invoke(this, chatGuid);
    }

    public async Task<bool> AddParticipantAsync(string chatGuid, string address)
    {
        try
        {
            var response = await _api.AddParticipantAsync(chatGuid, address);
            if (response.Data is null) return false;
        }
        catch { return false; }

        await LoadChatsAsync();
        return true;
    }

    public async Task<bool> RemoveParticipantAsync(string chatGuid, string address)
    {
        try
        {
            var response = await _api.RemoveParticipantAsync(chatGuid, address);
            if (response.Data is null) return false;
        }
        catch { return false; }

        await LoadChatsAsync();
        return true;
    }

    public async Task<bool> LeaveChatAsync(string chatGuid)
    {
        try
        {
            await _api.LeaveChatAsync(chatGuid);
        }
        catch { return false; }

        await LoadChatsAsync();
        return true;
    }

    public async Task<bool> SetChatIconAsync(string chatGuid, Stream iconStream, string fileName)
    {
        try
        {
            await _api.SetChatIconAsync(chatGuid, iconStream, fileName);
        }
        catch { return false; }

        await using var db = await _dbFactory.CreateDbContextAsync();
        var chat = await db.Chats.FirstOrDefaultAsync(c => c.Guid == chatGuid);
        if (chat is not null)
        {
            chat.CustomAvatarPath = fileName;
            await db.SaveChangesAsync();
        }

        ChatUpdated?.Invoke(this, chatGuid);
        return true;
    }

    public async Task<bool> DeleteChatIconAsync(string chatGuid)
    {
        try
        {
            await _api.DeleteChatIconAsync(chatGuid);
        }
        catch { return false; }

        await using var db = await _dbFactory.CreateDbContextAsync();
        var chat = await db.Chats.FirstOrDefaultAsync(c => c.Guid == chatGuid);
        if (chat is not null)
        {
            chat.CustomAvatarPath = null;
            await db.SaveChangesAsync();
        }

        ChatUpdated?.Invoke(this, chatGuid);
        return true;
    }

    private int FindInsertIndex(ChatEntity chat)
    {
        if (chat.IsPinned)
        {
            for (var i = 0; i < _chats.Count; i++)
            {
                if (!_chats[i].Chat.IsPinned) return i;
                if ((chat.PinIndex ?? 0) < (_chats[i].Chat.PinIndex ?? 0)) return i;
            }
            return _chats.Count;
        }

        var pinnedCount = _chats.Count(c => c.Chat.IsPinned);
        for (var i = pinnedCount; i < _chats.Count; i++)
        {
            if ((chat.LatestMessageDate ?? 0) > (_chats[i].Chat.LatestMessageDate ?? 0))
                return i;
        }
        return _chats.Count;
    }
}
