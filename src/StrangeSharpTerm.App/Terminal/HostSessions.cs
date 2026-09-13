using StrangeSharpTerm.Model;
using StrangeSharpTerm.Security;
using StrangeSharpTerm.Terminal;
using StrangeSharpTerm.Transport;

namespace StrangeSharpTerm.App.Terminal;

/// <summary>
/// Everything the window can ask a host for.
///
/// One seam rather than four: a shell, a directory listing, somewhere to start a
/// forward, and a health check are all things one connection carries, and the
/// window should not have to know that they arrive by different routes.
/// </summary>
public interface IHostSessions
{
    /// <summary>A shell on a pty the server owns.</summary>
    TerminalSession Shell(Connection connection);

    IRemoteFiles Files(Connection connection);

    ITunnels Tunnels(Connection connection);

    /// <summary>Lazy: built without connecting, because a dashboard is built before it is asked.</summary>
    IServerHealth Health(Connection connection);
}

/// <summary>
/// One authenticated session per host, and everything else riding it.
///
/// This is what <see cref="ConnectionPool"/> was written for in M2, and until
/// now nothing used it: each feature built its own factory and authenticated
/// again, so a host with a shell, a browser, a set of tunnels and a dashboard
/// open had four connections to it. Counting them in <c>sshd.log</c> is how that
/// was noticed.
///
/// It is the property the whole transport design turns on — ControlMaster gave
/// the Swift app one authentication per host, and SSH.NET gives it natively —
/// so it belongs in one place that everything goes through.
/// </summary>
public sealed class HostSessions : IHostSessions, IDisposable
{
    private readonly Func<InventoryTree> _tree;
    private readonly ConnectionPool _pool;

    /// <param name="tree">
    /// Read each time rather than held: the inventory is edited while the app
    /// runs, and a session opened against last week's settings is not the one
    /// the user is looking at.
    /// </param>
    public HostSessions(Func<InventoryTree> tree, IHostKeyPrompt? prompt = null)
    {
        _tree = tree;
        var refuse = prompt ?? new RefuseUnknownHostKeys();
        _pool = new ConnectionPool(resolved =>
            new SshSessionFactory(
                    new PlatformSecretStore(),
                    refuse,
                    id => _tree().Credentials.GetValueOrDefault(id))
                .Connect(resolved));
    }

    /// <summary>How many hosts are connected. The number the pool exists to keep down.</summary>
    public int OpenCount => _pool.OpenCount;

    public TerminalSession Shell(Connection connection) =>
        new(SshTerminalChannel.Open(Session(connection)));

    /// <summary>
    /// SFTP, which costs a second authentication whatever we do: SSH.NET's
    /// client owns its own session rather than opening a subsystem channel on
    /// this one. It is cached per session, so it is one extra and not one per
    /// listing — see <see cref="SshNetSession.OpenSftp"/>.
    /// </summary>
    public IRemoteFiles Files(Connection connection) => new SftpFiles(Session(connection).OpenSftp());

    public ITunnels Tunnels(Connection connection) => new SshTunnels(Session(connection));

    public IServerHealth Health(Connection connection) => new Lazily(() => new SshServerHealth(Session(connection)));

    /// <summary>Closes a host's session, and everything riding it.</summary>
    public void Disconnect(NodeId host) => _pool.Disconnect(host);

    public void Dispose() => _pool.Dispose();

    private SshNetSession Session(Connection connection) =>
        (SshNetSession)_pool.Session(_tree().Resolve(connection.Id));

    /// <summary>
    /// A health check that connects when it is first used.
    ///
    /// The dashboard is built the moment a host is selected, and selecting a
    /// host deliberately does not connect to it.
    /// </summary>
    private sealed class Lazily(Func<IServerHealth> open) : IServerHealth
    {
        private IServerHealth? _opened;

        public ServerMetrics Collect() => (_opened ??= open()).Collect();
    }
}
