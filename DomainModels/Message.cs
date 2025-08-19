using MessageHub.Shared;

namespace MessageHub;

public enum MessageStatus
{
    Pending,      // Message created but not yet sent
    Sent,         // Message submitted to SMSC (not final - waiting for DLR)
    Failed,       // Message submission failed
    Delivered,    // DLR: Message successfully delivered to recipient
    Expired,      // DLR: Message expired before delivery
    Rejected,     // DLR: Message rejected by network/recipient
    Undelivered,  // DLR: Message could not be delivered
    Unknown,      // DLR: Delivery status unknown
    Accepted      // DLR: Message accepted but delivery status unclear
}

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
    public DateTime? DeliveredAt { get; set; }        // When delivery receipt was received
    public string? DeliveryReceiptText { get; set; }  // Raw delivery receipt text (SMPP DLR or HTTP webhook payload)
    public string? DeliveryStatus { get; set; }       // Provider delivery status (DELIVRD, delivered, failed, etc.)
    public int? ErrorCode { get; set; }               // Provider error code (SMPP error code, HTTP error code, etc.)
    public int? NetworkErrorCode { get; set; }        // Network-specific error code (HTTP status codes, network errors)
}