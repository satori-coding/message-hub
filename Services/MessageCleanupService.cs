using MessageHub.Channels.Shared;
using MessageHub.DomainModels;
using Microsoft.EntityFrameworkCore;

namespace MessageHub.Services;

/// <summary>
/// Background service that handles timeout-based status updates for messages
/// that don't receive delivery receipts within the expected timeframe
/// </summary>
public class MessageCleanupService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<MessageCleanupService> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromMinutes(5); // Run every 5 minutes

    public MessageCleanupService(IServiceProvider serviceProvider, ILogger<MessageCleanupService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MessageCleanupService started - checking for timed-out messages every {Interval}", _interval);
        
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingMessages();
                await Task.Delay(_interval, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in message cleanup service");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); // Wait 30s on error
            }
        }
        
        _logger.LogInformation("MessageCleanupService stopping");
    }

    private async Task ProcessPendingMessages()
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        // Find all "Sent" messages  
        var sentMessages = await dbContext.Messages
            .Where(m => m.Status == MessageStatus.Sent && m.SentAt.HasValue)
            .ToListAsync();
            
        // Get all tenants with their channel configurations
        var tenantsWithChannels = await dbContext.Tenants
            .Include(t => t.ChannelConfigurations)
            .Where(t => t.IsActive)
            .ToListAsync();

        var processedCount = 0;
        var currentTime = DateTime.UtcNow;

        foreach (var message in sentMessages)
        {
            // Find the tenant for this message
            var tenant = tenantsWithChannels.FirstOrDefault(t => t.Id == message.TenantId);
            if (tenant == null)
            {
                _logger.LogWarning("No active tenant found for message ID {MessageId} (TenantId: {TenantId})", 
                    message.Id, message.TenantId);
                continue;
            }
            
            // Find the channel configuration for this message
            var channelConfig = tenant.ChannelConfigurations.FirstOrDefault(c => 
                c.ChannelName == message.ProviderName || c.IsDefault);
                
            if (channelConfig == null)
            {
                _logger.LogWarning("No channel configuration found for message ID {MessageId}, tenant '{Tenant}'", 
                    message.Id, tenant.Name);
                continue;
            }

            // Only process SMPP messages from channels that don't expect delivery receipts
            if (channelConfig is TenantSmppConfiguration smppConfig)
            {
                if (smppConfig.ExpectDeliveryReceipts == true)
                {
                    continue; // Skip messages from channels that expect DLRs
                }
                
                // Check if message has timed out based on tenant configuration
                var timeoutMinutes = smppConfig.DeliveryReceiptTimeoutMinutes;
                var timeoutThreshold = message.SentAt.Value.AddMinutes(timeoutMinutes);
                
                if (currentTime >= timeoutThreshold)
                {
                    // Parse timeout status from configuration
                    var timeoutStatusString = smppConfig.TimeoutStatus;
                    if (Enum.TryParse<MessageStatus>(timeoutStatusString, out var timeoutStatus))
                    {
                        message.Status = timeoutStatus;
                    }
                    else
                    {
                        message.Status = MessageStatus.AssumedDelivered; // Fallback
                        _logger.LogWarning("Invalid TimeoutStatus '{Status}' for tenant '{Tenant}', using AssumedDelivered", 
                            timeoutStatusString, tenant.Name);
                    }
                    
                    message.UpdatedAt = currentTime;
                    message.DeliveryStatus = $"TIMEOUT_{timeoutStatus.ToString().ToUpper()}";
                    
                    _logger.LogInformation("Updated message ID {MessageId} from Sent to {NewStatus} (timeout after {Timeout} minutes for tenant '{Tenant}')", 
                        message.Id, message.Status, timeoutMinutes, tenant.Name);
                        
                    processedCount++;
                }
            }
        }

        if (processedCount > 0)
        {
            await dbContext.SaveChangesAsync();
            _logger.LogInformation("Successfully updated {Count} timed-out messages based on tenant configurations", processedCount);
        }
        else
        {
            _logger.LogDebug("No timed-out messages found in current cleanup cycle");
        }
    }
}