using System.Text.Json;
using BlueBubbles.Core.Data.Entities;
using BlueBubbles.Core.Models;
using BlueBubbles.Core.Services;

namespace BlueBubbles.Windows.Tests;

public class IncomingMessageProcessorTests
{
    private static (IncomingMessageProcessor Processor, ActionHandler Handler, RecordingMessagesService MsgSvc, RecordingChatsService ChatsSvc)
        CreateProcessor()
    {
        var handler = new ActionHandler();
        var msgSvc = new RecordingMessagesService();
        var chatsSvc = new RecordingChatsService();
        var notifSvc = new NoOpNotificationService();
        var processor = new IncomingMessageProcessor(handler, msgSvc, chatsSvc, notifSvc);
        return (processor, handler, msgSvc, chatsSvc);
    }

    [Fact]
    public async Task NewMessage_SavesAndUpdatesChat()
    {
        var (processor, handler, msgSvc, chatsSvc) = CreateProcessor();
        processor.Start();

        var processedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        processor.MessageProcessed += (_, _) => processedTcs.TrySetResult();

        handler.NewMessageReceived += delegate { };

        var json = JsonSerializer.Deserialize<JsonElement>("""
        {
            "data": {
                "originalROWID": 1,
                "guid": "msg-100",
                "text": "Hello from test",
                "isFromMe": false,
                "error": 0,
                "isDelivered": true,
                "hasDdResults": false,
                "itemType": 0,
                "groupActionType": 0,
                "hasAttachments": false,
                "hasReactions": false,
                "hasApplePayloadData": false,
                "wasDeliveredQuietly": false,
                "didNotifyRecipient": false,
                "isBookmarked": false,
                "handle": { "originalROWID": 1, "address": "+11234567890", "service": "iMessage" },
                "chats": [{ "guid": "iMessage;-;+11234567890", "isArchived": false, "isPinned": false, "hasUnreadMessage": false, "lockChatName": false, "lockChatIcon": false }]
            }
        }
        """);

        handler.HandleEvent(SocketEvents.NewMessage, json, "Test");

        var completed = await Task.WhenAny(processedTcs.Task, Task.Delay(3000));
        Assert.True(processedTcs.Task.IsCompleted, "MessageProcessed was not fired");

        Assert.Single(msgSvc.SavedMessages);
        Assert.Equal("iMessage;-;+11234567890", msgSvc.SavedMessages[0].ChatGuid);
        Assert.Equal("msg-100", msgSvc.SavedMessages[0].Message.Guid);

        Assert.Single(chatsSvc.HandledNewMessages);
        Assert.Equal("iMessage;-;+11234567890", chatsSvc.HandledNewMessages[0].ChatGuid);
        Assert.Equal("Hello from test", chatsSvc.HandledNewMessages[0].Text);

        // The chat is created (if missing) before the message is saved, and the persist is announced —
        // this is what lets a brand-new chat someone else starts surface without a manual sync.
        Assert.Single(chatsSvc.EnsuredChats);
        Assert.Equal("iMessage;-;+11234567890", chatsSvc.EnsuredChats[0]);
        Assert.Single(chatsSvc.PersistedNotifications);
        Assert.Equal("iMessage;-;+11234567890", chatsSvc.PersistedNotifications[0]);
    }

    [Fact]
    public async Task AssociatedMessage_Skipped()
    {
        var (processor, handler, msgSvc, chatsSvc) = CreateProcessor();
        processor.Start();

        var firedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        processor.MessageProcessed += (_, _) => firedTcs.TrySetResult();

        // Fire the event manually
        var json = JsonSerializer.Deserialize<JsonElement>($$"""
        {
            "data": {
                "originalROWID": 2,
                "guid": "msg-reaction",
                "text": "Liked",
                "isFromMe": false,
                "error": 0,
                "isDelivered": true,
                "hasDdResults": false,
                "itemType": 0,
                "groupActionType": 0,
                "hasAttachments": false,
                "hasReactions": false,
                "hasApplePayloadData": false,
                "wasDeliveredQuietly": false,
                "didNotifyRecipient": false,
                "isBookmarked": false,
                "associatedMessageGuid": "msg-parent",
                "chats": [{ "guid": "iMessage;-;+11234567890", "isArchived": false, "isPinned": false, "hasUnreadMessage": false, "lockChatName": false, "lockChatIcon": false }]
            }
        }
        """);

        handler.HandleEvent(SocketEvents.NewMessage, json, "Test");

        // Wait briefly — the event should NOT fire
        await Task.Delay(500);
        Assert.False(firedTcs.Task.IsCompleted);
        Assert.Empty(msgSvc.SavedMessages);
    }

    [Fact]
    public async Task UpdatedMessage_UpdatesInDb()
    {
        var (processor, handler, msgSvc, _) = CreateProcessor();
        processor.Start();

        var json = JsonSerializer.Deserialize<JsonElement>("""
        {
            "data": {
                "originalROWID": 3,
                "guid": "msg-200",
                "text": "Edited text",
                "isFromMe": true,
                "error": 0,
                "isDelivered": true,
                "hasDdResults": false,
                "itemType": 0,
                "groupActionType": 0,
                "hasAttachments": false,
                "hasReactions": false,
                "hasApplePayloadData": false,
                "wasDeliveredQuietly": false,
                "didNotifyRecipient": false,
                "isBookmarked": false
            }
        }
        """);

        handler.HandleEvent(SocketEvents.UpdatedMessage, json, "Test");

        await Task.Delay(500);

        Assert.Single(msgSvc.UpdatedMessages);
        Assert.Equal("msg-200", msgSvc.UpdatedMessages[0].Guid);
    }

    [Fact]
    public async Task ProcessedEvent_IncludesChatGuidAndIsFromMe()
    {
        var (processor, handler, _, _) = CreateProcessor();
        processor.Start();

        IncomingMessageProcessedEventArgs? args = null;
        processor.MessageProcessed += (_, e) => args = e;

        var json = JsonSerializer.Deserialize<JsonElement>("""
        {
            "data": {
                "originalROWID": 4,
                "guid": "msg-300",
                "text": "From me",
                "isFromMe": true,
                "error": 0,
                "isDelivered": true,
                "hasDdResults": false,
                "itemType": 0,
                "groupActionType": 0,
                "hasAttachments": false,
                "hasReactions": false,
                "hasApplePayloadData": false,
                "wasDeliveredQuietly": false,
                "didNotifyRecipient": false,
                "isBookmarked": false,
                "chats": [{ "guid": "iMessage;-;+19998887777", "isArchived": false, "isPinned": false, "hasUnreadMessage": false, "lockChatName": false, "lockChatIcon": false }]
            },
            "tempGuid": "temp-abc"
        }
        """);

        handler.HandleEvent(SocketEvents.NewMessage, json, "Test");

        await Task.Delay(1000);

        Assert.NotNull(args);
        Assert.Equal("iMessage;-;+19998887777", args!.ChatGuid);
        Assert.True(args.IsFromMe);
    }

    [Fact]
    public async Task MultipleMessages_ProcessedSequentially()
    {
        var (processor, handler, msgSvc, _) = CreateProcessor();
        processor.Start();

        var processedCount = 0;
        var allDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        processor.MessageProcessed += (_, _) =>
        {
            if (Interlocked.Increment(ref processedCount) == 3)
                allDone.TrySetResult();
        };

        for (var i = 0; i < 3; i++)
        {
            var json = JsonSerializer.Deserialize<JsonElement>($$"""
            {
                "data": {
                    "originalROWID": {{10 + i}},
                    "guid": "msg-seq-{{i}}",
                    "text": "Message {{i}}",
                    "isFromMe": false,
                    "error": 0,
                    "isDelivered": true,
                    "hasDdResults": false,
                    "itemType": 0,
                    "groupActionType": 0,
                    "hasAttachments": false,
                    "hasReactions": false,
                    "hasApplePayloadData": false,
                    "wasDeliveredQuietly": false,
                    "didNotifyRecipient": false,
                    "isBookmarked": false,
                    "chats": [{ "guid": "iMessage;-;+11234567890", "isArchived": false, "isPinned": false, "hasUnreadMessage": false, "lockChatName": false, "lockChatIcon": false }]
                }
            }
            """);
            handler.HandleEvent(SocketEvents.NewMessage, json, "Test");
        }

        var completed = await Task.WhenAny(allDone.Task, Task.Delay(5000));
        Assert.True(allDone.Task.IsCompleted, "Not all messages were processed");
        Assert.Equal(3, msgSvc.SavedMessages.Count);

        Assert.Equal("msg-seq-0", msgSvc.SavedMessages[0].Message.Guid);
        Assert.Equal("msg-seq-1", msgSvc.SavedMessages[1].Message.Guid);
        Assert.Equal("msg-seq-2", msgSvc.SavedMessages[2].Message.Guid);
    }

    private static (IncomingMessageProcessor Processor, ActionHandler Handler, RecordingMessagesService MsgSvc, RecordingNotificationService NotifSvc)
        CreateProcessorWithNotifications()
    {
        var handler = new ActionHandler();
        var msgSvc = new RecordingMessagesService();
        var notifSvc = new RecordingNotificationService();
        var processor = new IncomingMessageProcessor(handler, msgSvc, new RecordingChatsService(), notifSvc);
        return (processor, handler, msgSvc, notifSvc);
    }

    private static JsonElement ReactionJson(string guid, string associatedGuid, string type, bool isFromMe)
        => JsonSerializer.Deserialize<JsonElement>($$"""
        {
            "data": {
                "guid": "{{guid}}",
                "associatedMessageGuid": "{{associatedGuid}}",
                "associatedMessageType": "{{type}}",
                "isFromMe": {{(isFromMe ? "true" : "false")}},
                "error": 0,
                "isDelivered": true,
                "hasDdResults": false,
                "itemType": 0,
                "groupActionType": 0,
                "hasAttachments": false,
                "hasReactions": false,
                "hasApplePayloadData": false,
                "wasDeliveredQuietly": false,
                "didNotifyRecipient": false,
                "isBookmarked": false,
                "handle": { "originalROWID": 1, "address": "+11234567890", "service": "iMessage" },
                "chats": [{ "guid": "iMessage;-;+11234567890", "isArchived": false, "isPinned": false, "hasUnreadMessage": false, "lockChatName": false, "lockChatIcon": false }]
            }
        }
        """);

    [Fact]
    public async Task Reaction_FromOther_PersistedAndNotifies()
    {
        var (processor, handler, msgSvc, notifSvc) = CreateProcessorWithNotifications();
        processor.Start();

        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        msgSvc.ReactionSaved += () => saved.TrySetResult();

        // The notification is raised on a separate async hop after the reaction is
        // persisted, so wait for it explicitly instead of relying on ordering.
        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        notifSvc.NotificationAdded += () => notified.TrySetResult();

        handler.HandleEvent(SocketEvents.NewMessage,
            ReactionJson("react-1", "p:0/msg-parent", "love", isFromMe: false), "Test");

        await Task.WhenAny(saved.Task, Task.Delay(3000));
        Assert.True(saved.Task.IsCompleted, "Reaction was not persisted");

        await Task.WhenAny(notified.Task, Task.Delay(3000));
        Assert.True(notified.Task.IsCompleted, "Notification was not raised");

        Assert.Single(msgSvc.SavedReactions);
        Assert.Equal("iMessage;-;+11234567890", msgSvc.SavedReactions[0].ChatGuid);
        Assert.Equal("love", msgSvc.SavedReactions[0].Reaction.AssociatedMessageType);

        var notification = Assert.Single(notifSvc.Notifications);
        Assert.True(notification.IsReaction);
        Assert.Equal("Loved a message", notification.MessageText);

        // A reaction must not be added to the normal message stream.
        Assert.Empty(msgSvc.SavedMessages);
    }

    [Fact]
    public async Task ChatUpdated_PersistsTheChatFromThePayload()
    {
        // The four group events used to reach view models only, so nothing wrote them to the cache.
        var (processor, handler, _, chatsSvc) = CreateProcessor();
        processor.Start();

        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        chatsSvc.ChatUpdateApplied += () => applied.TrySetResult();

        var json = JsonSerializer.Deserialize<JsonElement>("""
        {
            "guid": "msg-group-1",
            "itemType": 2,
            "chats": [{ "guid": "iMessage;+;chat-renamed", "displayName": "Renamed" }]
        }
        """);

        handler.HandleEvent(SocketEvents.GroupNameChange, json, "Test");

        await Task.WhenAny(applied.Task, Task.Delay(3000));
        Assert.True(applied.Task.IsCompleted, "ApplyChatUpdateAsync was never called");

        var chat = Assert.Single(chatsSvc.AppliedChatUpdates);
        Assert.Equal("iMessage;+;chat-renamed", chat.Guid);
        Assert.Equal("Renamed", chat.DisplayName);
    }

    [Fact]
    public async Task Reaction_FromMe_PersistedButNotNotified()
    {
        var (processor, handler, msgSvc, notifSvc) = CreateProcessorWithNotifications();
        processor.Start();

        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        msgSvc.ReactionSaved += () => saved.TrySetResult();

        handler.HandleEvent(SocketEvents.NewMessage,
            ReactionJson("react-mine", "msg-parent", "like", isFromMe: true), "Test");

        await Task.WhenAny(saved.Task, Task.Delay(3000));
        Assert.True(saved.Task.IsCompleted);

        Assert.Single(msgSvc.SavedReactions);
        Assert.Empty(notifSvc.Notifications);
    }

    [Fact]
    public async Task ReactionRemoval_PersistedButNotNotified()
    {
        var (processor, handler, msgSvc, notifSvc) = CreateProcessorWithNotifications();
        processor.Start();

        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        msgSvc.ReactionSaved += () => saved.TrySetResult();

        handler.HandleEvent(SocketEvents.NewMessage,
            ReactionJson("react-removal", "msg-parent", "-love", isFromMe: false), "Test");

        await Task.WhenAny(saved.Task, Task.Delay(3000));
        Assert.True(saved.Task.IsCompleted);

        Assert.Single(msgSvc.SavedReactions);
        Assert.Empty(notifSvc.Notifications);
    }

    [Fact]
    public async Task UpdatedMessage_AnnouncesPersistForOwningChat()
    {
        var (processor, handler, msgSvc, chatsSvc) = CreateProcessor();
        msgSvc.UpdatedMessageChatGuid = "iMessage;-;+11234567890";
        processor.Start();

        var persisted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        chatsSvc.MessagesPersisted += (_, _) => persisted.TrySetResult();

        handler.HandleEvent(SocketEvents.UpdatedMessage, JsonSerializer.Deserialize<JsonElement>("""
        {
            "data": {
                "originalROWID": 7,
                "guid": "msg-edited",
                "text": "Edited text",
                "isFromMe": true,
                "error": 0,
                "isDelivered": true,
                "hasDdResults": false,
                "itemType": 0,
                "groupActionType": 0,
                "hasAttachments": false,
                "hasReactions": false,
                "hasApplePayloadData": false,
                "wasDeliveredQuietly": false,
                "didNotifyRecipient": false,
                "isBookmarked": false
            }
        }
        """), "Test");

        await Task.WhenAny(persisted.Task, Task.Delay(3000));
        Assert.True(persisted.Task.IsCompleted, "MessagesPersisted was not raised for an update");
        Assert.Equal("iMessage;-;+11234567890", Assert.Single(chatsSvc.PersistedNotifications));
    }

    [Fact]
    public async Task FailedEvent_IsLoggedAndQueueKeepsDraining()
    {
        var (processor, handler, msgSvc, _) = CreateProcessor();
        msgSvc.ThrowOnNextSave = true;

        // Capture live rather than reading AppLog.Entries: that ring buffer is shared with every
        // other test running in parallel and can evict our line before we look.
        var logged = new List<string>();
        void OnEntry(string entry) { lock (logged) logged.Add(entry); }
        AppLog.EntryAdded += OnEntry;
        try
        {
            processor.Start();

            handler.HandleEvent(SocketEvents.NewMessage, NewMessageJson("msg-boom"), "Test");
            handler.HandleEvent(SocketEvents.NewMessage, NewMessageJson("msg-after-boom"), "Test");

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (msgSvc.SavedMessages.Count == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(50);

            // The failing event must not kill the reader loop.
            Assert.Equal("msg-after-boom", Assert.Single(msgSvc.SavedMessages).Message.Guid);

            // ...and the failure must not be silent.
            lock (logged)
                Assert.Contains(logged, e => e.Contains("Failed to process") && e.Contains("msg-boom"));
        }
        finally
        {
            AppLog.EntryAdded -= OnEntry;
        }
    }

    private static JsonElement NewMessageJson(string guid)
        => JsonSerializer.Deserialize<JsonElement>($$"""
        {
            "data": {
                "guid": "{{guid}}",
                "text": "Body",
                "isFromMe": false,
                "error": 0,
                "isDelivered": true,
                "hasDdResults": false,
                "itemType": 0,
                "groupActionType": 0,
                "hasAttachments": false,
                "hasReactions": false,
                "hasApplePayloadData": false,
                "wasDeliveredQuietly": false,
                "didNotifyRecipient": false,
                "isBookmarked": false,
                "handle": { "originalROWID": 1, "address": "+11234567890", "service": "iMessage" },
                "chats": [{ "guid": "iMessage;-;+11234567890", "isArchived": false, "isPinned": false, "hasUnreadMessage": false, "lockChatName": false, "lockChatIcon": false }]
            }
        }
        """);
}

internal class NoOpNotificationService : INotificationService
{
    public void HandleNewMessage(NewMessageNotification notification) { }
    public void ClearNotificationsForChat(string chatGuid) { }
    public void ClearAllNotifications() { }
}

internal class RecordingMessagesService : IMessagesService
{
    public List<(string ChatGuid, Message Message)> SavedMessages { get; } = [];
    public List<Message> UpdatedMessages { get; } = [];
    public List<(string ChatGuid, Message Reaction)> SavedReactions { get; } = [];

    public Task<List<MessageEntity>> LoadMessagesAsync(int chatId, int limit = 50, long? beforeDate = null)
        => Task.FromResult(new List<MessageEntity>());

    public Task<List<MessageEntity>> LoadMessagesAsync(IReadOnlyList<int> chatIds, int limit = 50, long? beforeDate = null)
        => Task.FromResult(new List<MessageEntity>());

    public Task<List<MessageEntity>> LoadMessagesAfterAsync(int chatId, long afterDate)
        => Task.FromResult(new List<MessageEntity>());

    public Task<List<MessageEntity>> LoadMessagesAfterAsync(IReadOnlyList<int> chatIds, long afterDate)
        => Task.FromResult(new List<MessageEntity>());

    public Task<List<MessageEntity>> FetchOlderMessagesFromServerAsync(
        int chatId, string chatGuid, int limit = 25, CancellationToken ct = default)
        => Task.FromResult(new List<MessageEntity>());

    public Task<bool> EnsureChatHydratedAsync(
        int chatId, string chatGuid, int limit = 50, CancellationToken ct = default)
        => Task.FromResult(false);

    public Task<bool> RefreshLatestFromServerAsync(
        int chatId, string chatGuid, int limit = 50, CancellationToken ct = default)
        => Task.FromResult(false);

    /// <summary>When set, the next call to <see cref="SaveIncomingMessageAsync"/> throws — used to prove
    /// the processor logs the failure and keeps draining the queue.</summary>
    public bool ThrowOnNextSave { get; set; }

    public Task SaveIncomingMessageAsync(string chatGuid, Message message)
    {
        if (ThrowOnNextSave)
        {
            ThrowOnNextSave = false;
            throw new InvalidOperationException("injected save failure");
        }

        SavedMessages.Add((chatGuid, message));
        return Task.CompletedTask;
    }

    /// <summary>Chat GUID handed back by <see cref="UpdateMessageAsync"/>, standing in for the owning
    /// chat the real service looks up from the cached row.</summary>
    public string? UpdatedMessageChatGuid { get; set; }

    public Task<string?> UpdateMessageAsync(Message message)
    {
        UpdatedMessages.Add(message);
        return Task.FromResult(UpdatedMessageChatGuid);
    }

    public List<string> DeletedGuids { get; } = [];

    public Task<bool> DeleteMessageAsync(string chatGuid, string messageGuid)
    {
        DeletedGuids.Add(messageGuid);
        return Task.FromResult(true);
    }

    public Task<List<AttachmentEntity>> LoadMediaAttachmentsAsync(int chatId, int limit = 50, int offset = 0)
        => Task.FromResult(new List<AttachmentEntity>());

    public Task<List<AttachmentEntity>> LoadMediaAttachmentsAsync(IReadOnlyList<int> chatIds, int limit = 50, int offset = 0)
        => Task.FromResult(new List<AttachmentEntity>());

    public Task<List<MessageEntity>> LoadReactionsAsync(IReadOnlyCollection<string> parentGuids)
        => Task.FromResult(new List<MessageEntity>());

    public Task<List<MessageEntity>> GetMessagesByGuidsAsync(IReadOnlyCollection<string> guids)
        => Task.FromResult(new List<MessageEntity>());

    public event Action? ReactionSaved;

    public Task SaveReactionAsync(string chatGuid, Message reaction)
    {
        SavedReactions.Add((chatGuid, reaction));
        ReactionSaved?.Invoke();
        return Task.CompletedTask;
    }
}

internal class RecordingNotificationService : INotificationService
{
    public List<NewMessageNotification> Notifications { get; } = [];
    public event Action? NotificationAdded;
    public void HandleNewMessage(NewMessageNotification notification)
    {
        Notifications.Add(notification);
        NotificationAdded?.Invoke();
    }
    public void ClearNotificationsForChat(string chatGuid) { }
    public void ClearAllNotifications() { }
}

internal class RecordingChatsService : IChatsService
{
    public List<(string ChatGuid, string? Text, long DateCreated, bool IsFromMe)> HandledNewMessages { get; } = [];
    public List<string> EnsuredChats { get; } = [];
    public List<Chat> AppliedChatUpdates { get; } = [];
    public event Action? ChatUpdateApplied;
    public List<string> PersistedNotifications { get; } = [];

    public IReadOnlyList<ChatWithParticipants> Chats => [];
    public IReadOnlyList<ChatWithParticipants> ArchivedChats => [];

    public event EventHandler? ChatsChanged;
    public event EventHandler<string>? ChatUpdated;
    public event EventHandler? ArchivedChatsChanged;
    public event EventHandler<string>? MessagesPersisted;

    public Task LoadChatsAsync() => Task.CompletedTask;
    public Task LoadArchivedChatsAsync() => Task.CompletedTask;

    public Task HandleNewMessageAsync(string chatGuid, string? messageText, long dateCreated, bool isFromMe, string? senderAddress = null)
    {
        HandledNewMessages.Add((chatGuid, messageText, dateCreated, isFromMe));
        return Task.CompletedTask;
    }

    public Task MarkChatReadAsync(string chatGuid, bool read, bool notifyServer = true) => Task.CompletedTask;
    public Task TogglePinAsync(string chatGuid) => Task.CompletedTask;
    public Task ReorderPinsAsync(List<string> chatGuids) => Task.CompletedTask;
    public Task ArchiveChatAsync(string chatGuid) => Task.CompletedTask;
    public Task UnarchiveChatAsync(string chatGuid) => Task.CompletedTask;
    public Task<bool> DeleteChatAsync(string chatGuid) => Task.FromResult(true);
    public Task<bool> RenameChatAsync(string chatGuid, string newName) => Task.FromResult(true);
    public Task ToggleMuteAsync(string chatGuid) => Task.CompletedTask;
    public Task<bool> AddParticipantAsync(string chatGuid, string address) => Task.FromResult(true);
    public Task<bool> RemoveParticipantAsync(string chatGuid, string address) => Task.FromResult(true);
    public Task<bool> LeaveChatAsync(string chatGuid) => Task.FromResult(true);
    public Task<bool> SetChatIconAsync(string chatGuid, Stream iconStream, string fileName) => Task.FromResult(true);
    public Task<bool> DeleteChatIconAsync(string chatGuid) => Task.FromResult(true);
    public string? FindExistingChatGuid(IEnumerable<string> addresses) => null;
    public Task EnsureChatInDatabaseAsync(Chat chat, string? messageText) => Task.CompletedTask;

    public Task EnsureChatExistsAsync(Chat chatData)
    {
        EnsuredChats.Add(chatData.Guid);
        return Task.CompletedTask;
    }

    public Task ApplyChatUpdateAsync(Chat chatData)
    {
        AppliedChatUpdates.Add(chatData);
        ChatUpdateApplied?.Invoke();
        return Task.CompletedTask;
    }

    public void NotifyMessagesPersisted(string chatGuid)
    {
        PersistedNotifications.Add(chatGuid);
        MessagesPersisted?.Invoke(this, chatGuid);
    }
}
