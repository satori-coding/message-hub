using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MessageHub.Channels.Smpp;

/// <summary>
/// Extension methods for configuring SMPP channel services in dependency injection
/// PURPOSE: Provides easy setup of SMPP channel with proper configuration and logging
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds SMPP channel services to the service collection using configuration from appsettings.json
    /// SETUP:
    /// 1. Reads "SmppSettings" section from configuration (appsettings.json/Key Vault)
    /// 2. Creates SmppChannelConfiguration with server endpoints and credentials
    /// 3. Registers SmppChannel as singleton (manages connection pool for lifetime of app)
    /// 4. Injects logging for comprehensive SMPP operation monitoring
    /// 
    /// USAGE: In Program.cs call services.AddSmppChannel(configuration)
    /// </summary>
    public static IServiceCollection AddSmppChannel(this IServiceCollection services, IConfiguration configuration)
    {
        // 1. Read SMPP configuration from "SmppSettings" in appsettings.json
        //    This includes Host, Port, SystemId, Password, timeouts, etc.
        var smppConfig = new SmppChannelConfiguration();
        configuration.GetSection("SmppSettings").Bind(smppConfig);
        
        // 2. Register configuration as singleton for dependency injection
        services.AddSingleton(smppConfig);
        
        // 3. Register SMPP channel as singleton (manages connection pool internally)
        //    Singleton is important: connection pooling only works with single instance
        services.AddSingleton<ISmppChannel>(serviceProvider =>
        {
            var logger = serviceProvider.GetRequiredService<ILogger<SmppChannel>>();
            return new SmppChannel(smppConfig, logger);
        });
        
        return services;
    }

    /// <summary>
    /// Adds SMPP channel services with programmatic configuration (alternative to appsettings.json)
    /// PURPOSE: Allows setting SMPP configuration in code instead of configuration files
    /// USAGE: services.AddSmppChannel(config => { config.Host = "smpp.provider.com"; config.Port = 2775; })
    /// </summary>
    public static IServiceCollection AddSmppChannel(this IServiceCollection services, Action<SmppChannelConfiguration> configureOptions)
    {
        // 1. Create configuration and apply custom settings via lambda
        var config = new SmppChannelConfiguration();
        configureOptions(config);
        
        // 2. Register configuration as singleton
        services.AddSingleton(config);
        
        // 3. Register SMPP channel as singleton with dependency injection
        services.AddSingleton<ISmppChannel>(serviceProvider =>
        {
            var logger = serviceProvider.GetRequiredService<ILogger<SmppChannel>>();
            return new SmppChannel(config, logger);
        });
        
        return services;
    }
}