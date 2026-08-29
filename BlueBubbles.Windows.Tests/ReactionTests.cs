using System.Text.Json;
using BlueBubbles.Core.Data.Entities;
using BlueBubbles.Core.Models;
using BlueBubbles.Core.Services;
using BlueBubbles.Core.Utils;

namespace BlueBubbles.Windows.Tests;

public class ReactionTypesTests
{
    [Theory]
    [InlineData("ABC-123", "ABC-123")]            // bare guid
    [InlineData("p:0/ABC-123", "ABC-123")]        // part prefix
    [InlineData("p:2/ABC-123", "ABC-123")]
    [InlineData("bp:0/ABC-123", "ABC-123")]       // bp prefix
    [InlineData(null, null)]
    public void NormalizeAssociatedGuid_StripsPartPrefix(string? input, string? expected)
    {
        Assert.Equal(expected, ReactionTypes.NormalizeAssociatedGuid(input));
    }

    [Theory]
    [InlineData("p:0/ABC", null, 0)]
    [InlineData("p:3/ABC", null, 3)]
    [InlineData("bp:0/ABC", null, 0)]
    [InlineData("ABC", null, 0)]                   // no prefix → default 0
    [InlineData("p:0/ABC", 5, 5)]                  // explicit part wins
    public void ResolveAssociatedPart_PrefersExplicit_ThenParses(string raw, int? explicitPart, int expected)
    {
        Assert.Equal(expected, ReactionTypes.ResolveAssociatedPart(raw, explicitPart));
    }

    [Theory]
    [InlineData("love", true)]
    [InlineData("emphasize", true)]
    [InlineData("-love", true)]                    // removal is still a (known) reaction
    [InlineData("sticker", false)]
    [InlineData(null, false)]
    public void IsReaction_RecognizesKnownTypes(string? type, bool expected)
    {
        Assert.Equal(expected, ReactionTypes.IsReaction(type));
    }

    [Fact]
    public void IsRemoval_And_BaseType()
    {
        Assert.True(ReactionTypes.IsRemoval("-love"));
        Assert.False(ReactionTypes.IsRemoval("love"));
        Assert.Equal("love", ReactionTypes.BaseType("-love"));
        Assert.Equal("love", ReactionTypes.BaseType("love"));
    }

    [Fact]
    public void ToEmoji_MapsAllSixTypes()
    {
        foreach (var type in ReactionTypes.All)
            Assert.False(string.IsNullOrEmpty(ReactionTypes.ToEmoji(type)));

        // Removal marker resolves to the base emoji.
        Assert.Equal(ReactionTypes.ToEmoji("love"), ReactionTypes.ToEmoji("-love"));
    }
}

public class ReactionSummarizerTests
{
    private static ReactionRecord R(string type, bool isFromMe, string? addr, long date, string? guid = null)
        => new(guid ?? Guid.NewGuid().ToString(), type, isFromMe, addr, date);

    [Fact]
    public void SingleReaction_ProducesOneBadge()
    {
        var summary = ReactionSummarizer.Summarize([R("love", false, "+1", 100)]);

        var badge = Assert.Single(summary);
        Assert.Equal("love", badge.ReactionType);
        Assert.Equal(1, badge.Count);
        Assert.False(badge.IncludesMe);
    }

    [Fact]
    public void SameTypeFromMultipleReactors_CountsEach()
    {
        var summary = ReactionSummarizer.Summarize([
            R("like", false, "+1", 100),
            R("like", false, "+2", 110),
            R("like", true, null, 120),
        ]);

        var badge = Assert.Single(summary);
        Assert.Equal(3, badge.Count);
        Assert.True(badge.IncludesMe);
    }

    [Fact]
    public void LatestReactionPerReactorWins()
    {
        // Same reactor switches love → like; only like should remain.
        var summary = ReactionSummarizer.Summarize([
            R("love", false, "+1", 100),
            R("like", false, "+1", 200),
        ]);

        var badge = Assert.Single(summary);
        Assert.Equal("like", badge.ReactionType);
        Assert.Equal(1, badge.Count);
    }

    [Fact]
    public void Removal_CancelsReactorReaction()
    {
        var summary = ReactionSummarizer.Summarize([
            R("love", true, null, 100),
            R("-love", true, null, 200),
        ]);

        Assert.Empty(summary);
    }

    [Fact]
    public void Removal_ThenReApply_ShowsReaction()
    {
        var summary = ReactionSummarizer.Summarize([
            R("love", true, null, 100),
            R("-love", true, null, 200),
            R("love", true, null, 300),
        ]);

        var badge = Assert.Single(summary);
        Assert.Equal("love", badge.ReactionType);
        Assert.True(badge.IncludesMe);
    }

    [Fact]
    public void MixedTypes_AreGroupedInCanonicalOrder()
    {
        var summary = ReactionSummarizer.Summarize([
            R("question", false, "+1", 100),
            R("love", false, "+2", 110),
            R("like", false, "+3", 120),
        ]);

        Assert.Equal(3, summary.Count);
        // Canonical order is love, like, dislike, laugh, emphasize, question.
        Assert.Equal("love", summary[0].ReactionType);
        Assert.Equal("like", summary[1].ReactionType);
        Assert.Equal("question", summary[2].ReactionType);
    }

    [Fact]
    public void NonReactionAssociations_AreIgnored()
    {
        var summary = ReactionSummarizer.Summarize([
            R("sticker", false, "+1", 100),
            R("love", false, "+1", 110),
        ]);

        var badge = Assert.Single(summary);
        Assert.Equal("love", badge.ReactionType);
    }

    [Fact]
    public void SelfReaction_ReturnsMyActiveType()
    {
        Assert.Equal("love", ReactionSummarizer.SelfReaction([
            R("love", true, null, 100),
            R("like", false, "+1", 110),
        ]));
    }

    [Fact]
    public void SelfReaction_NullAfterRemoval()
    {
        Assert.Null(ReactionSummarizer.SelfReaction([
            R("love", true, null, 100),
            R("-love", true, null, 200),
        ]));
    }

    [Fact]
    public void SelfReaction_NullWhenOnlyOthersReacted()
    {
        Assert.Null(ReactionSummarizer.SelfReaction([R("love", false, "+1", 100)]));
    }
}

public class MessagesServiceReactionTests
{
    private static MessagesService CreateService(TestDbContextFactory factory)
        => new(factory, new SyncMockApiService([]), new MockChatsService());

    private static ChatEntity SeedChatWithParent(TestDbContextFactory factory, string chatGuid, string parentGuid)
    {
        using var db = factory.CreateDbContext();
        var chat = new ChatEntity { Guid = chatGuid };
        db.Chats.Add(chat);
        db.SaveChanges();

        db.Messages.Add(new MessageEntity
        {
            Guid = parentGuid,
            ChatId = chat.Id,
            Text = "Original message",
            DateCreated = 1_000,
            IsFromMe = true
        });
        db.SaveChanges();
        return chat;
    }

    private static Message MakeReaction(string guid, string associatedGuid, string type,
        bool isFromMe, string? address, long date)
    {
        var handleJson = address is null
            ? "null"
            : $$"""{ "originalROWID": 1, "address": "{{address}}", "service": "iMessage" }""";

        var json = $$"""
        {
            "guid": "{{guid}}",
            "associatedMessageGuid": "{{associatedGuid}}",
            "associatedMessageType": "{{type}}",
            "isFromMe": {{(isFromMe ? "true" : "false")}},
            "dateCreated": {{date}},
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
            "handle": {{handleJson}}
        }
        """;
        return JsonSerializer.Deserialize<Message>(json, JsonDefaults.Options)!;
    }

    [Fact]
    public async Task SaveReaction_NormalizesGuid_AndFlagsParent()
    {
        var factory = TestDbContextFactory.Create();
        var svc = CreateService(factory);
        SeedChatWithParent(factory, "chat;+1", "parent-guid");

        // Server sends the associated GUID with a part prefix.
        var reaction = MakeReaction("react-1", "p:0/parent-guid", "love", isFromMe: false, "+15550001111", 2_000);
        await svc.SaveReactionAsync("chat;+1", reaction);

        await using var db = factory.CreateDbContext();
        var stored = db.Messages.Single(m => m.Guid == "react-1");
        Assert.Equal("parent-guid", stored.AssociatedMessageGuid);   // prefix stripped
        Assert.Equal(0, stored.AssociatedMessagePart);
        Assert.Equal("love", stored.AssociatedMessageType);

        var parent = db.Messages.Single(m => m.Guid == "parent-guid");
        Assert.True(parent.HasReactions);
    }

    [Fact]
    public async Task LoadReactions_ReturnsReactionsForParents_WithHandle()
    {
        var factory = TestDbContextFactory.Create();
        var svc = CreateService(factory);
        SeedChatWithParent(factory, "chat;+1", "parent-guid");

        await svc.SaveReactionAsync("chat;+1",
            MakeReaction("react-love", "parent-guid", "love", false, "+15550001111", 2_000));
        await svc.SaveReactionAsync("chat;+1",
            MakeReaction("react-like", "parent-guid", "like", true, null, 2_100));

        var reactions = await svc.LoadReactionsAsync(["parent-guid"]);

        Assert.Equal(2, reactions.Count);
        Assert.Contains(reactions, r => r.AssociatedMessageType == "love" && r.Handle?.Address == "+15550001111");
        Assert.Contains(reactions, r => r.AssociatedMessageType == "like" && r.IsFromMe);
    }

    [Fact]
    public async Task LoadReactions_EmptyInput_ReturnsEmpty()
    {
        var factory = TestDbContextFactory.Create();
        var svc = CreateService(factory);

        Assert.Empty(await svc.LoadReactionsAsync([]));
    }
}
