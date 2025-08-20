using Microsoft.EntityFrameworkCore;
using MessageHub.Channels.Shared;

namespace MessageHub;

public class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options)
    {
    }

    public DbSet<Message> Messages { get; set; }
    public DbSet<MessagePart> MessageParts { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Message>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Recipient).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Content).IsRequired().HasMaxLength(10000); // Updated for long messages
            entity.Property(e => e.Status).HasConversion<string>();
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.UpdatedAt).IsRequired();
            
            // Navigation property for MessageParts
            entity.HasMany(e => e.Parts)
                  .WithOne(p => p.Message)
                  .HasForeignKey(p => p.MessageId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MessagePart>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.MessageId).IsRequired();
            entity.Property(e => e.ProviderMessageId).IsRequired().HasMaxLength(255);
            entity.Property(e => e.PartNumber).IsRequired();
            entity.Property(e => e.TotalParts).IsRequired();
            entity.Property(e => e.Status).HasConversion<string>();
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.UpdatedAt).IsRequired();
            
            // Index for fast DLR lookup by ProviderMessageId
            entity.HasIndex(e => e.ProviderMessageId);
            
            // Composite index for message parts ordering
            entity.HasIndex(e => new { e.MessageId, e.PartNumber });
        });
    }
}