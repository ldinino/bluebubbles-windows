using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BlueBubbles.Core.Data.Entities;

[Table("Chats")]
public class ChatEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required]
    [MaxLength(256)]
    public string Guid { get; set; } = string.Empty;

    [MaxLength(256)]
    public string? ChatIdentifier { get; set; }

    [MaxLength(256)]
    public string? DisplayName { get; set; }

    public bool IsArchived { get; set; }

    public bool IsPinned { get; set; }

    public int? PinIndex { get; set; }

    public bool HasUnreadMessage { get; set; }

    [MaxLength(64)]
    public string? Service { get; set; }

    [MaxLength(64)]
    public string? MuteType { get; set; }

    [MaxLength(256)]
    public string? MuteArgs { get; set; }

    public bool? AutoSendReadReceipts { get; set; }

    public bool? AutoSendTypingIndicators { get; set; }

    public long? DateDeleted { get; set; }

    public int? Style { get; set; }

    public bool LockChatName { get; set; }

    public bool LockChatIcon { get; set; }

    [MaxLength(256)]
    public string? LastReadMessageGuid { get; set; }

    [MaxLength(1024)]
    public string? CustomAvatarPath { get; set; }

    public long? LatestMessageDate { get; set; }

    public long? OldestSyncedMessageDate { get; set; }

    public ICollection<ChatParticipant> ChatParticipants { get; set; } = new List<ChatParticipant>();
    public ICollection<MessageEntity> Messages { get; set; } = new List<MessageEntity>();
}
