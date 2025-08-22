using Microsoft.EntityFrameworkCore;
using MessageHub.DomainModels;

namespace MessageHub.Services;

/// <summary>
/// Implementation of tenant management service
/// </summary>
public class TenantService : ITenantService
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<TenantService> _logger;

    public TenantService(ApplicationDbContext context, ILogger<TenantService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<Tenant?> GetTenantBySubscriptionKeyAsync(string subscriptionKey)
    {
        if (string.IsNullOrWhiteSpace(subscriptionKey))
        {
            _logger.LogWarning("Empty subscription key provided");
            return null;
        }

        try
        {
            var tenant = await _context.Tenants
                .Include(t => t.ChannelConfigurations)
                .FirstOrDefaultAsync(t => t.SubscriptionKey == subscriptionKey && t.IsActive);

            if (tenant == null)
            {
                _logger.LogWarning("No active tenant found for subscription key: {SubscriptionKey}", 
                    subscriptionKey.Substring(0, Math.Min(8, subscriptionKey.Length)) + "***");
                return null;
            }

            _logger.LogInformation("Tenant resolved: {TenantName} (ID: {TenantId})", tenant.Name, tenant.Id);
            return tenant;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resolving tenant by subscription key");
            return null;
        }
    }

    public async Task<Tenant?> GetTenantByIdAsync(int tenantId)
    {
        try
        {
            var tenant = await _context.Tenants
                .Include(t => t.ChannelConfigurations)
                .FirstOrDefaultAsync(t => t.Id == tenantId && t.IsActive);

            if (tenant == null)
            {
                _logger.LogWarning("No active tenant found with ID: {TenantId}", tenantId);
            }

            return tenant;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting tenant by ID: {TenantId}", tenantId);
            return null;
        }
    }

    public async Task<List<TenantChannelConfiguration>> GetTenantChannelConfigurationsAsync(int tenantId)
    {
        try
        {
            var configurations = await _context.Set<TenantChannelConfiguration>()
                .Where(c => c.TenantId == tenantId && c.IsActive)
                .OrderByDescending(c => c.Priority)
                .ThenBy(c => c.ChannelName)
                .ToListAsync();

            _logger.LogInformation("Retrieved {Count} channel configurations for tenant {TenantId}", 
                configurations.Count, tenantId);
            return configurations;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting channel configurations for tenant: {TenantId}", tenantId);
            return new List<TenantChannelConfiguration>();
        }
    }

    public async Task<TenantChannelConfiguration?> GetChannelConfigurationAsync(int tenantId, string channelName)
    {
        if (string.IsNullOrWhiteSpace(channelName))
        {
            _logger.LogWarning("Empty channel name provided for tenant {TenantId}", tenantId);
            return null;
        }

        try
        {
            var configuration = await _context.Set<TenantChannelConfiguration>()
                .FirstOrDefaultAsync(c => c.TenantId == tenantId && 
                                         c.ChannelName == channelName && 
                                         c.IsActive);

            if (configuration == null)
            {
                _logger.LogWarning("No active channel configuration found for tenant {TenantId}, channel {ChannelName}", 
                    tenantId, channelName);
            }

            return configuration;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting channel configuration for tenant {TenantId}, channel {ChannelName}", 
                tenantId, channelName);
            return null;
        }
    }

    public async Task<TenantChannelConfiguration?> GetDefaultChannelConfigurationAsync(int tenantId)
    {
        try
        {
            // First try to get explicitly marked default configuration
            var defaultConfig = await _context.Set<TenantChannelConfiguration>()
                .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.IsDefault && c.IsActive);

            if (defaultConfig != null)
            {
                _logger.LogInformation("Found explicit default channel configuration for tenant {TenantId}: {ChannelName}", 
                    tenantId, defaultConfig.ChannelName);
                return defaultConfig;
            }

            // Fall back to highest priority active configuration
            var highestPriorityConfig = await _context.Set<TenantChannelConfiguration>()
                .Where(c => c.TenantId == tenantId && c.IsActive)
                .OrderByDescending(c => c.Priority)
                .FirstOrDefaultAsync();

            if (highestPriorityConfig != null)
            {
                _logger.LogInformation("Using highest priority channel configuration for tenant {TenantId}: {ChannelName}", 
                    tenantId, highestPriorityConfig.ChannelName);
            }
            else
            {
                _logger.LogWarning("No active channel configurations found for tenant {TenantId}", tenantId);
            }

            return highestPriorityConfig;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting default channel configuration for tenant: {TenantId}", tenantId);
            return null;
        }
    }

    public bool ValidateTenantAccess(Tenant tenant)
    {
        if (tenant == null)
        {
            _logger.LogWarning("Tenant is null during access validation");
            return false;
        }

        if (!tenant.IsActive)
        {
            _logger.LogWarning("Tenant {TenantId} ({TenantName}) is not active", tenant.Id, tenant.Name);
            return false;
        }

        if (!tenant.HasActiveConfigurations)
        {
            _logger.LogWarning("Tenant {TenantId} ({TenantName}) has no active channel configurations", 
                tenant.Id, tenant.Name);
            return false;
        }

        _logger.LogInformation("Tenant {TenantId} ({TenantName}) access validated successfully", 
            tenant.Id, tenant.Name);
        return true;
    }
}