namespace BlueBubbles.Core.Services;

public interface INotificationService
{
    void HandleNewMessage(NewMessageNotification notification);
    void ClearNotificationsForChat(string chatGuid);
    void ClearAllNotifications();
}

public record NewMessageNotification(
    string ChatGuid,
    string MessageGuid,
    string? SenderAddress,
    string? MessageText,
    bool IsFromMe,
    bool IsReaction,
    bool WasDeliveredQuietly);
