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

        _ = Task.Run(ProcessAsync);
    }

    private async Task ProcessAsync()
    {
        await foreach (var evt in _queue.Reader.ReadAllAsync())
        {
            try
            {
                switch (evt.Type)
                {
                    case IncomingEventType.NewMessage:
                        await ProcessNewMessageAsync(evt.Args);
                        break;
                    case IncomingEventType.UpdatedMessage:
                        await ProcessUpdatedMessageAsync(evt.Args);
                        break;
                    case IncomingEventType.Reaction:
                        await ProcessReactionAsync(evt.Args);
                        break;
                }
            }
            catch
            {
                // Swallow to keep the queue processing
            }
        }
    }

    private async Task ProcessNewMessageAsync(MessageEventArgs e)
    {
        var chatGuid = e.Message.Chats?.FirstOrDefault()?.Guid;
        if (chatGuid is null) return;

        if (e.Message.AssociatedMessageGuid is not null) return;

        await _messagesService.SaveIncomingMessageAsync(chatGuid, e.Message);
        await _chatsService.HandleNewMessageAsync(
            chatGuid, e.Message.Text, e.Message.DateCreated ?? 0,
            e.Message.IsFromMe, e.Message.Handle?.Address);

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
        await _messagesService.UpdateMessageAsync(e.Message);
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

    private record IncomingEvent(IncomingEventType Type, MessageEventArgs Args);
    private enum IncomingEventType { NewMessage, UpdatedMessage, Reaction }
}
