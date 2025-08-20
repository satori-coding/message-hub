using MessageHub.Channels.Shared;

namespace MessageHub.Channels.Smpp;

/// <summary>
/// Configuration settings for SMPP channel
/// </summary>
public class SmppChannelConfiguration
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 2775;
    public string SystemId { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public int MaxConnections { get; set; } = 3;
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(30);
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
    /// Validates the configuration
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host))
            throw new ArgumentException("SMPP Host is required", nameof(Host));
            
        if (string.IsNullOrWhiteSpace(SystemId))
            throw new ArgumentException("SMPP SystemId is required", nameof(SystemId));
            
        if (string.IsNullOrWhiteSpace(Password))
            throw new ArgumentException("SMPP Password is required", nameof(Password));
            
        if (Port <= 0 || Port > 65535)
            throw new ArgumentException("SMPP Port must be between 1 and 65535", nameof(Port));
            
        if (MaxConnections <= 0)
            throw new ArgumentException("MaxConnections must be greater than 0", nameof(MaxConnections));
    }
}