using System.Text.Json;
using BlueBubbles.Core.Data;
using BlueBubbles.Core.Data.Entities;
using BlueBubbles.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BlueBubbles.Windows.Tests;

public class DataModelTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BlueBubblesDbContext _db;

    public DataModelTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<BlueBubblesDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new BlueBubblesDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public void Database_CreatesAllTables()
    {
        _db.Chats.ToList();
        _db.Messages.ToList();
        _db.Handles.ToList();
        _db.Attachments.ToList();
        _db.ChatParticipants.ToList();
        _db.FcmData.ToList();
    }

    [Fact]
    public void Chat_WithParticipants_PersistsAndLoads()
    {
        var handle1 = new HandleEntity { Address = "+15551234567", Service = "iMessage", UniqueAddressAndService = "+15551234567/iMessage" };
        var handle2 = new HandleEntity { Address = "+15559876543", Service = "iMessage", UniqueAddressAndService = "+15559876543/iMessage" };
        _db.Handles.AddRange(handle1, handle2);
        _db.SaveChanges();

        var chat = new ChatEntity
        {
            Guid = "iMessage;-;chat123456",
            ChatIdentifier = "chat123456",
            DisplayName = "Test Group",
            IsPinned = true,
            Service = "iMessage"
        };
        _db.Chats.Add(chat);
        _db.SaveChanges();

        _db.ChatParticipants.AddRange(
            new ChatParticipant { ChatId = chat.Id, HandleId = handle1.Id },
            new ChatParticipant { ChatId = chat.Id, HandleId = handle2.Id }
        );
        _db.SaveChanges();

        var loaded = _db.Chats
            .Include(c => c.ChatParticipants)
            .ThenInclude(cp => cp.Handle)
            .Single(c => c.Guid == "iMessage;-;chat123456");

        Assert.Equal("Test Group", loaded.DisplayName);
        Assert.True(loaded.IsPinned);
        Assert.Equal(2, loaded.ChatParticipants.Count);
        Assert.Contains(loaded.ChatParticipants, cp => cp.Handle.Address == "+15551234567");
        Assert.Contains(loaded.ChatParticipants, cp => cp.Handle.Address == "+15559876543");
    }

    [Fact]
    public void Message_WithAttachments_PersistsAndLoads()
    {
        var chat = new ChatEntity { Guid = "iMessage;-;chat-attach", Service = "iMessage" };
        _db.Chats.Add(chat);
        _db.SaveChanges();

        var message = new MessageEntity
        {
            Guid = "msg-001",
            Text = "Check this out!",
            IsFromMe = true,
            DateCreated = 1700000000000,
            ChatId = chat.Id,
            HasAttachments = true
        };
        _db.Messages.Add(message);
        _db.SaveChanges();

        _db.Attachments.AddRange(
            new AttachmentEntity
            {
                Guid = "att-001",
                MimeType = "image/jpeg",
                TransferName = "photo.jpg",
                TotalBytes = 1024000,
                Height = 1920,
                Width = 1080,
                MessageId = message.Id
            },
            new AttachmentEntity
            {
                Guid = "att-002",
                MimeType = "video/mp4",
                TransferName = "video.mp4",
                TotalBytes = 5242880,
                MessageId = message.Id
            }
        );
        _db.SaveChanges();

        var loaded = _db.Messages
            .Include(m => m.Attachments)
            .Single(m => m.Guid == "msg-001");

        Assert.Equal("Check this out!", loaded.Text);
        Assert.Equal(2, loaded.Attachments.Count);
        Assert.Contains(loaded.Attachments, a => a.MimeType == "image/jpeg");
        Assert.Contains(loaded.Attachments, a => a.MimeType == "video/mp4");
    }

    [Fact]
    public void Chat_WithMessages_QueryByDateCreated()
    {
        var chat = new ChatEntity { Guid = "iMessage;-;chat-query", Service = "iMessage" };
        _db.Chats.Add(chat);
        _db.SaveChanges();

        _db.Messages.AddRange(
            new MessageEntity { Guid = "msg-old", Text = "Old message", DateCreated = 1600000000000, IsFromMe = false, ChatId = chat.Id },
            new MessageEntity { Guid = "msg-mid", Text = "Mid message", DateCreated = 1650000000000, IsFromMe = true, ChatId = chat.Id },
            new MessageEntity { Guid = "msg-new", Text = "New message", DateCreated = 1700000000000, IsFromMe = false, ChatId = chat.Id }
        );
        _db.SaveChanges();

        var recentMessages = _db.Messages
            .Where(m => m.ChatId == chat.Id && m.DateCreated > 1620000000000)
            .OrderByDescending(m => m.DateCreated)
            .ToList();

        Assert.Equal(2, recentMessages.Count);
        Assert.Equal("msg-new", recentMessages[0].Guid);
        Assert.Equal("msg-mid", recentMessages[1].Guid);
    }

    [Fact]
    public void GuidIndexes_EnforceUniqueness()
    {
        var chat = new ChatEntity { Guid = "iMessage;-;unique-chat", Service = "iMessage" };
        _db.Chats.Add(chat);
        _db.SaveChanges();

        var duplicate = new ChatEntity { Guid = "iMessage;-;unique-chat", Service = "iMessage" };
        _db.Chats.Add(duplicate);

        Assert.Throws<DbUpdateException>(() => _db.SaveChanges());
    }

    [Fact]
    public void FcmData_PersistsAndLoads()
    {
        _db.FcmData.Add(new FcmDataEntity
        {
            ProjectId = "my-project-123",
            StorageBucket = "my-project-123.appspot.com",
            ApiKey = "AIzaSyTestKey123",
            FirebaseUrl = "https://my-project-123-default-rtdb.firebaseio.com",
            ClientId = "500464701389",
            ApplicationId = "1:500464701389:android:abc123"
        });
        _db.SaveChanges();

        var loaded = _db.FcmData.Single();
        Assert.Equal("my-project-123", loaded.ProjectId);
        Assert.True(loaded.IsValid);
    }

    [Fact]
    public void CascadeDelete_RemovesMessagesAndAttachments()
    {
        var chat = new ChatEntity { Guid = "iMessage;-;cascade-test", Service = "iMessage" };
        _db.Chats.Add(chat);
        _db.SaveChanges();

        var msg = new MessageEntity { Guid = "msg-cascade", Text = "Will be deleted", DateCreated = 1700000000000, IsFromMe = true, ChatId = chat.Id };
        _db.Messages.Add(msg);
        _db.SaveChanges();

        _db.Attachments.Add(new AttachmentEntity { Guid = "att-cascade", MimeType = "image/png", TotalBytes = 100, MessageId = msg.Id });
        _db.SaveChanges();

        _db.Chats.Remove(chat);
        _db.SaveChanges();

        Assert.Empty(_db.Messages.Where(m => m.Guid == "msg-cascade"));
        Assert.Empty(_db.Attachments.Where(a => a.Guid == "att-cascade"));
    }
}

public class MappingTests
{
    [Fact]
    public void HandleDto_RoundTrips_ThroughEntity()
    {
        var dto = new Handle(
            OriginalRowId: 42,
            Address: "+15551234567",
            Service: "iMessage",
            Country: "US",
            FormattedAddress: "(555) 123-4567",
            Color: null,
            DefaultPhone: null,
            DefaultEmail: null,
            UniqueAddressAndService: "+15551234567/iMessage"
        );

        var entity = dto.ToEntity();
        var roundTripped = entity.ToDto();

        Assert.Equal(dto.Address, roundTripped.Address);
        Assert.Equal(dto.Service, roundTripped.Service);
        Assert.Equal(dto.Country, roundTripped.Country);
        Assert.Equal(dto.FormattedAddress, roundTripped.FormattedAddress);
        Assert.Equal(dto.UniqueAddressAndService, roundTripped.UniqueAddressAndService);
    }

    [Fact]
    public void AttachmentDto_RoundTrips_ThroughEntity()
    {
        var dto = new Attachment(
            OriginalRowId: 10,
            Guid: "att-guid-001",
            Uti: "public.jpeg",
            MimeType: "image/jpeg",
            IsOutgoing: false,
            TransferName: "IMG_001.jpg",
            TotalBytes: 2048000,
            Height: 1920,
            Width: 1080,
            HasLivePhoto: false,
            Metadata: null
        );

        var entity = dto.ToEntity();
        var roundTripped = entity.ToDto();

        Assert.Equal(dto.Guid, roundTripped.Guid);
        Assert.Equal(dto.MimeType, roundTripped.MimeType);
        Assert.Equal(dto.TotalBytes, roundTripped.TotalBytes);
        Assert.Equal(dto.Height, roundTripped.Height);
        Assert.Equal(dto.Width, roundTripped.Width);
    }

    [Fact]
    public void MessageDto_RoundTrips_ThroughEntity()
    {
        var dto = new Message(
            OriginalRowId: 100,
            Guid: "msg-guid-001",
            HandleId: 42,
            OtherHandle: null,
            Text: "Hello, this is a test message!",
            Subject: null,
            Country: "US",
            Error: 0,
            DateCreated: 1700000000000,
            DateRead: 1700000060000,
            DateDelivered: 1700000030000,
            IsDelivered: true,
            IsFromMe: true,
            HasDdResults: false,
            DatePlayed: null,
            ItemType: 0,
            GroupTitle: null,
            GroupActionType: 0,
            BalloonBundleId: null,
            AssociatedMessageGuid: null,
            AssociatedMessagePart: null,
            AssociatedMessageType: null,
            ExpressiveSendStyleId: null,
            Handle: null,
            HasAttachments: false,
            HasReactions: false,
            DateDeleted: null,
            Metadata: null,
            ThreadOriginatorGuid: null,
            ThreadOriginatorPart: null,
            Attachments: null,
            Chats: null,
            AttributedBody: null,
            MessageSummaryInfo: null,
            PayloadData: null,
            HasApplePayloadData: false,
            DateEdited: null,
            WasDeliveredQuietly: false,
            DidNotifyRecipient: false,
            IsBookmarked: false
        );

        var entity = dto.ToEntity(chatId: 1);
        var roundTripped = entity.ToDto();

        Assert.Equal(dto.Guid, roundTripped.Guid);
        Assert.Equal(dto.Text, roundTripped.Text);
        Assert.Equal(dto.DateCreated, roundTripped.DateCreated);
        Assert.Equal(dto.DateRead, roundTripped.DateRead);
        Assert.Equal(dto.DateDelivered, roundTripped.DateDelivered);
        Assert.Equal(dto.IsFromMe, roundTripped.IsFromMe);
        Assert.Equal(dto.IsDelivered, roundTripped.IsDelivered);
        Assert.Equal(dto.IsBookmarked, roundTripped.IsBookmarked);
    }

    [Fact]
    public void MessageDto_WithAttributedBody_SerializesJson()
    {
        var attributedBody = new List<AttributedBody>
        {
            new("Hello world", new List<Run>
            {
                new(new List<int> { 0, 11 }, new RunAttributes(0, null, null, null))
            })
        };

        var dto = new Message(
            OriginalRowId: null, Guid: "msg-ab-001", HandleId: null, OtherHandle: null,
            Text: "Hello world", Subject: null, Country: null, Error: 0,
            DateCreated: 1700000000000, DateRead: null, DateDelivered: null,
            IsDelivered: false, IsFromMe: true, HasDdResults: false, DatePlayed: null,
            ItemType: 0, GroupTitle: null, GroupActionType: 0, BalloonBundleId: null,
            AssociatedMessageGuid: null, AssociatedMessagePart: null, AssociatedMessageType: null,
            ExpressiveSendStyleId: null, Handle: null, HasAttachments: false, HasReactions: false,
            DateDeleted: null, Metadata: null, ThreadOriginatorGuid: null, ThreadOriginatorPart: null,
            Attachments: null, Chats: null, AttributedBody: attributedBody, MessageSummaryInfo: null,
            PayloadData: null, HasApplePayloadData: false, DateEdited: null,
            WasDeliveredQuietly: false, DidNotifyRecipient: false, IsBookmarked: false
        );

        var entity = dto.ToEntity();
        Assert.NotNull(entity.AttributedBodyJson);

        var roundTripped = entity.ToDto();
        Assert.NotNull(roundTripped.AttributedBody);
        Assert.Single(roundTripped.AttributedBody);
        Assert.Equal("Hello world", roundTripped.AttributedBody[0].String);
    }

    [Fact]
    public void MessageDto_FromRealServerJson_Deserializes()
    {
        var json = """
        {
            "originalROWID": 12345,
            "guid": "p:0/iMessage;-;+15551234567",
            "handleId": 1,
            "text": "Hey, what's up?",
            "subject": null,
            "country": "US",
            "error": 0,
            "dateCreated": 1700000000000,
            "dateRead": null,
            "dateDelivered": 1700000030000,
            "isDelivered": true,
            "isFromMe": false,
            "hasDdResults": false,
            "datePlayed": null,
            "itemType": 0,
            "groupTitle": null,
            "groupActionType": 0,
            "balloonBundleId": null,
            "associatedMessageGuid": null,
            "associatedMessagePart": null,
            "associatedMessageType": null,
            "expressiveSendStyleId": null,
            "handle": {
                "originalROWID": 1,
                "address": "+15551234567",
                "service": "iMessage",
                "country": "US",
                "formattedAddress": null,
                "color": null,
                "defaultPhone": null,
                "defaultEmail": null,
                "uniqueAddrAndService": "+15551234567/iMessage"
            },
            "hasAttachments": true,
            "hasReactions": false,
            "dateDeleted": null,
            "metadata": null,
            "threadOriginatorGuid": null,
            "threadOriginatorPart": null,
            "attachments": [
                {
                    "originalROWID": 5000,
                    "guid": "att/p:0/iMessage;-;+15551234567/001",
                    "uti": "public.jpeg",
                    "mimeType": "image/jpeg",
                    "isOutgoing": false,
                    "transferName": "IMG_4567.jpg",
                    "totalBytes": 3145728,
                    "height": 4032,
                    "width": 3024,
                    "hasLivePhoto": false,
                    "metadata": null
                }
            ],
            "chats": null,
            "attributedBody": [
                {
                    "string": "Hey, what's up?",
                    "runs": [
                        {
                            "range": [0, 15],
                            "attributes": {
                                "__kIMMessagePartAttributeName": 0
                            }
                        }
                    ]
                }
            ],
            "messageSummaryInfo": null,
            "payloadData": null,
            "hasApplePayloadData": false,
            "dateEdited": null,
            "wasDeliveredQuietly": false,
            "didNotifyRecipient": false,
            "isBookmarked": false
        }
        """;

        var msg = JsonSerializer.Deserialize<Message>(json);

        Assert.NotNull(msg);
        Assert.Equal("p:0/iMessage;-;+15551234567", msg.Guid);
        Assert.Equal("Hey, what's up?", msg.Text);
        Assert.False(msg.IsFromMe);
        Assert.Equal(1700000000000, msg.DateCreated);
        Assert.NotNull(msg.Handle);
        Assert.Equal("+15551234567", msg.Handle.Address);
        Assert.NotNull(msg.Attachments);
        Assert.Single(msg.Attachments);
        Assert.Equal("image/jpeg", msg.Attachments[0].MimeType);
        Assert.Equal(3145728, msg.Attachments[0].TotalBytes);
        Assert.NotNull(msg.AttributedBody);
        Assert.Single(msg.AttributedBody);
        Assert.Equal("Hey, what's up?", msg.AttributedBody[0].String);
    }

    [Fact]
    public void ChatDto_RoundTrips_ThroughEntity()
    {
        var dto = new Chat(
            Guid: "iMessage;-;chat-round-trip",
            ChatIdentifier: "chat-round-trip",
            DisplayName: "My Group",
            Participants: null,
            LastMessage: null,
            IsArchived: false,
            IsPinned: true,
            HasUnreadMessage: true,
            Service: "iMessage",
            MuteType: null,
            MuteArgs: null,
            AutoSendReadReceipts: true,
            AutoSendTypingIndicators: false,
            DateDeleted: null,
            Style: 43,
            LockChatName: false,
            LockChatIcon: false,
            LastReadMessageGuid: "msg-guid-999"
        );

        var entity = dto.ToEntity();
        Assert.Equal("iMessage;-;chat-round-trip", entity.Guid);
        Assert.Equal("My Group", entity.DisplayName);
        Assert.True(entity.IsPinned);
        Assert.True(entity.HasUnreadMessage);
        Assert.Equal(43, entity.Style);
    }

    [Fact]
    public void FcmData_FromServerResponse_MapsCorrectly()
    {
        var serverResponse = new FcmData(
            ProjectInfo: new FcmProjectInfo("my-project-123", "my-project-123.appspot.com", "https://my-project-123-default-rtdb.firebaseio.com"),
            Client: new List<FcmClient>
            {
                new(
                    ClientInfo: new FcmClientInfo("1:500464701389:android:abc123"),
                    OAuthClient: new List<FcmOAuthClient> { new("500464701389-xyz.apps.googleusercontent.com") },
                    ApiKey: new List<FcmApiKey> { new("AIzaSyTestKey123") }
                )
            }
        );

        var entity = serverResponse.ToEntity();

        Assert.Equal("my-project-123", entity.ProjectId);
        Assert.Equal("my-project-123.appspot.com", entity.StorageBucket);
        Assert.Equal("https://my-project-123-default-rtdb.firebaseio.com", entity.FirebaseUrl);
        Assert.Equal("AIzaSyTestKey123", entity.ApiKey);
        Assert.Equal("500464701389", entity.ClientId);
        Assert.Equal("1:500464701389:android:abc123", entity.ApplicationId);
        Assert.True(entity.IsValid);
    }
}
