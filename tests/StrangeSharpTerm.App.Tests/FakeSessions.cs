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

    public Func<Connection, IRemoteCommands>? OnCommands { get; set; }

    /// <summary>Which hosts count as connected. Empty is the default, as a fresh window is.</summary>
    public HashSet<string> Open { get; } = [];

    /// <summary>
    /// Every host asked for, in order, whatever it was asked for.
    ///
    /// Locked, because the window asks from two threads: opening a shell, a
    /// browser or a workspace goes through <c>Task.Run</c> -- a connection
    /// blocks on a network -- while building a conversation asks on the thread
    /// it was called on. A plain List mutated from both is a test that fails
    /// somewhere else, under load, for no reason anybody can see.
    /// </summary>
    public IReadOnlyList<string> Asked
    {
        get
        {
            lock (_asked)
                return [.. _asked];
        }
    }

    private readonly List<string> _asked = [];

    private void Note(string what)
    {
        lock (_asked)
            _asked.Add(what);
    }

    public TerminalSession Shell(Connection connection)
    {
        Note($"shell {connection.Name}");
        return OnShell?.Invoke(connection) ?? new TerminalSession(new DeadChannel());
    }

    public IRemoteFiles Files(Connection connection)
    {
        Note($"files {connection.Name}");
        return OnFiles?.Invoke(connection) ?? throw new NotSupportedException("this test has no files");
    }

    public ITunnels Tunnels(Connection connection)
    {
        Note($"tunnels {connection.Name}");
        return OnTunnels?.Invoke(connection) ?? throw new NotSupportedException("this test has no tunnels");
    }

    public IServerHealth Health(Connection connection)
    {
        Note($"health {connection.Name}");
        return OnHealth?.Invoke(connection) ?? new NoHealth();
    }

    public IRemoteCommands Commands(Connection connection)
    {
        Note($"commands {connection.Name}");
        return OnCommands?.Invoke(connection) ?? new NoCommands();
    }

    public bool IsOpen(Connection connection) => Open.Contains(connection.Name);

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

    private sealed class NoCommands : IRemoteCommands
    {
        public CommandResult Run(string command, TimeSpan timeout) =>
            throw new NotSupportedException("this test has no server to run on");
    }
}
