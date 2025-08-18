using Microsoft.AspNetCore.Mvc;
using MessageHub.Shared;

namespace MessageHub;

[ApiController]
[Route("api/[controller]")]
public class SmsController : ControllerBase
{
    private readonly SmsService _smsService;
    private readonly ILogger<SmsController> _logger;

    public SmsController(SmsService smsService, ILogger<SmsController> logger)
    {
        _smsService = smsService;
        _logger = logger;
    }

    [HttpGet("{id}/status")]
    public async Task<ActionResult<SmsStatusResponse>> GetSmsStatus(int id)
    {
        _logger.LogInformation("Getting SMS status for ID: {SmsMessageId}", id);

        var smsMessage = await _smsService.GetSmsMessageAsync(id);
        
        if (smsMessage == null)
        {
            _logger.LogWarning("SMS message with ID {SmsMessageId} not found", id);
            return NotFound($"SMS message with ID {id} not found");
        }

        var response = new SmsStatusResponse
        {
            Id = smsMessage.Id,
            PhoneNumber = smsMessage.PhoneNumber,
            Status = smsMessage.Status.ToString(),
            CreatedAt = smsMessage.CreatedAt,
            SentAt = smsMessage.SentAt,
            UpdatedAt = smsMessage.UpdatedAt,
            
            // Delivery Receipt Information
            ProviderMessageId = smsMessage.ProviderMessageId,
            DeliveredAt = smsMessage.DeliveredAt,
            DeliveryStatus = smsMessage.DeliveryStatus,
            ErrorCode = smsMessage.ErrorCode,
            DeliveryReceiptText = smsMessage.DeliveryReceiptText
        };

        _logger.LogInformation("Retrieved SMS status for ID: {SmsMessageId}, Status: {Status}", id, smsMessage.Status);

        return Ok(response);
    }

    [HttpGet]
    public async Task<ActionResult<List<SmsStatusResponse>>> GetAllSmsMessages()
    {
        _logger.LogInformation("Getting all SMS messages");

        var smsMessages = await _smsService.GetAllSmsMessagesAsync();
        
        var response = smsMessages.Select(sms => new SmsStatusResponse
        {
            Id = sms.Id,
            PhoneNumber = sms.PhoneNumber,
            Status = sms.Status.ToString(),
            CreatedAt = sms.CreatedAt,
            SentAt = sms.SentAt,
            UpdatedAt = sms.UpdatedAt,
            
            // Delivery Receipt Information
            ProviderMessageId = sms.ProviderMessageId,
            DeliveredAt = sms.DeliveredAt,
            DeliveryStatus = sms.DeliveryStatus,
            ErrorCode = sms.ErrorCode,
            DeliveryReceiptText = sms.DeliveryReceiptText
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
            var smsMessage = await _smsService.CreateAndSendSmsAsync(request.PhoneNumber, request.Content, channelType);
            
            var response = new SendSmsResponse
            {
                Id = smsMessage.Id,
                PhoneNumber = smsMessage.PhoneNumber,
                Status = smsMessage.Status.ToString(),
                CreatedAt = smsMessage.CreatedAt,
                Message = "SMS wurde erfolgreich zur Verarbeitung angenommen"
            };

            _logger.LogInformation("SMS request processed successfully with ID: {SmsMessageId}", smsMessage.Id);
            
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