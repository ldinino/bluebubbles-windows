using System.Text.Json;
using BlueBubbles.Core.Data.Entities;
using BlueBubbles.Core.Models;
using BlueBubbles.Core.Utils;

namespace BlueBubbles.Core.Data;

public static class MappingExtensions
{
    private static readonly JsonSerializerOptions JsonOptions = JsonDefaults.Options;

    // ── Handle ──

    public static HandleEntity ToEntity(this Handle dto) => new()
    {
        OriginalRowId = dto.OriginalRowId,
        Address = dto.Address,
        Service = dto.Service,
        Country = dto.Country,
        FormattedAddress = dto.FormattedAddress,
        Color = dto.Color,
        DefaultPhone = dto.DefaultPhone,
        DefaultEmail = dto.DefaultEmail,
        UniqueAddressAndService = dto.UniqueAddressAndService
            ?? $"{dto.Address}/{dto.Service}"
    };

    public static Handle ToDto(this HandleEntity entity) => new(
        OriginalRowId: entity.OriginalRowId ?? entity.Id,
        Address: entity.Address,
        Service: entity.Service,
        Country: entity.Country,
        FormattedAddress: entity.FormattedAddress,
        Color: entity.Color,
        DefaultPhone: entity.DefaultPhone,
        DefaultEmail: entity.DefaultEmail,
        UniqueAddressAndService: entity.UniqueAddressAndService
    );

    // ── Attachment ──

    public static AttachmentEntity ToEntity(this Attachment dto) => new()
    {
        OriginalRowId = dto.OriginalRowId,
        Guid = dto.Guid,
        Uti = dto.Uti,
        MimeType = dto.MimeType,
        IsOutgoing = dto.IsOutgoing,
        TransferName = dto.TransferName,
        TotalBytes = dto.TotalBytes,
        Height = dto.Height,
        Width = dto.Width,
        HasLivePhoto = dto.HasLivePhoto,
        MetadataJson = dto.Metadata is not null
            ? JsonSerializer.Serialize(dto.Metadata, JsonOptions) : null
    };

    public static Attachment ToDto(this AttachmentEntity entity) => new(
        OriginalRowId: entity.OriginalRowId,
        Guid: entity.Guid,
        Uti: entity.Uti,
        MimeType: entity.MimeType,
        IsOutgoing: entity.IsOutgoing,
        TransferName: entity.TransferName,
        TotalBytes: entity.TotalBytes,
        Height: entity.Height,
        Width: entity.Width,
        HasLivePhoto: entity.HasLivePhoto,
        Metadata: entity.MetadataJson is not null
            ? JsonSerializer.Deserialize<Dictionary<string, object?>>(entity.MetadataJson, JsonOptions) : null
    );

    // ── Message ──

    public static MessageEntity ToEntity(this Message dto, int chatId = 0) => new()
    {
        OriginalRowId = dto.OriginalRowId,
        Guid = dto.Guid,
        OtherHandle = dto.OtherHandle,
        Text = dto.Text,
        Subject = dto.Subject,
        Country = dto.Country,
        Error = dto.Error,
        DateCreated = dto.DateCreated,
        DateRead = dto.DateRead,
        DateDelivered = dto.DateDelivered,
        IsDelivered = dto.IsDelivered,
        IsFromMe = dto.IsFromMe,
        HasDdResults = dto.HasDdResults,
        DatePlayed = dto.DatePlayed,
        ItemType = dto.ItemType,
        GroupTitle = dto.GroupTitle,
        GroupActionType = dto.GroupActionType,
        BalloonBundleId = dto.BalloonBundleId,
        AssociatedMessageGuid = dto.AssociatedMessageGuid,
        AssociatedMessagePart = dto.AssociatedMessagePart,
        AssociatedMessageType = dto.AssociatedMessageType,
        ExpressiveSendStyleId = dto.ExpressiveSendStyleId,
        HasAttachments = dto.HasAttachments,
        HasReactions = dto.HasReactions,
        DateDeleted = dto.DateDeleted,
        MetadataJson = dto.Metadata is not null
            ? JsonSerializer.Serialize(dto.Metadata, JsonOptions) : null,
        ThreadOriginatorGuid = dto.ThreadOriginatorGuid,
        ThreadOriginatorPart = dto.ThreadOriginatorPart,
        AttributedBodyJson = dto.AttributedBody is not null
            ? JsonSerializer.Serialize(dto.AttributedBody, JsonOptions) : null,
        MessageSummaryInfoJson = dto.MessageSummaryInfo is not null
            ? JsonSerializer.Serialize(dto.MessageSummaryInfo, JsonOptions) : null,
        PayloadDataJson = dto.PayloadData is not null
            ? JsonSerializer.Serialize(dto.PayloadData, JsonOptions) : null,
        HasApplePayloadData = dto.HasApplePayloadData,
        DateEdited = dto.DateEdited,
        WasDeliveredQuietly = dto.WasDeliveredQuietly,
        DidNotifyRecipient = dto.DidNotifyRecipient,
        IsBookmarked = dto.IsBookmarked,
        ChatId = chatId
    };

    public static Message ToDto(this MessageEntity entity) => new(
        OriginalRowId: entity.OriginalRowId,
        Guid: entity.Guid,
        HandleId: entity.HandleId,
        OtherHandle: entity.OtherHandle,
        Text: entity.Text,
        Subject: entity.Subject,
        Country: entity.Country,
        Error: entity.Error,
        DateCreated: entity.DateCreated,
        DateRead: entity.DateRead,
        DateDelivered: entity.DateDelivered,
        IsDelivered: entity.IsDelivered,
        IsFromMe: entity.IsFromMe,
        HasDdResults: entity.HasDdResults,
        DatePlayed: entity.DatePlayed,
        ItemType: entity.ItemType,
        GroupTitle: entity.GroupTitle,
        GroupActionType: entity.GroupActionType,
        BalloonBundleId: entity.BalloonBundleId,
        AssociatedMessageGuid: entity.AssociatedMessageGuid,
        AssociatedMessagePart: entity.AssociatedMessagePart,
        AssociatedMessageType: entity.AssociatedMessageType,
        ExpressiveSendStyleId: entity.ExpressiveSendStyleId,
        Handle: entity.Handle?.ToDto(),
        HasAttachments: entity.HasAttachments,
        HasReactions: entity.HasReactions,
        DateDeleted: entity.DateDeleted,
        Metadata: entity.MetadataJson is not null
            ? JsonSerializer.Deserialize<Dictionary<string, object?>>(entity.MetadataJson, JsonOptions) : null,
        ThreadOriginatorGuid: entity.ThreadOriginatorGuid,
        ThreadOriginatorPart: entity.ThreadOriginatorPart,
        Attachments: entity.Attachments?.Select(a => a.ToDto()).ToList(),
        Chats: null,
        AttributedBody: entity.AttributedBodyJson is not null
            ? JsonSerializer.Deserialize<List<AttributedBody>>(entity.AttributedBodyJson, JsonOptions) : null,
        MessageSummaryInfo: entity.MessageSummaryInfoJson is not null
            ? JsonSerializer.Deserialize<List<MessageSummaryInfo>>(entity.MessageSummaryInfoJson, JsonOptions) : null,
        PayloadData: entity.PayloadDataJson is not null
            ? JsonSerializer.Deserialize<PayloadData>(entity.PayloadDataJson, JsonOptions) : null,
        HasApplePayloadData: entity.HasApplePayloadData,
        DateEdited: entity.DateEdited,
        WasDeliveredQuietly: entity.WasDeliveredQuietly,
        DidNotifyRecipient: entity.DidNotifyRecipient,
        IsBookmarked: entity.IsBookmarked
    );

    // ── Chat ──

    public static ChatEntity ToEntity(this Chat dto) => new()
    {
        Guid = dto.Guid,
        ChatIdentifier = dto.ChatIdentifier,
        DisplayName = dto.DisplayName,
        IsArchived = dto.IsArchived,
        IsPinned = dto.IsPinned,
        HasUnreadMessage = dto.HasUnreadMessage ?? false,
        Service = dto.Service,
        MuteType = dto.MuteType,
        MuteArgs = dto.MuteArgs,
        AutoSendReadReceipts = dto.AutoSendReadReceipts,
        AutoSendTypingIndicators = dto.AutoSendTypingIndicators,
        DateDeleted = dto.DateDeleted,
        Style = dto.Style,
        LockChatName = dto.LockChatName,
        LockChatIcon = dto.LockChatIcon,
        LastReadMessageGuid = dto.LastReadMessageGuid,
        CustomAvatarPath = dto.CustomAvatarPath,
        PinIndex = dto.PinIndex,
        LatestMessageDate = dto.LastMessage?.DateCreated
    };

    public static Chat ToDto(this ChatEntity entity) => new(
        Guid: entity.Guid,
        ChatIdentifier: entity.ChatIdentifier,
        DisplayName: entity.DisplayName,
        Participants: entity.ChatParticipants?.Select(cp => cp.Handle.ToDto()).ToList(),
        LastMessage: entity.Messages?
            .OrderByDescending(m => m.DateCreated)
            .FirstOrDefault()?.ToDto(),
        IsArchived: entity.IsArchived,
        IsPinned: entity.IsPinned,
        HasUnreadMessage: entity.HasUnreadMessage,
        Service: entity.Service,
        MuteType: entity.MuteType,
        MuteArgs: entity.MuteArgs,
        AutoSendReadReceipts: entity.AutoSendReadReceipts,
        AutoSendTypingIndicators: entity.AutoSendTypingIndicators,
        DateDeleted: entity.DateDeleted,
        Style: entity.Style,
        LockChatName: entity.LockChatName,
        LockChatIcon: entity.LockChatIcon,
        LastReadMessageGuid: entity.LastReadMessageGuid,
        CustomAvatarPath: entity.CustomAvatarPath,
        PinIndex: entity.PinIndex
    );

    // ── FcmData ──

    public static FcmDataEntity ToEntity(this Models.FcmData dto)
    {
        var projectInfo = dto.ProjectInfo;
        var client = dto.Client?.FirstOrDefault();
        var oauthClient = client?.OAuthClient?.FirstOrDefault();
        var apiKey = client?.ApiKey?.FirstOrDefault();
        var clientId = oauthClient?.ClientId;
        if (clientId?.Contains('-') == true)
            clientId = clientId[..clientId.IndexOf('-')];

        return new FcmDataEntity
        {
            ProjectId = projectInfo?.ProjectId,
            StorageBucket = projectInfo?.StorageBucket,
            FirebaseUrl = projectInfo?.FirebaseUrl,
            ApiKey = apiKey?.CurrentKey,
            ClientId = clientId,
            ApplicationId = client?.ClientInfo?.MobileSdkAppId
        };
    }
}
