using Porta.Pty;
using StrangeSharpTerm.Terminal;

namespace StrangeSharpTerm.App.Terminal;

/// <summary>
/// What the renderer sees: two streams, a resize, and an exit.
///
/// The streams are the session's, so the control's reads feed the session's
/// engine on the way past and the two can never show different things. There is
/// no local process behind any of it — the pty belongs to the server — which is
/// exactly why this works the same on both platforms.
/// </summary>
internal sealed class SessionPtyConnection(TerminalSession session) : IPtyConnection
{
    public Stream ReaderStream => session.Stream;

    public Stream WriterStream => session.Stream;

    /// <summary>
    /// Nothing local was spawned, so there is no process id. The control shows
    /// this and nothing more; a sentinel is more honest than inventing one.
    /// </summary>
    public int Pid => -1;

    public int ExitCode { get; private set; }

    public bool SupportsCancellableRead => false;

    /// <summary>
    /// Required by the interface and never raised: <c>PtyExitedEventArgs</c> has an
    /// internal constructor, so nothing outside the library can build one. It
    /// costs nothing here, because that event is about a local child process and
    /// there is none. The session's own Ended event is what the app listens to.
    /// </summary>
    public event EventHandler<PtyExitedEventArgs>? ProcessExited
    {
        add { }
        remove { }
    }

    public void Resize(int cols, int rows) => session.Resize(cols, rows);

    public void Kill()
    {
        ExitCode = 0;
        session.Dispose();
    }

    public bool WaitForExit(int milliseconds)
    {
        var ended = false;
        void Note(object? sender, EventArgs e) => ended = true;

        session.Ended += Note;
        try
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
            while (!ended && DateTime.UtcNow < deadline)
                Thread.Sleep(50);
        }
        finally
        {
            session.Ended -= Note;
        }
        return ended;
    }

    public void Dispose() => session.Dispose();
}
