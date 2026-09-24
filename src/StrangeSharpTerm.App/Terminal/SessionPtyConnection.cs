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
internal sealed class SessionPtyConnection(TerminalSession session, FallbackFonts? fonts = null) : IPtyConnection
{
    /// <summary>
    /// The session's stream, with each chunk shown to <paramref name="fonts"/>
    /// on the way past: on the control's reading thread, before the control
    /// writes it into its engine, so nothing it draws is new to it by then.
    /// </summary>
    public Stream ReaderStream { get; } = fonts is null ? session.Stream : new Observed(session.Stream, fonts.Observe);

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

    /// <summary>
    /// Reads through to another stream and hands on what it read. Writes are
    /// not its business: the control writes keystrokes to <see cref="WriterStream"/>.
    /// </summary>
    private sealed class Observed(Stream inner, Observer observe) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            if (read > 0)
                observe(buffer[..read]);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            if (read > 0)
                observe(buffer.Span[..read]);
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private delegate void Observer(ReadOnlySpan<byte> bytes);
}
