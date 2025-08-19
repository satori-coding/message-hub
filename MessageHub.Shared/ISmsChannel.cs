namespace MessageHub.Shared;

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
    public string? ErrorMessage { get; set; }
    public int? ErrorCode { get; set; }
    public int? NetworkErrorCode { get; set; }
    public Dictionary<string, object>? ChannelData { get; set; } = new();

    public static MessageResult CreateSuccess(string providerMessageId, Dictionary<string, object>? channelData = null)
    {
        return new MessageResult
        {
            Success = true,
            ProviderMessageId = providerMessageId,
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
/// Message entity for channel operations
/// </summary>
public class Message
{
    public int Id { get; set; }
    public string Recipient { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public ChannelType ChannelType { get; set; } = ChannelType.SMPP;
    public string? ProviderName { get; set; }
    public string? ChannelData { get; set; }
    public string? ProviderMessageId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SentAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public string Status { get; set; } = "Pending";
    public string? DeliveryReceiptText { get; set; }
    public string? DeliveryStatus { get; set; }
    public int? ErrorCode { get; set; }
}