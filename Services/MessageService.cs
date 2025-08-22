using Microsoft.EntityFrameworkCore;
using MessageHub.Channels.Smpp;
using MessageHub.Channels.Http;
using MessageHub.Channels.Shared;
using MessageHub.Services;
using MessageHub.DomainModels;

namespace MessageHub;

/// <summary>
/// Service for handling message operations using multiple channels with multi-tenant support
/// </summary>
public class MessageService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ILogger<MessageService> _logger;
    private readonly IConfiguration _configuration;
    private readonly ITenantChannelManager _tenantChannelManager;
    private readonly Dictionary<ChannelType, IMessageChannel> _legacyChannels; // For backward compatibility

    public MessageService(
        ApplicationDbContext dbContext, 
        ILogger<MessageService> logger,
        IConfiguration configuration,
        ITenantChannelManager tenantChannelManager,
        IEnumerable<IMessageChannel> legacyChannels)
    {
        _dbContext = dbContext;
        _logger = logger;
        _configuration = configuration;
        _tenantChannelManager = tenantChannelManager;
        
        // Build dictionary of legacy channels for backward compatibility (single-tenant mode)
        _legacyChannels = legacyChannels.ToDictionary(c => c.ChannelType, c => c);
        
        var isMultiTenant = _configuration.GetValue<bool>("MultiTenantSettings:EnableMultiTenant", false);
        _logger.LogInformation("MessageService initialized in {Mode} mode with {ChannelCount} legacy channels: {Channels}",
            isMultiTenant ? "multi-tenant" : "single-tenant",
            _legacyChannels.Count, 
            string.Join(", ", _legacyChannels.Values.Select(c => c.ProviderName)));
    }

    /// <summary>
    /// Creates a new message in the database with Queued status for background processing
    /// </summary>
    public async Task<Message> CreateMessageAsync(
        string recipient, 
        string content, 
        ChannelType channelType = ChannelType.SMPP,
        int? tenantId = null)
    {
        _logger.LogInformation("Creating new message for queue processing to {Recipient}, Content length: {ContentLength}", 
            recipient, content.Length);

        // Get tenant information if provided
        Tenant? tenant = null;
        if (tenantId.HasValue)
        {
            // Note: We don't fetch tenant here to avoid circular dependency, 
            // just set the ID and name will be set during processing
        }

        var message = new Message
        {
            Recipient = recipient,
            Content = content,
            Status = MessageStatus.Queued,  // NEW: Start with Queued status
            ChannelType = channelType,
            TenantId = tenantId,
            TenantName = tenant?.Name,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _dbContext.Messages.Add(message);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Message created with ID: {MessageId} and queued for processing", message.Id);

        return message;
    }

    /// <summary>
    /// Creates and sends a message directly using the specified channel (multi-tenant aware)
    /// </summary>
    public async Task<Message> CreateAndSendMessageAsync(
        string recipient, 
        string content, 
        ChannelType channelType = ChannelType.SMPP,
        int? tenantId = null,
        string? channelName = null)
    {
        _logger.LogInformation("Creating and sending new message to {Recipient}, Content length: {ContentLength}", 
            recipient, content.Length);

        // Get tenant information if provided
        Tenant? tenant = null;
        if (tenantId.HasValue)
        {
            // Note: We don't fetch tenant here to avoid circular dependency, 
            // just set the ID and name will be set during processing
        }

        var message = new Message
        {
            Recipient = recipient,
            Content = content,
            Status = MessageStatus.Pending,
            ChannelType = channelType,
            TenantId = tenantId,
            TenantName = tenant?.Name, // Will be set during channel processing if needed
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _dbContext.Messages.Add(message);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Message created with ID: {MessageId}", message.Id);

        // Send message via specified channel
        await SendMessageAsync(message.Id, channelName);

        return message;
    }

    /// <summary>
    /// Sends a message by ID via the channel specified in the message (multi-tenant aware)
    /// </summary>
    public async Task SendMessageAsync(int messageId, string? channelName = null)
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
                IMessageChannel? channel = null;
                
                // Multi-tenant mode: Get tenant-specific channel
                if (message.TenantId.HasValue)
                {
                    channel = await _tenantChannelManager.GetChannelAsync(message.TenantId.Value, channelName);
                    if (channel == null)
                    {
                        _logger.LogError("No tenant channel available for tenant {TenantId}, channel {ChannelName} for message ID: {MessageId}", 
                            message.TenantId, channelName ?? "default", messageId);
                        await UpdateMessageStatusAsync(message, MessageStatus.Failed);
                        return;
                    }
                }
                else
                {
                    // Legacy single-tenant mode
                    if (!_legacyChannels.TryGetValue(message.ChannelType, out channel))
                    {
                        _logger.LogError("No legacy channel available for type {ChannelType} for message ID: {MessageId}", 
                            message.ChannelType, messageId);
                        await UpdateMessageStatusAsync(message, MessageStatus.Failed);
                        return;
                    }
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
    /// Updates message status by ID for background processing
    /// </summary>
    public async Task UpdateMessageStatusAsync(int messageId, MessageStatus status)
    {
        _logger.LogInformation("Updating message ID {MessageId} status to {NewStatus}", messageId, status);

        var message = await _dbContext.Messages.FindAsync(messageId);
        if (message == null)
        {
            _logger.LogError("Message with ID {MessageId} not found for status update", messageId);
            return;
        }

        var oldStatus = message.Status;
        message.Status = status;
        message.UpdatedAt = DateTime.UtcNow;

        _dbContext.Messages.Update(message);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Message ID {MessageId} status updated from {OldStatus} to {NewStatus}", 
            messageId, oldStatus, status);
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
    /// Gets a message by ID with tenant filtering
    /// </summary>
    public async Task<Message?> GetMessageAsync(int id, int? tenantId = null)
    {
        _logger.LogInformation("Retrieving message with ID: {MessageId} (Tenant: {TenantId})", id, tenantId);
        
        if (tenantId.HasValue)
        {
            // Multi-tenant mode: Only return message if it belongs to the tenant
            return await _dbContext.Messages
                .Include(m => m.Parts)
                .FirstOrDefaultAsync(m => m.Id == id && m.TenantId == tenantId);
        }
        else
        {
            // Single-tenant mode: Return any message (for backward compatibility)
            return await _dbContext.Messages
                .Include(m => m.Parts)
                .FirstOrDefaultAsync(m => m.Id == id);
        }
    }

    /// <summary>
    /// Gets all messages ordered by creation date with tenant filtering
    /// </summary>
    public async Task<List<Message>> GetAllMessagesAsync(int? tenantId = null)
    {
        _logger.LogInformation("Retrieving messages (Tenant: {TenantId})", tenantId);
        
        var query = _dbContext.Messages.Include(m => m.Parts);
        
        if (tenantId.HasValue)
        {
            // Multi-tenant mode: Only return messages belonging to the tenant
            return await query
                .Where(m => m.TenantId == tenantId)
                .OrderByDescending(s => s.CreatedAt)
                .ToListAsync();
        }
        else
        {
            // Single-tenant mode: Return all messages (for backward compatibility)
            return await query
                .OrderByDescending(s => s.CreatedAt)
                .ToListAsync();
        }
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