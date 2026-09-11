using System.Net;
using System.Net.Sockets;

namespace StrangeSharpTerm.Transport;

/// <summary>
/// Checks whether something is listening on a local port.
///
/// Worth doing rather than trusting our own bookkeeping: a forward is reported as
/// started when the request is accepted, which is not the same as a port that
/// carries traffic. The only honest answer to "is this tunnel actually up?" is to
/// try the port.
/// </summary>
public static class LocalPortProbe
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>True when a TCP connection to <c>127.0.0.1:port</c> is accepted.</summary>
    public static async Task<bool> IsListeningAsync(
        int port, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (port is < 1 or > 65535)
            return false;

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Bounded, so an unreachable address cannot stall a dashboard for the
        // kernel's full connect timeout.
        deadline.CancelAfter(timeout ?? DefaultTimeout);

        try
        {
            await socket.ConnectAsync(IPAddress.Loopback, port, deadline.Token);
            return true;
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException)
        {
            return false;
        }
    }
}
