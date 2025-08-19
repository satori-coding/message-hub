using System.Text.Json;
using Microsoft.Extensions.Logging;
using MessageHub.Channels.Shared;

namespace MessageHub.Channels.Http;

/// <summary>
/// HTTP/REST SMS Channel implementation
/// Supports multiple HTTP SMS providers (Twilio, AWS SNS, Azure SMS, etc.)
/// </summary>
public class HttpSmsChannel : IMessageChannel
{
    private readonly HttpSmsChannelConfiguration _configuration;
    private readonly ILogger<HttpSmsChannel> _logger;
    private readonly HttpClient _httpClient;

    public ChannelType ChannelType => ChannelType.HTTP;
    public string ProviderName => _configuration.ProviderName;

    public HttpSmsChannel(HttpSmsChannelConfiguration configuration, ILogger<HttpSmsChannel> logger, HttpClient httpClient)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        
        // Validate configuration
        _configuration.Validate();

        _logger.LogInformation("HTTP SMS Channel initialized for provider: {ProviderName}", _configuration.ProviderName);
    }

    public async Task<MessageResult> SendAsync(Message message)
    {
        if (message == null)
            return MessageResult.CreateFailure("Message cannot be null");

        if (string.IsNullOrWhiteSpace(message.Recipient) || string.IsNullOrWhiteSpace(message.Content))
            return MessageResult.CreateFailure("Phone number and content are required");

        _logger.LogInformation("Sending SMS via HTTP channel to {Recipient} using provider {ProviderName}",
            message.Recipient, _configuration.ProviderName);

        var startTime = DateTime.UtcNow;

        try
        {
            // Build HTTP request based on provider configuration
            var httpRequest = await BuildHttpRequestAsync(message);

            // Send HTTP request
            var response = await _httpClient.SendAsync(httpRequest);
            var responseContent = await response.Content.ReadAsStringAsync();

            var duration = DateTime.UtcNow - startTime;
            _logger.LogDebug("HTTP SMS request completed in {Duration}ms. Status: {StatusCode}",
                duration.TotalMilliseconds, response.StatusCode);

            // Parse response based on provider
            var result = await ParseResponseAsync(response, responseContent);

            if (result.Success)
            {
                _logger.LogInformation("SMS sent successfully via HTTP. Provider Message ID: {ProviderMessageId}",
                    result.ProviderMessageId);
                
                // Store additional HTTP channel data
                result.ChannelData["HttpStatusCode"] = (int)response.StatusCode;
                result.ChannelData["ResponseTime"] = duration.TotalMilliseconds;
                result.ChannelData["ProviderName"] = _configuration.ProviderName;
            }
            else
            {
                _logger.LogError("HTTP SMS send failed. Error: {ErrorMessage}, Code: {ErrorCode}",
                    result.ErrorMessage, result.ErrorCode);
            }

            return result;
        }
        catch (HttpRequestException httpEx)
        {
            var duration = DateTime.UtcNow - startTime;
            _logger.LogError(httpEx, "HTTP request failed after {Duration}ms. Provider: {ProviderName}",
                duration.TotalMilliseconds, _configuration.ProviderName);

            return MessageResult.CreateFailure(
                $"HTTP request failed: {httpEx.Message}",
                networkErrorCode: httpEx.Data["StatusCode"] as int?
            );
        }
        catch (TaskCanceledException timeoutEx)
        {
            var duration = DateTime.UtcNow - startTime;
            _logger.LogError(timeoutEx, "HTTP request timed out after {Duration}ms. Provider: {ProviderName}",
                duration.TotalMilliseconds, _configuration.ProviderName);

            return MessageResult.CreateFailure("HTTP request timed out", networkErrorCode: 408);
        }
        catch (Exception ex)
        {
            var duration = DateTime.UtcNow - startTime;
            _logger.LogError(ex, "Unexpected error during HTTP SMS send after {Duration}ms. Provider: {ProviderName}",
                duration.TotalMilliseconds, _configuration.ProviderName);

            return MessageResult.CreateFailure($"Unexpected error: {ex.Message}");
        }
    }

    public async Task<bool> IsHealthyAsync()
    {
        try
        {
            // Simple health check - verify configuration and test HTTP client
            if (string.IsNullOrEmpty(_configuration.ApiUrl) || string.IsNullOrEmpty(_configuration.ApiKey))
            {
                _logger.LogWarning("HTTP SMS Channel health check failed: Missing configuration");
                return false;
            }

            // Optional: Send a simple GET request to provider health endpoint if available
            if (!string.IsNullOrEmpty(_configuration.HealthCheckUrl))
            {
                var request = new HttpRequestMessage(HttpMethod.Get, _configuration.HealthCheckUrl);
                if (!string.IsNullOrEmpty(_configuration.ApiKey))
                {
                    request.Headers.Add("Authorization", $"Bearer {_configuration.ApiKey}");
                }

                using var response = await _httpClient.SendAsync(request);
                var isHealthy = response.IsSuccessStatusCode;

                _logger.LogDebug("HTTP SMS Channel health check completed. Healthy: {IsHealthy}, Status: {StatusCode}",
                    isHealthy, response.StatusCode);

                return isHealthy;
            }

            // If no health check URL, assume healthy if configuration is valid
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HTTP SMS Channel health check failed");
            return false;
        }
    }

    private async Task<HttpRequestMessage> BuildHttpRequestAsync(Message message)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _configuration.ApiUrl);

        // Add authentication headers
        if (!string.IsNullOrEmpty(_configuration.ApiKey))
        {
            if (_configuration.AuthorizationType == "Bearer")
                request.Headers.Add("Authorization", $"Bearer {_configuration.ApiKey}");
            else if (_configuration.AuthorizationType == "ApiKey")
                request.Headers.Add(_configuration.ApiKeyHeaderName, _configuration.ApiKey);
        }

        // Build request body based on provider template
        var requestBody = BuildRequestBody(message);
        request.Content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json");

        _logger.LogDebug("Built HTTP request for {ProviderName}. URL: {Url}", 
            _configuration.ProviderName, _configuration.ApiUrl);

        return request;
    }

    private string BuildRequestBody(Message message)
    {
        // Use provider-specific template or generic format
        var body = new Dictionary<string, object>();

        // Apply provider-specific body template
        if (!string.IsNullOrEmpty(_configuration.RequestBodyTemplate))
        {
            // Replace placeholders in template
            var template = _configuration.RequestBodyTemplate;
            template = template.Replace("{PhoneNumber}", message.Recipient);
            template = template.Replace("{Content}", message.Content);
            template = template.Replace("{From}", _configuration.FromNumber ?? "MessageHub");
            
            return template;
        }

        // Generic format (works with many providers)
        body["to"] = message.Recipient;
        body["message"] = message.Content;
        if (!string.IsNullOrEmpty(_configuration.FromNumber))
            body["from"] = _configuration.FromNumber;

        return JsonSerializer.Serialize(body);
    }

    private async Task<MessageResult> ParseResponseAsync(HttpResponseMessage response, string responseContent)
    {
        try
        {
            if (response.IsSuccessStatusCode)
            {
                // Try to extract message ID from response
                var messageId = ExtractMessageIdFromResponse(responseContent);
                
                var channelData = new Dictionary<string, object>
                {
                    ["RawResponse"] = responseContent,
                    ["HttpStatusCode"] = (int)response.StatusCode
                };

                return MessageResult.CreateSuccess(messageId ?? Guid.NewGuid().ToString(), channelData);
            }
            else
            {
                _logger.LogError("HTTP SMS API returned error. Status: {StatusCode}, Response: {Response}",
                    response.StatusCode, responseContent);

                return MessageResult.CreateFailure(
                    $"HTTP API error: {response.StatusCode}",
                    errorCode: (int)response.StatusCode,
                    networkErrorCode: (int)response.StatusCode
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing HTTP SMS response: {Response}", responseContent);
            return MessageResult.CreateFailure($"Response parsing error: {ex.Message}");
        }
    }

    private string? ExtractMessageIdFromResponse(string responseContent)
    {
        try
        {
            if (string.IsNullOrEmpty(responseContent))
                return null;

            // Try to parse JSON response and extract message ID
            using var document = JsonDocument.Parse(responseContent);
            var root = document.RootElement;

            // Common field names for message ID across different providers
            var messageIdFields = new[] { "messageId", "message_id", "id", "sid", "messageUuid", "uuid" };
            
            foreach (var fieldName in messageIdFields)
            {
                if (root.TryGetProperty(fieldName, out var property))
                {
                    return property.GetString();
                }
            }

            _logger.LogDebug("Could not extract message ID from response: {Response}", responseContent);
            return null;
        }
        catch (JsonException)
        {
            _logger.LogDebug("Response is not valid JSON, using as plain text message ID: {Response}", responseContent);
            return responseContent.Length > 100 ? responseContent[..100] : responseContent;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting message ID from response");
            return null;
        }
    }
}