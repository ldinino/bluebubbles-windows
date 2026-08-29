using BlueBubbles.Core.Data.Entities;
using BlueBubbles.Core.Utils;

namespace BlueBubbles.Windows.ViewModels;

/// <summary>A group/system event (name change, add, remove, leave) in the message timeline. Not a
/// chat bubble: no sender side, tail, avatar, reactions or attachments — just a centred line.</summary>
public class SystemEventViewModel
{
    public string MessageGuid { get; }
    public long DateCreated { get; }
    public string Text { get; }

    public SystemEventViewModel(MessageEntity message, Func<string, string>? resolveSender)
    {
        MessageGuid = message.Guid;
        DateCreated = message.DateCreated ?? 0;
        Text = SystemEventDescriber.Describe(message, resolveSender, selfLabel: "You");
    }
}
