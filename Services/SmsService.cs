using Microsoft.EntityFrameworkCore;
using MessageHub.SmppChannel;
using MessageHub.HttpSmsChannel;
using MessageHub.Shared;

namespace MessageHub;

/// <summary>
/// Service for handling SMS operations using multiple channels (SMPP, HTTP, etc.)
/// </summary>
public class SmsService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ILogger<SmsService> _logger;
    private readonly ISmppChannel _smppChannel;
    private readonly Dictionary<ChannelType, ISmsChannel> _smsChannels;

    public SmsService(ApplicationDbContext dbContext, ILogger<SmsService> logger, 
                      ISmppChannel smppChannel, IEnumerable<ISmsChannel> smsChannels)
    {
        _dbContext = dbContext;
        _logger = logger;
        _smppChannel = smppChannel;
        
        // Build dictionary of available channels by type
        _smsChannels = smsChannels.ToDictionary(c => c.ChannelType, c => c);
        
        _logger.LogInformation("SmsService initialized with {ChannelCount} channels: {Channels}",
            _smsChannels.Count, string.Join(", ", _smsChannels.Values.Select(c => c.ProviderName)));
    }

    /// <summary>
    /// Creates and sends an SMS message directly using the specified channel
    /// </summary>
    public async Task<SmsMessage> CreateAndSendSmsAsync(string phoneNumber, string content, ChannelType channelType = ChannelType.SMPP)
    {
        _logger.LogInformation("Creating and sending new SMS to {PhoneNumber}, Content length: {ContentLength}", 
            phoneNumber, content.Length);

        var smsMessage = new SmsMessage
        {
            PhoneNumber = phoneNumber,
            Content = content,
            Status = SmsStatus.Pending,
            ChannelType = channelType,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _dbContext.SmsMessages.Add(smsMessage);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("SMS message created with ID: {SmsMessageId}", smsMessage.Id);

        // Send SMS via specified channel
        await SendSmsAsync(smsMessage.Id);

        return smsMessage;
    }

    /// <summary>
    /// Sends an SMS message by ID via the channel specified in the message
    /// </summary>
    public async Task SendSmsAsync(int smsMessageId)
    {
        _logger.LogInformation("Starting SMS send process for message ID: {SmsMessageId}", smsMessageId);
        
        var startTime = DateTime.UtcNow;
        
        try
        {
            var smsMessage = await _dbContext.SmsMessages.FindAsync(smsMessageId);
            if (smsMessage == null)
            {
                _logger.LogError("SMS message with ID {SmsMessageId} not found", smsMessageId);
                return;
            }

            _logger.LogInformation("Found SMS message: Phone={PhoneNumber}, Content length={ContentLength}", 
                smsMessage.PhoneNumber, smsMessage.Content.Length);

            try
            {
                // Get the appropriate channel for this message
                if (!_smsChannels.TryGetValue(smsMessage.ChannelType, out var channel))
                {
                    _logger.LogError("No channel available for type {ChannelType} for message ID: {SmsMessageId}", 
                        smsMessage.ChannelType, smsMessageId);
                    await UpdateSmsStatusAsync(smsMessage, SmsStatus.Failed);
                    return;
                }

                _logger.LogInformation("Sending SMS via {ChannelType} channel ({ProviderName}) to {PhoneNumber}", 
                    smsMessage.ChannelType, channel.ProviderName, smsMessage.PhoneNumber);

                // Send via the selected channel
                var result = await channel.SendSmsAsync(smsMessage);

                if (result.Success && !string.IsNullOrEmpty(result.ProviderMessageId))
                {
                    _logger.LogInformation("SMS sent successfully for message ID: {SmsMessageId}, Provider ID: {ProviderMessageId}, Channel: {ChannelType}", 
                        smsMessageId, result.ProviderMessageId, smsMessage.ChannelType);
                    
                    smsMessage.SentAt = DateTime.UtcNow;
                    smsMessage.ProviderMessageId = result.ProviderMessageId;
                    smsMessage.ProviderName = channel.ProviderName;
                    
                    // Store channel-specific data if available
                    if (result.ChannelData != null && result.ChannelData.Any())
                    {
                        smsMessage.ChannelData = System.Text.Json.JsonSerializer.Serialize(result.ChannelData);
                    }
                    
                    await UpdateSmsStatusAsync(smsMessage, SmsStatus.Sent);
                }
                else
                {
                    _logger.LogError("{ChannelType} send failed for message ID: {SmsMessageId}, Error: {ErrorMessage}", 
                        smsMessage.ChannelType, smsMessageId, result.ErrorMessage);
                    await UpdateSmsStatusAsync(smsMessage, SmsStatus.Failed);
                }
            }
            catch (Exception channelEx)
            {
                _logger.LogError(channelEx, "{ChannelType} send failed for message ID: {SmsMessageId}", 
                    smsMessage.ChannelType, smsMessageId);
                await UpdateSmsStatusAsync(smsMessage, SmsStatus.Failed);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending SMS for message ID: {SmsMessageId}", smsMessageId);
            
            var smsMessage = await _dbContext.SmsMessages.FindAsync(smsMessageId);
            if (smsMessage != null)
            {
                await UpdateSmsStatusAsync(smsMessage, SmsStatus.Failed);
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