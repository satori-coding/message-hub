namespace MessageHub.HttpSmsChannel;

/// <summary>
/// Configuration for HTTP SMS Channel
/// Supports multiple HTTP SMS providers (Twilio, AWS SNS, Azure SMS, etc.)
/// </summary>
public class HttpSmsChannelConfiguration
{
    /// <summary>
    /// Provider name (e.g., "Twilio", "AWS_SNS", "Azure_SMS")
    /// </summary>
    public string ProviderName { get; set; } = "HTTP";

    /// <summary>
    /// HTTP API endpoint URL
    /// </summary>
    public string ApiUrl { get; set; } = string.Empty;

    /// <summary>
    /// API key for authentication
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Authorization type: "Bearer", "ApiKey", etc.
    /// </summary>
    public string AuthorizationType { get; set; } = "Bearer";

    /// <summary>
    /// Header name for API key (if AuthorizationType is "ApiKey")
    /// </summary>
    public string ApiKeyHeaderName { get; set; } = "X-API-Key";

    /// <summary>
    /// From number or sender ID
    /// </summary>
    public string? FromNumber { get; set; }

    /// <summary>
    /// Custom request body template with placeholders: {PhoneNumber}, {Content}, {From}
    /// If not specified, uses generic format
    /// </summary>
    public string? RequestBodyTemplate { get; set; }

    /// <summary>
    /// Optional health check endpoint URL
    /// </summary>
    public string? HealthCheckUrl { get; set; }

    /// <summary>
    /// HTTP request timeout in milliseconds
    /// </summary>
    public int TimeoutMs { get; set; } = 30000; // 30 seconds default

    /// <summary>
    /// Maximum number of retry attempts
    /// </summary>
    public int MaxRetryAttempts { get; set; } = 2;

    /// <summary>
    /// Webhook endpoint URL for delivery receipts (if supported by provider)
    /// </summary>
    public string? WebhookUrl { get; set; }

    /// <summary>
    /// Webhook secret for verifying delivery receipts
    /// </summary>
    public string? WebhookSecret { get; set; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ProviderName))
            throw new ArgumentException("ProviderName is required", nameof(ProviderName));

        if (string.IsNullOrWhiteSpace(ApiUrl))
            throw new ArgumentException("ApiUrl is required", nameof(ApiUrl));

        if (!Uri.IsWellFormedUriString(ApiUrl, UriKind.Absolute))
            throw new ArgumentException("ApiUrl must be a valid absolute URL", nameof(ApiUrl));

        if (string.IsNullOrWhiteSpace(ApiKey))
            throw new ArgumentException("ApiKey is required", nameof(ApiKey));

        if (TimeoutMs <= 0)
            throw new ArgumentException("TimeoutMs must be positive", nameof(TimeoutMs));

        if (MaxRetryAttempts < 0)
            throw new ArgumentException("MaxRetryAttempts cannot be negative", nameof(MaxRetryAttempts));

        if (!string.IsNullOrEmpty(HealthCheckUrl) && !Uri.IsWellFormedUriString(HealthCheckUrl, UriKind.Absolute))
            throw new ArgumentException("HealthCheckUrl must be a valid absolute URL", nameof(HealthCheckUrl));

        if (!string.IsNullOrEmpty(WebhookUrl) && !Uri.IsWellFormedUriString(WebhookUrl, UriKind.Absolute))
            throw new ArgumentException("WebhookUrl must be a valid absolute URL", nameof(WebhookUrl));
    }
}

/// <summary>
/// Predefined configurations for popular SMS providers
/// </summary>
public static class HttpSmsProviderTemplates
{
    /// <summary>
    /// Twilio SMS API configuration template
    /// </summary>
    public static HttpSmsChannelConfiguration Twilio(string accountSid, string authToken, string fromNumber)
    {
        return new HttpSmsChannelConfiguration
        {
            ProviderName = "Twilio",
            ApiUrl = $"https://api.twilio.com/2010-04-01/Accounts/{accountSid}/Messages.json",
            ApiKey = authToken,
            AuthorizationType = "Basic", // Twilio uses Basic auth
            FromNumber = fromNumber,
            RequestBodyTemplate = "{{\"To\":\"{PhoneNumber}\",\"From\":\"{From}\",\"Body\":\"{Content}\"}}",
            HealthCheckUrl = $"https://api.twilio.com/2010-04-01/Accounts/{accountSid}.json",
            TimeoutMs = 10000
        };
    }

    /// <summary>
    /// AWS SNS SMS configuration template
    /// </summary>
    public static HttpSmsChannelConfiguration AwsSns(string accessKeyId, string secretAccessKey, string region = "us-east-1")
    {
        return new HttpSmsChannelConfiguration
        {
            ProviderName = "AWS_SNS",
            ApiUrl = $"https://sns.{region}.amazonaws.com/",
            ApiKey = secretAccessKey,
            AuthorizationType = "AWS", // Special AWS signing
            RequestBodyTemplate = "{{\"Message\":\"{Content}\",\"PhoneNumber\":\"{PhoneNumber}\"}}",
            TimeoutMs = 15000
        };
    }

    /// <summary>
    /// Generic HTTP SMS provider template
    /// </summary>
    public static HttpSmsChannelConfiguration Generic(string providerName, string apiUrl, string apiKey, string? fromNumber = null)
    {
        return new HttpSmsChannelConfiguration
        {
            ProviderName = providerName,
            ApiUrl = apiUrl,
            ApiKey = apiKey,
            AuthorizationType = "Bearer",
            FromNumber = fromNumber,
            TimeoutMs = 10000
        };
    }
}