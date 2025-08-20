namespace MessageHub.Channels.Shared;

/// <summary>
/// Channel type enumeration
/// </summary>
public enum ChannelType
{
    SMPP,
    HTTP,
    EMAIL,
    PUSH,
    WHATSAPP
}

/// <summary>
/// Common interface for all message channel implementations (SMPP, HTTP, Email, Push, etc.)
/// </summary>
public interface IMessageChannel
{
    /// <summary>
    /// Channel type identifier
    /// </summary>
    ChannelType ChannelType { get; }
    
    /// <summary>
    /// Provider name (e.g., "SMPP", "Twilio", "AWS_SNS")
    /// </summary>
    string ProviderName { get; }
    
    /// <summary>
    /// Send message via this channel
    /// </summary>
    /// <param name="message">Message to send</param>
    /// <returns>Result of message send operation</returns>
    Task<MessageResult> SendAsync(Message message);
    
    /// <summary>
    /// Check if channel is healthy and ready to send messages
    /// </summary>
    /// <returns>True if channel is healthy</returns>
    Task<bool> IsHealthyAsync();
}

/// <summary>
/// Result of message send operation via any channel
/// </summary>
public class MessageResult
{
    public bool Success { get; set; }
    public string? ProviderMessageId { get; set; }
    public List<string> ProviderMessageIds { get; set; } = new(); // For multi-part SMS
    public int MessageParts { get; set; } = 1; // Number of SMS parts
    public string? ErrorMessage { get; set; }
    public int? ErrorCode { get; set; }
    public int? NetworkErrorCode { get; set; }
    public Dictionary<string, object>? ChannelData { get; set; } = new();
    
    /// <summary>
    /// Primary message ID - returns first ID from the list or single ProviderMessageId
    /// </summary>
    public string PrimaryMessageId => ProviderMessageIds.FirstOrDefault() ?? ProviderMessageId ?? "";

    public static MessageResult CreateSuccess(string providerMessageId, Dictionary<string, object>? channelData = null)
    {
        return new MessageResult
        {
            Success = true,
            ProviderMessageId = providerMessageId,
            ProviderMessageIds = new List<string> { providerMessageId },
            MessageParts = 1,
            ChannelData = channelData ?? new()
        };
    }
    
    public static MessageResult CreateSuccessMultiPart(List<string> providerMessageIds, Dictionary<string, object>? channelData = null)
    {
        return new MessageResult
        {
            Success = true,
            ProviderMessageId = providerMessageIds.FirstOrDefault(), // Backward compatibility
            ProviderMessageIds = providerMessageIds,
            MessageParts = providerMessageIds.Count,
            ChannelData = channelData ?? new()
        };
    }

    public static MessageResult CreateFailure(string errorMessage, int? errorCode = null, int? networkErrorCode = null)
    {
        return new MessageResult
        {
            Success = false,
            ErrorMessage = errorMessage,
            ErrorCode = errorCode,
            NetworkErrorCode = networkErrorCode
        };
    }
}

/// <summary>
/// Message status enumeration
/// </summary>
public enum MessageStatus
{
    Pending,         // Message created but not yet sent
    Sent,           // Message submitted to provider (waiting for DLR)
    Failed,         // Message submission failed
    Delivered,      // DLR: Message successfully delivered to recipient
    PartiallyDelivered, // NEW: Some SMS parts delivered, others pending/failed (SMPP multi-part)
    AssumedDelivered, // No DLR received, but assumed delivered after timeout
    DeliveryUnknown, // DLR timeout exceeded, delivery status unclear
    Expired,        // DLR: Message expired before delivery
    Rejected,       // DLR: Message rejected by network/recipient
    Undelivered,    // DLR: Message could not be delivered
    Unknown,        // DLR: Delivery status unknown
    Accepted        // DLR: Message accepted but delivery status unclear
}

/// <summary>
/// MessagePart entity for tracking individual SMS parts in multi-part messages (SMPP channels)
/// </summary>
public class MessagePart
{
    public int Id { get; set; }
    public int MessageId { get; set; }                          // FK to Message
    public Message Message { get; set; } = null!;              // Navigation property
    
    // SMS Part Identification
    public string ProviderMessageId { get; set; } = string.Empty;  // SMPP message ID for this part
    public int PartNumber { get; set; }                         // 1, 2, 3, etc.
    public int TotalParts { get; set; }                         // Total parts in message
    
    // Per-Part Status Tracking
    public MessageStatus Status { get; set; } = MessageStatus.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SentAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    
    // Per-Part Delivery Receipt Data
    public string? DeliveryReceiptText { get; set; }            // Raw DLR text for this part
    public string? DeliveryStatus { get; set; }                // DELIVRD, ACCEPTD, etc.
    public int? ErrorCode { get; set; }                        // Provider error code
    public int? NetworkErrorCode { get; set; }                 // Network error code
}

/// <summary>
/// Message entity for channel operations
/// </summary>
public class Message
{
    public int Id { get; set; }
    public string Recipient { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public MessageStatus Status { get; set; } = MessageStatus.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SentAt { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    
    // Multi-Channel Support fields
    public ChannelType ChannelType { get; set; } = ChannelType.SMPP;    // Default to SMPP for backward compatibility
    public string? ProviderName { get; set; }                           // Provider name: "SMPP", "Twilio", "AWS_SNS", etc.
    public string? ChannelData { get; set; }                            // JSON string for channel-specific data
    
    // Universal Delivery Receipt fields (used by all channels)
    public string? ProviderMessageId { get; set; }    // Provider message ID (SMPP message ID, HTTP API response ID, etc.)
    public int? MessageParts { get; set; }            // Number of SMS parts for multi-part messages
    public DateTime? DeliveredAt { get; set; }        // When delivery receipt was received
    public string? DeliveryReceiptText { get; set; }  // Raw delivery receipt text (SMPP DLR or HTTP webhook payload)
    public string? DeliveryStatus { get; set; }       // Provider delivery status (DELIVRD, delivered, failed, etc.)
    public int? ErrorCode { get; set; }               // Provider error code (SMPP error code, HTTP error code, etc.)
    public int? NetworkErrorCode { get; set; }        // Network-specific error code (HTTP status codes, network errors)
    
    // SMPP Multi-Part Support (Navigation Property)
    public List<MessagePart> Parts { get; set; } = new();
    
    // Computed Properties
    /// <summary>
    /// Check if this message has SMS parts (SMPP multi-part)
    /// </summary>
    public bool HasParts => Parts.Any();
    
    /// <summary>
    /// Compute overall status from individual SMS parts (SMPP) or return main Status (HTTP)
    /// </summary>
    public MessageStatus OverallStatus 
    { 
        get 
        {
            // HTTP channels: Use main Status field
            if (!HasParts) return Status; 
            
            // SMPP channels: Aggregate status from parts
            if (Parts.All(p => p.Status == MessageStatus.Delivered))
                return MessageStatus.Delivered;
            if (Parts.All(p => p.Status == MessageStatus.Failed))
                return MessageStatus.Failed;
            if (Parts.Any(p => p.Status == MessageStatus.Delivered))
                return MessageStatus.PartiallyDelivered;
                
            // All parts have same status (Pending, Sent, etc.)
            return Parts.First().Status;
        }
    }
}

