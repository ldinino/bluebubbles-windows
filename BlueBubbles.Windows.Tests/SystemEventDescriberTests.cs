using BlueBubbles.Core.Data.Entities;
using BlueBubbles.Core.Utils;

namespace BlueBubbles.Windows.Tests;

/// <summary>
/// B12: group/system event rows carry no text, so the timeline has to recognise them and render a
/// description instead of an empty bubble. The recognition + wording live in Core so they are
/// reachable from this suite; the WinUI item that consumes them is not.
/// </summary>
public class SystemEventDescriberTests
{
    private static MessageEntity Row(
        int itemType = 0,
        int groupActionType = 0,
        string? groupTitle = null,
        bool isFromMe = false,
        string? address = "+15550100001") =>
        new()
        {
            Guid = "guid-system-event",
            Text = null,
            ItemType = itemType,
            GroupActionType = groupActionType,
            GroupTitle = groupTitle,
            IsFromMe = isFromMe,
            Handle = address is null
                ? null
                : new HandleEntity { Address = address, Service = "iMessage" }
        };

    [Fact]
    public void OrdinaryMessageIsNotASystemEvent()
    {
        Assert.False(SystemEventDescriber.IsSystemEvent(Row()));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 0)]
    [InlineData(3, 0)]
    [InlineData(99, 0)]
    public void NonZeroItemTypeIsASystemEvent(int itemType, int groupActionType)
    {
        Assert.True(SystemEventDescriber.IsSystemEvent(Row(itemType, groupActionType)));
    }

    [Fact]
    public void NonZeroGroupActionTypeAloneIsASystemEvent()
    {
        Assert.True(SystemEventDescriber.IsSystemEvent(Row(itemType: 0, groupActionType: 1)));
    }

    [Fact]
    public void EverySystemEventProducesNonEmptyText()
    {
        // The whole point of B12: these rows have NULL text, so the description must never be blank.
        for (var itemType = 0; itemType <= 4; itemType++)
        {
            for (var action = 0; action <= 2; action++)
            {
                var row = Row(itemType, action);
                if (!SystemEventDescriber.IsSystemEvent(row)) continue;
                Assert.False(string.IsNullOrWhiteSpace(SystemEventDescriber.Describe(row)));
            }
        }
    }

    [Fact]
    public void NamedConversationUsesTheResolvedContactNameAndTitle()
    {
        var text = SystemEventDescriber.Describe(
            Row(itemType: 2, groupTitle: "Trivia Night"),
            resolveSender: _ => "Dana Example");

        Assert.Equal("Dana Example named the conversation \"Trivia Night\".", text);
    }

    [Fact]
    public void SelfLabelIsCallerSuppliedSoTheUiAndTheExportCanDiffer()
    {
        var row = Row(itemType: 3, isFromMe: true);

        Assert.Equal("Me left the conversation.", SystemEventDescriber.Describe(row));
        Assert.Equal("You left the conversation.",
            SystemEventDescriber.Describe(row, resolveSender: null, selfLabel: "You"));
    }

    [Fact]
    public void UnknownEventIsLabelledGenericallyRatherThanGuessed()
    {
        var text = SystemEventDescriber.Describe(Row(itemType: 7, groupActionType: 4));

        Assert.Contains("Unrecognised system event", text);
        Assert.Contains("itemType 7", text);
        Assert.Contains("groupActionType 4", text);
    }
}
