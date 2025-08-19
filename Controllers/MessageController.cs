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
    public async Task<ActionResult<SmsStatusResponse>> GetSmsStatus(int id)
    {
        _logger.LogInformation("Getting SMS status for ID: {SmsMessageId}", id);

        var message = await _messageService.GetMessageAsync(id);
        
        if (message == null)
        {
            _logger.LogWarning("SMS message with ID {SmsMessageId} not found", id);
            return NotFound($"SMS message with ID {id} not found");
        }

        var response = new SmsStatusResponse
        {
            Id = message.Id,
            PhoneNumber = message.Recipient,
            Status = message.Status.ToString(),
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

        _logger.LogInformation("Retrieved SMS status for ID: {MessageId}, Status: {Status}", id, message.Status);

        return Ok(response);
    }

    [HttpGet]
    public async Task<ActionResult<List<SmsStatusResponse>>> GetAllSmsMessages()
    {
        _logger.LogInformation("Getting all SMS messages");

        var messages = await _messageService.GetAllMessagesAsync();
        
        var response = messages.Select(message => new SmsStatusResponse
        {
            Id = message.Id,
            PhoneNumber = message.Recipient,
            Status = message.Status.ToString(),
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

        _logger.LogInformation("Retrieved {Count} SMS messages", response.Count);

        return Ok(response);
    }

    [HttpPost("send")]
    public async Task<ActionResult<SendSmsResponse>> SendSms([FromBody] SendSmsRequest request)
    {
        _logger.LogInformation("Received request to send SMS to {PhoneNumber} via {ChannelType} channel", 
            request.PhoneNumber, request.ChannelType ?? ChannelType.SMPP);

        if (string.IsNullOrWhiteSpace(request.PhoneNumber) || string.IsNullOrWhiteSpace(request.Content))
        {
            _logger.LogWarning("Invalid SMS request: PhoneNumber or Content is empty");
            return BadRequest("PhoneNumber and Content are required");
        }

        if (request.Content.Length > 1000)
        {
            _logger.LogWarning("SMS content too long: {Length} characters", request.Content.Length);
            return BadRequest("SMS content cannot exceed 1000 characters");
        }

        try
        {
            // Use specified channel type or default to SMPP
            var channelType = request.ChannelType ?? ChannelType.SMPP;
            var message = await _messageService.CreateAndSendMessageAsync(request.PhoneNumber, request.Content, channelType);
            
            var response = new SendSmsResponse
            {
                Id = message.Id,
                PhoneNumber = message.Recipient,
                Status = message.Status.ToString(),
                CreatedAt = message.CreatedAt,
                Message = "SMS wurde erfolgreich zur Verarbeitung angenommen"
            };

            _logger.LogInformation("SMS request processed successfully with ID: {MessageId}", message.Id);
            
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing SMS send request");
            return StatusCode(500, "Fehler beim Verarbeiten der SMS-Anfrage");
        }
    }
}

public class SmsStatusResponse
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

public class SendSmsRequest
{
    public string PhoneNumber { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public ChannelType? ChannelType { get; set; } = null; // Optional channel selection
}

public class SendSmsResponse
{
    public int Id { get; set; }
    public string PhoneNumber { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string Message { get; set; } = string.Empty;
}