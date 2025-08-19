using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MessageHub.Channels.Smpp;

/// <summary>
/// Extension methods for configuring SMPP channel services
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds SMPP channel services to the service collection
    /// </summary>
    public static IServiceCollection AddSmppChannel(this IServiceCollection services, IConfiguration configuration)
    {
        // Read SMPP configuration
        var smppConfig = new SmppChannelConfiguration();
        configuration.GetSection("SmppSettings").Bind(smppConfig);
        
        // Register configuration
        services.AddSingleton(smppConfig);
        
        // Register SMPP channel as singleton (manages connection pool internally)
        services.AddSingleton<ISmppChannel>(serviceProvider =>
        {
            var logger = serviceProvider.GetRequiredService<ILogger<SmppChannel>>();
            return new SmppChannel(smppConfig, logger);
        });
        
        return services;
    }

    /// <summary>
    /// Adds SMPP channel services with custom configuration
    /// </summary>
    public static IServiceCollection AddSmppChannel(this IServiceCollection services, Action<SmppChannelConfiguration> configureOptions)
    {
        var config = new SmppChannelConfiguration();
        configureOptions(config);
        
        // Register configuration
        services.AddSingleton(config);
        
        // Register SMPP channel as singleton
        services.AddSingleton<ISmppChannel>(serviceProvider =>
        {
            var logger = serviceProvider.GetRequiredService<ILogger<SmppChannel>>();
            return new SmppChannel(config, logger);
        });
        
        return services;
    }
}