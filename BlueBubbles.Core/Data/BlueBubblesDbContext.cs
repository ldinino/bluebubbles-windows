using BlueBubbles.Core.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace BlueBubbles.Core.Data;

public class BlueBubblesDbContext : DbContext
{
    public DbSet<ChatEntity> Chats => Set<ChatEntity>();
    public DbSet<MessageEntity> Messages => Set<MessageEntity>();
    public DbSet<HandleEntity> Handles => Set<HandleEntity>();
    public DbSet<AttachmentEntity> Attachments => Set<AttachmentEntity>();
    public DbSet<ChatParticipant> ChatParticipants => Set<ChatParticipant>();
    public DbSet<FcmDataEntity> FcmData => Set<FcmDataEntity>();

    public BlueBubblesDbContext(DbContextOptions<BlueBubblesDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ChatParticipant composite key
        modelBuilder.Entity<ChatParticipant>(entity =>
        {
            entity.HasKey(cp => new { cp.ChatId, cp.HandleId });

            entity.HasOne(cp => cp.Chat)
                .WithMany(c => c.ChatParticipants)
                .HasForeignKey(cp => cp.ChatId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(cp => cp.Handle)
                .WithMany(h => h.ChatParticipants)
                .HasForeignKey(cp => cp.HandleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Chat
        modelBuilder.Entity<ChatEntity>(entity =>
        {
            entity.HasIndex(e => e.Guid).IsUnique();
            entity.HasIndex(e => e.LatestMessageDate);
            entity.HasIndex(e => e.OldestSyncedMessageDate);
            entity.HasIndex(e => e.IsPinned);
        });

        // Message
        modelBuilder.Entity<MessageEntity>(entity =>
        {
            entity.HasIndex(e => e.Guid).IsUnique();
            entity.HasIndex(e => e.ChatId);
            entity.HasIndex(e => e.DateCreated);
            entity.HasIndex(e => e.AssociatedMessageGuid);
            entity.HasIndex(e => e.ThreadOriginatorGuid);

            entity.HasOne(m => m.Chat)
                .WithMany(c => c.Messages)
                .HasForeignKey(m => m.ChatId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(m => m.Handle)
                .WithMany(h => h.Messages)
                .HasForeignKey(m => m.HandleId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // Handle
        modelBuilder.Entity<HandleEntity>(entity =>
        {
            entity.HasIndex(e => e.Address);
            entity.HasIndex(e => e.UniqueAddressAndService).IsUnique()
                .HasFilter(null); // SQLite doesn't support filtered indexes
        });

        // Attachment
        modelBuilder.Entity<AttachmentEntity>(entity =>
        {
            entity.HasIndex(e => e.Guid).IsUnique();

            entity.HasOne(a => a.Message)
                .WithMany(m => m.Attachments)
                .HasForeignKey(a => a.MessageId)
                .OnDelete(DeleteBehavior.Cascade);
        });

    }
}
