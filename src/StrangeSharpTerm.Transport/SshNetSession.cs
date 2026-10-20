using Renci.SshNet;

namespace StrangeSharpTerm.Transport;

public sealed record CommandResult(int ExitStatus, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitStatus == 0 && !Stopped;

    /// <summary>
    /// Whether the wait ran out and the command was told to stop: a TERM to
    /// its process group, which is what the server does with a signal it
    /// supports. What arrived before that is still in the output.
    /// </summary>
    public bool Stopped { get; init; }

    /// <summary>
    /// The signal the server says the command ended by, or null. After
    /// <see cref="Stopped"/>, null means the server never confirmed the
    /// command ended -- it ignored the signal, or does not support them -- and
    /// it may still be running.
    /// </summary>
    public string? ExitSignal { get; init; }
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

    /// <summary>
    /// Runs a command and waits for it. One channel on the existing connection.
    ///
    /// A command that outlives <paramref name="timeout"/> is stopped rather
    /// than waited on, and comes back <see cref="CommandResult.Stopped"/> with
    /// what it had said by then. Cancelling stops it as well, and then throws:
    /// a person pressing Stop means the command too, not only the waiting.
    /// </summary>
    public CommandResult Run(string commandText, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        using var command = Client.CreateCommand(commandText);
        return Wait(command, timeout, cancellationToken, _ => { });
    }

    /// <summary>
    /// Waits for a command that is started here, stopping it when the time or
    /// the caller runs out.
    ///
    /// Stopping is SSH.NET's: cancelling the execution sends the server a TERM
    /// for the command, and OpenSSH delivers it to the command's whole process
    /// group -- a pipeline, a loop and a shell's children all end with it,
    /// which was checked against a real sshd rather than assumed. A process
    /// that ignores TERM survives, and a server that does not implement
    /// signals never answers; either way no exit signal comes back, and
    /// <see cref="CommandResult.ExitSignal"/> says so by being null.
    /// </summary>
    /// <param name="started">What to do once it is running: write its input, for <see cref="RunFeeding"/>.</param>
    private static CommandResult Wait(SshCommand command, TimeSpan? timeout, CancellationToken cancellationToken, Action<SshCommand> started)
    {
        using var limit = timeout is { } span ? new CancellationTokenSource(span) : new CancellationTokenSource();
        using var either = CancellationTokenSource.CreateLinkedTokenSource(limit.Token, cancellationToken);

        var running = command.ExecuteAsync(either.Token);
        started(command);

        try
        {
            running.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (either.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new CommandResult(-1, command.Result, command.Error) { Stopped = true, ExitSignal = command.ExitSignal };
        }

        return new CommandResult(command.ExitStatus ?? -1, command.Result, command.Error) { ExitSignal = command.ExitSignal };
    }

    /// <summary>
    /// Runs a command with something written to its standard input, which is
    /// then closed.
    ///
    /// For one thing only so far: a host's password, handed to <c>sudo -S</c>.
    /// On the input rather than in the command because the command line is
    /// public -- anybody on the server running <c>ps</c> reads it -- and the
    /// input is not. Closed straight after, so that once <c>sudo</c> has read
    /// its line the command that follows reads the end of its input and not a
    /// second copy.
    /// </summary>
    /// <remarks>
    /// The bytes are cleared as soon as they are written. The string they came
    /// from is not this method's to clear, and SSH.NET holds the same password
    /// as a string to authenticate with; this is the one copy that is only here.
    /// Nothing in this method logs <paramref name="input"/>, and nothing may.
    /// </remarks>
    public CommandResult RunFeeding(string commandText, TimeSpan? timeout, string input, CancellationToken cancellationToken = default)
    {
        using var command = Client.CreateCommand(commandText);
        return Wait(command, timeout, cancellationToken, running =>
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(input);
            try
            {
                using var stdin = running.CreateInputStream();
                stdin.Write(bytes, 0, bytes.Length);
            }
            finally
            {
                Array.Clear(bytes);
            }
        });
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
        StartForward(forward);
        return forward;
    }

    /// <summary>
    /// Starts a forward of any kind and keeps it: disposing the session stops
    /// whatever is still up, so quitting cannot leave a port bound.
    /// </summary>
    public void StartForward(ForwardedPort forward)
    {
        Client.AddForwardedPort(forward);
        forward.Start();
        _owned.Add(forward);
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
