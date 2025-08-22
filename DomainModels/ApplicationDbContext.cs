using Microsoft.EntityFrameworkCore;
using MessageHub.Channels.Shared;
using MessageHub.DomainModels;

namespace MessageHub;

public class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options)
    {
    }

    public DbSet<Message> Messages { get; set; }
    public DbSet<MessagePart> MessageParts { get; set; }
    public DbSet<Tenant> Tenants { get; set; }
    public DbSet<TenantSmppConfiguration> TenantSmppConfigurations { get; set; }
    public DbSet<TenantHttpConfiguration> TenantHttpConfigurations { get; set; }

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

        modelBuilder.Entity<Tenant>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(255);
            entity.Property(e => e.SubscriptionKey).IsRequired().HasMaxLength(255);
            entity.Property(e => e.IsActive).IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.UpdatedAt).IsRequired();
            
            // Unique constraint on subscription key
            entity.HasIndex(e => e.SubscriptionKey).IsUnique();
            
            // Navigation properties
            entity.HasMany(e => e.Messages)
                  .WithOne()
                  .HasForeignKey(m => m.TenantId)
                  .OnDelete(DeleteBehavior.Cascade);
                  
            entity.HasMany(e => e.ChannelConfigurations)
                  .WithOne(c => c.Tenant)
                  .HasForeignKey(c => c.TenantId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TenantChannelConfiguration>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TenantId).IsRequired();
            entity.Property(e => e.ChannelName).IsRequired().HasMaxLength(255);
            entity.Property(e => e.ChannelType).HasConversion<string>();
            entity.Property(e => e.IsActive).IsRequired();
            entity.Property(e => e.IsDefault).IsRequired();
            entity.Property(e => e.Priority).IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.UpdatedAt).IsRequired();
            
            // Unique constraint on tenant + channel name
            entity.HasIndex(e => new { e.TenantId, e.ChannelName }).IsUnique();
            
            // Discriminator for inheritance
            entity.HasDiscriminator<string>("ConfigurationType")
                  .HasValue<TenantSmppConfiguration>("SMPP")
                  .HasValue<TenantHttpConfiguration>("HTTP");
        });

        modelBuilder.Entity<TenantSmppConfiguration>(entity =>
        {
            entity.Property(e => e.Host).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Port).IsRequired();
            entity.Property(e => e.SystemId).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Password).IsRequired().HasMaxLength(255);
            entity.Property(e => e.MaxConnections).IsRequired();
            entity.Property(e => e.TimeoutStatus).HasMaxLength(50);
        });

        modelBuilder.Entity<TenantHttpConfiguration>(entity =>
        {
            entity.Property(e => e.ApiUrl).IsRequired().HasMaxLength(500);
            entity.Property(e => e.ApiKey).IsRequired().HasMaxLength(255);
            entity.Property(e => e.AuthUsername).HasMaxLength(255);
            entity.Property(e => e.AuthPassword).HasMaxLength(255);
            entity.Property(e => e.FromNumber).HasMaxLength(50);
            entity.Property(e => e.ProviderName).IsRequired().HasMaxLength(255);
            entity.Property(e => e.WebhookUrl).HasMaxLength(500);
        });
    }
}