using Inetlab.SMPP;
using Inetlab.SMPP.Common;
using Inetlab.SMPP.PDU;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using MessageHub.Channels.Shared;

namespace MessageHub.Channels.Smpp;

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

            _logger.LogDebug("Message submit prepared. From={From}, To={To}, ContentLength={ContentLength}, DeliveryReceipt={RequestDR}", 
                message.From, message.PhoneNumber, message.Content.Length, message.RequestDeliveryReceipt);

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
                    
                    var submitTask = Task.Run(() => {
                        var smsBuilder = SMS.ForSubmit()
                            .From(message.From)
                            .To(message.PhoneNumber.TrimStart('+'))
                            .Text(message.Content);
                        
                        if (message.RequestDeliveryReceipt)
                        {
                            smsBuilder = smsBuilder.DeliveryReceipt();
                        }
                        
                        return connection.Client.Submit(smsBuilder);
                    });
                    var timeoutTask = Task.Delay(_configuration.SubmitTimeout); // Configurable submit timeout
                    
                    var completedTask = await Task.WhenAny(submitTask, timeoutTask);
                    
                    if (completedTask == timeoutTask)
                    {
                        _logger.LogWarning("SMPP submit timed out after {Timeout}s on attempt {Retry} for {PhoneNumber}", 
                            _configuration.SubmitTimeout.TotalSeconds, retryCount + 1, message.PhoneNumber);
                        
                        if (retryCount < maxRetries)
                        {
                            ReturnConnection(connection);
                            connection = await GetConnectionAsync();
                            retryCount++;
                            continue;
                        }
                        else
                        {
                            return SmppSendResult.Failure($"Submit timed out after {maxRetries + 1} attempts ({_configuration.SubmitTimeout.TotalSeconds}s each)");
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

            // Collect all message IDs from the responses (for multi-part SMS)
            var smppMessageIds = submitResponses.Select(resp => resp.MessageId).ToList();
            var primaryMessageId = smppMessageIds.First();
            
            _logger.LogInformation("Message submitted successfully via SMPP. Message parts: {MessageParts}, Primary ID: {SmppMessageId}, All IDs: [{AllIds}] for {PhoneNumber}", 
                smppMessageIds.Count, primaryMessageId, string.Join(", ", smppMessageIds), message.PhoneNumber);

            _logger.LogDebug("SMPP client status after submit: {Status}", connection.Client.Status);

            // Return appropriate result based on number of parts
            if (smppMessageIds.Count == 1)
            {
                return SmppSendResult.Success(primaryMessageId);
            }
            else
            {
                return SmppSendResult.SuccessMultiPart(smppMessageIds);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending message to {PhoneNumber}", message.PhoneNumber);
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
            {
                _logger.LogDebug("Connection marked as unhealthy");
                return false;
            }

            // Test connection with EnquireLink with timeout
            var enquireLinkTask = Task.Run(() => connection.Client.EnquireLink());
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(5)); // Health check timeout
            
            var completedTask = await Task.WhenAny(enquireLinkTask, timeoutTask);
            
            if (completedTask == timeoutTask)
            {
                _logger.LogDebug("Connection health validation timed out after 5s");
                return false;
            }
            
            await enquireLinkTask; // Get any potential exception
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
        var startTime = DateTime.UtcNow;
        
        try
        {
            _logger.LogInformation("Starting SMPP connection to {Host}:{Port} (Connection timeout: {ConnectionTimeout}s, Bind timeout: {BindTimeout}s)", 
                _configuration.Host, _configuration.Port, _configuration.ConnectionTimeout.TotalSeconds, _configuration.BindTimeout.TotalSeconds);
            
            // Connect to SMPP server with timeout
            var connectTask = Task.Run(() => client.Connect(_configuration.Host, _configuration.Port));
            var timeoutTask = Task.Delay(_configuration.ConnectionTimeout);
            
            var completedTask = await Task.WhenAny(connectTask, timeoutTask);
            
            if (completedTask == timeoutTask)
            {
                _logger.LogError("SMPP connection timed out after {Timeout}s to {Host}:{Port}", 
                    _configuration.ConnectionTimeout.TotalSeconds, _configuration.Host, _configuration.Port);
                client.Dispose();
                throw new TimeoutException($"SMPP connection timed out after {_configuration.ConnectionTimeout.TotalSeconds}s to {_configuration.Host}:{_configuration.Port}");
            }
            
            var connected = await connectTask;
            if (!connected)
            {
                _logger.LogError("SMPP connection failed to {Host}:{Port} - Connect returned false", _configuration.Host, _configuration.Port);
                client.Dispose();
                throw new InvalidOperationException($"Failed to connect to SMPP server {_configuration.Host}:{_configuration.Port} - Connection rejected");
            }
            
            var connectDuration = DateTime.UtcNow - startTime;
            _logger.LogInformation("Connected to SMPP server {Host}:{Port} in {Duration}ms", 
                _configuration.Host, _configuration.Port, connectDuration.TotalMilliseconds);

            // Register delivery receipt event handler BEFORE binding
            client.evDeliverSm += OnDeliveryReceiptHandler;
            _logger.LogDebug("Registered evDeliverSm event handler for DLR processing");

            // Bind as transceiver (send and receive DLRs) with timeout
            var bindStart = DateTime.UtcNow;
            var bindTask = Task.Run(() => client.Bind(_configuration.SystemId, _configuration.Password, ConnectionMode.Transceiver));
            var bindTimeoutTask = Task.Delay(_configuration.BindTimeout);
            
            var bindCompletedTask = await Task.WhenAny(bindTask, bindTimeoutTask);
            
            if (bindCompletedTask == bindTimeoutTask)
            {
                _logger.LogError("SMPP bind timed out after {Timeout}s to {Host}:{Port} with SystemId: {SystemId}", 
                    _configuration.BindTimeout.TotalSeconds, _configuration.Host, _configuration.Port, _configuration.SystemId);
                client.Dispose();
                throw new TimeoutException($"SMPP bind timed out after {_configuration.BindTimeout.TotalSeconds}s to {_configuration.Host}:{_configuration.Port}");
            }
            
            var bindResp = await bindTask;
            
            if (bindResp.Header.Status != CommandStatus.ESME_ROK)
            {
                _logger.LogError("SMPP bind failed with status: {Status} to {Host}:{Port} with SystemId: {SystemId}", 
                    bindResp.Header.Status, _configuration.Host, _configuration.Port, _configuration.SystemId);
                client.Dispose();
                throw new InvalidOperationException($"SMPP bind failed with status: {bindResp.Header.Status}");
            }

            var bindDuration = DateTime.UtcNow - bindStart;
            var totalDuration = DateTime.UtcNow - startTime;
            _logger.LogInformation("Successfully bound to SMPP server as transceiver with DLR handling enabled. Bind: {BindDuration}ms, Total: {TotalDuration}ms", 
                bindDuration.TotalMilliseconds, totalDuration.TotalMilliseconds);

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
                // This is a mobile-originated (MO) message, not a delivery receipt
                _logger.LogDebug("Received MO message from {SourceAddr} (not a delivery receipt)", deliverSm.SourceAddress.Address);
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
        var startTime = DateTime.UtcNow;
        
        try
        {
            _logger.LogInformation("Starting SMPP send operation for {PhoneNumber} (API timeout: {ApiTimeout}s)", 
                message.Recipient, _configuration.ApiTimeout.TotalSeconds);
            
            // Convert Message to SmppMessage
            var smppMessage = new SmppMessage
            {
                PhoneNumber = message.Recipient,
                Content = message.Content,
                From = "MessageHub",
                RequestDeliveryReceipt = true
            };

            // Call the existing SMPP implementation with overall API timeout
            var sendTask = SendSmsAsync(smppMessage);
            var apiTimeoutTask = Task.Delay(_configuration.ApiTimeout);
            
            var completedTask = await Task.WhenAny(sendTask, apiTimeoutTask);
            
            if (completedTask == apiTimeoutTask)
            {
                var duration = DateTime.UtcNow - startTime;
                _logger.LogError("SMPP API timeout after {ApiTimeout}s for {PhoneNumber} (actual duration: {ActualDuration}ms)", 
                    _configuration.ApiTimeout.TotalSeconds, message.Recipient, duration.TotalMilliseconds);
                    
                return MessageResult.CreateFailure(
                    $"SMPP API timeout after {_configuration.ApiTimeout.TotalSeconds}s",
                    errorCode: -1 // Timeout error code
                );
            }
            
            var smppResult = await sendTask;
            var totalDuration = DateTime.UtcNow - startTime;
            
            // Convert SmppSendResult to MessageResult
            if (smppResult.IsSuccess)
            {
                _logger.LogInformation("SMPP send operation completed successfully for {PhoneNumber} in {Duration}ms", 
                    message.Recipient, totalDuration.TotalMilliseconds);
                    
                var channelData = new Dictionary<string, object>
                {
                    ["SmppMessageId"] = smppResult.SmppMessageId ?? "",
                    ["SmppMessageIds"] = smppResult.SmppMessageIds,
                    ["MessageParts"] = smppResult.MessageParts,
                    ["ChannelType"] = "SMPP",
                    ["Duration"] = totalDuration.TotalMilliseconds
                };

                // Return appropriate result based on number of parts
                if (smppResult.MessageParts == 1)
                {
                    return MessageResult.CreateSuccess(smppResult.SmppMessageId ?? "", channelData);
                }
                else
                {
                    return MessageResult.CreateSuccessMultiPart(smppResult.SmppMessageIds, channelData);
                }
            }
            else
            {
                _logger.LogWarning("SMPP send operation failed for {PhoneNumber} in {Duration}ms: {ErrorMessage}", 
                    message.Recipient, totalDuration.TotalMilliseconds, smppResult.ErrorMessage);
                    
                return MessageResult.CreateFailure(
                    smppResult.ErrorMessage ?? "Unknown SMPP error",
                    smppResult.Exception?.HResult
                );
            }
        }
        catch (Exception ex)
        {
            var duration = DateTime.UtcNow - startTime;
            _logger.LogError(ex, "SMPP send operation exception for {PhoneNumber} after {Duration}ms: {ErrorMessage}", 
                message.Recipient, duration.TotalMilliseconds, ex.Message);
                
            return MessageResult.CreateFailure(
                $"SMPP error: {ex.Message}",
                ex.HResult
            );
        }
    }
}