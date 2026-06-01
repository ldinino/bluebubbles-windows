using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BlueBubbles.Core.Data.Entities;

[Table("Attachments")]
public class AttachmentEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int? OriginalRowId { get; set; }

    [Required]
    [MaxLength(256)]
    public string Guid { get; set; } = string.Empty;

    [MaxLength(256)]
    public string? Uti { get; set; }

    [MaxLength(256)]
    public string? MimeType { get; set; }

    public bool IsOutgoing { get; set; }

    [MaxLength(512)]
    public string? TransferName { get; set; }

    public long TotalBytes { get; set; }

    public int? Height { get; set; }

    public int? Width { get; set; }

    public bool HasLivePhoto { get; set; }

    public string? MetadataJson { get; set; }

    public int MessageId { get; set; }
    public MessageEntity Message { get; set; } = null!;
}
