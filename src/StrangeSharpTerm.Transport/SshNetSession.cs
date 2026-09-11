using Renci.SshNet;

namespace StrangeSharpTerm.Transport;

public sealed record CommandResult(int ExitStatus, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitStatus == 0;
}

/// <summary>
/// A live connection to one host, and everything that rides on it.
///
/// Shell channels, commands and port forwards all share the single
/// authentication this session performed, which is the property ControlMaster
/// existed to provide. SFTP is the exception — see <see cref="OpenSftp"/>.
/// </summary>
public sealed class SshNetSession(SshClient client, ConnectionInfo connectionInfo, string? hostKeyFingerprint)
    : ISshSession
{
    private readonly List<IDisposable> _owned = [];
    private readonly List<IDisposable> _underneath = [];

    public SshClient Client { get; } = client;

    public ConnectionInfo ConnectionInfo { get; } = connectionInfo;

    /// <summary>The fingerprint the server offered, in <c>ssh-keygen -lf</c>'s form.</summary>
    public string? HostKeyFingerprint { get; } = hostKeyFingerprint;

    public bool IsConnected => Client.IsConnected;

    /// <summary>Runs a command and waits for it. One channel on the existing connection.</summary>
    public CommandResult Run(string commandText, TimeSpan? timeout = null)
    {
        using var command = Client.CreateCommand(commandText);
        if (timeout is { } limit)
            command.CommandTimeout = limit;
        var output = command.Execute();
        return new CommandResult(command.ExitStatus ?? -1, output, command.Error);
    }

    /// <summary>
    /// A pty-backed shell channel: the terminal pane's byte source. The server
    /// allocates the pty, so there is no local one to manage on either platform.
    /// </summary>
    public ShellStream OpenShell(string terminalName, uint columns, uint rows, int bufferSize = 4096)
    {
        var shell = Client.CreateShellStream(terminalName, columns, rows, 0, 0, bufferSize);
        _owned.Add(shell);
        return shell;
    }

    /// <summary>
    /// Starts a local forward and returns it started. Stopping it releases the
    /// port; disposing the session stops any that are still running.
    /// </summary>
    public ForwardedPortLocal StartLocalForward(string boundHost, uint boundPort, string host, uint port)
    {
        var forward = new ForwardedPortLocal(boundHost, boundPort, host, port);
        Client.AddForwardedPort(forward);
        forward.Start();
        _owned.Add(forward);
        return forward;
    }

    /// <summary>
    /// An SFTP client for this host.
    ///
    /// This one costs a second authentication: SSH.NET's SftpClient owns its own
    /// session rather than opening a subsystem channel on an existing one. The
    /// Swift app's SFTP rode the ControlMaster connection, so this is a real
    /// difference — it is the price of deleting 690 lines of hand-written SFTP
    /// wire protocol, and it is why the client is cached per session rather than
    /// created per transfer.
    /// </summary>
    public SftpClient OpenSftp()
    {
        if (_sftp is { IsConnected: true })
            return _sftp;

        _sftp?.Dispose();
        _sftp = new SftpClient(ConnectionInfo);
        _sftp.Connect();
        return _sftp;
    }

    private SftpClient? _sftp;

    /// <summary>
    /// Something this session is carried by, such as the jump-host sessions its
    /// forwards run over. Closed after this session, never before.
    /// </summary>
    internal void Carry(IDisposable hop) => _underneath.Add(hop);

    public void Dispose()
    {
        foreach (var owned in _owned)
        {
            try
            {
                owned.Dispose();
            }
            catch (Exception)
            {
                // Already going away; a noisy channel must not block the rest.
            }
        }
        _owned.Clear();
        _sftp?.Dispose();
        Client.Dispose();

        // Only now: a jump host still has to carry this session's traffic while
        // it is closing.
        for (var i = _underneath.Count - 1; i >= 0; i--)
            _underneath[i].Dispose();
        _underneath.Clear();
    }
}
