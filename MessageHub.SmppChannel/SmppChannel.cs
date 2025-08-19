using Inetlab.SMPP;
using Inetlab.SMPP.Common;
using Inetlab.SMPP.PDU;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using MessageHub.Shared;

namespace MessageHub.SmppChannel;

/// <summary>
/// Main SMPP channel implementation with connection pooling and delivery receipt handling
/// </summary>
public class SmppChannel : ISmppChannel, IMessageChannel, IAsyncDisposable
{
    private readonly SmppChannelConfiguration _configuration;
    private readonly ILogger<SmppChannel> _logger;
    private readonly ConcurrentQueue<SmppConnection> _availableConnections;
    private readonly ConcurrentDictionary<int, SmppConnection> _allConnections;
    private readonly SemaphoreSlim _connectionSemaphore;
    private readonly Timer _keepAliveTimer;
    private volatile bool _disposed = false;
    private int _connectionCounter = 0;

    public event Action<SmppDeliveryReceipt>? OnDeliveryReceiptReceived;

    // IMessageChannel implementation
    public ChannelType ChannelType => ChannelType.SMPP;
    public string ProviderName => "SMPP";

    public SmppChannel(SmppChannelConfiguration configuration, ILogger<SmppChannel> logger)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        
        // Validate configuration
        _configuration.Validate();

        _availableConnections = new ConcurrentQueue<SmppConnection>();
        _allConnections = new ConcurrentDictionary<int, SmppConnection>();
        _connectionSemaphore = new SemaphoreSlim(_configuration.MaxConnections, _configuration.MaxConnections);

        // Start keep-alive timer
        _keepAliveTimer = new Timer(SendKeepAlive, null, _configuration.KeepAliveInterval, _configuration.KeepAliveInterval);

        _logger.LogInformation("SMPP Channel initialized with max {MaxConnections} connections to {Host}:{Port}", 
            _configuration.MaxConnections, _configuration.Host, _configuration.Port);
    }

    public async Task<SmppSendResult> SendSmsAsync(SmppMessage message)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SmppChannel));

        if (string.IsNullOrWhiteSpace(message.PhoneNumber) || string.IsNullOrWhiteSpace(message.Content))
        {
            return SmppSendResult.Failure("Phone number and content are required");
        }

        _logger.LogInformation("Begin SendSmsAsync for phone {PhoneNumber}", message.PhoneNumber);

        SmppConnection? connection = null;
        
        try
        {
            // Get connection from pool with enhanced validation
            connection = await GetConnectionAsync();
            _logger.LogDebug("SMPP connection retrieved from pool. IsHealthy={IsHealthy}, Status={Status}", 
                connection.IsHealthy, connection.Client.Status);

            // Additional connection validation before sending
            if (!connection.IsHealthy || connection.Client.Status != ConnectionStatus.Bound)
            {
                _logger.LogWarning("SMPP connection unhealthy, requesting fresh connection. Status={Status}", 
                    connection.Client.Status);
                
                // Return bad connection and get a new one
                ReturnConnection(connection);
                connection = await GetConnectionAsync();
                
                // If still bad, throw exception
                if (!connection.IsHealthy || connection.Client.Status != ConnectionStatus.Bound)
                {
                    return SmppSendResult.Failure($"Unable to get healthy SMPP connection. Status={connection.Client.Status}");
                }
            }

            // Build SMS submit request
            var sms = SMS.ForSubmit()
                .From(message.From)
                .To(message.PhoneNumber.TrimStart('+'))
                .Text(message.Content);

            // Request delivery receipt if configured
            if (message.RequestDeliveryReceipt)
            {
                sms = sms.DeliveryReceipt();
            }

            _logger.LogDebug("SMS submit prepared. From={From}, To={To}, ContentLength={ContentLength}", 
                message.From, message.PhoneNumber, message.Content.Length);

            // Submit with retry logic and timeout handling
            SubmitSmResp[]? submitResponses = null;
            int retryCount = 0;
            const int maxRetries = 2;
            
            while (retryCount <= maxRetries)
            {
                try 
                {
                    // Test connection status immediately before submit
                    if (connection.Client.Status != ConnectionStatus.Bound)
                    {
                        _logger.LogWarning("Connection not bound before submit attempt {Retry}. Status: {Status}", retryCount + 1, connection.Client.Status);
                        
                        if (retryCount < maxRetries)
                        {
                            // Return bad connection and get new one
                            ReturnConnection(connection);
                            connection = await GetConnectionAsync();
                            retryCount++;
                            continue;
                        }
                        else
                        {
                            return SmppSendResult.Failure($"Connection not bound after {maxRetries} retries. Status: {connection.Client.Status}");
                        }
                    }

                    // Submit with timeout
                    _logger.LogInformation("SMPP client status before submit attempt {Retry}: {Status}", retryCount + 1, connection.Client.Status);
                    
                    var submitTask = Task.Run(() => connection.Client.Submit(sms));
                    var timeoutTask = Task.Delay(TimeSpan.FromSeconds(5)); // 5 second timeout
                    
                    var completedTask = await Task.WhenAny(submitTask, timeoutTask);
                    
                    if (completedTask == timeoutTask)
                    {
                        _logger.LogWarning("Submit timed out on attempt {Retry}", retryCount + 1);
                        
                        if (retryCount < maxRetries)
                        {
                            ReturnConnection(connection);
                            connection = await GetConnectionAsync();
                            retryCount++;
                            continue;
                        }
                        else
                        {
                            return SmppSendResult.Failure("Submit timed out after multiple attempts");
                        }
                    }
                    
                    submitResponses = await submitTask;
                    _logger.LogDebug("Submit successful on attempt {Retry}", retryCount + 1);
                    
                    // Check for connection errors in response
                    bool hasConnectionError = submitResponses.Any(resp => 
                        resp.Header.Status == CommandStatus.SMPPCLIENT_NOCONN ||
                        resp.Header.Status.ToString().Contains("NOCONN"));
                        
                    if (hasConnectionError && retryCount < maxRetries)
                    {
                        _logger.LogWarning("Submit returned connection error on attempt {Retry}, retrying with new connection", retryCount + 1);
                        
                        ReturnConnection(connection);
                        connection = await GetConnectionAsync();
                        retryCount++;
                        continue;
                    }
                    
                    break; // Success - exit retry loop
                }
                catch (Exception submitEx)
                {
                    _logger.LogError(submitEx, "Submit failed on attempt {Retry}: {Message}", retryCount + 1, submitEx.Message);
                    
                    if (retryCount < maxRetries)
                    {
                        ReturnConnection(connection);
                        connection = await GetConnectionAsync();
                        retryCount++;
                        _logger.LogInformation("Retrying submit with new connection, attempt {Retry}", retryCount + 1);
                        continue;
                    }
                    else
                    {
                        return SmppSendResult.Failure($"Submit failed after {maxRetries + 1} attempts: {submitEx.Message}", submitEx);
                    }
                }
            }
            
            // Validate responses
            if (submitResponses == null)
            {
                return SmppSendResult.Failure("Submit failed - no responses received");
            }

            _logger.LogDebug("SubmitSm responses received. Count={Count}", submitResponses.Length);
            
            // Log each response status
            foreach (var resp in submitResponses)
            {
                _logger.LogInformation("SubmitSmResp: Status={Status}, Sequence={Sequence}, MessageId={MessageId}", 
                    resp.Header.Status, resp.Header.Sequence, resp.MessageId);
            }

            // Check for successful submission
            if (!submitResponses.All(x => x.Header.Status == CommandStatus.ESME_ROK))
            {
                var failedResponses = submitResponses.Where(x => x.Header.Status != CommandStatus.ESME_ROK);
                var errorStatuses = string.Join(", ", failedResponses.Select(x => x.Header.Status));
                _logger.LogError("SMPP submit failed. Statuses: {Statuses}", errorStatuses);
                return SmppSendResult.Failure($"SMPP submit failed with statuses: {errorStatuses}");
            }

            var smppMessageId = submitResponses.First().MessageId;
            _logger.LogInformation("SMS submitted successfully via SMPP. Message ID: {SmppMessageId} for {PhoneNumber}", 
                smppMessageId, message.PhoneNumber);

            _logger.LogDebug("SMPP client status after submit: {Status}", connection.Client.Status);

            return SmppSendResult.Success(smppMessageId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending SMS to {PhoneNumber}", message.PhoneNumber);
            return SmppSendResult.Failure($"SMPP send failed: {ex.Message}", ex);
        }
        finally
        {
            // Always return connection to pool
            if (connection != null)
            {
                ReturnConnection(connection);
                _logger.LogDebug("SMPP connection returned to pool. IsHealthy={IsHealthy}, Status={Status}", 
                    connection.IsHealthy, connection.Client.Status);
            }
        }
    }

    public Task<bool> IsHealthyAsync()
    {
        try
        {
            var healthyConnections = _allConnections.Values.Count(c => c.IsHealthy);
            _logger.LogDebug("Health check: {HealthyConnections}/{TotalConnections} connections healthy", 
                healthyConnections, _allConnections.Count);
            return Task.FromResult(healthyConnections > 0 || _allConnections.Count == 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during health check");
            return Task.FromResult(false);
        }
    }

    private async Task<SmppConnection> GetConnectionAsync()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SmppChannel));

        await _connectionSemaphore.WaitAsync();

        try
        {
            // Try to get an available connection with enhanced health validation
            if (_availableConnections.TryDequeue(out var availableConnection))
            {
                // Enhanced health check with EnquireLink test
                if (await ValidateConnectionHealthAsync(availableConnection))
                {
                    availableConnection.IsAvailable = false;
                    availableConnection.LastUsed = DateTime.UtcNow;
                    _logger.LogDebug("Reusing existing healthy SMPP connection");
                    return availableConnection;
                }
                else
                {
                    _logger.LogWarning("Connection failed health validation, disposing");
                    _allConnections.TryRemove(availableConnection.GetHashCode(), out _);
                    availableConnection.Dispose();
                }
            }

            // Create new connection
            _logger.LogInformation("Creating new SMPP connection to {Host}:{Port}", _configuration.Host, _configuration.Port);
            var connection = await CreateConnectionAsync();
            
            var connectionId = Interlocked.Increment(ref _connectionCounter);
            _allConnections.TryAdd(connectionId, connection);
            
            connection.IsAvailable = false;
            connection.LastUsed = DateTime.UtcNow;

            return connection;
        }
        catch (Exception ex)
        {
            _connectionSemaphore.Release();
            _logger.LogError(ex, "Failed to get SMPP connection");
            throw;
        }
    }

    private async Task<bool> ValidateConnectionHealthAsync(SmppConnection connection)
    {
        try
        {
            if (!connection.IsHealthy)
                return false;

            // Test connection with EnquireLink
            await Task.Run(() => connection.Client.EnquireLink());
            _logger.LogDebug("Connection health validation successful");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Connection health validation failed: {Message}", ex.Message);
            return false;
        }
    }

    private void ReturnConnection(SmppConnection connection)
    {
        if (_disposed || connection == null)
        {
            _connectionSemaphore.Release();
            return;
        }

        try
        {
            if (connection.IsHealthy)
            {
                connection.IsAvailable = true;
                connection.LastUsed = DateTime.UtcNow;
                _availableConnections.Enqueue(connection);
                _logger.LogDebug("Returned healthy SMPP connection to pool");
            }
            else
            {
                _logger.LogWarning("Disposing unhealthy returned SMPP connection");
                _allConnections.TryRemove(connection.GetHashCode(), out _);
                connection.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error returning SMPP connection to pool");
        }
        finally
        {
            _connectionSemaphore.Release();
        }
    }

    private async Task<SmppConnection> CreateConnectionAsync()
    {
        var client = new SmppClient();
        
        try
        {
            // Connect to SMPP server
            var connected = await Task.Run(() => client.Connect(_configuration.Host, _configuration.Port));
            if (!connected)
            {
                throw new InvalidOperationException($"Failed to connect to SMPP server {_configuration.Host}:{_configuration.Port}");
            }
            _logger.LogInformation("Connected to SMPP server {Host}:{Port}", _configuration.Host, _configuration.Port);

            // Bind as transmitter (send only, simpler than transceiver)
            var bindResp = await Task.Run(() => client.Bind(_configuration.SystemId, _configuration.Password, ConnectionMode.Transmitter));

            if (bindResp.Header.Status != CommandStatus.ESME_ROK)
            {
                throw new InvalidOperationException($"SMPP bind failed with status: {bindResp.Header.Status}");
            }

            _logger.LogInformation("Successfully bound to SMPP server as transmitter");

            return new SmppConnection(client);
        }
        catch
        {
            // Clean up on failure
            try
            {
                await Task.Run(() => client.Disconnect());
            }
            catch { }
            
            client.Dispose();
            throw;
        }
    }

    private void OnDeliveryReceiptHandler(object sender, DeliverSm deliverSm)
    {
        try
        {
            // Check if this is a delivery receipt using the library's MessageType
            if (deliverSm.MessageType == MessageTypes.SMSCDeliveryReceipt)
            {
                _logger.LogInformation("Received delivery receipt from {SourceAddr}", deliverSm.SourceAddress.Address);

                var receiptText = deliverSm.Receipt?.ToString() ?? "";
                var sourceAddr = deliverSm.SourceAddress.Address;
                var smppMessageId = deliverSm.Receipt?.MessageId ?? "";

                if (!string.IsNullOrEmpty(smppMessageId))
                {
                    var receipt = new SmppDeliveryReceipt
                    {
                        SmppMessageId = smppMessageId,
                        SourceAddress = sourceAddr,
                        ReceiptText = receiptText,
                        ReceivedAt = DateTime.UtcNow,
                        DeliveryStatus = ParseDeliveryStatus(receiptText),
                        ErrorCode = ParseErrorCode(receiptText)
                    };

                    // Fire the event
                    OnDeliveryReceiptReceived?.Invoke(receipt);
                    
                    _logger.LogDebug("Delivery receipt processed successfully for SMPP message ID: {SmppMessageId}", smppMessageId);
                }
                else
                {
                    _logger.LogWarning("Could not extract SMPP message ID from delivery receipt");
                }
            }
            else
            {
                // This is a mobile-originated (MO) SMS, not a delivery receipt
                _logger.LogDebug("Received MO SMS from {SourceAddr} (not a delivery receipt)", deliverSm.SourceAddress.Address);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in delivery receipt handler");
        }
    }

    private string ParseDeliveryStatus(string receiptText)
    {
        try
        {
            var match = System.Text.RegularExpressions.Regex.Match(receiptText, @"stat:([^\s]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.ToUpper() : "UNKNOWN";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing delivery status from receipt: {ReceiptText}", receiptText);
            return "UNKNOWN";
        }
    }

    private int? ParseErrorCode(string receiptText)
    {
        try
        {
            var match = System.Text.RegularExpressions.Regex.Match(receiptText, @"err:(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int errorCode))
                return errorCode;
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing error code from receipt: {ReceiptText}", receiptText);
            return null;
        }
    }

    private void SendKeepAlive(object? state)
    {
        if (_disposed) return;

        try
        {
            var connections = _allConnections.Values.ToList();
            _logger.LogDebug("Sending keep-alive to {Count} connections", connections.Count);

            foreach (var connection in connections)
            {
                try
                {
                    if (connection.IsHealthy)
                    {
                        // Send enquire_link
                        Task.Run(() => connection.Client.EnquireLink());
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to send keep-alive to SMPP connection");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during keep-alive process");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _logger.LogInformation("Disposing SMPP Channel");

        _keepAliveTimer?.Dispose();
        _connectionSemaphore?.Dispose();

        // Dispose all connections
        foreach (var connection in _allConnections.Values)
        {
            try
            {
                connection.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing SMPP connection");
            }
        }

        _allConnections.Clear();
        
        // Clear available connections queue
        while (_availableConnections.TryDequeue(out _)) { }

        await Task.CompletedTask;
    }

    // IMessageChannel implementation
    async Task<MessageResult> IMessageChannel.SendAsync(Message message)
    {
        // Convert Message to SmppMessage
        var smppMessage = new SmppMessage
        {
            PhoneNumber = message.Recipient,
            Content = message.Content,
            From = "MessageHub",
            RequestDeliveryReceipt = true
        };

        // Call the existing SMPP implementation
        var smppResult = await SendSmsAsync(smppMessage);

        // Convert SmppSendResult to MessageResult
        if (smppResult.IsSuccess)
        {
            return MessageResult.CreateSuccess(
                smppResult.SmppMessageId ?? "",
                new Dictionary<string, object>
                {
                    ["SmppMessageId"] = smppResult.SmppMessageId ?? "",
                    ["ChannelType"] = "SMPP"
                }
            );
        }
        else
        {
            return MessageResult.CreateFailure(
                smppResult.ErrorMessage ?? "Unknown SMPP error",
                smppResult.Exception?.HResult
            );
        }
    }
}