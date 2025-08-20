namespace MessageHub.Channels.Smpp;

/// <summary>
/// Represents a message to be sent via SMPP channel
/// </summary>
public class SmppMessage
{
    public string PhoneNumber { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string From { get; set; } = "MessageHub";
    public bool RequestDeliveryReceipt { get; set; } = true;
}

/// <summary>
/// Result of sending a message via SMPP channel
/// </summary>
public class SmppSendResult
{
    public bool IsSuccess { get; set; }
    public string? SmppMessageId { get; set; }
    public List<string> SmppMessageIds { get; set; } = new(); // For multi-part SMS
    public int MessageParts { get; set; } = 1; // Number of SMS parts
    public string? ErrorMessage { get; set; }
    public Exception? Exception { get; set; }
    
    /// <summary>
    /// Primary message ID - returns first ID from the list or single SmppMessageId
    /// </summary>
    public string PrimaryMessageId => SmppMessageIds.FirstOrDefault() ?? SmppMessageId ?? "";

    public static SmppSendResult Success(string smppMessageId)
    {
        return new SmppSendResult
        {
            IsSuccess = true,
            SmppMessageId = smppMessageId,
            SmppMessageIds = new List<string> { smppMessageId },
            MessageParts = 1
        };
    }
    
    public static SmppSendResult SuccessMultiPart(List<string> smppMessageIds)
    {
        return new SmppSendResult
        {
            IsSuccess = true,
            SmppMessageId = smppMessageIds.FirstOrDefault(), // Backward compatibility
            SmppMessageIds = smppMessageIds,
            MessageParts = smppMessageIds.Count
        };
    }

    public static SmppSendResult Failure(string errorMessage, Exception? exception = null)
    {
        return new SmppSendResult
        {
            IsSuccess = false,
            ErrorMessage = errorMessage,
            Exception = exception
        };
    }
}

/// <summary>
/// Represents a delivery receipt received from SMPP provider
/// </summary>
public class SmppDeliveryReceipt
{
    public string SmppMessageId { get; set; } = string.Empty;
    public string SourceAddress { get; set; } = string.Empty;
    public string ReceiptText { get; set; } = string.Empty;
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
    public string DeliveryStatus { get; set; } = string.Empty;
    public int? ErrorCode { get; set; }
}

/// <summary>
/// Main interface for SMPP channel operations
/// </summary>
public interface ISmppChannel
{
    /// <summary>
    /// Sends a message via SMPP
    /// </summary>
    Task<SmppSendResult> SendSmsAsync(SmppMessage message);

    /// <summary>
    /// Event fired when a delivery receipt is received
    /// </summary>
    event Action<SmppDeliveryReceipt> OnDeliveryReceiptReceived;

    /// <summary>
    /// Gets the health status of the SMPP channel
    /// </summary>
    Task<bool> IsHealthyAsync();

    /// <summary>
    /// Disposes the SMPP channel resources
    /// </summary>
    ValueTask DisposeAsync();
}