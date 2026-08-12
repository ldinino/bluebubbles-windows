using System.Threading.Channels;
using BlueBubbles.Core.Models;
using BlueBubbles.Core.Utils;

namespace BlueBubbles.Core.Services;

public class IncomingMessageProcessor : IIncomingMessageProcessor
{
    private readonly IActionHandler _actionHandler;
    private readonly IMessagesService _messagesService;
    private readonly IChatsService _chatsService;
    private readonly INotificationService _notificationService;
    private readonly Channel<IncomingEvent> _queue;

    public event EventHandler<IncomingMessageProcessedEventArgs>? MessageProcessed;

    public IncomingMessageProcessor(
        IActionHandler actionHandler,
        IMessagesService messagesService,
        IChatsService chatsService,
        INotificationService notificationService)
    {
        _actionHandler = actionHandler;
        _messagesService = messagesService;
        _chatsService = chatsService;
        _notificationService = notificationService;
        _queue = Channel.CreateUnbounded<IncomingEvent>(new UnboundedChannelOptions
        {
            SingleReader = true
        });
    }

    public void Start()
    {
        _actionHandler.NewMessageReceived += (_, e) =>
            _queue.Writer.TryWrite(new IncomingEvent(IncomingEventType.NewMessage, e));
        _actionHandler.MessageUpdated += (_, e) =>
            _queue.Writer.TryWrite(new IncomingEvent(IncomingEventType.UpdatedMessage, e));
        _actionHandler.ReactionReceived += (_, e) =>
            _queue.Writer.TryWrite(new IncomingEvent(IncomingEventType.Reaction,
                new MessageEventArgs(e.Reaction, null)));
        // Group renames / membership changes only ever reached the view models, so nothing wrote them
        // to the cache the conversation list reads from.
        _actionHandler.ChatUpdated += (_, e) =>
        {
            if (e.Chat is not null)
                _queue.Writer.TryWrite(new IncomingEvent(IncomingEventType.ChatUpdated, null, e.Chat));
        };

        _ = Task.Run(ProcessAsync);
    }

    /// <summary>Drains the queue. Invariant: every branch that persists a message must also announce
    /// it (<see cref="IChatsService.NotifyMessagesPersisted"/> and/or <see cref="MessageProcessed"/>)
    /// so the conversation list and the open thread refresh without a manual sync. Failures are
    /// contained per-event to keep the queue alive, but never silently: a swallowed exception here
    /// means half-persisted data with no UI event, which is exactly the bug class we cannot see
    /// without a log line.</summary>
    private async Task ProcessAsync()
    {
        await foreach (var evt in _queue.Reader.ReadAllAsync())
        {
            try
            {
                switch (evt.Type)
                {
                    case IncomingEventType.NewMessage:
                        await ProcessNewMessageAsync(evt.Args!);
                        break;
                    case IncomingEventType.UpdatedMessage:
                        await ProcessUpdatedMessageAsync(evt.Args!);
                        break;
                    case IncomingEventType.Reaction:
                        await ProcessReactionAsync(evt.Args!);
                        break;
                    case IncomingEventType.ChatUpdated:
                        await _chatsService.ApplyChatUpdateAsync(evt.Chat!);
                        break;
                }
            }
            catch (Exception ex)
            {
                // Keep draining the queue, but leave a trail: chat + message GUID are what make this
                // diagnosable after the fact.
                AppLog.Error(LogCategory.Socket,
                    $"Failed to process {evt.Type} event " +
                    $"(chat={evt.Args?.Message.Chats?.FirstOrDefault()?.Guid ?? evt.Chat?.Guid ?? "?"}, " +
                    $"message={evt.Args?.Message.Guid ?? "-"}): {ex}");
            }
        }
    }

    private async Task ProcessNewMessageAsync(MessageEventArgs e)
    {
        var chatGuid = e.Message.Chats?.FirstOrDefault()?.Guid;
        if (chatGuid is null) return;

        if (e.Message.AssociatedMessageGuid is not null) return;

        // A chat someone else just started arrives here with no local row yet. Create it first —
        // otherwise SaveIncomingMessageAsync and HandleNewMessageAsync both bail on the missing chat
        // and the conversation never surfaces until a manual incremental sync.
        var chatData = e.Message.Chats?.FirstOrDefault();
        if (chatData is not null)
            await _chatsService.EnsureChatExistsAsync(chatData);

        await _messagesService.SaveIncomingMessageAsync(chatGuid, e.Message);
        await _chatsService.HandleNewMessageAsync(
            chatGuid,
            MessagePreview.Derive(e.Message.Text, e.Message.Attachments?.Select(a => a.MimeType)),
            e.Message.DateCreated ?? 0,
            e.Message.IsFromMe, e.Message.Handle?.Address);
        _chatsService.NotifyMessagesPersisted(chatGuid);

        MessageProcessed?.Invoke(this,
            new IncomingMessageProcessedEventArgs(chatGuid, e.Message.IsFromMe));

        _notificationService.HandleNewMessage(new NewMessageNotification(
            chatGuid,
            e.Message.Guid,
            e.Message.Handle?.Address,
            e.Message.Text,
            e.Message.IsFromMe,
            e.Message.AssociatedMessageGuid is not null,
            e.Message.WasDeliveredQuietly));
    }

    private async Task ProcessUpdatedMessageAsync(MessageEventArgs e)
    {
        // An edit / unsend / delivery receipt mutates a row in place, so nothing else announces it.
        // The update payload usually carries no chat, so take the owning chat from the cached row.
        var chatGuid = await _messagesService.UpdateMessageAsync(e.Message)
            ?? e.Message.Chats?.FirstOrDefault()?.Guid;
        if (chatGuid is not null)
            _chatsService.NotifyMessagesPersisted(chatGuid);
    }

    private async Task ProcessReactionAsync(MessageEventArgs e)
    {
        var chatGuid = e.Message.Chats?.FirstOrDefault()?.Guid;
        if (chatGuid is null) return;

        await _messagesService.SaveReactionAsync(chatGuid, e.Message);

        // Only notify for reactions added by others — never for removals or our own.
        if (e.Message.IsFromMe || ReactionTypes.IsRemoval(e.Message.AssociatedMessageType))
            return;

        var verb = ReactionTypes.ToVerb(e.Message.AssociatedMessageType ?? string.Empty);
        _notificationService.HandleNewMessage(new NewMessageNotification(
            chatGuid,
            e.Message.Guid,
            e.Message.Handle?.Address,
            $"{verb} a message",
            e.Message.IsFromMe,
            IsReaction: true,
            e.Message.WasDeliveredQuietly));
    }

    private record IncomingEvent(IncomingEventType Type, MessageEventArgs? Args, Chat? Chat = null);
    private enum IncomingEventType { NewMessage, UpdatedMessage, Reaction, ChatUpdated }
}
