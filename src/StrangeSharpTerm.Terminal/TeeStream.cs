namespace StrangeSharpTerm.Terminal;

/// <summary>
/// Passes a stream through while copying everything read to a consumer.
///
/// The point is that there is exactly one reader of the channel. A UI control
/// reads bytes to draw them; the engine needs the same bytes to know what is on
/// screen; and if each read the channel separately they would each get half. The
/// control reads this, and the engine is fed on the way past.
/// </summary>
internal sealed class TeeStream(Stream inner, Action<ReadOnlySpan<byte>> consume, Action atEnd) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite;
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

    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

    public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);

    public override void Flush() => inner.Flush();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
