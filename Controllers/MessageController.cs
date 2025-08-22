using Microsoft.AspNetCore.Mvc;
using MessageHub.Channels.Shared;
using MessageHub.Services;
using MessageHub.DomainModels;
using MassTransit;

namespace MessageHub;

[ApiController]
[Route("api/[controller]")]
public class MessageController : ControllerBase
{
    private readonly MessageService _messageService;
    private readonly ITenantService _tenantService;
    private readonly ILogger<MessageController> _logger;
    private readonly IConfiguration _configuration;
    private readonly IBus _bus;

    public MessageController(
        MessageService messageService, 
        ITenantService tenantService,
        ILogger<MessageController> logger,
        IConfiguration configuration,
        IBus bus)
    {
        _messageService = messageService;
        _tenantService = tenantService;
        _logger = logger;
        _configuration = configuration;
        _bus = bus;
    }

    [HttpGet("{id}/status")]
    public async Task<ActionResult<MessageStatusResponse>> GetMessageStatus(int id)
    {
        // Validate tenant (always required in multi-tenant architecture)
        var tenantValidation = await ValidateTenantAsync();
        if (tenantValidation.Tenant == null)
        {
            return tenantValidation.ErrorResult!;
        }

        _logger.LogInformation("Getting message status for ID: {MessageId} (Tenant: {TenantId})", 
            id, tenantValidation.Tenant?.Id);

        var message = await _messageService.GetMessageAsync(id, tenantValidation.Tenant?.Id);
        
        if (message == null)
        {
            _logger.LogWarning("Message with ID {MessageId} not found", id);
            return NotFound($"Message with ID {id} not found");
        }

        var response = new MessageStatusResponse
        {
            Id = message.Id,
            PhoneNumber = message.Recipient,
            Status = GetDisplayStatus(message), // Enhanced status display
            CreatedAt = message.CreatedAt,
            SentAt = message.SentAt,
            UpdatedAt = message.UpdatedAt,
            
            // Delivery Receipt Information
            ProviderMessageId = message.ProviderMessageId,
            DeliveredAt = message.DeliveredAt,
            DeliveryStatus = message.DeliveryStatus,
            ErrorCode = message.ErrorCode,
            DeliveryReceiptText = message.DeliveryReceiptText
        };

        _logger.LogInformation("Retrieved message status for ID: {MessageId}, Status: {Status}", id, message.Status);

        return Ok(response);
    }

    [HttpGet]
    public async Task<ActionResult<List<MessageStatusResponse>>> GetAllMessages()
    {
        // Validate tenant (always required in multi-tenant architecture)
        var tenantValidation = await ValidateTenantAsync();
        if (tenantValidation.Tenant == null)
        {
            return tenantValidation.ErrorResult!;
        }

        _logger.LogInformation("Getting all messages (Tenant: {TenantId})", tenantValidation.Tenant?.Id);

        var messages = await _messageService.GetAllMessagesAsync(tenantValidation.Tenant?.Id);
        
        var response = messages.Select(message => new MessageStatusResponse
        {
            Id = message.Id,
            PhoneNumber = message.Recipient,
            Status = GetDisplayStatus(message), // Enhanced status display
            CreatedAt = message.CreatedAt,
            SentAt = message.SentAt,
            UpdatedAt = message.UpdatedAt,
            
            // Delivery Receipt Information
            ProviderMessageId = message.ProviderMessageId,
            DeliveredAt = message.DeliveredAt,
            DeliveryStatus = message.DeliveryStatus,
            ErrorCode = message.ErrorCode,
            DeliveryReceiptText = message.DeliveryReceiptText
        }).ToList();

        _logger.LogInformation("Retrieved {Count} messages", response.Count);

        return Ok(response);
    }

    [HttpPost("send")]
    public async Task<ActionResult<SendMessageResponse>> SendMessage([FromBody] SendMessageRequest request)
    {
        // Validate tenant (always required in multi-tenant architecture)
        var tenantValidation = await ValidateTenantAsync();
        if (tenantValidation.Tenant == null)
        {
            return tenantValidation.ErrorResult!;
        }

        _logger.LogInformation("Received request to send message to {PhoneNumber} via {ChannelType} channel (Tenant: {TenantId})", 
            request.PhoneNumber, request.ChannelType ?? ChannelType.SMPP, tenantValidation.Tenant?.Id);

        if (string.IsNullOrWhiteSpace(request.PhoneNumber) || string.IsNullOrWhiteSpace(request.Content))
        {
            _logger.LogWarning("Invalid message request: PhoneNumber or Content is empty");
            return BadRequest("PhoneNumber and Content are required");
        }

        // Allow longer messages - SMPP library will handle automatic splitting
        // Reasonable limit for very long messages (SMS supports up to 255 parts * ~153 chars = ~39k chars)
        if (request.Content.Length > 10000)
        {
            _logger.LogWarning("Message content too long: {Length} characters", request.Content.Length);
            return BadRequest("Message content cannot exceed 10000 characters");
        }

        try
        {
            // 1. Create message in database with Queued status
            var channelType = request.ChannelType ?? ChannelType.SMPP;
            var message = await _messageService.CreateMessageAsync(
                request.PhoneNumber, 
                request.Content, 
                channelType, 
                tenantValidation.Tenant?.Id);
            
            // 2. Publish command to queue for background processing
            var command = new SendMessageCommand(
                request.PhoneNumber,
                request.Content,
                channelType,
                tenantValidation.Tenant?.Id,
                request.ChannelName,
                message.Id);
                
            await _bus.Publish(command);
            
            // 3. Generate status URL for client polling
            var statusUrl = $"{Request.Scheme}://{Request.Host}/api/message/{message.Id}/status";
            
            // 4. Return immediate response with status URL
            var response = new SendMessageResponse
            {
                Id = message.Id,
                PhoneNumber = message.Recipient,
                Status = GetDisplayStatus(message), // "Queued for processing"
                StatusUrl = statusUrl,  // NEW: Direct status query URL
                CreatedAt = message.CreatedAt,
                Message = GetStatusMessage(message.Status)
            };

            _logger.LogInformation("Message queued successfully with ID: {MessageId}, StatusUrl: {StatusUrl}", 
                message.Id, statusUrl);
            
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message send request");
            return StatusCode(500, "Error processing message request");
        }
    }

    private string GetDisplayStatus(Message message)
    {
        return message.Status switch
        {
            MessageStatus.Queued => "Queued for processing",
            MessageStatus.Pending => "Processing...",
            MessageStatus.Sent => "Sent (awaiting delivery confirmation)",
            MessageStatus.AssumedDelivered => "Assumed Delivered (no DLR received)",
            MessageStatus.DeliveryUnknown => "Delivery Unknown (DLR timeout)",
            MessageStatus.Delivered => "Delivered (confirmed)",
            MessageStatus.PartiallyDelivered => "Partially Delivered (multi-part SMS)",
            _ => message.Status.ToString()
        };
    }

    private string GetStatusMessage(MessageStatus status)
    {
        return status switch
        {
            MessageStatus.Queued => "Message queued for processing",
            MessageStatus.Pending => "Message is being processed",
            MessageStatus.Sent => "Message sent successfully, awaiting delivery confirmation",
            MessageStatus.Failed => "Message delivery failed",
            MessageStatus.Delivered => "Message delivered successfully",
            MessageStatus.PartiallyDelivered => "Some SMS parts delivered, others pending or failed",
            MessageStatus.AssumedDelivered => "Message sent successfully, delivery assumed (no receipt received)",
            MessageStatus.DeliveryUnknown => "Message sent but delivery status unknown",
            MessageStatus.Expired => "Message expired before delivery",
            MessageStatus.Rejected => "Message rejected by network or recipient",
            MessageStatus.Undelivered => "Message could not be delivered",
            MessageStatus.Unknown => "Message status unknown",
            MessageStatus.Accepted => "Message accepted by provider",
            _ => "Message processed"
        };
    }

    private async Task<(Tenant? Tenant, ActionResult? ErrorResult)> ValidateTenantAsync()
    {
        var subscriptionKey = Request.Headers["X-Subscription-Key"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(subscriptionKey))
        {
            _logger.LogWarning("Missing X-Subscription-Key header (required for multi-tenant architecture)");
            return (null, Unauthorized("X-Subscription-Key header is required"));
        }

        var tenant = await _tenantService.GetTenantBySubscriptionKeyAsync(subscriptionKey);
        if (tenant == null)
        {
            _logger.LogWarning("Invalid subscription key provided: {SubscriptionKey}", 
                subscriptionKey.Substring(0, Math.Min(8, subscriptionKey.Length)) + "***");
            return (null, Unauthorized("Invalid subscription key"));
        }

        if (!_tenantService.ValidateTenantAccess(tenant))
        {
            _logger.LogWarning("Tenant access validation failed for tenant {TenantId} ({TenantName})", 
                tenant.Id, tenant.Name);
            return (null, StatusCode(403, "Tenant access denied"));
        }

        return (tenant, null);
    }

}

public class MessageStatusResponse
{
    public int Id { get; set; }
    public string PhoneNumber { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? SentAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    
    // Delivery Receipt Information
    public string? ProviderMessageId { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public string? DeliveryStatus { get; set; }
    public int? ErrorCode { get; set; }
    public string? DeliveryReceiptText { get; set; }
}

public class SendMessageRequest
{
    public string PhoneNumber { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public ChannelType? ChannelType { get; set; } = null; // Optional channel selection
    public string? ChannelName { get; set; } = null; // Optional specific channel name for multi-tenant
}

public class SendMessageResponse
{
    public int Id { get; set; }
    public string PhoneNumber { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string StatusUrl { get; set; } = string.Empty;  // NEW: Direct status query URL
    public DateTime CreatedAt { get; set; }
    public string Message { get; set; } = string.Empty;
}