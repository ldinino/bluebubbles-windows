using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BlueBubbles.Core.Data.Entities;

[Table("Messages")]
public class MessageEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int? OriginalRowId { get; set; }

    [Required]
    [MaxLength(256)]
    public string Guid { get; set; } = string.Empty;

    public int? HandleId { get; set; }
    public HandleEntity? Handle { get; set; }

    public int? OtherHandle { get; set; }

    public string? Text { get; set; }

    [MaxLength(512)]
    public string? Subject { get; set; }

    [MaxLength(10)]
    public string? Country { get; set; }

    public int Error { get; set; }

    public long? DateCreated { get; set; }

    public long? DateRead { get; set; }

    public long? DateDelivered { get; set; }

    public bool IsDelivered { get; set; }

    public bool IsFromMe { get; set; }

    public bool HasDdResults { get; set; }

    public long? DatePlayed { get; set; }

    public int ItemType { get; set; }

    [MaxLength(512)]
    public string? GroupTitle { get; set; }

    public int GroupActionType { get; set; }

    [MaxLength(256)]
    public string? BalloonBundleId { get; set; }

    [MaxLength(256)]
    public string? AssociatedMessageGuid { get; set; }

    public int? AssociatedMessagePart { get; set; }

    [MaxLength(128)]
    public string? AssociatedMessageType { get; set; }

    [MaxLength(256)]
    public string? ExpressiveSendStyleId { get; set; }

    public bool HasAttachments { get; set; }

    public bool HasReactions { get; set; }

    public long? DateDeleted { get; set; }

    public string? MetadataJson { get; set; }

    [MaxLength(256)]
    public string? ThreadOriginatorGuid { get; set; }

    [MaxLength(128)]
    public string? ThreadOriginatorPart { get; set; }

    public string? AttributedBodyJson { get; set; }

    public string? MessageSummaryInfoJson { get; set; }

    public string? PayloadDataJson { get; set; }

    public bool HasApplePayloadData { get; set; }

    public long? DateEdited { get; set; }

    public bool WasDeliveredQuietly { get; set; }

    public bool DidNotifyRecipient { get; set; }

    public bool IsBookmarked { get; set; }

    public int ChatId { get; set; }
    public ChatEntity Chat { get; set; } = null!;

    public ICollection<AttachmentEntity> Attachments { get; set; } = new List<AttachmentEntity>();
}
