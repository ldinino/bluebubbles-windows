using BlueBubbles.Core.Configuration;
using BlueBubbles.Core.Data.Entities;
using BlueBubbles.Core.Services;

namespace BlueBubbles.Windows.Tests;

public class ConversationMergerTests : IDisposable
{
    private readonly string _tempDir;

    public ConversationMergerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "bb_merge_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    // Alex's card carries both addresses (the merge case); John has a single number.
    private async Task<IContactResolverService> LoadContactsAsync()
    {
        var path = Path.Combine(_tempDir, "test.vcf");
        await File.WriteAllTextAsync(path, """
            BEGIN:VCARD
            VERSION:3.0
            FN:Alex Rivera
            TEL:+15550001234
            EMAIL:alex.rivera@example.com
            END:VCARD
            BEGIN:VCARD
            VERSION:3.0
            FN:John Doe
            TEL:+15551234567
            END:VCARD
            """);
        var svc = new ContactResolverService(new AppSettings());
        await svc.LoadFromVCardAsync(path);
        return svc;
    }

    private static ChatWithParticipants Chat(string guid, int id, string address, long date,
        params string[] extraAddresses)
    {
        var chat = new ChatEntity { Id = id, Guid = guid, LatestMessageDate = date };
        var handles = new List<HandleEntity> { new() { Address = address, Service = "iMessage" } };
        foreach (var a in extraAddresses)
            handles.Add(new HandleEntity { Address = a, Service = "iMessage" });
        return new ChatWithParticipants(chat, handles, "preview");
    }

    [Fact]
    public async Task Merge_TwoOneToOneChats_SameContact_FoldIntoOne()
    {
        var contacts = await LoadContactsAsync();
        // Source order is recency-sorted: the email thread received the more recent message.
        var emailChat = Chat("iMessage;-;alex.rivera@example.com", 1, "alex.rivera@example.com", 200);
        var phoneChat = Chat("iMessage;-;+15550001234", 2, "+15550001234", 100);

        var merged = ConversationMerger.Merge([emailChat, phoneChat], contacts);

        var m = Assert.Single(merged);
        Assert.True(m.IsMerged);
        // Primary identity prefers the phone (shown on the info bar)...
        Assert.Equal("iMessage;-;+15550001234", m.PrimaryChat.Guid);
        Assert.Equal("+15550001234", m.PrimaryAddress);
        // ...but the send target follows the most-recently-active thread (the email here).
        Assert.Equal("iMessage;-;alex.rivera@example.com", m.MostRecent.Chat.Guid);
        Assert.Equal(200, m.Timestamp);
        Assert.Equal(2, m.ConstituentGuids.Count);
        Assert.Contains(1, m.ConstituentChatIds);
        Assert.Contains(2, m.ConstituentChatIds);
        // Participants are phone-first for the "phone / email" details row.
        Assert.Equal("+15550001234", m.Participants[0].Address);
        Assert.Equal("alex.rivera@example.com", m.Participants[1].Address);
    }

    [Fact]
    public async Task Merge_SameNumberOverMultipleThreads_DedupesParticipant()
    {
        var contacts = await LoadContactsAsync();
        // An iMessage and an SMS thread for the same number (plus a differently-formatted copy) all merge,
        // but the participant list must not repeat the number.
        var imessage = Chat("iMessage;-;+15550001234", 1, "+15550001234", 100);
        var sms = Chat("SMS;-;+15550001234", 2, "(555) 000-1234", 90);
        var email = Chat("iMessage;-;alex.rivera@example.com", 3, "alex.rivera@example.com", 80);

        var m = Assert.Single(ConversationMerger.Merge([imessage, sms, email], contacts));

        Assert.True(m.IsMerged);
        Assert.Equal(3, m.ConstituentGuids.Count);   // all three threads still load
        Assert.Equal(2, m.Participants.Count);        // but the phone is listed once
        Assert.Equal("+15550001234", m.Participants[0].Address);
        Assert.Equal("alex.rivera@example.com", m.Participants[1].Address);
    }

    [Fact]
    public async Task Merge_SamePhoneOnlyTwoThreads_ShowsNumberOnce()
    {
        var contacts = await LoadContactsAsync();
        var a = Chat("iMessage;-;+15551234567", 1, "+15551234567", 100);
        var b = Chat("SMS;-;+15551234567", 2, "+15551234567", 90);

        var m = Assert.Single(ConversationMerger.Merge([a, b], contacts));

        Assert.True(m.IsMerged);
        Assert.Single(m.Participants);
        Assert.Equal("+15551234567", m.Participants[0].Address);
    }

    [Fact]
    public async Task Merge_DifferentContacts_NotMerged()
    {
        var contacts = await LoadContactsAsync();
        var alex = Chat("iMessage;-;+15550001234", 1, "+15550001234", 100);
        var john = Chat("iMessage;-;+15551234567", 2, "+15551234567", 90);

        var merged = ConversationMerger.Merge([alex, john], contacts);

        Assert.Equal(2, merged.Count);
        Assert.All(merged, m => Assert.False(m.IsMerged));
    }

    [Fact]
    public async Task Merge_GroupChat_NeverMerged()
    {
        var contacts = await LoadContactsAsync();
        // A group that happens to include Alex's two addresses is still a real group, not a merge.
        var group = Chat("iMessage;-;group", 1, "alex.rivera@example.com", 100, "+15550001234");

        var merged = ConversationMerger.Merge([group], contacts);

        var m = Assert.Single(merged);
        Assert.False(m.IsMerged);
        Assert.Equal(2, m.Participants.Count);
    }

    [Fact]
    public async Task Merge_UnknownAddresses_EachStandAlone()
    {
        var contacts = await LoadContactsAsync();
        var a = Chat("iMessage;-;+19998887777", 1, "+19998887777", 100);
        var b = Chat("iMessage;-;stranger@example.com", 2, "stranger@example.com", 90);

        var merged = ConversationMerger.Merge([a, b], contacts);

        Assert.Equal(2, merged.Count);
        Assert.All(merged, m => Assert.False(m.IsMerged));
    }

    [Fact]
    public async Task Merge_SingleKnownThread_Unchanged()
    {
        var contacts = await LoadContactsAsync();
        var phoneOnly = Chat("iMessage;-;+15550001234", 1, "+15550001234", 100);

        var merged = ConversationMerger.Merge([phoneOnly], contacts);

        var m = Assert.Single(merged);
        Assert.False(m.IsMerged);
        Assert.Equal("+15550001234", m.PrimaryAddress);
    }

    [Fact]
    public async Task Merge_AggregatesUnreadAndPinned_AcrossConstituents()
    {
        var contacts = await LoadContactsAsync();
        var emailChat = Chat("iMessage;-;alex.rivera@example.com", 1, "alex.rivera@example.com", 200);
        emailChat.Chat.HasUnreadMessage = true;
        var phoneChat = Chat("iMessage;-;+15550001234", 2, "+15550001234", 100);
        phoneChat.Chat.IsPinned = true;

        var m = Assert.Single(ConversationMerger.Merge([emailChat, phoneChat], contacts));

        Assert.True(m.HasUnread);
        Assert.True(m.IsPinned);
    }
}
