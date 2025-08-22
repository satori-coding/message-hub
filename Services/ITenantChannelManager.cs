using MessageHub.Channels.Shared;
using MessageHub.DomainModels;

namespace MessageHub.Services;

/// <summary>
/// Manager for tenant-specific channel instances
/// </summary>
public interface ITenantChannelManager
{
    /// <summary>
    /// Get channel instance for specific tenant and channel configuration
    /// </summary>
    /// <param name="tenantId">Tenant ID</param>
    /// <param name="channelName">Channel configuration name (optional - uses default if null)</param>
    /// <returns>Channel instance ready for sending messages</returns>
    Task<IMessageChannel?> GetChannelAsync(int tenantId, string? channelName = null);
    
    /// <summary>
    /// Get default channel for tenant
    /// </summary>
    /// <param name="tenantId">Tenant ID</param>
    /// <returns>Default channel instance</returns>
    Task<IMessageChannel?> GetDefaultChannelAsync(int tenantId);
    
    /// <summary>
    /// Check if tenant has any healthy channels available
    /// </summary>
    /// <param name="tenantId">Tenant ID</param>
    /// <returns>True if at least one healthy channel exists</returns>
    Task<bool> HasHealthyChannelAsync(int tenantId);
    
    /// <summary>
    /// Remove and dispose channel instance for tenant (useful for configuration updates)
    /// </summary>
    /// <param name="tenantId">Tenant ID</param>
    /// <param name="channelName">Channel name (optional - removes all if null)</param>
    Task RemoveChannelAsync(int tenantId, string? channelName = null);
    
    /// <summary>
    /// Get health status of all channels for tenant
    /// </summary>
    /// <param name="tenantId">Tenant ID</param>
    /// <returns>Dictionary of channel name to health status</returns>
    Task<Dictionary<string, bool>> GetChannelHealthStatusAsync(int tenantId);
    
    /// <summary>
    /// Dispose all tenant channels (useful for shutdown)
    /// </summary>
    Task DisposeAllChannelsAsync();
}