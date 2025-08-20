using Microsoft.AspNetCore.Mvc;
using MessageHub.Channels.Shared;

namespace MessageHub;

[ApiController]
[Route("api/[controller]")]
public class MessageController : ControllerBase
{
    private readonly MessageService _messageService;
    private readonly ILogger<MessageController> _logger;

    public MessageController(MessageService messageService, ILogger<MessageController> logger)
    {
        _messageService = messageService;
        _logger = logger;
    }

    [HttpGet("{id}/status")]
    public async Task<ActionResult<MessageStatusResponse>> GetMessageStatus(int id)
    {
        _logger.LogInformation("Getting message status for ID: {MessageId}", id);

        var message = await _messageService.GetMessageAsync(id);
        
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
        _logger.LogInformation("Getting all messages");

        var messages = await _messageService.GetAllMessagesAsync();
        
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
        _logger.LogInformation("Received request to send message to {PhoneNumber} via {ChannelType} channel", 
            request.PhoneNumber, request.ChannelType ?? ChannelType.SMPP);

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
            // Use specified channel type or default to SMPP
            var channelType = request.ChannelType ?? ChannelType.SMPP;
            var message = await _messageService.CreateAndSendMessageAsync(request.PhoneNumber, request.Content, channelType);
            
            var response = new SendMessageResponse
            {
                Id = message.Id,
                PhoneNumber = message.Recipient,
                Status = GetDisplayStatus(message), // Enhanced status display
                CreatedAt = message.CreatedAt,
                Message = "Nachricht wurde erfolgreich zur Verarbeitung angenommen"
            };

            _logger.LogInformation("Message request processed successfully with ID: {MessageId}", message.Id);
            
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message send request");
            return StatusCode(500, "Fehler beim Verarbeiten der Nachrichten-Anfrage");
        }
    }

    private string GetDisplayStatus(Message message)
    {
        return message.Status switch
        {
            MessageStatus.Sent => "Sent (DLR pending)",
            MessageStatus.AssumedDelivered => "Assumed Delivered (no DLR received)",
            MessageStatus.DeliveryUnknown => "Delivery Unknown (DLR timeout)",
            MessageStatus.Delivered => "Delivered (confirmed)",
            _ => message.Status.ToString()
        };
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
}

public class SendMessageResponse
{
    public int Id { get; set; }
    public string PhoneNumber { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string Message { get; set; } = string.Empty;
}