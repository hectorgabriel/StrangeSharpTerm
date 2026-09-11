namespace StrangeSharpTerm.Terminal.Tests;

/// <summary>
/// A channel with a script: bytes the far end "sends", and a record of what was
/// written back to it. Reading past the script ends the session, as a closed
/// shell does.
/// </summary>
internal sealed class FakeChannel(string incoming = "") : ITerminalChannel
{
    private readonly MemoryStream _written = new();

    public ChannelStream Duplex { get; } = new(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(incoming)));

    public Stream Stream => Duplex;

    public List<(int Columns, int Rows)> Resizes { get; } = [];

    public string Written => System.Text.Encoding.UTF8.GetString(Duplex.Written.ToArray());

    public void Resize(int columns, int rows) => Resizes.Add((columns, rows));

    public void Dispose() => _written.Dispose();

    /// <summary>Reads come from the script; writes are kept for the test to inspect.</summary>
    internal sealed class ChannelStream(MemoryStream incoming) : Stream
    {
        public MemoryStream Written { get; } = new();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => incoming.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => incoming.Read(buffer);

        public override void Write(byte[] buffer, int offset, int count) => Written.Write(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) => Written.Write(buffer);

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
