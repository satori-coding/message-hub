using Microsoft.EntityFrameworkCore;
using MessageHub.Channels.Smpp;
using MessageHub.Channels.Http;
using MessageHub.Channels.Shared;

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

        // Send message via specified channel
        await SendMessageAsync(message.Id);

        return message;
    }

    /// <summary>
    /// Sends a message by ID via the channel specified in the message
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

            _logger.LogInformation("Found message: Phone={PhoneNumber}, Content length={ContentLength}", 
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

                _logger.LogInformation("Sending message via {ChannelType} channel ({ProviderName}) to {PhoneNumber}", 
                    message.ChannelType, channel.ProviderName, message.Recipient);

                // Send via the selected channel
                var result = await channel.SendAsync(message);

                if (result.Success && !string.IsNullOrEmpty(result.ProviderMessageId))
                {
                    _logger.LogInformation("Message sent successfully for message ID: {MessageId}, Provider ID: {ProviderMessageId}, Channel: {ChannelType}, Parts: {MessageParts}", 
                        messageId, result.ProviderMessageId, message.ChannelType, result.MessageParts);
                    
                    message.SentAt = DateTime.UtcNow;
                    message.ProviderMessageId = result.ProviderMessageId;
                    message.MessageParts = result.MessageParts;
                    message.ProviderName = channel.ProviderName;
                    
                    // Store channel-specific data if available
                    if (result.ChannelData != null && result.ChannelData.Any())
                    {
                        message.ChannelData = System.Text.Json.JsonSerializer.Serialize(result.ChannelData);
                    }
                    
                    // Create MessagePart records for SMPP multi-part messages
                    if (message.ChannelType == ChannelType.SMPP && result.MessageParts > 1 && result.ProviderMessageIds.Any())
                    {
                        await CreateMessagePartsAsync(message, result.ProviderMessageIds);
                    }
                    
                    await UpdateMessageStatusAsync(message, MessageStatus.Sent);
                }
                else
                {
                    _logger.LogError("{ChannelType} send failed for message ID: {MessageId}, Error: {ErrorMessage}", 
                        message.ChannelType, messageId, result.ErrorMessage);
                    await UpdateMessageStatusAsync(message, MessageStatus.Failed);
                }
            }
            catch (Exception channelEx)
            {
                _logger.LogError(channelEx, "{ChannelType} send failed for message ID: {MessageId}", 
                    message.ChannelType, messageId);
                await UpdateMessageStatusAsync(message, MessageStatus.Failed);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending message for message ID: {MessageId}", messageId);
            
            var message = await _dbContext.Messages.FindAsync(messageId);
            if (message != null)
            {
                await UpdateMessageStatusAsync(message, MessageStatus.Failed);
            }
        }
        finally
        {
            var duration = DateTime.UtcNow - startTime;
            _logger.LogInformation("Message send process completed for message ID: {MessageId} in {Duration}ms", 
                messageId, duration.TotalMilliseconds);
        }
    }

    /// <summary>
    /// Creates MessagePart records for SMPP multi-part messages
    /// </summary>
    private async Task CreateMessagePartsAsync(Message message, List<string> providerMessageIds)
    {
        _logger.LogInformation("Creating {PartCount} MessagePart records for message ID {MessageId}", 
            providerMessageIds.Count, message.Id);

        var messageParts = new List<MessagePart>();
        
        for (int i = 0; i < providerMessageIds.Count; i++)
        {
            var messagePart = new MessagePart
            {
                MessageId = message.Id,
                ProviderMessageId = providerMessageIds[i],
                PartNumber = i + 1,
                TotalParts = providerMessageIds.Count,
                Status = MessageStatus.Sent, // Parts inherit status from parent message
                SentAt = message.SentAt,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            
            messageParts.Add(messagePart);
        }

        _dbContext.MessageParts.AddRange(messageParts);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Created {PartCount} MessagePart records for message ID {MessageId}: IDs [{ProviderIds}]", 
            messageParts.Count, message.Id, string.Join(", ", providerMessageIds));
    }

    /// <summary>
    /// Updates the message status in database
    /// </summary>
    private async Task UpdateMessageStatusAsync(Message message, MessageStatus status)
    {
        _logger.LogInformation("Updating message ID {MessageId} status from {OldStatus} to {NewStatus}", 
            message.Id, message.Status, status);

        message.Status = status;
        message.UpdatedAt = DateTime.UtcNow;

        _dbContext.Messages.Update(message);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Message ID {MessageId} status updated successfully", message.Id);
    }

    /// <summary>
    /// Gets a message by ID
    /// </summary>
    public async Task<Message?> GetMessageAsync(int id)
    {
        _logger.LogInformation("Retrieving message with ID: {MessageId}", id);
        return await _dbContext.Messages.FindAsync(id);
    }

    /// <summary>
    /// Gets all messages ordered by creation date
    /// </summary>
    public async Task<List<Message>> GetAllMessagesAsync()
    {
        _logger.LogInformation("Retrieving all messages");
        return await _dbContext.Messages.OrderByDescending(s => s.CreatedAt).ToListAsync();
    }

    /// <summary>
    /// Processes delivery receipt from SMPP channel with enhanced multi-part support
    /// </summary>
    public async Task ProcessDeliveryReceiptAsync(SmppDeliveryReceipt receipt)
    {
        try
        {
            _logger.LogInformation("Processing delivery receipt for SMPP message ID: {SmppMessageId}", receipt.SmppMessageId);

            // Try to find MessagePart first (for SMPP multi-part messages)
            var messagePart = await _dbContext.MessageParts
                .Include(mp => mp.Message)
                .FirstOrDefaultAsync(mp => mp.ProviderMessageId == receipt.SmppMessageId);
                
            if (messagePart != null)
            {
                // This is a multi-part SMS delivery receipt
                _logger.LogInformation("Found MessagePart for SMPP message ID {SmppMessageId}: MessageID={MessageId}, Part={PartNumber}/{TotalParts}", 
                    receipt.SmppMessageId, messagePart.MessageId, messagePart.PartNumber, messagePart.TotalParts);

                // Update individual SMS part status
                var partStatus = MapDeliveryStatusToMessageStatus(receipt.DeliveryStatus);
                messagePart.Status = partStatus;
                messagePart.DeliveredAt = receipt.ReceivedAt;
                messagePart.DeliveryReceiptText = receipt.ReceiptText;
                messagePart.DeliveryStatus = receipt.DeliveryStatus;
                messagePart.ErrorCode = receipt.ErrorCode;
                messagePart.UpdatedAt = DateTime.UtcNow;

                // Update parent message's overall status based on all parts
                var parentMessage = messagePart.Message;
                await UpdateParentMessageFromParts(parentMessage);

                _logger.LogInformation("Successfully processed multi-part delivery receipt: MessagePart {PartNumber}/{TotalParts} -> {PartStatus}, Overall Message Status -> {OverallStatus}", 
                    messagePart.PartNumber, messagePart.TotalParts, partStatus, parentMessage.OverallStatus);
                
                await _dbContext.SaveChangesAsync();
                return;
            }

            // Fallback: Single-part message or direct message lookup
            var message = await _dbContext.Messages
                .FirstOrDefaultAsync(s => s.ProviderMessageId == receipt.SmppMessageId);

            if (message == null)
            {
                _logger.LogWarning("Message not found for SMPP message ID: {SmppMessageId} (checked both MessageParts and Messages tables)", receipt.SmppMessageId);
                return;
            }

            _logger.LogInformation("Found single-part message ID {MessageId} for SMPP message ID {SmppMessageId}", 
                message.Id, receipt.SmppMessageId);

            // Map delivery status to MessageStatus
            var newStatus = MapDeliveryStatusToMessageStatus(receipt.DeliveryStatus);
            
            _logger.LogInformation("Updating single-part message ID {MessageId}: {OldStatus} -> {NewStatus} (SMPP: {SmppStatus})",
                message.Id, message.Status, newStatus, receipt.DeliveryStatus);

            // Update the message with delivery receipt information
            message.Status = newStatus;
            message.DeliveredAt = receipt.ReceivedAt;
            message.DeliveryReceiptText = receipt.ReceiptText;
            message.DeliveryStatus = receipt.DeliveryStatus;
            message.ErrorCode = receipt.ErrorCode;
            message.UpdatedAt = DateTime.UtcNow;

            // Update database
            _dbContext.Messages.Update(message);
            await _dbContext.SaveChangesAsync();

            _logger.LogInformation("Successfully processed single-part delivery receipt for message ID {MessageId}: Status={Status}", 
                message.Id, message.Status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing delivery receipt for SMPP message ID: {SmppMessageId}", receipt.SmppMessageId);
        }
    }

    /// <summary>
    /// Updates parent message status based on all its MessageParts
    /// </summary>
    private async Task UpdateParentMessageFromParts(Message parentMessage)
    {
        // Load all parts if not already loaded
        if (!parentMessage.Parts.Any())
        {
            await _dbContext.Entry(parentMessage)
                .Collection(m => m.Parts)
                .LoadAsync();
        }

        // Compute overall status using the Message.OverallStatus property
        var previousStatus = parentMessage.Status;
        parentMessage.Status = parentMessage.OverallStatus;
        parentMessage.UpdatedAt = DateTime.UtcNow;

        // Update delivery timestamp if all parts are delivered
        if (parentMessage.Status == MessageStatus.Delivered && !parentMessage.DeliveredAt.HasValue)
        {
            parentMessage.DeliveredAt = DateTime.UtcNow;
        }

        _logger.LogInformation("Updated parent message ID {MessageId} status: {PreviousStatus} -> {NewStatus} based on {PartCount} parts", 
            parentMessage.Id, previousStatus, parentMessage.Status, parentMessage.Parts.Count);
    }

    /// <summary>
    /// Maps SMPP delivery status to MessageStatus enum
    /// </summary>
    private static MessageStatus MapDeliveryStatusToMessageStatus(string deliveryStatus)
    {
        return deliveryStatus?.ToUpper() switch
        {
            "DELIVRD" => MessageStatus.Delivered,    // Message delivered successfully
            "EXPIRED" => MessageStatus.Expired,      // Message expired before delivery
            "DELETED" => MessageStatus.Expired,      // Message deleted (treat as expired)
            "UNDELIV" => MessageStatus.Undelivered,  // Message undelivered
            "ACCEPTD" => MessageStatus.Accepted,     // Message accepted (intermediate)
            "UNKNOWN" => MessageStatus.Unknown,      // Unknown delivery status
            "REJECTD" => MessageStatus.Rejected,     // Message rejected
            "ENROUTE" => MessageStatus.Sent,         // Message en route (intermediate, keep as Sent)
            _ => MessageStatus.Unknown
        };
    }
}