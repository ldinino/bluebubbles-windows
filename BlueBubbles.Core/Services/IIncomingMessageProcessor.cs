namespace BlueBubbles.Core.Services;

public interface IIncomingMessageProcessor
{
    event EventHandler<IncomingMessageProcessedEventArgs>? MessageProcessed;

    void Start();
}

public record IncomingMessageProcessedEventArgs(string ChatGuid, bool IsFromMe);
