using Porta.Pty;
using Renci.SshNet;

namespace StrangeSharpTerm.Spikes.Terminal;

/// <summary>
/// The whole question this spike exists to answer: can Iciclecreek's
/// TerminalControl be driven by an SSH channel instead of a local pty?
///
/// It can, because IPtyConnection is only two streams, a resize, and an exit
/// signal -- and SSH.NET's ShellStream is a Stream whose remote end already has
/// a real pty attached by the server. There is no local pty anywhere in this
/// file, which is precisely why this design reaches Windows: no ConPTY, no
/// openpty, nothing platform-specific.
/// </summary>
internal sealed class SshPtyConnection : IPtyConnection
{
    private readonly ShellStream _stream;
    private readonly SshClient _client;

    public SshPtyConnection(SshClient client, ShellStream stream)
    {
        _client = client;
        _stream = stream;
        _client.ErrorOccurred += (_, _) => RaiseExited(255);
    }

    public Stream ReaderStream => _stream;
    public Stream WriterStream => _stream;

    // Nothing local was spawned, so there is no pid. The control uses this for
    // display only; a sentinel is more honest than inventing one.
    public int Pid => -1;
    public int ExitCode { get; private set; }
    public bool SupportsCancellableRead => false;

    /// <summary>
    /// Required by the interface, and never raised. PtyExitedEventArgs has an
    /// internal constructor, so no implementation outside Porta.Pty can
    /// construct one. That is a limitation of the seam, not a blocker: the
    /// control's exit handling is about a local child process, and there isn't
    /// one here. We own the SshClient, so we know when the session ends and
    /// report it ourselves through <see cref="Exited"/>.
    /// </summary>
    public event EventHandler<PtyExitedEventArgs>? ProcessExited
    {
        add { }
        remove { }
    }

    /// <summary>Raised when the SSH session behind this terminal ends.</summary>
    public event EventHandler<int>? Exited;

    /// <summary>
    /// The remote pty is resized with a window-change channel request. In the
    /// Swift app this was SwiftTerm's job and the delegate hook was an empty
    /// no-op; here it is one call, and it is still not our arithmetic.
    /// </summary>
    public void Resize(int cols, int rows)
        => _stream.ChangeWindowSize((uint)cols, (uint)rows, (uint)(cols * 8), (uint)(rows * 16));

    public void Kill()
    {
        try { _stream.Close(); } catch { /* closing a dead channel is not an error */ }
        try { if (_client.IsConnected) _client.Disconnect(); } catch { }
        RaiseExited(0);
    }

    public bool WaitForExit(int milliseconds)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < deadline)
        {
            if (!_client.IsConnected) return true;
            Thread.Sleep(50);
        }
        return !_client.IsConnected;
    }

    private void RaiseExited(int code)
    {
        ExitCode = code;
        Exited?.Invoke(this, code);
    }

    public void Dispose()
    {
        _stream.Dispose();
        _client.Dispose();
    }
}
