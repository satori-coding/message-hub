using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using MessageHub.Shared;

namespace MessageHub.HttpSmsChannel;

/// <summary>
/// Extension methods for configuring HTTP SMS Channel services
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Add HTTP SMS Channel services to dependency injection
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configuration">HTTP SMS Channel configuration</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddHttpSmsChannel(
        this IServiceCollection services, 
        HttpSmsChannelConfiguration configuration)
    {
        if (configuration == null)
            throw new ArgumentNullException(nameof(configuration));

        // Validate configuration
        configuration.Validate();

        // Register configuration
        services.AddSingleton(configuration);

        // Configure HttpClient for SMS API calls
        services.AddHttpClient<HttpSmsChannel>(client =>
        {
            client.Timeout = TimeSpan.FromMilliseconds(configuration.TimeoutMs);
            client.DefaultRequestHeaders.Add("User-Agent", "MessageHub-HttpSmsChannel/1.0");
        });

        // Register HTTP SMS Channel as ISmsChannel
        services.AddScoped<ISmsChannel, HttpSmsChannel>(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<HttpSmsChannel>>();
            var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
            var httpClient = httpClientFactory.CreateClient(nameof(HttpSmsChannel));

            return new HttpSmsChannel(configuration, logger, httpClient);
        });

        return services;
    }

    /// <summary>
    /// Add HTTP SMS Channel services from IConfiguration
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configuration">Application configuration</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddHttpSmsChannel(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Create configuration from appsettings
        var httpSmsConfig = new HttpSmsChannelConfiguration();
        
        // Bind from HttpSmsChannel section or use default provider
        var section = configuration.GetSection("HttpSmsChannel");
        if (section.Exists())
        {
            section.Bind(httpSmsConfig);
        }
        else
        {
            // Default to a generic test configuration
            httpSmsConfig = HttpSmsProviderTemplates.Generic(
                "TestProvider",
                "https://api.test-sms-provider.com/send",
                "test-api-key",
                "TestSender"
            );
        }

        return services.AddHttpSmsChannel(httpSmsConfig);
    }

    /// <summary>
    /// Add HTTP SMS Channel services with Twilio configuration
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="accountSid">Twilio Account SID</param>
    /// <param name="authToken">Twilio Auth Token</param>
    /// <param name="fromNumber">Twilio phone number to send from</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddTwilioSmsChannel(
        this IServiceCollection services,
        string accountSid,
        string authToken,
        string fromNumber)
    {
        var configuration = HttpSmsProviderTemplates.Twilio(accountSid, authToken, fromNumber);
        return services.AddHttpSmsChannel(configuration);
    }

    /// <summary>
    /// Add HTTP SMS Channel services with AWS SNS configuration
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="accessKeyId">AWS Access Key ID</param>
    /// <param name="secretAccessKey">AWS Secret Access Key</param>
    /// <param name="region">AWS region</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddAwsSnsSmsChannel(
        this IServiceCollection services,
        string accessKeyId,
        string secretAccessKey,
        string region = "us-east-1")
    {
        var configuration = HttpSmsProviderTemplates.AwsSns(accessKeyId, secretAccessKey, region);
        return services.AddHttpSmsChannel(configuration);
    }

    /// <summary>
    /// Add HTTP SMS Channel services with generic HTTP provider configuration
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="providerName">Provider name</param>
    /// <param name="apiUrl">API endpoint URL</param>
    /// <param name="apiKey">API key for authentication</param>
    /// <param name="fromNumber">Optional from number</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddGenericHttpSmsChannel(
        this IServiceCollection services,
        string providerName,
        string apiUrl,
        string apiKey,
        string? fromNumber = null)
    {
        var configuration = HttpSmsProviderTemplates.Generic(providerName, apiUrl, apiKey, fromNumber);
        return services.AddHttpSmsChannel(configuration);
    }
}