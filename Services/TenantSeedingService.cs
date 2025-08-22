using Microsoft.EntityFrameworkCore;
using MessageHub.DomainModels;

namespace MessageHub.Services;

/// <summary>
/// Service for seeding tenants from configuration into database
/// </summary>
public static class TenantSeedingService
{
    /// <summary>
    /// Seeds tenants from configuration if database is empty
    /// </summary>
    public static async Task SeedTenantsFromConfigurationAsync(ApplicationDbContext context, IConfiguration configuration, ILogger logger)
    {
        // Check if multi-tenant mode is enabled
        var isMultiTenantEnabled = configuration.GetValue<bool>("MultiTenantSettings:EnableMultiTenant", false);
        if (!isMultiTenantEnabled)
        {
            logger.LogInformation("Multi-tenant mode disabled, skipping tenant seeding");
            return;
        }

        // Check if tenants already exist
        var existingTenants = await context.Tenants.AnyAsync();
        if (existingTenants)
        {
            logger.LogInformation("Tenants already exist in database, skipping seeding");
            return;
        }

        // Load tenant configurations
        var tenantsConfig = configuration.GetSection("Tenants").Get<List<TenantConfigurationDto>>();
        if (tenantsConfig == null || !tenantsConfig.Any())
        {
            logger.LogInformation("No tenant configurations found in appsettings, skipping seeding");
            return;
        }

        logger.LogInformation("Seeding {TenantCount} tenants from configuration...", tenantsConfig.Count);

        foreach (var tenantConfig in tenantsConfig)
        {
            try
            {
                var tenant = new Tenant
                {
                    Name = tenantConfig.Name,
                    SubscriptionKey = tenantConfig.SubscriptionKey,
                    IsActive = tenantConfig.IsActive,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                context.Tenants.Add(tenant);
                await context.SaveChangesAsync(); // Save tenant to get ID

                logger.LogInformation("Created tenant: {TenantName} (ID: {TenantId})", tenant.Name, tenant.Id);

                // Create channel configurations
                if (tenantConfig.Channels != null && tenantConfig.Channels.Any())
                {
                    foreach (var channelConfig in tenantConfig.Channels)
                    {
                        await CreateChannelConfigurationAsync(context, tenant.Id, channelConfig, logger);
                    }
                }
                else
                {
                    logger.LogWarning("Tenant {TenantName} has no channel configurations", tenant.Name);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error seeding tenant {TenantName}", tenantConfig.Name);
            }
        }

        logger.LogInformation("Tenant seeding completed successfully");
    }

    private static async Task CreateChannelConfigurationAsync(
        ApplicationDbContext context, 
        int tenantId, 
        ChannelConfigurationDto channelConfig, 
        ILogger logger)
    {
        try
        {
            TenantChannelConfiguration? configuration = null;

            if (channelConfig.ChannelType == "SMPP" && channelConfig.SmppConfiguration != null)
            {
                configuration = new TenantSmppConfiguration
                {
                    TenantId = tenantId,
                    ChannelName = channelConfig.ChannelName,
                    ChannelType = Channels.Shared.ChannelType.SMPP,
                    IsActive = true,
                    IsDefault = channelConfig.IsDefault,
                    Priority = channelConfig.Priority,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    
                    // SMPP-specific properties
                    Host = channelConfig.SmppConfiguration.Host,
                    Port = channelConfig.SmppConfiguration.Port,
                    SystemId = channelConfig.SmppConfiguration.SystemId,
                    Password = channelConfig.SmppConfiguration.Password,
                    MaxConnections = channelConfig.SmppConfiguration.MaxConnections,
                    ConnectionTimeout = TimeSpan.Parse(channelConfig.SmppConfiguration.ConnectionTimeout ?? "00:00:30"),
                    BindTimeout = TimeSpan.Parse(channelConfig.SmppConfiguration.BindTimeout ?? "00:00:15"),
                    SubmitTimeout = TimeSpan.Parse(channelConfig.SmppConfiguration.SubmitTimeout ?? "00:00:10"),
                    ApiTimeout = TimeSpan.Parse(channelConfig.SmppConfiguration.ApiTimeout ?? "00:00:45"),
                    KeepAliveInterval = TimeSpan.Parse(channelConfig.SmppConfiguration.KeepAliveInterval ?? "00:00:30"),
                    ExpectDeliveryReceipts = channelConfig.SmppConfiguration.ExpectDeliveryReceipts,
                    DeliveryReceiptTimeoutMinutes = channelConfig.SmppConfiguration.DeliveryReceiptTimeoutMinutes,
                    TimeoutStatus = channelConfig.SmppConfiguration.TimeoutStatus ?? "AssumedDelivered"
                };
            }
            else if (channelConfig.ChannelType == "HTTP" && channelConfig.HttpConfiguration != null)
            {
                configuration = new TenantHttpConfiguration
                {
                    TenantId = tenantId,
                    ChannelName = channelConfig.ChannelName,
                    ChannelType = Channels.Shared.ChannelType.HTTP,
                    IsActive = true,
                    IsDefault = channelConfig.IsDefault,
                    Priority = channelConfig.Priority,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    
                    // HTTP-specific properties
                    ProviderName = channelConfig.HttpConfiguration.ProviderName,
                    ApiUrl = channelConfig.HttpConfiguration.ApiUrl,
                    ApiKey = channelConfig.HttpConfiguration.ApiKey,
                    AuthUsername = channelConfig.HttpConfiguration.AuthUsername,
                    AuthPassword = channelConfig.HttpConfiguration.AuthPassword,
                    FromNumber = channelConfig.HttpConfiguration.FromNumber,
                    RequestTimeout = TimeSpan.Parse(channelConfig.HttpConfiguration.RequestTimeout ?? "00:00:30"),
                    MaxRetries = channelConfig.HttpConfiguration.MaxRetries,
                    WebhookUrl = channelConfig.HttpConfiguration.WebhookUrl
                };
            }

            if (configuration != null)
            {
                context.Set<TenantChannelConfiguration>().Add(configuration);
                await context.SaveChangesAsync();
                
                logger.LogInformation("Created {ChannelType} channel '{ChannelName}' for tenant ID {TenantId}", 
                    channelConfig.ChannelType, channelConfig.ChannelName, tenantId);
            }
            else
            {
                logger.LogWarning("Unsupported channel type {ChannelType} for channel {ChannelName}", 
                    channelConfig.ChannelType, channelConfig.ChannelName);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating channel configuration {ChannelName} for tenant ID {TenantId}", 
                channelConfig.ChannelName, tenantId);
        }
    }
}

/// <summary>
/// DTOs for configuration mapping
/// </summary>
public class TenantConfigurationDto
{
    public string Name { get; set; } = string.Empty;
    public string SubscriptionKey { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public List<ChannelConfigurationDto>? Channels { get; set; }
}

public class ChannelConfigurationDto
{
    public string ChannelName { get; set; } = string.Empty;
    public string ChannelType { get; set; } = string.Empty; // "SMPP" or "HTTP"
    public bool IsDefault { get; set; } = false;
    public int Priority { get; set; } = 0;
    public SmppConfigurationDto? SmppConfiguration { get; set; }
    public HttpConfigurationDto? HttpConfiguration { get; set; }
}

public class SmppConfigurationDto
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 2775;
    public string SystemId { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public int MaxConnections { get; set; } = 3;
    public string? ConnectionTimeout { get; set; }
    public string? BindTimeout { get; set; }
    public string? SubmitTimeout { get; set; }
    public string? ApiTimeout { get; set; }
    public string? KeepAliveInterval { get; set; }
    public bool ExpectDeliveryReceipts { get; set; } = false;
    public int DeliveryReceiptTimeoutMinutes { get; set; } = 30;
    public string? TimeoutStatus { get; set; }
}

public class HttpConfigurationDto
{
    public string ProviderName { get; set; } = string.Empty;
    public string ApiUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string? AuthUsername { get; set; }
    public string? AuthPassword { get; set; }
    public string? FromNumber { get; set; }
    public string? RequestTimeout { get; set; }
    public int MaxRetries { get; set; } = 2;
    public string? WebhookUrl { get; set; }
}