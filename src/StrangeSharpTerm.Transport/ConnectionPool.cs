using StrangeSharpTerm.Model;

namespace StrangeSharpTerm.Transport;

/// <summary>
/// One authenticated connection to a host, over which everything else runs:
/// shell channels, SFTP, port forwards and commands.
/// </summary>
public interface ISshSession : IDisposable
{
    bool IsConnected { get; }
}

/// <summary>
/// One session per host, shared by every consumer.
///
/// This is what <c>ControlMaster</c> was for: authenticate once, then multiplex.
/// SSH.NET gives the multiplexing natively, so what is left is the bookkeeping —
/// who is connected, and what to do when a connection has gone away underneath
/// its users. No control socket, no path-length arithmetic, and it works on
/// Windows.
/// </summary>
public sealed class ConnectionPool(Func<ResolvedConnection, ISshSession> connect) : IDisposable
{
    private readonly Dictionary<NodeId, ISshSession> _sessions = [];
    private readonly Lock _gate = new();
    private bool _disposed;

    /// <summary>Sessions currently held open.</summary>
    public int OpenCount
    {
        get
        {
            lock (_gate)
                return _sessions.Count;
        }
    }

    public bool IsOpen(NodeId connection)
    {
        lock (_gate)
            return _sessions.TryGetValue(connection, out var session) && session.IsConnected;
    }

    /// <summary>
    /// The session for a host, opening one if needed.
    ///
    /// A session that has dropped is replaced rather than handed back: the caller
    /// asked for a usable connection, and returning a dead one only moves the
    /// failure somewhere less helpful.
    /// </summary>
    public ISshSession Session(ResolvedConnection connection)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (_sessions.TryGetValue(connection.Connection.Id, out var existing))
            {
                if (existing.IsConnected)
                    return existing;
                Close(connection.Connection.Id, existing);
            }

            var session = connect(connection);
            _sessions[connection.Connection.Id] = session;
            return session;
        }
    }

    public void Disconnect(NodeId connection)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(connection, out var session))
                Close(connection, session);
        }
    }

    public void DisconnectAll()
    {
        lock (_gate)
        {
            foreach (var (id, session) in _sessions.ToArray())
                Close(id, session);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        DisconnectAll();
    }

    private void Close(NodeId connection, ISshSession session)
    {
        _sessions.Remove(connection);
        // A session that throws on the way out must not keep the pool from
        // forgetting it; it is already removed above.
        try
        {
            session.Dispose();
        }
        catch (Exception)
        {
            // Nothing useful to do: the caller is disconnecting either way.
        }
    }
}
