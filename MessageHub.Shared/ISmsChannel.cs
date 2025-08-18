namespace MessageHub.Shared;

/// <summary>
/// Channel type enumeration
/// </summary>
public enum ChannelType
{
    SMPP,
    HTTP
}

/// <summary>
/// Common interface for all SMS channel implementations (SMPP, HTTP, etc.)
/// </summary>
public interface ISmsChannel
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
    /// Send SMS via this channel
    /// </summary>
    /// <param name="message">SMS message to send</param>
    /// <returns>Result of SMS send operation</returns>
    Task<SmsChannelResult> SendSmsAsync(SmsMessage message);
    
    /// <summary>
    /// Check if channel is healthy and ready to send messages
    /// </summary>
    /// <returns>True if channel is healthy</returns>
    Task<bool> IsHealthyAsync();
}

/// <summary>
/// Result of SMS send operation via any channel
/// </summary>
public class SmsChannelResult
{
    public bool Success { get; set; }
    public string? ProviderMessageId { get; set; }
    public string? ErrorMessage { get; set; }
    public int? ErrorCode { get; set; }
    public int? NetworkErrorCode { get; set; }
    public Dictionary<string, object>? ChannelData { get; set; } = new();

    public static SmsChannelResult CreateSuccess(string providerMessageId, Dictionary<string, object>? channelData = null)
    {
        return new SmsChannelResult
        {
            Success = true,
            ProviderMessageId = providerMessageId,
            ChannelData = channelData ?? new()
        };
    }

    public static SmsChannelResult CreateFailure(string errorMessage, int? errorCode = null, int? networkErrorCode = null)
    {
        return new SmsChannelResult
        {
            Success = false,
            ErrorMessage = errorMessage,
            ErrorCode = errorCode,
            NetworkErrorCode = networkErrorCode
        };
    }
}

/// <summary>
/// SMS message entity for channel operations
/// </summary>
public class SmsMessage
{
    public int Id { get; set; }
    public string PhoneNumber { get; set; } = string.Empty;
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