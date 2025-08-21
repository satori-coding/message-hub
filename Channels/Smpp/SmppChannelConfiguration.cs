using MessageHub.Channels.Shared;

namespace MessageHub.Channels.Smpp;

/// <summary>
/// Configuration settings for SMPP channel - defines how to connect to external SMS provider
/// This class contains all settings needed to establish and maintain SMPP connections
/// </summary>
public class SmppChannelConfiguration
{
    /// <summary>
    /// SMPP server hostname or IP address (e.g., "smpp.provider.com" or "192.168.1.100")
    /// This is the external SMS provider's SMPP endpoint
    /// </summary>
    public string Host { get; set; } = string.Empty;
    
    /// <summary>
    /// SMPP server port (typically 2775 for SMPP)
    /// Combined with Host, this forms the complete endpoint address
    /// </summary>
    public int Port { get; set; } = 2775;
    
    /// <summary>
    /// SMPP username/system ID for authentication
    /// Provided by your SMS provider when you sign up for SMPP access
    /// </summary>
    public string SystemId { get; set; } = string.Empty;
    
    /// <summary>
    /// SMPP password for authentication
    /// Provided by your SMS provider along with SystemId
    /// </summary>
    public string Password { get; set; } = string.Empty;
    
    /// <summary>
    /// Maximum number of concurrent SMPP connections in the pool
    /// More connections = higher throughput, but uses more resources
    /// Typical values: 1-5 connections
    /// </summary>
    public int MaxConnections { get; set; } = 3;
    
    /// <summary>
    /// How often to send keep-alive messages (enquire_link) to SMPP server
    /// Prevents idle connections from being closed by the server
    /// Typical values: 30-60 seconds
    /// </summary>
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>
    /// Timeout for establishing TCP connection to SMPP server
    /// How long to wait for initial network connection before giving up
    /// </summary>
    public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(30);
    
    /// <summary>
    /// Timeout for SMPP bind operation (authentication)
    /// </summary>
    public TimeSpan BindTimeout { get; set; } = TimeSpan.FromSeconds(15);
    
    /// <summary>
    /// Timeout for individual SMS submit operations
    /// </summary>
    public TimeSpan SubmitTimeout { get; set; } = TimeSpan.FromSeconds(10);
    
    /// <summary>
    /// Overall API request timeout (covers entire send operation)
    /// </summary>
    public TimeSpan ApiTimeout { get; set; } = TimeSpan.FromSeconds(45);
    
    /// <summary>
    /// Whether this SMPP provider supports and sends delivery receipts
    /// </summary>
    public bool ExpectDeliveryReceipts { get; set; } = true;
    
    /// <summary>
    /// How long to wait for a DLR before assuming delivery (minutes)
    /// </summary>
    public int DeliveryReceiptTimeoutMinutes { get; set; } = 30;
    
    /// <summary>
    /// Status to set when DLR timeout is reached
    /// </summary>
    public MessageStatus TimeoutStatus { get; set; } = MessageStatus.AssumedDelivered;
    
    /// <summary>
    /// Validates that all required configuration values are provided
    /// CHECKS:
    /// 1. Host is provided (can't connect without server address)
    /// 2. SystemId and Password are provided (can't authenticate without credentials)
    /// 3. Port is valid (1-65535 range)
    /// 4. MaxConnections is positive (need at least 1 connection)
    /// 
    /// Called during SmppChannel initialization to catch config errors early
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host))
            throw new ArgumentException("SMPP Host is required - specify SMS provider's server address", nameof(Host));
            
        if (string.IsNullOrWhiteSpace(SystemId))
            throw new ArgumentException("SMPP SystemId is required - get this from your SMS provider", nameof(SystemId));
            
        if (string.IsNullOrWhiteSpace(Password))
            throw new ArgumentException("SMPP Password is required - get this from your SMS provider", nameof(Password));
            
        if (Port <= 0 || Port > 65535)
            throw new ArgumentException("SMPP Port must be between 1 and 65535 (typically 2775)", nameof(Port));
            
        if (MaxConnections <= 0)
            throw new ArgumentException("MaxConnections must be greater than 0 (typically 1-5)", nameof(MaxConnections));
    }
}