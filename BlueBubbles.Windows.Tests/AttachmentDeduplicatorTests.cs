using BlueBubbles.Core.Data.Entities;
using BlueBubbles.Core.Services;

namespace BlueBubbles.Windows.Tests;

// PUNCHLIST B7. Caches written before the identity fix already hold the same server attachment
// twice under two Apple GUID forms, and 0.23.0 is published, so the write-side fix alone leaves
// every existing install rendering photos twice.
public class AttachmentDeduplicatorTests
{
    private static (TestDbContextFactory Factory, int MessageId) SeedMessage(string guid = "msg-1")
    {
        var factory = TestDbContextFactory.Create();
        using var db = factory.CreateDbContext();
        var chat = new ChatEntity { Guid = "chat-1" };
        db.Chats.Add(chat);
        db.SaveChanges();
        var msg = new MessageEntity { Guid = guid, ChatId = chat.Id, DateCreated = 1000 };
        db.Messages.Add(msg);
        db.SaveChanges();
        return (factory, msg.Id);
    }

    private static AttachmentEntity Row(int messageId, int rowId, string guid, int? width = null) =>
        new()
        {
            Guid = guid,
            MessageId = messageId,
            OriginalRowId = rowId,
            TransferName = "IMG_9015.png",
            MimeType = "image/png",
            TotalBytes = 52349,
            Width = width,
            Height = width is null ? null : 275
        };

    [Fact]
    public async Task Collapse_SameServerRowUnderTwoGuidForms_KeepsTheMostRecentlyWrittenRow()
    {
        var (factory, messageId) = SeedMessage();
        using (var seed = factory.CreateDbContext())
        {
            seed.Attachments.Add(Row(messageId, 9022, "929F3235-90D6-48BB-8895-5CA8B753323C"));
            seed.SaveChanges();
            seed.Attachments.Add(Row(messageId, 9022, "at_0_msg-1", 600));
            seed.SaveChanges();
        }

        using var db = factory.CreateDbContext();
        var removed = await AttachmentDeduplicator.CollapseDuplicatesAsync(db);

        Assert.Equal(1, removed);
        var survivor = Assert.Single(db.Attachments.Where(a => a.MessageId == messageId).ToList());
        // The later row carries the GUID the server is currently serving, so it is the one that
        // can still be downloaded.
        Assert.Equal("at_0_msg-1", survivor.Guid);
        Assert.Equal(600, survivor.Width);
    }

    [Fact]
    public async Task Collapse_RunTwice_SecondRunIsANoOp()
    {
        var (factory, messageId) = SeedMessage();
        using (var seed = factory.CreateDbContext())
        {
            seed.Attachments.Add(Row(messageId, 9022, "plain-guid"));
            seed.SaveChanges();
            seed.Attachments.Add(Row(messageId, 9022, "at_0_msg-1", 600));
            seed.SaveChanges();
        }

        using var db = factory.CreateDbContext();
        Assert.Equal(1, await AttachmentDeduplicator.CollapseDuplicatesAsync(db));
        Assert.Equal(0, await AttachmentDeduplicator.CollapseDuplicatesAsync(db));
        Assert.Single(db.Attachments.Where(a => a.MessageId == messageId).ToList());
    }

    [Fact]
    public async Task Collapse_SameFileUnderDistinctServerRows_KeepsBoth()
    {
        var (factory, messageId) = SeedMessage();
        using (var seed = factory.CreateDbContext())
        {
            seed.Attachments.Add(Row(messageId, 7944, "at_0_msg-1", 300));
            seed.Attachments.Add(Row(messageId, 7951, "at_1_msg-1"));
            seed.SaveChanges();
        }

        using var db = factory.CreateDbContext();
        Assert.Equal(0, await AttachmentDeduplicator.CollapseDuplicatesAsync(db));
        Assert.Equal(2, db.Attachments.Count(a => a.MessageId == messageId));
    }

    // OriginalRowId is the identity, so the same ROWID on two DIFFERENT messages is two different
    // attachments and must survive — collapsing on the ROWID alone would delete real data.
    [Fact]
    public async Task Collapse_SameServerRowOnDifferentMessages_KeepsBoth()
    {
        var (factory, firstId) = SeedMessage("msg-a");
        int secondId;
        using (var seed = factory.CreateDbContext())
        {
            var chat = seed.Chats.First();
            var second = new MessageEntity { Guid = "msg-b", ChatId = chat.Id, DateCreated = 2000 };
            seed.Messages.Add(second);
            seed.SaveChanges();
            secondId = second.Id;
            seed.Attachments.Add(Row(firstId, 9022, "at_0_msg-a"));
            seed.Attachments.Add(Row(secondId, 9022, "at_0_msg-b"));
            seed.SaveChanges();
        }

        using var db = factory.CreateDbContext();
        Assert.Equal(0, await AttachmentDeduplicator.CollapseDuplicatesAsync(db));
        Assert.Equal(2, db.Attachments.Count());
    }

    [Fact]
    public async Task Collapse_RowsWithoutAServerRowId_AreLeftAlone()
    {
        var (factory, messageId) = SeedMessage();
        using (var seed = factory.CreateDbContext())
        {
            seed.Attachments.Add(new AttachmentEntity
            { Guid = "a", MessageId = messageId, OriginalRowId = null, TotalBytes = 1 });
            seed.Attachments.Add(new AttachmentEntity
            { Guid = "b", MessageId = messageId, OriginalRowId = null, TotalBytes = 1 });
            seed.SaveChanges();
        }

        using var db = factory.CreateDbContext();
        Assert.Equal(0, await AttachmentDeduplicator.CollapseDuplicatesAsync(db));
        Assert.Equal(2, db.Attachments.Count(a => a.MessageId == messageId));
    }
}
