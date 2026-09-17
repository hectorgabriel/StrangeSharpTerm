using System.Text;
using StrangeSharpTerm.Model;
using XTerm.Buffer;
using XTerm.Options;
using Engine = XTerm.Terminal;

namespace StrangeSharpTerm.Terminal;

public sealed record TerminalSessionOptions
{
    public int Columns { get; init; } = 80;
    public int Rows { get; init; } = 24;

    /// <summary>How far back the assistant can read. The Swift app kept the library default.</summary>
    public int Scrollback { get; init; } = 1000;

    public TerminalPalette Palette { get; init; } = TerminalPalette.StrangeTermDark;
}

/// <summary>
/// One terminal: a byte channel, the engine that interprets it, and the little
/// that the rest of the app needs from the pair.
///
/// The engine is fed by whoever reads <see cref="Stream"/> — a UI control, or
/// <see cref="RunAsync"/> when there is no UI. Both paths run the same bytes
/// through the same engine, which is why what the assistant reads is exactly
/// what the user is looking at, and why a headless driver can assert on it.
/// </summary>
public sealed class TerminalSession : IDisposable
{
    private readonly ITerminalChannel _channel;
    private readonly Engine _engine;
    private readonly TeeStream _stream;

    /// <summary>
    /// What the control actually reads: the channel's bytes, and any this app
    /// wants shown, in the order they arrived.
    /// </summary>
    private readonly FeedStream _feed = new();

    private readonly CancellationTokenSource _pumping = new();
    private readonly Lock _gate = new();
    private string _title = "";
    private bool _ended;

    public TerminalSession(ITerminalChannel channel, TerminalSessionOptions? options = null)
    {
        Options = options ?? new TerminalSessionOptions();
        _channel = channel;
        _engine = new Engine(new TerminalOptions
        {
            Cols = Options.Columns,
            Rows = Options.Rows,
            Scrollback = Options.Scrollback,
        });
        _stream = new TeeStream(_feed, channel.Stream, Consume, MarkEnded);

        // One reader of the channel, on a thread of its own, feeding everything
        // it gets to the one thing above. Nothing else may read the channel:
        // two readers each get half of what arrives, which is an escape
        // sequence torn down the middle.
        _pump = Task.Factory.StartNew(
            Pump,
            _pumping.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    private readonly Task _pump;

    /// <summary>
    /// Moves what the server says into the feed, until it stops saying
    /// anything.
    ///
    /// A channel that returns nothing has closed, and that is the end of the
    /// session: the feed is completed so the reader above sees the same end it
    /// used to see from the channel itself.
    /// </summary>
    private void Pump()
    {
        var buffer = new byte[8192];
        try
        {
            while (!_pumping.IsCancellationRequested)
            {
                var read = _channel.Stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                    break;
                _feed.Feed(buffer.AsSpan(0, read));
            }
        }
        catch (Exception)
        {
            // A channel torn down under us is how a session ends when the far
            // end goes away, and it is reported as the end rather than thrown
            // out of a background thread nobody is waiting on.
        }
        finally
        {
            _feed.Complete();
        }
    }

    /// <summary>Back to the start of the line and clear it: what <see cref="Show"/> writes over.</summary>
    private static readonly byte[] Erase = Encoding.UTF8.GetBytes("\r[K");

    /// <summary>
    /// Writes into what the pane shows, and nowhere else.
    ///
    /// It arrives on the same path the server's own output does, so the control
    /// draws it exactly as it draws anything else -- and the far end never sees
    /// a byte of it. This is how the app can say what it is doing on a host in
    /// the window you are watching, without typing into your shell and without
    /// racing whatever you are typing.
    /// </summary>
    public void Show(string text)
    {
        var (prompt, column) = Waiting();

        // Take the prompt off the line before writing over it, because it is
        // put back at the end and two of them is worse than none. Erasing
        // rather than leaving it costs nothing: the line reappears below,
        // character for character, when the last of this is written.
        if (prompt.Length > 0)
            _feed.Feed(Erase);

        _feed.Feed(Encoding.UTF8.GetBytes(text));

        // Put it back underneath. The shell does not know any of this happened
        // -- nothing was sent to it, so it has no reason to draw its prompt
        // again -- and a terminal whose last line is somebody else's output
        // looks like one that has hung. Which is what it looked like.
        //
        // A copy, not the prompt itself: what the buffer keeps for us is the
        // text of a line and not its colours, so this comes back in the
        // ordinary foreground. Better than a cursor sitting under somebody
        // else's output with no prompt in front of it.
        if (prompt.Length == 0)
            return;

        _feed.Feed(Encoding.UTF8.GetBytes(prompt));

        // Trailing spaces are trimmed off a line when it is read back, and the
        // one after a prompt's $ is the difference between a cursor in the
        // right place and one a character to the left. The column the cursor
        // was actually in is what says how many to put back.
        if (column > prompt.Length)
            _feed.Feed(Encoding.UTF8.GetBytes(new string(' ', column - prompt.Length)));
    }

    /// <summary>
    /// The line the shell is sitting on and where the cursor is in it.
    ///
    /// Usually the prompt, and whatever has been typed at it so far -- which
    /// matters as much as the prompt does: losing half a typed command would be
    /// worse than losing the prompt in front of it.
    /// </summary>
    private (string Line, int Column) Waiting()
    {
        lock (_gate)
        {
            var line = _engine.Buffer.GetLine(_engine.Buffer.Y + _engine.Buffer.YBase);
            return line is null ? ("", 0) : (TextOf(line).TrimEnd(), _engine.Buffer.X);
        }
    }

    public NodeId Id { get; } = NodeId.New();

    public TerminalSessionOptions Options { get; }

    /// <summary>The engine, for a renderer that draws its cells.</summary>
    public Engine Engine => _engine;

    /// <summary>
    /// What a UI control attaches to. Bytes read through it reach the engine on
    /// the way past, so the control and the engine never diverge.
    /// </summary>
    public Stream Stream => _stream;

    public int Columns { get; private set; }

    public int Rows { get; private set; }

    /// <summary>The title the far end set, via the usual escape sequence.</summary>
    public string Title => _title;

    public event EventHandler<string>? TitleChanged;

    /// <summary>The far end closed the channel: the shell exited, or the connection dropped.</summary>
    public event EventHandler? Ended;

    /// <summary>Raised for input this session originated, which is what broadcast fans out.</summary>
    public event EventHandler<ReadOnlyMemory<byte>>? InputSent;

    /// <summary>Sends keystrokes, and tells anyone listening that it did.</summary>
    public void Send(string text) => Send(Encoding.UTF8.GetBytes(text));

    public void Send(ReadOnlyMemory<byte> bytes)
    {
        Write(bytes.Span);
        InputSent?.Invoke(this, bytes);
    }

    /// <summary>
    /// Announces input that something else has already written to the channel.
    ///
    /// A UI control writes the keystroke itself, through the stream it was
    /// attached to; sending it again here would double every character. Broadcast
    /// still has to hear about it, so this raises the event and writes nothing.
    /// </summary>
    public void ObserveInput(string text) => InputSent?.Invoke(this, Encoding.UTF8.GetBytes(text));

    /// <summary>
    /// Sends without announcing it. This is how a broadcast reaches the other
    /// panes without each of them broadcasting in turn.
    /// </summary>
    internal void Write(ReadOnlySpan<byte> bytes)
    {
        _channel.Stream.Write(bytes);
        _channel.Stream.Flush();
    }

    public void Resize(int columns, int rows)
    {
        if (columns < 1 || rows < 1 || (columns == Columns && rows == Rows))
            return;

        lock (_gate)
            _engine.Resize(columns, rows);
        // The server owns the pty, so it has to be told: this is the
        // window-change request, and without it the far end keeps wrapping at the
        // old width.
        _channel.Resize(columns, rows);
        (Columns, Rows) = (columns, rows);
    }

    /// <summary>What the terminal is currently displaying, one string per row.</summary>
    public string VisibleText
    {
        get
        {
            lock (_gate)
                return string.Join('\n', _engine.GetVisibleLines().Select(line => line.TrimEnd()));
        }
    }

    /// <summary>
    /// The tail of the session, scrollback included.
    ///
    /// Not <see cref="VisibleText"/>: that reads the visible rows only, so the
    /// command that caused an error has usually scrolled off by the time anyone
    /// asks about it. Wrapped rows are rejoined into the single line the user
    /// actually typed, because handing an assistant an 80-column chop of every
    /// long path is worse than useless.
    /// </summary>
    public string? RecentText(int maxLines = 200)
    {
        lock (_gate)
        {
            var buffer = _engine.Buffer;
            var lines = new List<string>();
            StringBuilder? current = null;

            for (var y = 0; y < buffer.Length; y++)
            {
                var line = buffer.GetLine(y);
                if (line is null)
                    continue;

                if (line.IsWrapped && current is not null)
                {
                    current.Append(TextOf(line));
                    continue;
                }
                if (current is not null)
                    lines.Add(current.ToString().TrimEnd());
                current = new StringBuilder(TextOf(line));
            }
            if (current is not null)
                lines.Add(current.ToString().TrimEnd());

            while (lines.Count > 0 && lines[^1].Length == 0)
                lines.RemoveAt(lines.Count - 1);

            return lines.Count == 0 ? null : string.Join('\n', lines.TakeLast(maxLines));
        }
    }

    /// <summary>
    /// Reads the channel into the engine until it ends. For headless callers;
    /// a UI control reads <see cref="Stream"/> itself and needs none of this.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var buffer = new byte[4096];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await _stream.ReadAsync(buffer, cancellationToken);
                if (read <= 0)
                    break;
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The channel went away, which is how a session ordinarily ends.
        }
        MarkEnded();
    }

    public void Dispose()
    {
        // The channel first: the pump is blocked reading it, and closing it is
        // what lets that read return so the thread can finish.
        _pumping.Cancel();
        _channel.Dispose();
        _feed.Complete();
        _pump.Wait(TimeSpan.FromSeconds(2));

        _stream.Dispose();
        _feed.Dispose();
        _pumping.Dispose();
        _engine.Dispose();
    }

    /// <summary>Feeds bytes to the engine and reports a title the far end changed.</summary>
    private void Consume(ReadOnlySpan<byte> bytes)
    {
        string title;
        lock (_gate)
        {
            _engine.Write(bytes);
            title = _engine.Title ?? "";
        }

        if (title == _title)
            return;
        _title = title;
        TitleChanged?.Invoke(this, title);
    }

    private void MarkEnded()
    {
        if (_ended)
            return;
        _ended = true;
        Ended?.Invoke(this, EventArgs.Empty);
    }

    private static string TextOf(BufferLine line)
    {
        var text = new StringBuilder(line.Length);
        for (var x = 0; x < line.Length; x++)
        {
            var cell = line[x];
            // Width 0 is the second half of a wide character; its content already
            // arrived with the first.
            if (cell.Width == 0)
                continue;
            text.Append(cell.Content.Length == 0 ? " " : cell.Content);
        }
        return text.ToString();
    }
}
