using BlueBubbles.Core.Data.Entities;
using BlueBubbles.Core.Models;
using BlueBubbles.Core.Services;

namespace BlueBubbles.Windows.Tests;

public class MessagesServiceTests
{
    private static (MessagesService Service, TestDbContextFactory Factory, SyncMockApiService Api) CreateService(
        SyncMockApiService? api = null)
    {
        var factory = TestDbContextFactory.Create();
        api ??= new SyncMockApiService([]);
        return (new MessagesService(factory, api, new MockChatsService()), factory, api);
    }

    private static ChatEntity SeedChat(TestDbContextFactory factory, string guid = "chat;+11234567890")
    {
        using var db = factory.CreateDbContext();
        var chat = new ChatEntity { Guid = guid };
        db.Chats.Add(chat);
        db.SaveChanges();
        return chat;
    }

    private static void SeedMessages(TestDbContextFactory factory, int chatId, int count, long startDate = 1000000)
    {
        using var db = factory.CreateDbContext();
        for (var i = 0; i < count; i++)
        {
            db.Messages.Add(new MessageEntity
            {
                Guid = $"msg-{i}",
                ChatId = chatId,
                Text = $"Message {i}",
                DateCreated = startDate + i * 1000,
                IsFromMe = i % 2 == 0
            });
        }
        db.SaveChanges();
    }

    [Fact]
    public async Task LoadMessages_ReturnsSortedOldestFirst()
    {
        var (svc, factory, _) = CreateService();
        var chat = SeedChat(factory);
        SeedMessages(factory, chat.Id, 5);

        var messages = await svc.LoadMessagesAsync(chat.Id);

        Assert.Equal(5, messages.Count);
        Assert.True(messages[0].DateCreated < messages[^1].DateCreated);
    }

    // Sending a photo with a caption from iOS produces two messages carrying the SAME timestamp.
    // Date alone leaves their order to chance, which showed the caption above the photo.
    [Fact]
    public async Task LoadMessages_SameTimestamp_OrdersByServerRowId()
    {
        var (svc, factory, _) = CreateService();
        var chat = SeedChat(factory);

        using (var db = factory.CreateDbContext())
        {
            // Inserted caption-first so a naive sort would keep the wrong order.
            db.Messages.Add(new MessageEntity
            {
                Guid = "caption", ChatId = chat.Id, Text = "look at this one",
                DateCreated = 1700000000000, OriginalRowId = 502, IsFromMe = true
            });
            db.Messages.Add(new MessageEntity
            {
                Guid = "photo", ChatId = chat.Id,
                DateCreated = 1700000000000, OriginalRowId = 501, IsFromMe = true
            });
            db.SaveChanges();
        }

        var messages = await svc.LoadMessagesAsync(chat.Id);

        Assert.Equal(["photo", "caption"], messages.Select(m => m.Guid));
    }

    [Fact]
    public async Task LoadMessages_LocalMessageWithNoRowId_SortsAfterServerMessagesAtSameTime()
    {
        var (svc, factory, _) = CreateService();
        var chat = SeedChat(factory);

        using (var db = factory.CreateDbContext())
        {
            db.Messages.Add(new MessageEntity
            {
                Guid = "pending", ChatId = chat.Id, Text = "just sent",
                DateCreated = 1700000000000, OriginalRowId = null, IsFromMe = true
            });
            db.Messages.Add(new MessageEntity
            {
                Guid = "acked", ChatId = chat.Id, Text = "already on the server",
                DateCreated = 1700000000000, OriginalRowId = 900, IsFromMe = true
            });
            db.SaveChanges();
        }

        var messages = await svc.LoadMessagesAsync(chat.Id);

        Assert.Equal(["acked", "pending"], messages.Select(m => m.Guid));
    }

    [Fact]
    public async Task LoadMessages_RespectsLimit()
    {
        var (svc, factory, _) = CreateService();
        var chat = SeedChat(factory);
        SeedMessages(factory, chat.Id, 20);

        var messages = await svc.LoadMessagesAsync(chat.Id, limit: 10);

        Assert.Equal(10, messages.Count);
    }

    [Fact]
    public async Task LoadMessages_ReturnsNewestFirst_WhenLimited()
    {
        var (svc, factory, _) = CreateService();
        var chat = SeedChat(factory);
        SeedMessages(factory, chat.Id, 20);

        var messages = await svc.LoadMessagesAsync(chat.Id, limit: 5);

        // Should return the 5 newest messages (reversed to oldest-first)
        Assert.Equal("msg-15", messages[0].Guid);
        Assert.Equal("msg-19", messages[^1].Guid);
    }

    [Fact]
    public async Task LoadMessages_BeforeDate_ReturnsOlderMessages()
    {
        var (svc, factory, _) = CreateService();
        var chat = SeedChat(factory);
        SeedMessages(factory, chat.Id, 10, startDate: 1000000);

        // Load messages before the 6th message (date = 1005000)
        var messages = await svc.LoadMessagesAsync(chat.Id, limit: 50, beforeDate: 1005000);

        Assert.Equal(5, messages.Count);
        Assert.True(messages.All(m => m.DateCreated < 1005000));
    }

    [Fact]
    public async Task LoadMessages_ExcludesDeleted()
    {
        var (svc, factory, _) = CreateService();
        var chat = SeedChat(factory);

        using var db = factory.CreateDbContext();
        db.Messages.Add(new MessageEntity
        {
            Guid = "msg-alive", ChatId = chat.Id, Text = "alive", DateCreated = 1000
        });
        db.Messages.Add(new MessageEntity
        {
            Guid = "msg-deleted", ChatId = chat.Id, Text = "deleted",
            DateCreated = 2000, DateDeleted = 3000
        });
        db.SaveChanges();

        var messages = await svc.LoadMessagesAsync(chat.Id);

        Assert.Single(messages);
        Assert.Equal("msg-alive", messages[0].Guid);
    }

    [Fact]
    public async Task LoadMessages_ExcludesReactions()
    {
        var (svc, factory, _) = CreateService();
        var chat = SeedChat(factory);

        using var db = factory.CreateDbContext();
        db.Messages.Add(new MessageEntity
        {
            Guid = "msg-normal", ChatId = chat.Id, Text = "hello", DateCreated = 1000
        });
        db.Messages.Add(new MessageEntity
        {
            Guid = "msg-reaction", ChatId = chat.Id, Text = "Loved",
            DateCreated = 2000, AssociatedMessageGuid = "msg-normal"
        });
        db.SaveChanges();

        var messages = await svc.LoadMessagesAsync(chat.Id);

        Assert.Single(messages);
        Assert.Equal("msg-normal", messages[0].Guid);
    }

    [Fact]
    public async Task LoadMessages_IncludesHandle()
    {
        var (svc, factory, _) = CreateService();
        var chat = SeedChat(factory);

        using var db = factory.CreateDbContext();
        var handle = new HandleEntity { Address = "+11234567890", Service = "iMessage" };
        db.Handles.Add(handle);
        db.SaveChanges();

        db.Messages.Add(new MessageEntity
        {
            Guid = "msg-with-handle", ChatId = chat.Id, Text = "hi",
            DateCreated = 1000, HandleId = handle.Id
        });
        db.SaveChanges();

        var messages = await svc.LoadMessagesAsync(chat.Id);

        Assert.Single(messages);
        Assert.NotNull(messages[0].Handle);
        Assert.Equal("+11234567890", messages[0].Handle!.Address);
    }

    [Fact]
    public async Task LoadMessages_OnlyReturnsChatMessages()
    {
        var (svc, factory, _) = CreateService();
        var chat1 = SeedChat(factory, "chat-1");
        var chat2 = SeedChat(factory, "chat-2");

        using var db = factory.CreateDbContext();
        db.Messages.Add(new MessageEntity
        {
            Guid = "msg-chat1", ChatId = chat1.Id, Text = "in chat 1", DateCreated = 1000
        });
        db.Messages.Add(new MessageEntity
        {
            Guid = "msg-chat2", ChatId = chat2.Id, Text = "in chat 2", DateCreated = 2000
        });
        db.SaveChanges();

        var messages = await svc.LoadMessagesAsync(chat1.Id);

        Assert.Single(messages);
        Assert.Equal("msg-chat1", messages[0].Guid);
    }

    [Fact]
    public async Task LoadMessages_EmptyChat_ReturnsEmptyList()
    {
        var (svc, factory, _) = CreateService();
        var chat = SeedChat(factory);

        var messages = await svc.LoadMessagesAsync(chat.Id);

        Assert.Empty(messages);
    }

    // ── SaveIncomingMessageAsync ──

    private static Message MakeMessage(string guid, string? text = null,
        Handle? handle = null, long? dateCreated = null) =>
        new(null, guid, null, null, text, null, null, 0,
            dateCreated ?? 1700000000000, null, null,
            false, false, false, null, 0, null, 0, null, null, null, null, null,
            handle, false, false, null, null, null, null, null, null, null, null,
            null, false, null, false, false, false);

    [Fact]
    public async Task SaveIncoming_DuplicateGuid_IsNoOp()
    {
        var (svc, factory, _) = CreateService();
        var chat = SeedChat(factory);

        using (var db = factory.CreateDbContext())
        {
            db.Messages.Add(new MessageEntity
            {
                Guid = "msg-dup", ChatId = chat.Id, Text = "existing", DateCreated = 1000
            });
            db.SaveChanges();
        }

        await svc.SaveIncomingMessageAsync(chat.Guid, MakeMessage("msg-dup", "new text"));

        using (var db = factory.CreateDbContext())
        {
            var msgs = db.Messages.Where(m => m.Guid == "msg-dup").ToList();
            Assert.Single(msgs);
            Assert.Equal("existing", msgs[0].Text);
        }
    }

    private static Attachment MakeAttachment(string guid, string? mime = "image/jpeg") =>
        new(42, guid, "public.jpeg", mime, false, "IMG_0001.JPEG", 1234, 1155, 662, false, null);

    // PUNCHLIST B2: the live socket path stored HasAttachments = true and zero attachment rows, so
    // an inbound image only appeared once a sync re-fetched the window.
    [Fact]
    public async Task SaveIncoming_WithAttachment_PersistsAttachmentRowOnTheMessage()
    {
        var (svc, factory, _) = CreateService();
        var chat = SeedChat(factory);

        var message = MakeMessage("msg-att", "look at this") with
        {
            HasAttachments = true,
            Attachments = [MakeAttachment("att-1")]
        };

        await svc.SaveIncomingMessageAsync(chat.Guid, message);

        using var db = factory.CreateDbContext();
        var saved = db.Messages.Single(m => m.Guid == "msg-att");
        var att = Assert.Single(db.Attachments.Where(a => a.Guid == "att-1").ToList());
        Assert.Equal(saved.Id, att.MessageId);
        Assert.Equal("image/jpeg", att.MimeType);
        Assert.Equal("IMG_0001.JPEG", att.TransferName);
        Assert.Equal(662, att.Width);
        Assert.Equal(1155, att.Height);
    }

    // The server is truth and a re-fetched window is authoritative for its range, so the socket save
    // must reuse the helper's GUID dedupe rather than adding a duplicate row for the same attachment.
    [Fact]
    public async Task SaveIncoming_AttachmentAlreadyStored_DoesNotDuplicateTheRow()
    {
        var (svc, factory, _) = CreateService();
        var chat = SeedChat(factory);

        using (var db = factory.CreateDbContext())
        {
            var existing = new MessageEntity { Guid = "msg-other", ChatId = chat.Id, DateCreated = 1000 };
            db.Messages.Add(existing);
            db.SaveChanges();
            db.Attachments.Add(new AttachmentEntity { Guid = "att-1", MessageId = existing.Id });
            db.SaveChanges();
        }

        var message = MakeMessage("msg-att", "look at this") with
        {
            HasAttachments = true,
            Attachments = [MakeAttachment("att-1")]
        };

        await svc.SaveIncomingMessageAsync(chat.Guid, message);

        using var db2 = factory.CreateDbContext();
        Assert.Single(db2.Attachments.Where(a => a.Guid == "att-1").ToList());
    }

    // The socket save and the server re-fetch share one attachment writer, so the bulk path needs a
    // guard of its own — otherwise a change made for the socket path can silently gut sync.
    [Fact]
    public async Task RefreshLatest_PersistsAttachmentRowsFromTheServerWindow()
    {
        var serverMsg = MakeMessage("msg-sync-att", "photo", null, 1000) with
        {
            HasAttachments = true,
            Attachments = [MakeAttachment("att-sync")]
        };
        var api = new SyncMockApiService([], new Dictionary<string, List<Message>>
        {
            ["chat-sa"] = [serverMsg]
        });
        var (svc, factory, _) = CreateService(api);
        var chat = SeedChat(factory, "chat-sa");

        await svc.RefreshLatestFromServerAsync(chat.Id, "chat-sa");

        using var db = factory.CreateDbContext();
        var msg = db.Messages.Single(m => m.Guid == "msg-sync-att");
        var att = Assert.Single(db.Attachments.Where(a => a.Guid == "att-sync").ToList());
        Assert.Equal(msg.Id, att.MessageId);
    }

    private static Attachment MakeAttachmentWithRow(int rowId, string guid, string transferName) =>
        new(rowId, guid, "public.jpeg", "image/jpeg", false, transferName, 52349, 275, 600, false, null);

    // PUNCHLIST B7: Apple rewrites an attachment's GUID as the transfer completes, so the socket
    // save sees a plain UUID and the later server re-fetch sees `at_0_<messageGuid>` for the SAME
    // server row. Deduping on the GUID alone stored it twice and the photo rendered twice.
    [Fact]
    public async Task SocketThenRefetch_SameServerRowUnderBothGuidForms_StoresOneRow()
    {
        var refetched = MakeMessage("msg-b7", "photo", null, 1000) with
        {
            HasAttachments = true,
            Attachments = [MakeAttachmentWithRow(9022, "at_0_msg-b7", "IMG_9015.png")]
        };
        var api = new SyncMockApiService([], new Dictionary<string, List<Message>>
        {
            ["chat-b7"] = [refetched]
        });
        var (svc, factory, _) = CreateService(api);
        var chat = SeedChat(factory, "chat-b7");

        // First sighting: the live socket payload, carrying Apple's pre-transfer plain GUID.
        await svc.SaveIncomingMessageAsync("chat-b7", MakeMessage("msg-b7", "photo", null, 1000) with
        {
            HasAttachments = true,
            Attachments = [MakeAttachmentWithRow(9022, "929F3235-90D6-48BB-8895-5CA8B753323C", "IMG_9015.png")]
        });

        // Second sighting: the same server row (originalROWID 9022) under the rewritten GUID.
        await svc.RefreshLatestFromServerAsync(chat.Id, "chat-b7");

        using var db = factory.CreateDbContext();
        var msg = db.Messages.Single(m => m.Guid == "msg-b7");
        var rows = db.Attachments.Where(a => a.MessageId == msg.Id).ToList();
        Assert.Single(rows);
        Assert.Equal(9022, rows[0].OriginalRowId);
    }

    // The identity rule must not over-collapse: a message legitimately can carry the same file
    // twice, and the server gives those two attachments distinct originalROWIDs (real cache,
    // message 421: at_0_/at_1_ with the same TransferName and TotalBytes but ROWIDs 7944/7951).
    [Fact]
    public async Task SaveIncoming_SameFileTwiceUnderDistinctServerRows_KeepsBothRows()
    {
        var (svc, factory, _) = CreateService();
        var chat = SeedChat(factory, "chat-b7b");

        await svc.SaveIncomingMessageAsync("chat-b7b", MakeMessage("msg-b7b", "gif", null, 1000) with
        {
            HasAttachments = true,
            Attachments =
            [
                MakeAttachmentWithRow(7944, "at_0_msg-b7b", "same.gif"),
                MakeAttachmentWithRow(7951, "at_1_msg-b7b", "same.gif")
            ]
        });

        using var db = factory.CreateDbContext();
        var msg = db.Messages.Single(m => m.Guid == "msg-b7b");
        var rows = db.Attachments.Where(a => a.MessageId == msg.Id).OrderBy(a => a.OriginalRowId).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal([7944, 7951], rows.Select(r => r.OriginalRowId).ToArray());
    }

    [Fact]
    public async Task RefreshLatest_PreservesLocalBookmark_StillAppliesServerText()
    {
        // IsBookmarked is client-owned; a re-fetch/true-up (which carries the server default false)
        // must not clear a locally-set bookmark, while server-owned fields like Text still update.
        var serverMsg = MakeMessage("msg-1", "hello updated", null, 1000);
        var api = new SyncMockApiService([], new Dictionary<string, List<Message>>
        {
            ["chat-bm"] = [serverMsg]
        });
        var (svc, factory, _) = CreateService(api);
        var chat = SeedChat(factory, "chat-bm");

        using (var db = factory.CreateDbContext())
        {
            db.Messages.Add(new MessageEntity
            {
                Guid = "msg-1", ChatId = chat.Id, Text = "hello", DateCreated = 1000, IsBookmarked = true
            });
            db.SaveChanges();
        }

        await svc.RefreshLatestFromServerAsync(chat.Id, "chat-bm");

        using var db2 = factory.CreateDbContext();
        var msg = db2.Messages.Single(m => m.Guid == "msg-1");
        Assert.True(msg.IsBookmarked);            // preserved
        Assert.Equal("hello updated", msg.Text);  // server-owned applied
    }

    [Fact]
    public async Task RefreshLatest_SoftDeletesMessageServerOmittedFromWindow()
    {
        // A delete we missed over the socket must converge: the server's returned page is authoritative
        // for its [oldest..newest] range, so a local message inside that span but absent from the page
        // is soft-deleted. Messages outside the span (older/newer) are left alone.
        var handle = MakeHandle("+15551234567");
        var serverWindow = new Dictionary<string, List<Message>>
        {
            ["chat-x"] = [
                MakeMessage("msg-2", "two", handle, 2000),
                MakeMessage("msg-3", "three", handle, 3000)
            ]
        };
        var api = new SyncMockApiService([], serverWindow);
        var (svc, factory, _) = CreateService(api);
        var chat = SeedChat(factory, "chat-x");

        using (var db = factory.CreateDbContext())
        {
            db.Messages.Add(new MessageEntity { Guid = "msg-1", ChatId = chat.Id, Text = "one", DateCreated = 1000 });    // older than window
            db.Messages.Add(new MessageEntity { Guid = "msg-2", ChatId = chat.Id, Text = "two", DateCreated = 2000 });    // in window, present
            db.Messages.Add(new MessageEntity { Guid = "msg-gone", ChatId = chat.Id, Text = "gone", DateCreated = 2500 });// in window, omitted -> delete
            db.Messages.Add(new MessageEntity { Guid = "msg-4", ChatId = chat.Id, Text = "four", DateCreated = 4000 });   // newer than window
            db.SaveChanges();
        }

        await svc.RefreshLatestFromServerAsync(chat.Id, "chat-x");

        using var db2 = factory.CreateDbContext();
        Assert.Null(db2.Messages.Single(m => m.Guid == "msg-1").DateDeleted);       // outside (older) -> survives
        Assert.Null(db2.Messages.Single(m => m.Guid == "msg-2").DateDeleted);       // present -> survives
        Assert.NotNull(db2.Messages.Single(m => m.Guid == "msg-gone").DateDeleted); // omitted from covered span -> deleted
        Assert.Null(db2.Messages.Single(m => m.Guid == "msg-4").DateDeleted);       // outside (newer) -> survives
    }

    [Fact]
    public async Task SaveIncoming_NewHandle_CreatesHandleEntity()
    {
        var (svc, factory, _) = CreateService();
        var chat = SeedChat(factory);

        var handle = new Handle(0, "+15551234567", "iMessage", "US", null, null, null, null, null);
        await svc.SaveIncomingMessageAsync(chat.Guid, MakeMessage("msg-h1", "hello", handle));

        using var db = factory.CreateDbContext();
        var savedHandle = db.Handles.FirstOrDefault(h => h.Address == "+15551234567");
        Assert.NotNull(savedHandle);
        Assert.Equal("iMessage", savedHandle.Service);

        var savedMsg = db.Messages.First(m => m.Guid == "msg-h1");
        Assert.Equal(savedHandle.Id, savedMsg.HandleId);
    }

    [Fact]
    public async Task SaveIncoming_ExistingHandle_ReusesIt()
    {
        var (svc, factory, _) = CreateService();
        var chat = SeedChat(factory);

        using (var db = factory.CreateDbContext())
        {
            db.Handles.Add(new HandleEntity { Address = "+15551234567", Service = "iMessage" });
            db.SaveChanges();
        }

        var handle = new Handle(0, "+15551234567", "iMessage", null, null, null, null, null, null);
        await svc.SaveIncomingMessageAsync(chat.Guid, MakeMessage("msg-reuse", "hello", handle));

        using var db2 = factory.CreateDbContext();
        Assert.Equal(1, db2.Handles.Count());
        var savedMsg = db2.Messages.First(m => m.Guid == "msg-reuse");
        Assert.NotNull(savedMsg.HandleId);
    }

    [Fact]
    public async Task SaveIncoming_UnknownChat_IsNoOp()
    {
        var (svc, factory, _) = CreateService();

        await svc.SaveIncomingMessageAsync("nonexistent-chat", MakeMessage("msg-orphan", "hello"));

        using var db = factory.CreateDbContext();
        Assert.Empty(db.Messages);
    }

    [Fact]
    public async Task SaveIncoming_DoesNotUpdateLatestMessageDateOrHasUnread()
    {
        var (svc, factory, _) = CreateService();

        using (var db = factory.CreateDbContext())
        {
            db.Chats.Add(new ChatEntity
            {
                Guid = "chat-noupdate", LatestMessageDate = 1000, HasUnreadMessage = false
            });
            db.SaveChanges();
        }

        await svc.SaveIncomingMessageAsync("chat-noupdate",
            MakeMessage("msg-noupdate", "hello", dateCreated: 9999));

        using (var db = factory.CreateDbContext())
        {
            var chat = db.Chats.First(c => c.Guid == "chat-noupdate");
            Assert.Equal(1000, chat.LatestMessageDate);
            Assert.False(chat.HasUnreadMessage);
        }
    }

    [Fact]
    public async Task SaveIncoming_ConcurrentSaves_AllSerialized()
    {
        var (svc, factory, _) = CreateService();
        var chat = SeedChat(factory);

        var tasks = Enumerable.Range(0, 10)
            .Select(i => svc.SaveIncomingMessageAsync(
                chat.Guid, MakeMessage($"msg-concurrent-{i}", $"text {i}")))
            .ToArray();

        await Task.WhenAll(tasks);

        using var db = factory.CreateDbContext();
        Assert.Equal(10, db.Messages.Count(m => m.ChatId == chat.Id));
    }

    // ── FetchOlderMessagesFromServerAsync ──

    private static Handle MakeHandle(string address, string service = "iMessage") =>
        new(0, address, service, null, null, null, null, null, null);

    [Fact]
    public async Task FetchOlder_NullWatermark_ReturnsEmpty()
    {
        var (svc, factory, api) = CreateService();
        var chat = SeedChat(factory);
        // OldestSyncedMessageDate is null by default

        var result = await svc.FetchOlderMessagesFromServerAsync(chat.Id, chat.Guid);

        Assert.Empty(result);
        Assert.Empty(api.ChatMessageCalls);
    }

    [Fact]
    public async Task FetchOlder_ExhaustedWatermark_ReturnsEmpty()
    {
        var (svc, factory, api) = CreateService();
        using (var db = factory.CreateDbContext())
        {
            var chat = new ChatEntity { Guid = "chat-exhausted", OldestSyncedMessageDate = 0 };
            db.Chats.Add(chat);
            db.SaveChanges();
        }

        using (var db = factory.CreateDbContext())
        {
            var chat = db.Chats.First(c => c.Guid == "chat-exhausted");
            var result = await svc.FetchOlderMessagesFromServerAsync(chat.Id, chat.Guid);
            Assert.Empty(result);
        }

        Assert.Empty(api.ChatMessageCalls);
    }

    [Fact]
    public async Task FetchOlder_PastOneYear_ReturnsEmpty()
    {
        var twoYearsAgo = DateTimeOffset.UtcNow.AddDays(-730).ToUnixTimeMilliseconds();
        var (svc, factory, api) = CreateService();
        using (var db = factory.CreateDbContext())
        {
            db.Chats.Add(new ChatEntity
            {
                Guid = "chat-old",
                OldestSyncedMessageDate = twoYearsAgo
            });
            db.SaveChanges();
        }

        using (var db = factory.CreateDbContext())
        {
            var chat = db.Chats.First(c => c.Guid == "chat-old");
            var result = await svc.FetchOlderMessagesFromServerAsync(chat.Id, chat.Guid);
            Assert.Empty(result);
        }

        Assert.Empty(api.ChatMessageCalls);
    }

    [Fact]
    public async Task FetchOlder_ServerReturnsMessages_SavesAndUpdatesWatermark()
    {
        var handle = MakeHandle("+15551234567");
        var oldestSynced = DateTimeOffset.UtcNow.AddDays(-5).ToUnixTimeMilliseconds();
        var olderMsgDate = oldestSynced - 60000;

        var chatMessages = new Dictionary<string, List<Message>>
        {
            ["chat-fetch"] = [MakeMessage("server-msg-1", "older msg", handle, olderMsgDate)]
        };
        var api = new SyncMockApiService([], chatMessages);
        var (svc, factory, _) = CreateService(api);

        using (var db = factory.CreateDbContext())
        {
            db.Chats.Add(new ChatEntity
            {
                Guid = "chat-fetch",
                OldestSyncedMessageDate = oldestSynced
            });
            db.SaveChanges();
        }

        List<MessageEntity> result;
        using (var db = factory.CreateDbContext())
        {
            var chat = db.Chats.First(c => c.Guid == "chat-fetch");
            result = await svc.FetchOlderMessagesFromServerAsync(chat.Id, chat.Guid);
        }

        Assert.Single(result);

        using (var db = factory.CreateDbContext())
        {
            Assert.Equal(1, db.Messages.Count());
            var chat = db.Chats.First(c => c.Guid == "chat-fetch");
            Assert.Equal(olderMsgDate, chat.OldestSyncedMessageDate);
        }

        var call = Assert.Single(api.ChatMessageCalls);
        Assert.Equal(oldestSynced, call.Before);
    }

    // ── EnsureChatHydratedAsync (on-open backfill safety net) ──

    [Fact]
    public async Task EnsureHydrated_EmptyChat_FetchesAndSaves()
    {
        var handle = MakeHandle("+15551234567");
        var msgDate = DateTimeOffset.UtcNow.AddDays(-3).ToUnixTimeMilliseconds();
        var chatMessages = new Dictionary<string, List<Message>>
        {
            ["chat-hydrate"] = [
                MakeMessage("h-1", "hi", handle, msgDate),
                MakeMessage("h-2", "there", handle, msgDate + 1000)
            ]
        };
        var api = new SyncMockApiService([], chatMessages);
        var (svc, factory, _) = CreateService(api);
        var chat = SeedChat(factory, "chat-hydrate");

        var hydrated = await svc.EnsureChatHydratedAsync(chat.Id, chat.Guid);

        Assert.True(hydrated);
        using var db = factory.CreateDbContext();
        Assert.Equal(2, db.Messages.Count(m => m.ChatId == chat.Id));
        Assert.Equal(msgDate, db.Chats.First(c => c.Id == chat.Id).OldestSyncedMessageDate);
    }

    [Fact]
    public async Task EnsureHydrated_ChatWithMessages_IsNoOp()
    {
        var api = new SyncMockApiService([]);
        var (svc, factory, _) = CreateService(api);
        var chat = SeedChat(factory, "chat-has-msgs");
        SeedMessages(factory, chat.Id, 3);

        var hydrated = await svc.EnsureChatHydratedAsync(chat.Id, chat.Guid);

        Assert.False(hydrated);
        Assert.Empty(api.ChatMessageCalls);   // never hit the server
    }

    [Fact]
    public async Task EnsureHydrated_ServerEmpty_ReturnsFalse()
    {
        var api = new SyncMockApiService([]);
        var (svc, factory, _) = CreateService(api);
        var chat = SeedChat(factory, "chat-still-empty");

        var hydrated = await svc.EnsureChatHydratedAsync(chat.Id, chat.Guid);

        Assert.False(hydrated);
        using var db = factory.CreateDbContext();
        Assert.Empty(db.Messages);
    }

    [Fact]
    public async Task FetchOlder_ServerReturnsEmpty_SetsExhaustedSentinel()
    {
        var oldestSynced = DateTimeOffset.UtcNow.AddDays(-5).ToUnixTimeMilliseconds();

        var api = new SyncMockApiService([]);
        var (svc, factory, _) = CreateService(api);

        using (var db = factory.CreateDbContext())
        {
            db.Chats.Add(new ChatEntity
            {
                Guid = "chat-empty-server",
                OldestSyncedMessageDate = oldestSynced
            });
            db.SaveChanges();
        }

        using (var db = factory.CreateDbContext())
        {
            var chat = db.Chats.First(c => c.Guid == "chat-empty-server");
            var result = await svc.FetchOlderMessagesFromServerAsync(chat.Id, chat.Guid);
            Assert.Empty(result);
        }

        using (var db = factory.CreateDbContext())
        {
            var chat = db.Chats.First(c => c.Guid == "chat-empty-server");
            Assert.Equal(0, chat.OldestSyncedMessageDate);
        }
    }
}
