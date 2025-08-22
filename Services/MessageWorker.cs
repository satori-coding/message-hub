using MassTransit;
using MessageHub.Channels.Shared;

namespace MessageHub.Services;

/// <summary>
/// Background worker that consumes SendMessageCommand from RabbitMQ queue
/// and processes SMS messages asynchronously
/// </summary>
public class MessageWorker : IConsumer<SendMessageCommand>
{
    private readonly MessageService _messageService;
    private readonly ILogger<MessageWorker> _logger;

    public MessageWorker(MessageService messageService, ILogger<MessageWorker> logger)
    {
        _messageService = messageService;
        _logger = logger;
    }

    /// <summary>
    /// Processes the queued message command
    /// </summary>
    public async Task Consume(ConsumeContext<SendMessageCommand> context)
    {
        var command = context.Message;
        
        _logger.LogInformation("Processing queued message command: MessageID={MessageId}, Phone={PhoneNumber}, Channel={ChannelType}, Tenant={TenantId}", 
            command.MessageId, command.PhoneNumber, command.ChannelType, command.TenantId);

        try
        {
            // Update status from Queued to Pending
            await _messageService.UpdateMessageStatusAsync(command.MessageId, MessageStatus.Pending);
            
            // Process the message using existing SendMessageAsync logic
            await _messageService.SendMessageAsync(command.MessageId, command.ChannelName);
            
            _logger.LogInformation("Successfully processed queued message: MessageID={MessageId}", command.MessageId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing queued message: MessageID={MessageId}", command.MessageId);
            
            // Update status to Failed on error
            try
            {
                await _messageService.UpdateMessageStatusAsync(command.MessageId, MessageStatus.Failed);
            }
            catch (Exception statusEx)
            {
                _logger.LogError(statusEx, "Failed to update message status to Failed for MessageID={MessageId}", command.MessageId);
            }
            
            // Re-throw to let MassTransit handle retry logic
            throw;
        }
    }
}