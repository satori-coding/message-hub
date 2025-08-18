using MessageHub.Shared;

namespace MessageHub;

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