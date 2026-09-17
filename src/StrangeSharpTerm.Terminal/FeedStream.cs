namespace StrangeSharpTerm.Terminal;

/// <summary>
/// What the terminal reads, with two things able to put bytes into it: the
/// server, and this app.
///
/// The control reads its bytes from one stream and blocks there when there is
/// nothing to read. That is the whole difficulty in showing it anything: bytes
/// handed to the engine behind its back update what the app knows about the
/// screen and nothing a person can see, and bytes written to the channel go to
/// the far end, which is not the same thing as saying something locally.
///
/// So the channel is pumped into this instead of being read directly, and
/// anything the app wants shown is put in beside it. One reader, two writers,
/// and the reader cannot tell them apart -- which is exactly right, because to
/// a terminal a byte is a byte however it arrived.
///
/// Blocking rather than System.IO.Pipelines: the control reads synchronously
/// as well as asynchronously, and a pipe read bridged back to sync is a
/// deadlock waiting for a quiet afternoon.
/// </summary>
internal sealed class FeedStream : Stream
{
    /// <summary>
    /// A plain object rather than the Lock the rest of this project uses: the
    /// reader waits here until something arrives, and waiting needs
    /// Monitor.Wait and PulseAll, which Lock has no equivalent of.
    /// </summary>
    private readonly object _gate = new();
    private readonly Queue<byte[]> _waiting = new();

    /// <summary>How far into the front buffer the reader has got.</summary>
    private int _at;

    private bool _done;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>Adds bytes for the reader, whoever they came from.</summary>
    public void Feed(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return;

        lock (_gate)
        {
            if (_done)
                return;
            _waiting.Enqueue(bytes.ToArray());
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// No more is coming. The reader drains what is left and then sees the end,
    /// which is what a closed channel looks like to everything above.
    /// </summary>
    public void Complete()
    {
        lock (_gate)
        {
            _done = true;
            Monitor.PulseAll(_gate);
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
            return 0;

        lock (_gate)
        {
            while (_waiting.Count == 0)
            {
                if (_done)
                    return 0;
                Monitor.Wait(_gate);
            }

            var front = _waiting.Peek();
            var taken = Math.Min(buffer.Length, front.Length - _at);
            front.AsSpan(_at, taken).CopyTo(buffer);
            _at += taken;

            if (_at == front.Length)
            {
                _waiting.Dequeue();
                _at = 0;
            }

            return taken;
        }
    }

    /// <summary>
    /// Asynchronously, on a pool thread, because the wait is a blocking one.
    ///
    /// The control reads this way, and a read that blocked its caller would
    /// block whatever thread it happened to be on -- which for a UI control is
    /// the one drawing the window.
    /// </summary>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await Task.Run(() => Read(buffer.Span), cancellationToken);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        Complete();
        base.Dispose(disposing);
    }
}
