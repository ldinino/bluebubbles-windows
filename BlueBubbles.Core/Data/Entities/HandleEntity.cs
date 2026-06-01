using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BlueBubbles.Core.Data.Entities;

[Table("Handles")]
public class HandleEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int? OriginalRowId { get; set; }

    [Required]
    [MaxLength(512)]
    public string Address { get; set; } = string.Empty;

    [Required]
    [MaxLength(64)]
    public string Service { get; set; } = "iMessage";

    [MaxLength(512)]
    public string? UniqueAddressAndService { get; set; }

    [MaxLength(10)]
    public string? Country { get; set; }

    [MaxLength(512)]
    public string? FormattedAddress { get; set; }

    [MaxLength(32)]
    public string? Color { get; set; }

    [MaxLength(512)]
    public string? DefaultPhone { get; set; }

    [MaxLength(512)]
    public string? DefaultEmail { get; set; }

    public ICollection<ChatParticipant> ChatParticipants { get; set; } = new List<ChatParticipant>();
    public ICollection<MessageEntity> Messages { get; set; } = new List<MessageEntity>();
}
