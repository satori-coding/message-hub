using Inetlab.SMPP;
using Inetlab.SMPP.Common;

namespace MessageHub.Channels.Smpp;

/// <summary>
/// Represents a pooled SMPP connection
/// </summary>
internal class SmppConnection : IDisposable
{
    public SmppClient Client { get; }
    public bool IsHealthy => Client.Status == ConnectionStatus.Bound;
    public DateTime LastUsed { get; set; }
    public bool IsAvailable { get; set; }

    public SmppConnection(SmppClient client)
    {
        Client = client ?? throw new ArgumentNullException(nameof(client));
        LastUsed = DateTime.UtcNow;
        IsAvailable = true;
    }

    public void Dispose()
    {
        try
        {
            if (Client.Status == ConnectionStatus.Bound)
            {
                Task.Run(() => Client.Disconnect()).Wait(TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception)
        {
            // Ignore disconnect errors during disposal
        }
        finally
        {
            Client?.Dispose();
        }
    }
}