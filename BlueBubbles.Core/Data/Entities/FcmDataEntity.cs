using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BlueBubbles.Core.Data.Entities;

[Table("FcmData")]
public class FcmDataEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [MaxLength(256)]
    public string? ProjectId { get; set; }

    [MaxLength(512)]
    public string? StorageBucket { get; set; }

    [MaxLength(512)]
    public string? ApiKey { get; set; }

    [MaxLength(1024)]
    public string? FirebaseUrl { get; set; }

    [MaxLength(256)]
    public string? ClientId { get; set; }

    [MaxLength(256)]
    public string? ApplicationId { get; set; }

    [NotMapped]
    public bool IsValid => ProjectId is not null && ApiKey is not null && ApplicationId is not null;
}
