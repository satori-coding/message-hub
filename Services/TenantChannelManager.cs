using System.Collections.Concurrent;
using MessageHub.Channels.Shared;
using MessageHub.Channels.Smpp;
using MessageHub.Channels.Http;
using MessageHub.DomainModels;

namespace MessageHub.Services;

/// <summary>
/// Implementation of tenant-specific channel management
/// </summary>
public class TenantChannelManager : ITenantChannelManager, IDisposable
{
    private readonly ITenantService _tenantService;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<TenantChannelManager> _logger;
    
    // Dictionary<TenantId, Dictionary<ChannelName, Channel>>
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<string, IMessageChannel>> _tenantChannels;
    private readonly object _channelCreationLock = new();
    private bool _disposed = false;

    public TenantChannelManager(
        ITenantService tenantService, 
        IServiceProvider serviceProvider,
        ILogger<TenantChannelManager> logger)
    {
        _tenantService = tenantService;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _tenantChannels = new ConcurrentDictionary<int, ConcurrentDictionary<string, IMessageChannel>>();
    }

    public async Task<IMessageChannel?> GetChannelAsync(int tenantId, string? channelName = null)
    {
        try
        {
            // If no channel name specified, use default
            if (string.IsNullOrWhiteSpace(channelName))
            {
                return await GetDefaultChannelAsync(tenantId);
            }

            // Get or create tenant channel dictionary
            var tenantChannelDict = _tenantChannels.GetOrAdd(tenantId, 
                _ => new ConcurrentDictionary<string, IMessageChannel>());

            // Check if channel already exists
            if (tenantChannelDict.TryGetValue(channelName, out var existingChannel))
            {
                // Verify channel is still healthy
                if (await existingChannel.IsHealthyAsync())
                {
                    _logger.LogDebug("Reusing existing channel for tenant {TenantId}, channel {ChannelName}", 
                        tenantId, channelName);
                    return existingChannel;
                }
                else
                {
                    _logger.LogWarning("Existing channel unhealthy for tenant {TenantId}, channel {ChannelName}. Recreating...", 
                        tenantId, channelName);
                    tenantChannelDict.TryRemove(channelName, out _);
                    if (existingChannel is IDisposable disposable)
                        disposable.Dispose();
                }
            }

            // Create new channel instance
            var newChannel = await CreateChannelAsync(tenantId, channelName);
            if (newChannel != null)
            {
                tenantChannelDict[channelName] = newChannel;
                _logger.LogInformation("Created new channel for tenant {TenantId}, channel {ChannelName}, type {ChannelType}", 
                    tenantId, channelName, newChannel.ChannelType);
            }

            return newChannel;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting channel for tenant {TenantId}, channel {ChannelName}", 
                tenantId, channelName);
            return null;
        }
    }

    public async Task<IMessageChannel?> GetDefaultChannelAsync(int tenantId)
    {
        try
        {
            var defaultConfig = await _tenantService.GetDefaultChannelConfigurationAsync(tenantId);
            if (defaultConfig == null)
            {
                _logger.LogWarning("No default channel configuration found for tenant {TenantId}", tenantId);
                return null;
            }

            return await GetChannelAsync(tenantId, defaultConfig.ChannelName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting default channel for tenant {TenantId}", tenantId);
            return null;
        }
    }

    public async Task<bool> HasHealthyChannelAsync(int tenantId)
    {
        try
        {
            var configurations = await _tenantService.GetTenantChannelConfigurationsAsync(tenantId);
            
            foreach (var config in configurations)
            {
                var channel = await GetChannelAsync(tenantId, config.ChannelName);
                if (channel != null && await channel.IsHealthyAsync())
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking healthy channels for tenant {TenantId}", tenantId);
            return false;
        }
    }

    public async Task RemoveChannelAsync(int tenantId, string? channelName = null)
    {
        try
        {
            if (!_tenantChannels.TryGetValue(tenantId, out var tenantChannelDict))
                return;

            if (string.IsNullOrWhiteSpace(channelName))
            {
                // Remove all channels for tenant
                foreach (var kvp in tenantChannelDict)
                {
                    if (kvp.Value is IDisposable disposable)
                        disposable.Dispose();
                }
                _tenantChannels.TryRemove(tenantId, out _);
                _logger.LogInformation("Removed all channels for tenant {TenantId}", tenantId);
            }
            else
            {
                // Remove specific channel
                if (tenantChannelDict.TryRemove(channelName, out var channel))
                {
                    if (channel is IDisposable disposable)
                        disposable.Dispose();
                    _logger.LogInformation("Removed channel {ChannelName} for tenant {TenantId}", channelName, tenantId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing channel for tenant {TenantId}, channel {ChannelName}", 
                tenantId, channelName);
        }
    }

    public async Task<Dictionary<string, bool>> GetChannelHealthStatusAsync(int tenantId)
    {
        var healthStatus = new Dictionary<string, bool>();

        try
        {
            var configurations = await _tenantService.GetTenantChannelConfigurationsAsync(tenantId);
            
            foreach (var config in configurations)
            {
                var channel = await GetChannelAsync(tenantId, config.ChannelName);
                healthStatus[config.ChannelName] = channel != null && await channel.IsHealthyAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting channel health status for tenant {TenantId}", tenantId);
        }

        return healthStatus;
    }

    public async Task DisposeAllChannelsAsync()
    {
        _logger.LogInformation("Disposing all tenant channels...");
        
        foreach (var tenantKvp in _tenantChannels)
        {
            foreach (var channelKvp in tenantKvp.Value)
            {
                if (channelKvp.Value is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
        }
        
        _tenantChannels.Clear();
        _logger.LogInformation("All tenant channels disposed");
    }

    private async Task<IMessageChannel?> CreateChannelAsync(int tenantId, string channelName)
    {
        lock (_channelCreationLock)
        {
            // Double-check pattern for thread safety
            if (_tenantChannels.TryGetValue(tenantId, out var tenantDict) && 
                tenantDict.TryGetValue(channelName, out var existingChannel))
            {
                return existingChannel;
            }

            try
            {
                var configuration = _tenantService.GetChannelConfigurationAsync(tenantId, channelName).Result;
                if (configuration == null)
                {
                    _logger.LogWarning("Channel configuration not found for tenant {TenantId}, channel {ChannelName}", 
                        tenantId, channelName);
                    return null;
                }

                return configuration switch
                {
                    TenantSmppConfiguration smppConfig => CreateSmppChannel(smppConfig),
                    TenantHttpConfiguration httpConfig => CreateHttpChannel(httpConfig),
                    _ => throw new InvalidOperationException($"Unsupported channel type: {configuration.GetType().Name}")
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating channel for tenant {TenantId}, channel {ChannelName}", 
                    tenantId, channelName);
                return null;
            }
        }
    }

    private IMessageChannel CreateSmppChannel(TenantSmppConfiguration config)
    {
        var smppChannelConfig = new SmppChannelConfiguration
        {
            Host = config.Host,
            Port = config.Port,
            SystemId = config.SystemId,
            Password = config.Password,
            MaxConnections = config.MaxConnections,
            ConnectionTimeout = config.ConnectionTimeout,
            BindTimeout = config.BindTimeout,
            SubmitTimeout = config.SubmitTimeout,
            ApiTimeout = config.ApiTimeout,
            KeepAliveInterval = config.KeepAliveInterval,
            ExpectDeliveryReceipts = config.ExpectDeliveryReceipts,
            DeliveryReceiptTimeoutMinutes = config.DeliveryReceiptTimeoutMinutes,
            TimeoutStatus = Enum.TryParse<MessageStatus>(config.TimeoutStatus, out var status) ? status : MessageStatus.AssumedDelivered
        };

        var logger = _serviceProvider.GetRequiredService<ILogger<SmppChannel>>();
        
        return new SmppChannel(smppChannelConfig, logger);
    }

    private IMessageChannel CreateHttpChannel(TenantHttpConfiguration config)
    {
        var httpChannelConfig = new HttpSmsChannelConfiguration
        {
            ProviderName = config.ProviderName,
            ApiUrl = config.ApiUrl,
            ApiKey = config.ApiKey,
            FromNumber = config.FromNumber,
            TimeoutMs = (int)config.RequestTimeout.TotalMilliseconds,
            MaxRetryAttempts = config.MaxRetries,
            WebhookUrl = config.WebhookUrl
        };

        var logger = _serviceProvider.GetRequiredService<ILogger<HttpSmsChannel>>();
        var httpClientFactory = _serviceProvider.GetRequiredService<IHttpClientFactory>();
        var httpClient = httpClientFactory.CreateClient("SMS");
        
        return new HttpSmsChannel(httpChannelConfig, logger, httpClient);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            DisposeAllChannelsAsync().Wait();
            _disposed = true;
        }
    }
}