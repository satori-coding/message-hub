using Inetlab.SMPP;
using Inetlab.SMPP.Common;

namespace MessageHub.Channels.Smpp;

/// <summary>
/// Wrapper for a pooled SMPP connection with metadata for pool management
/// PURPOSE:
/// - Wraps the raw SmppClient from Inetlab.SMPP library
/// - Tracks connection health and usage for pool management
/// - Provides clean disposal of SMPP resources
/// </summary>
internal class SmppConnection : IDisposable
{
    /// <summary>
    /// The actual SMPP client that communicates with external SMS provider
    /// This is from Inetlab.SMPP library and handles the SMPP protocol
    /// </summary>
    public SmppClient Client { get; }
    
    /// <summary>
    /// Connection is healthy if authenticated (Bound) with SMPP server
    /// ConnectionStatus.Bound means we can send SMS through this connection
    /// </summary>
    public bool IsHealthy => Client.Status == ConnectionStatus.Bound;
    
    /// <summary>
    /// When this connection was last used for sending SMS
    /// Used by pool to identify stale connections
    /// </summary>
    public DateTime LastUsed { get; set; }
    
    /// <summary>
    /// Whether this connection is available in the pool (not currently in use)
    /// False = currently being used to send an SMS
    /// True = ready for next SMS send operation
    /// </summary>
    public bool IsAvailable { get; set; }

    /// <summary>
    /// Constructor: Wraps an authenticated SMPP client for pool management
    /// INPUT: SmppClient that is already connected and bound to SMPP server
    /// </summary>
    public SmppConnection(SmppClient client)
    {
        Client = client ?? throw new ArgumentNullException(nameof(client));
        LastUsed = DateTime.UtcNow;  // Track when connection was created
        IsAvailable = true;          // Initially available for use
    }

    /// <summary>
    /// Properly closes SMPP connection and releases resources
    /// PROCESS:
    /// 1. Send unbind PDU to SMPP server (polite disconnect)
    /// 2. Close TCP connection
    /// 3. Dispose SmppClient resources
    /// 4. Handle any errors gracefully during cleanup
    /// </summary>
    public void Dispose()
    {
        try
        {
            // 1. If still connected, send unbind to SMPP server (clean disconnect)
            if (Client.Status == ConnectionStatus.Bound)
            {
                Task.Run(() => Client.Disconnect()).Wait(TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception)
        {
            // 2. Ignore disconnect errors during disposal (server might be down)
        }
        finally
        {
            // 3. Always dispose the underlying SmppClient resources
            Client?.Dispose();
        }
    }
}