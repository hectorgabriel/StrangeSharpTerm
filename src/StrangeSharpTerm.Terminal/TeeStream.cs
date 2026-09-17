namespace StrangeSharpTerm.Terminal;

/// <summary>
/// Passes a stream through while copying everything read to a consumer.
///
/// The point is that there is exactly one reader. A UI control reads bytes to
/// draw them; the engine needs the same bytes to know what is on screen; and if
/// each read separately they would each get half. The control reads this, and
/// the engine is fed on the way past.
/// </summary>
/// <param name="inner">
/// Where bytes to be drawn come from. Not the channel any more: the channel is
/// pumped into a <see cref="FeedStream"/> so the app can put a line of its own
/// beside the server's.
/// </param>
/// <param name="writes">
/// Where typed bytes go, which is still the channel. Reading and writing parted
/// company when the read side gained a second writer; keystrokes did not, and a
/// keystroke that went into the feed would be echoed to the person who typed it
/// and never sent anywhere.
/// </param>
internal sealed class TeeStream(
    Stream inner,
    Stream writes,
    Action<ReadOnlySpan<byte>> consume,
    Action atEnd) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => writes.CanWrite;
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
            consume(buffer[..read]);
        else
            atEnd();
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken);
        if (read > 0)
            consume(buffer.Span[..read]);
        else
            atEnd();
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count) => writes.Write(buffer, offset, count);

    public override void Write(ReadOnlySpan<byte> buffer) => writes.Write(buffer);

    public override void Flush() => writes.Flush();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
