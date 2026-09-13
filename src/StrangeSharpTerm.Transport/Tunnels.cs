using Renci.SshNet;
using StrangeSharpTerm.Model;

namespace StrangeSharpTerm.Transport;

/// <summary>
/// A forward that is up.
///
/// Stopping it releases the port, which is the property the integration gate
/// asserts: a tunnel that "stopped" while still holding its port cannot be
/// started again, and the error the second attempt gives blames the wrong thing.
/// </summary>
public interface IRunningTunnel : IDisposable
{
    bool IsRunning { get; }

    void Stop();
}

/// <summary>
/// Somewhere to start a tunnel.
///
/// The seam the tunnels pane is written against, for the reason the file
/// browser has one: what a pane does with a forward that will not start is a
/// decision, and testing it should not need a server.
/// </summary>
public interface ITunnels
{
    /// <summary>
    /// Starts one, or throws if the far end or the local port refuses.
    /// </summary>
    IRunningTunnel Start(PortForward forward);
}

/// <summary>
/// <see cref="ITunnels"/> over SSH.NET's forwarded ports — all three kinds the
/// model has, where the session had only the local one that M2 needed.
/// </summary>
public sealed class SshTunnels(SshNetSession session) : ITunnels
{
    public IRunningTunnel Start(PortForward forward)
    {
        // Empty means loopback, which is the model's safe default: a forward
        // bound to 0.0.0.0 is reachable by the whole network and is always an
        // explicit choice.
        var bind = forward.BindAddress.Length == 0 ? "127.0.0.1" : forward.BindAddress;

        ForwardedPort port = forward.Kind switch
        {
            PortForwardKind.Local => new ForwardedPortLocal(
                bind, (uint)forward.BindPort, forward.DestinationHost, (uint)forward.DestinationPort),
            PortForwardKind.Remote => new ForwardedPortRemote(
                bind, (uint)forward.BindPort, forward.DestinationHost, (uint)forward.DestinationPort),
            PortForwardKind.Dynamic => new ForwardedPortDynamic(bind, (uint)forward.BindPort),
            _ => throw new ArgumentOutOfRangeException(nameof(forward), forward.Kind, "unknown kind of forward"),
        };

        session.StartForward(port);
        return new Running(port);
    }

    private sealed class Running(ForwardedPort port) : IRunningTunnel
    {
        public bool IsRunning => port.IsStarted;

        public void Stop()
        {
            if (port.IsStarted)
                port.Stop();
        }

        public void Dispose()
        {
            Stop();
            port.Dispose();
        }
    }
}
