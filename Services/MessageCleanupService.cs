using MessageHub.Channels.Shared;
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
        
        // Find messages that have been "Sent" for longer than timeout
        var timeoutThreshold = DateTime.UtcNow.AddMinutes(-30); // 30 minutes ago
        
        var timedOutMessages = await dbContext.Messages
            .Where(m => m.Status == MessageStatus.Sent && 
                       m.SentAt.HasValue && 
                       m.SentAt.Value < timeoutThreshold)
            .ToListAsync();

        if (timedOutMessages.Any())
        {
            _logger.LogInformation("Processing {Count} timed-out messages", timedOutMessages.Count);
            
            foreach (var message in timedOutMessages)
            {
                // Update status based on configuration or default
                message.Status = MessageStatus.AssumedDelivered;
                message.UpdatedAt = DateTime.UtcNow;
                message.DeliveryStatus = "TIMEOUT_ASSUMED_DELIVERED";
                
                _logger.LogInformation("Updated message ID {MessageId} from Sent to AssumedDelivered (timeout after {Timeout} minutes)", 
                    message.Id, 30);
            }

            await dbContext.SaveChangesAsync();
            _logger.LogInformation("Successfully updated {Count} timed-out messages to AssumedDelivered status", timedOutMessages.Count);
        }
        else
        {
            _logger.LogDebug("No timed-out messages found in current cleanup cycle");
        }
    }
}