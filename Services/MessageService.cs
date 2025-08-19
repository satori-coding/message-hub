using Microsoft.EntityFrameworkCore;
using MessageHub.SmppChannel;
using MessageHub.HttpSmsChannel;
using MessageHub.Shared;

namespace MessageHub;

/// <summary>
/// Service for handling message operations using multiple channels (SMPP, HTTP, Email, Push, etc.)
/// </summary>
public class MessageService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ILogger<MessageService> _logger;
    private readonly ISmppChannel _smppChannel;
    private readonly Dictionary<ChannelType, IMessageChannel> _messageChannels;

    public MessageService(ApplicationDbContext dbContext, ILogger<MessageService> logger, 
                      ISmppChannel smppChannel, IEnumerable<IMessageChannel> messageChannels)
    {
        _dbContext = dbContext;
        _logger = logger;
        _smppChannel = smppChannel;
        
        // Build dictionary of available channels by type
        _messageChannels = messageChannels.ToDictionary(c => c.ChannelType, c => c);
        
        _logger.LogInformation("MessageService initialized with {ChannelCount} channels: {Channels}",
            _messageChannels.Count, string.Join(", ", _messageChannels.Values.Select(c => c.ProviderName)));
    }

    /// <summary>
    /// Creates and sends a message directly using the specified channel
    /// </summary>
    public async Task<Message> CreateAndSendMessageAsync(string recipient, string content, ChannelType channelType = ChannelType.SMPP)
    {
        _logger.LogInformation("Creating and sending new message to {Recipient}, Content length: {ContentLength}", 
            recipient, content.Length);

        var message = new Message
        {
            Recipient = recipient,
            Content = content,
            Status = MessageStatus.Pending,
            ChannelType = channelType,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _dbContext.Messages.Add(message);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Message created with ID: {MessageId}", message.Id);

        // Send SMS via specified channel
        await SendMessageAsync(message.Id);

        return message;
    }

    /// <summary>
    /// Sends an SMS message by ID via the channel specified in the message
    /// </summary>
    public async Task SendMessageAsync(int messageId)
    {
        _logger.LogInformation("Starting message send process for message ID: {MessageId}", messageId);
        
        var startTime = DateTime.UtcNow;
        
        try
        {
            var message = await _dbContext.Messages.FindAsync(messageId);
            if (message == null)
            {
                _logger.LogError("Message with ID {MessageId} not found", messageId);
                return;
            }

            _logger.LogInformation("Found SMS message: Phone={PhoneNumber}, Content length={ContentLength}", 
                message.Recipient, message.Content.Length);

            try
            {
                // Get the appropriate channel for this message
                if (!_messageChannels.TryGetValue(message.ChannelType, out var channel))
                {
                    _logger.LogError("No channel available for type {ChannelType} for message ID: {MessageId}", 
                        message.ChannelType, messageId);
                    await UpdateMessageStatusAsync(message, MessageStatus.Failed);
                    return;
                }

                _logger.LogInformation("Sending SMS via {ChannelType} channel ({ProviderName}) to {PhoneNumber}", 
                    message.ChannelType, channel.ProviderName, message.Recipient);

                // Send via the selected channel
                var result = await channel.SendAsync(message);

                if (result.Success && !string.IsNullOrEmpty(result.ProviderMessageId))
                {
                    _logger.LogInformation("Message sent successfully for message ID: {MessageId}, Provider ID: {ProviderMessageId}, Channel: {ChannelType}", 
                        messageId, result.ProviderMessageId, message.ChannelType);
                    
                    message.SentAt = DateTime.UtcNow;
                    message.ProviderMessageId = result.ProviderMessageId;
                    message.ProviderName = channel.ProviderName;
                    
                    // Store channel-specific data if available
                    if (result.ChannelData != null && result.ChannelData.Any())
                    {
                        message.ChannelData = System.Text.Json.JsonSerializer.Serialize(result.ChannelData);
                    }
                    
                    await UpdateMessageStatusAsync(message, MessageStatus.Sent);
                }
                else
                {
                    _logger.LogError("{ChannelType} send failed for message ID: {SmsMessageId}, Error: {ErrorMessage}", 
                        smsMessage.ChannelType, smsMessageId, result.ErrorMessage);
                    await UpdateMessageStatusAsync(message, MessageStatus.Failed);
                }
            }
            catch (Exception channelEx)
            {
                _logger.LogError(channelEx, "{ChannelType} send failed for message ID: {SmsMessageId}", 
                    smsMessage.ChannelType, smsMessageId);
                await UpdateMessageStatusAsync(message, MessageStatus.Failed);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending SMS for message ID: {SmsMessageId}", smsMessageId);
            
            var smsMessage = await _dbContext.SmsMessages.FindAsync(smsMessageId);
            if (smsMessage != null)
            {
                await UpdateMessageStatusAsync(message, MessageStatus.Failed);
            }
        }
        finally
        {
            var duration = DateTime.UtcNow - startTime;
            _logger.LogInformation("SMS send process completed for message ID: {SmsMessageId} in {Duration}ms", 
                smsMessageId, duration.TotalMilliseconds);
        }
    }

    /// <summary>
    /// Updates the SMS status in database
    /// </summary>
    private async Task UpdateSmsStatusAsync(SmsMessage smsMessage, SmsStatus status)
    {
        _logger.LogInformation("Updating SMS message ID {SmsMessageId} status from {OldStatus} to {NewStatus}", 
            smsMessage.Id, smsMessage.Status, status);

        smsMessage.Status = status;
        smsMessage.UpdatedAt = DateTime.UtcNow;

        _dbContext.SmsMessages.Update(smsMessage);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("SMS message ID {SmsMessageId} status updated successfully", smsMessage.Id);
    }

    /// <summary>
    /// Gets an SMS message by ID
    /// </summary>
    public async Task<SmsMessage?> GetSmsMessageAsync(int id)
    {
        _logger.LogInformation("Retrieving SMS message with ID: {SmsMessageId}", id);
        return await _dbContext.SmsMessages.FindAsync(id);
    }

    /// <summary>
    /// Gets all SMS messages ordered by creation date
    /// </summary>
    public async Task<List<SmsMessage>> GetAllSmsMessagesAsync()
    {
        _logger.LogInformation("Retrieving all SMS messages");
        return await _dbContext.SmsMessages.OrderByDescending(s => s.CreatedAt).ToListAsync();
    }

    /// <summary>
    /// Processes delivery receipt from SMPP channel
    /// </summary>
    public async Task ProcessDeliveryReceiptAsync(SmppDeliveryReceipt receipt)
    {
        try
        {
            _logger.LogInformation("Processing delivery receipt for SMPP message ID: {SmppMessageId}", receipt.SmppMessageId);

            // Find the SMS message by provider message ID
            var smsMessage = await _dbContext.SmsMessages
                .FirstOrDefaultAsync(s => s.ProviderMessageId == receipt.SmppMessageId);

            if (smsMessage == null)
            {
                _logger.LogWarning("SMS message not found for SMPP message ID: {SmppMessageId}", receipt.SmppMessageId);
                return;
            }

            _logger.LogInformation("Found SMS message ID {SmsMessageId} for SMPP message ID {SmppMessageId}", 
                smsMessage.Id, receipt.SmppMessageId);

            // Map delivery status to SmsStatus
            var newStatus = MapDeliveryStatusToSmsStatus(receipt.DeliveryStatus);
            
            _logger.LogInformation("Updating SMS message ID {SmsMessageId}: {OldStatus} -> {NewStatus} (SMPP: {SmppStatus})",
                smsMessage.Id, smsMessage.Status, newStatus, receipt.DeliveryStatus);

            // Update the SMS message with delivery receipt information
            smsMessage.Status = newStatus;
            smsMessage.DeliveredAt = receipt.ReceivedAt;
            smsMessage.DeliveryReceiptText = receipt.ReceiptText;
            smsMessage.DeliveryStatus = receipt.DeliveryStatus;
            smsMessage.ErrorCode = receipt.ErrorCode;
            smsMessage.UpdatedAt = DateTime.UtcNow;

            // Update database
            _dbContext.SmsMessages.Update(smsMessage);
            await _dbContext.SaveChangesAsync();

            _logger.LogInformation("Successfully processed delivery receipt for SMS ID {SmsMessageId}: Status={Status}", 
                smsMessage.Id, smsMessage.Status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing delivery receipt for SMPP message ID: {SmppMessageId}", receipt.SmppMessageId);
        }
    }

    /// <summary>
    /// Maps SMPP delivery status to SmsStatus enum
    /// </summary>
    private static SmsStatus MapDeliveryStatusToSmsStatus(string deliveryStatus)
    {
        return deliveryStatus?.ToUpper() switch
        {
            "DELIVRD" => SmsStatus.Delivered,    // Message delivered successfully
            "EXPIRED" => SmsStatus.Expired,      // Message expired before delivery
            "DELETED" => SmsStatus.Expired,      // Message deleted (treat as expired)
            "UNDELIV" => SmsStatus.Undelivered,  // Message undelivered
            "ACCEPTD" => SmsStatus.Accepted,     // Message accepted (intermediate)
            "UNKNOWN" => SmsStatus.Unknown,      // Unknown delivery status
            "REJECTD" => SmsStatus.Rejected,     // Message rejected
            "ENROUTE" => SmsStatus.Sent,         // Message en route (intermediate, keep as Sent)
            _ => SmsStatus.Unknown
        };
    }
}