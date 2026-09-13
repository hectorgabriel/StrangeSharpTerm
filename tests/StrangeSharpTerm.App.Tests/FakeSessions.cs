using StrangeSharpTerm.App.Terminal;
using StrangeSharpTerm.Model;
using StrangeSharpTerm.Terminal;
using StrangeSharpTerm.Transport;

namespace StrangeSharpTerm.App.Tests;

/// <summary>
/// Everything a host can be asked for, without a host.
///
/// One double for the four things <see cref="IHostSessions"/> hands out: a test
/// that only cares about shells says nothing about files, and gets a shell on a
/// channel that carries nothing.
/// </summary>
public sealed class FakeSessions : IHostSessions
{
    public Func<Connection, TerminalSession>? OnShell { get; set; }

    public Func<Connection, IRemoteFiles>? OnFiles { get; set; }

    public Func<Connection, ITunnels>? OnTunnels { get; set; }

    public Func<Connection, IServerHealth>? OnHealth { get; set; }

    /// <summary>Every host asked for, in order, whatever it was asked for.</summary>
    public List<string> Asked { get; } = [];

    public TerminalSession Shell(Connection connection)
    {
        Asked.Add($"shell {connection.Name}");
        return OnShell?.Invoke(connection) ?? new TerminalSession(new DeadChannel());
    }

    public IRemoteFiles Files(Connection connection)
    {
        Asked.Add($"files {connection.Name}");
        return OnFiles?.Invoke(connection) ?? throw new NotSupportedException("this test has no files");
    }

    public ITunnels Tunnels(Connection connection)
    {
        Asked.Add($"tunnels {connection.Name}");
        return OnTunnels?.Invoke(connection) ?? throw new NotSupportedException("this test has no tunnels");
    }

    public IServerHealth Health(Connection connection)
    {
        Asked.Add($"health {connection.Name}");
        return OnHealth?.Invoke(connection) ?? new NoHealth();
    }

    /// <summary>A channel that carries nothing, so a session needs no server.</summary>
    public sealed class DeadChannel : ITerminalChannel
    {
        public Stream Stream { get; } = new MemoryStream();

        public void Resize(int columns, int rows) { }

        public void Dispose() => Stream.Dispose();
    }

    private sealed class NoHealth : IServerHealth
    {
        public ServerMetrics Collect() => throw new NotSupportedException("this test has no server to ask");
    }
}
