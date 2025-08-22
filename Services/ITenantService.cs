using MessageHub.DomainModels;

namespace MessageHub.Services;

/// <summary>
/// Service for tenant resolution and management
/// </summary>
public interface ITenantService
{
    /// <summary>
    /// Resolve tenant by subscription key from HTTP headers
    /// </summary>
    /// <param name="subscriptionKey">The subscription key from X-Subscription-Key header</param>
    /// <returns>Tenant if valid key, null otherwise</returns>
    Task<Tenant?> GetTenantBySubscriptionKeyAsync(string subscriptionKey);
    
    /// <summary>
    /// Get tenant by ID
    /// </summary>
    /// <param name="tenantId">Tenant ID</param>
    /// <returns>Tenant if found, null otherwise</returns>
    Task<Tenant?> GetTenantByIdAsync(int tenantId);
    
    /// <summary>
    /// Get all active channel configurations for a tenant
    /// </summary>
    /// <param name="tenantId">Tenant ID</param>
    /// <returns>List of active channel configurations</returns>
    Task<List<TenantChannelConfiguration>> GetTenantChannelConfigurationsAsync(int tenantId);
    
    /// <summary>
    /// Get specific channel configuration by tenant and channel name
    /// </summary>
    /// <param name="tenantId">Tenant ID</param>
    /// <param name="channelName">Channel name</param>
    /// <returns>Channel configuration if found</returns>
    Task<TenantChannelConfiguration?> GetChannelConfigurationAsync(int tenantId, string channelName);
    
    /// <summary>
    /// Get default channel configuration for tenant
    /// </summary>
    /// <param name="tenantId">Tenant ID</param>
    /// <returns>Default channel configuration</returns>
    Task<TenantChannelConfiguration?> GetDefaultChannelConfigurationAsync(int tenantId);
    
    /// <summary>
    /// Validate that tenant has access to perform operations
    /// </summary>
    /// <param name="tenant">Tenant to validate</param>
    /// <returns>True if tenant is active and has valid configurations</returns>
    bool ValidateTenantAccess(Tenant tenant);
}