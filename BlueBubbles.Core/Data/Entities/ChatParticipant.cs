using System.ComponentModel.DataAnnotations.Schema;

namespace BlueBubbles.Core.Data.Entities;

[Table("ChatParticipants")]
public class ChatParticipant
{
    public int ChatId { get; set; }
    public ChatEntity Chat { get; set; } = null!;

    public int HandleId { get; set; }
    public HandleEntity Handle { get; set; } = null!;
}
