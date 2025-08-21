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

    /// <summary>
    /// Constructor: Sets up the SMPP channel with connection pooling and keep-alive mechanism
    /// 1. Validates configuration (host, credentials, timeouts)
    /// 2. Initializes connection pool data structures
    /// 3. Sets up semaphore to limit concurrent connections (e.g., max 3 connections)
    /// 4. Starts background timer for keep-alive (enquire_link every 30 seconds)
    /// </summary>
    public SmppChannel(SmppChannelConfiguration configuration, ILogger<SmppChannel> logger)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        
        // 1. Validate that required settings are provided (host, credentials, etc.)
        _configuration.Validate();

        // 2. Initialize connection pool structures:
        _availableConnections = new ConcurrentQueue<SmppConnection>();  // Pool of ready-to-use connections
        _allConnections = new ConcurrentDictionary<int, SmppConnection>(); // Track all connections for cleanup
        _connectionSemaphore = new SemaphoreSlim(_configuration.MaxConnections, _configuration.MaxConnections); // Limit concurrent connections

        // 3. Start background keep-alive timer (sends enquire_link to all connections periodically)
        _keepAliveTimer = new Timer(SendKeepAlive, null, _configuration.KeepAliveInterval, _configuration.KeepAliveInterval);

        _logger.LogInformation("SMPP Channel initialized with max {MaxConnections} connections to {Host}:{Port}", 
            _configuration.MaxConnections, _configuration.Host, _configuration.Port);
    }

    /// <summary>
    /// Main SMS sending method through SMPP protocol
    /// FLOW:
    /// 1. Validate input (phone number and content required)
    /// 2. Get connection from pool (reuse existing or create new)
    /// 3. Validate connection health (test with enquire_link)
    /// 4. Submit SMS with retry logic and timeout handling
    /// 5. Process response and extract provider message IDs
    /// 6. Return connection to pool for reuse
    /// 
    /// The SMPP server endpoint is configured in SmppChannelConfiguration.Host:Port
    /// </summary>
    public async Task<SmppSendResult> SendSmsAsync(SmppMessage message)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SmppChannel));

        // 1. Input validation - both phone number and message content are required
        if (string.IsNullOrWhiteSpace(message.PhoneNumber) || string.IsNullOrWhiteSpace(message.Content))
        {
            return SmppSendResult.Failure("Phone number and content are required");
        }

        _logger.LogInformation("Begin SendSmsAsync for phone {PhoneNumber}", message.PhoneNumber);

        SmppConnection? connection = null;
        
        try
        {
            // 2. Get connection from pool (reuses existing healthy connections or creates new ones)
            //    This calls GetConnectionAsync() which handles connection pooling logic
            connection = await GetConnectionAsync();
            _logger.LogDebug("SMPP connection retrieved from pool. IsHealthy={IsHealthy}, Status={Status}", 
                connection.IsHealthy, connection.Client.Status);

            // 3. Double-check connection health before using (connection could have gone stale)
            //    ConnectionStatus.Bound means we're authenticated and ready to send
            if (!connection.IsHealthy || connection.Client.Status != ConnectionStatus.Bound)
            {
                _logger.LogWarning("SMPP connection unhealthy, requesting fresh connection. Status={Status}", 
                    connection.Client.Status);
                
                // Return bad connection to pool (it will be disposed) and get a fresh one
                ReturnConnection(connection);
                connection = await GetConnectionAsync();
                
                // If we still can't get a healthy connection, fail the request
                if (!connection.IsHealthy || connection.Client.Status != ConnectionStatus.Bound)
                {
                    return SmppSendResult.Failure($"Unable to get healthy SMPP connection. Status={connection.Client.Status}");
                }
            }

            _logger.LogDebug("Message submit prepared. From={From}, To={To}, ContentLength={ContentLength}, DeliveryReceipt={RequestDR}", 
                message.From, message.PhoneNumber, message.Content.Length, message.RequestDeliveryReceipt);

            // 4. Submit SMS with comprehensive retry logic and timeout handling
            //    The SMPP protocol uses submit_sm PDUs (Protocol Data Units) to send messages
            //    We retry up to 2 times with fresh connections if needed
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

                    // Submit SMS message to SMPP server with timeout protection
                    _logger.LogInformation("SMPP client status before submit attempt {Retry}: {Status}", retryCount + 1, connection.Client.Status);
                    
                    // Build the submit_sm PDU with message details and optional delivery receipt request
                    var submitTask = Task.Run(() => {
                        var smsBuilder = SMS.ForSubmit()
                            .From(message.From)                                    // Sender ID
                            .To(message.PhoneNumber.TrimStart('+'))               // Remove + prefix for SMPP
                            .Text(message.Content);                               // Message content
                        
                        // Request delivery receipt (DLR) if configured
                        if (message.RequestDeliveryReceipt)
                        {
                            smsBuilder = smsBuilder.DeliveryReceipt();
                        }
                        
                        // Actually submit to SMPP server - this is where the external SMPP endpoint is called
                        return connection.Client.Submit(smsBuilder);
                    });
                    var timeoutTask = Task.Delay(_configuration.SubmitTimeout); // Configurable submit timeout (default 10s)
                    
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
            
            // 5. Process SMPP server responses (submit_sm_resp PDUs)
            //    Each response contains status and unique message ID from the provider
            if (submitResponses == null)
            {
                return SmppSendResult.Failure("Submit failed - no responses received");
            }

            _logger.LogDebug("SubmitSm responses received. Count={Count}", submitResponses.Length);
            
            // Log each response with provider-assigned message IDs
            foreach (var resp in submitResponses)
            {
                _logger.LogInformation("SubmitSmResp: Status={Status}, Sequence={Sequence}, MessageId={MessageId}", 
                    resp.Header.Status, resp.Header.Sequence, resp.MessageId);
            }

            // Verify all parts were accepted by SMPP server
            // CommandStatus.ESME_ROK means "No Error" in SMPP protocol
            if (!submitResponses.All(x => x.Header.Status == CommandStatus.ESME_ROK))
            {
                var failedResponses = submitResponses.Where(x => x.Header.Status != CommandStatus.ESME_ROK);
                var errorStatuses = string.Join(", ", failedResponses.Select(x => x.Header.Status));
                _logger.LogError("SMPP submit failed. Statuses: {Statuses}", errorStatuses);
                return SmppSendResult.Failure($"SMPP submit failed with statuses: {errorStatuses}");
            }

            // 6. Collect provider message IDs (used later for delivery receipt correlation)
            //    Long messages are split into multiple parts, each gets its own ID
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
            // 7. Always return connection to pool for reuse (crucial for performance)
            //    Healthy connections are queued for reuse, unhealthy ones are disposed
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

    /// <summary>
    /// Connection pool management - gets a ready-to-use SMPP connection
    /// FLOW:
    /// 1. Wait for available connection slot (semaphore limits concurrent connections)
    /// 2. Try to reuse existing healthy connection from pool
    /// 3. Validate connection health with enquire_link test
    /// 4. If no healthy connection available, create new one
    /// 5. Mark connection as "in use" and return it
    /// </summary>
    private async Task<SmppConnection> GetConnectionAsync()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SmppChannel));

        // 1. Wait for available connection slot (e.g., max 3 concurrent connections)
        await _connectionSemaphore.WaitAsync();

        try
        {
            // 2. Try to reuse an existing connection from the pool
            if (_availableConnections.TryDequeue(out var availableConnection))
            {
                // 3. Test connection health with enquire_link (SMPP heartbeat)
                if (await ValidateConnectionHealthAsync(availableConnection))
                {
                    availableConnection.IsAvailable = false;  // Mark as "in use"
                    availableConnection.LastUsed = DateTime.UtcNow;
                    _logger.LogDebug("Reusing existing healthy SMPP connection");
                    return availableConnection;
                }
                else
                {
                    // Connection is stale/broken, dispose it
                    _logger.LogWarning("Connection failed health validation, disposing");
                    _allConnections.TryRemove(availableConnection.GetHashCode(), out _);
                    availableConnection.Dispose();
                }
            }

            // 4. No healthy connection available, create a new one
            //    This will connect to the SMPP server endpoint specified in configuration
            _logger.LogInformation("Creating new SMPP connection to {Host}:{Port}", _configuration.Host, _configuration.Port);
            var connection = await CreateConnectionAsync();
            
            var connectionId = Interlocked.Increment(ref _connectionCounter);
            _allConnections.TryAdd(connectionId, connection);
            
            // 5. Mark new connection as "in use"
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

    /// <summary>
    /// Validates if an SMPP connection is still healthy and responsive
    /// PROCESS:
    /// 1. Check if connection is marked as healthy (Status == Bound)
    /// 2. Send enquire_link PDU to test server responsiveness
    /// 3. Wait max 5 seconds for enquire_link_resp
    /// 4. Return true if server responds, false if timeout or error
    /// </summary>
    private async Task<bool> ValidateConnectionHealthAsync(SmppConnection connection)
    {
        try
        {
            // 1. Quick check - if connection isn't bound, it's definitely not healthy
            if (!connection.IsHealthy)
            {
                _logger.LogDebug("Connection marked as unhealthy");
                return false;
            }

            // 2. Send enquire_link PDU (SMPP heartbeat) to test if server is responsive
            var enquireLinkTask = Task.Run(() => connection.Client.EnquireLink());
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(5)); // Health check timeout
            
            var completedTask = await Task.WhenAny(enquireLinkTask, timeoutTask);
            
            // 3. Check if enquire_link timed out (server not responding)
            if (completedTask == timeoutTask)
            {
                _logger.LogDebug("Connection health validation timed out after 5s");
                return false;
            }
            
            // 4. Get result and check for exceptions
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

    /// <summary>
    /// Returns a used connection back to the pool for reuse
    /// PROCESS:
    /// 1. Check if connection is still healthy
    /// 2. If healthy: mark as available and add back to pool queue
    /// 3. If unhealthy: dispose connection and remove from tracking
    /// 4. Always release semaphore slot for new connections
    /// </summary>
    private void ReturnConnection(SmppConnection connection)
    {
        if (_disposed || connection == null)
        {
            _connectionSemaphore.Release();
            return;
        }

        try
        {
            // 1. Check if connection is still healthy for reuse
            if (connection.IsHealthy)
            {
                // 2. Mark as available and return to pool for next SMS
                connection.IsAvailable = true;
                connection.LastUsed = DateTime.UtcNow;
                _availableConnections.Enqueue(connection);
                _logger.LogDebug("Returned healthy SMPP connection to pool");
            }
            else
            {
                // 3. Connection is broken, dispose it
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
            // 4. Always release semaphore slot so new connections can be created
            _connectionSemaphore.Release();
        }
    }

    /// <summary>
    /// Creates a new SMPP connection to the external SMS provider
    /// PROCESS:
    /// 1. Create SmppClient instance (from Inetlab.SMPP library)
    /// 2. Connect to SMPP server with timeout (TCP connection)
    /// 3. Authenticate with bind_transceiver (username/password)
    /// 4. Register delivery receipt handler for incoming DLRs
    /// 5. Return wrapped connection ready for SMS sending
    /// 
    /// This is where the actual connection to external SMPP server happens!
    /// Server endpoint comes from _configuration.Host and _configuration.Port
    /// </summary>
    private async Task<SmppConnection> CreateConnectionAsync()
    {
        var client = new SmppClient();
        var startTime = DateTime.UtcNow;
        
        try
        {
            _logger.LogInformation("Starting SMPP connection to {Host}:{Port} (Connection timeout: {ConnectionTimeout}s, Bind timeout: {BindTimeout}s)", 
                _configuration.Host, _configuration.Port, _configuration.ConnectionTimeout.TotalSeconds, _configuration.BindTimeout.TotalSeconds);
            
            // 1. Establish TCP connection to external SMPP server with timeout protection
            //    _configuration.Host and _configuration.Port specify the SMS provider endpoint
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

            // 2. Register handler for incoming delivery receipts BEFORE authentication
            //    When SMS provider sends delivery status updates, they come through evDeliverSm event
            client.evDeliverSm += OnDeliveryReceiptHandler;
            _logger.LogDebug("Registered evDeliverSm event handler for DLR processing");

            // 3. Authenticate with SMPP server using credentials (bind_transceiver PDU)
            //    Transceiver mode allows both sending SMS and receiving delivery receipts
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

    /// <summary>
    /// Handles incoming delivery receipts from SMPP server
    /// PROCESS:
    /// 1. Check if incoming message is a delivery receipt (not a mobile-originated SMS)
    /// 2. Extract message ID to correlate with originally sent SMS
    /// 3. Parse delivery status (DELIVRD, UNDELIV, etc.) and error codes
    /// 4. Fire event to notify MessageService about delivery status update
    /// 
    /// This is called when the external SMS provider sends delivery confirmations
    /// </summary>
    private void OnDeliveryReceiptHandler(object sender, DeliverSm deliverSm)
    {
        try
        {
            // 1. Check if this is a delivery receipt (vs. incoming SMS from mobile)
            if (deliverSm.MessageType == MessageTypes.SMSCDeliveryReceipt)
            {
                _logger.LogInformation("Received delivery receipt from {SourceAddr}", deliverSm.SourceAddress.Address);

                var receiptText = deliverSm.Receipt?.ToString() ?? "";
                var sourceAddr = deliverSm.SourceAddress.Address;
                var smppMessageId = deliverSm.Receipt?.MessageId ?? "";

                if (!string.IsNullOrEmpty(smppMessageId))
                {
                    // 2. Create structured delivery receipt object
                    var receipt = new SmppDeliveryReceipt
                    {
                        SmppMessageId = smppMessageId,                           // Correlate with sent SMS
                        SourceAddress = sourceAddr,
                        ReceiptText = receiptText,                               // Raw receipt text
                        ReceivedAt = DateTime.UtcNow,
                        DeliveryStatus = ParseDeliveryStatus(receiptText),       // DELIVRD, UNDELIV, etc.
                        ErrorCode = ParseErrorCode(receiptText)                  // Error details if failed
                    };

                    // 3. Fire event to notify MessageService about status update
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

    /// <summary>
    /// Background timer method: sends keep-alive messages to all SMPP connections
    /// PURPOSE:
    /// 1. Prevents SMPP servers from closing idle connections
    /// 2. Detects broken connections early (enquire_link will fail)
    /// 3. Runs every 30 seconds by default
    /// 
    /// Uses enquire_link PDU (SMPP heartbeat mechanism)
    /// </summary>
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
                        // Send enquire_link PDU to keep connection alive with SMPP server
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

    /// <summary>
    /// Universal message channel interface implementation
    /// FLOW:
    /// 1. Convert generic Message to SMPP-specific SmppMessage format
    /// 2. Apply overall API timeout (default 45s) to prevent infinite waits
    /// 3. Call the main SendSmsAsync method
    /// 4. Convert SMPP-specific result back to generic MessageResult
    /// 
    /// This is the entry point called by MessageService for SMPP channel
    /// </summary>
    async Task<MessageResult> IMessageChannel.SendAsync(Message message)
    {
        var startTime = DateTime.UtcNow;
        
        try
        {
            _logger.LogInformation("Starting SMPP send operation for {PhoneNumber} (API timeout: {ApiTimeout}s)", 
                message.Recipient, _configuration.ApiTimeout.TotalSeconds);
            
            // 1. Convert generic message to SMPP-specific format
            var smppMessage = new SmppMessage
            {
                PhoneNumber = message.Recipient,
                Content = message.Content,
                From = "MessageHub",
                RequestDeliveryReceipt = true
            };

            // 2. Execute SMS sending with overall timeout protection
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