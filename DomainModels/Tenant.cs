using MessageHub.Channels.Shared;

namespace MessageHub.DomainModels;

/// <summary>
/// Tenant entity representing a customer/organization using the SMS service
/// </summary>
public class Tenant
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string SubscriptionKey { get; set; } = string.Empty;  // API key for authentication
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    
    // Navigation properties
    public List<Message> Messages { get; set; } = new();
    public List<TenantChannelConfiguration> ChannelConfigurations { get; set; } = new();
    
    // Computed properties
    public bool HasActiveConfigurations => ChannelConfigurations.Any(c => c.IsActive);
}

/// <summary>
/// Base configuration for tenant-specific channel settings
/// </summary>
public abstract class TenantChannelConfiguration
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;
    
    public string ChannelName { get; set; } = string.Empty;     // Unique name for this configuration
    public ChannelType ChannelType { get; set; }               // SMPP, HTTP, etc.
    public bool IsActive { get; set; } = true;
    public bool IsDefault { get; set; } = false;               // Default channel for this tenant
    public int Priority { get; set; } = 0;                     // Higher priority = preferred channel
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// SMPP-specific configuration for tenant
/// </summary>
public class TenantSmppConfiguration : TenantChannelConfiguration
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 2775;
    public string SystemId { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public int MaxConnections { get; set; } = 3;
    
    // Timeout settings
    public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan BindTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan SubmitTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan ApiTimeout { get; set; } = TimeSpan.FromSeconds(45);
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(30);
    
    // DLR settings
    public bool ExpectDeliveryReceipts { get; set; } = false;
    public int DeliveryReceiptTimeoutMinutes { get; set; } = 30;
    public string TimeoutStatus { get; set; } = "AssumedDelivered";
    
    public TenantSmppConfiguration()
    {
        ChannelType = ChannelType.SMPP;
    }
}

/// <summary>
/// HTTP SMS provider configuration for tenant
/// </summary>
public class TenantHttpConfiguration : TenantChannelConfiguration  
{
    public string ApiUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string? AuthUsername { get; set; }
    public string? AuthPassword { get; set; }
    public string? FromNumber { get; set; }
    public string ProviderName { get; set; } = string.Empty;    // Twilio, AWS, etc.
    
    // HTTP-specific settings
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public int MaxRetries { get; set; } = 3;
    public string? WebhookUrl { get; set; }                     // For delivery receipts
    
    public TenantHttpConfiguration()
    {
        ChannelType = ChannelType.HTTP;
    }
}