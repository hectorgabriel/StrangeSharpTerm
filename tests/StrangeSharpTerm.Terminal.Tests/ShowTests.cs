using System.Text;
using StrangeSharpTerm.Terminal;

namespace StrangeSharpTerm.Terminal.Tests;

/// <summary>
/// Saying something in a pane without saying it to the server.
///
/// The control reads its bytes from one stream and blocks there when there is
/// nothing to read, so anything the app wants shown has to arrive on that
/// stream. These are about the two properties that makes possible and the one
/// it must not cost: what is shown reaches the reader, the far end never sees
/// it, and what the server sends still arrives in order.
/// </summary>
public class ShowTests
{
    /// <summary>A channel whose two directions can be inspected separately.</summary>
    private sealed class Loopback : ITerminalChannel
    {
        private readonly MemoryStream _sent = new();

        public Stream Stream { get; }

        public Loopback(string fromTheServer = "")
        {
            Incoming = new FromTheServer(Encoding.UTF8.GetBytes(fromTheServer), _sent);
            Stream = Incoming;
        }

        internal FromTheServer Incoming { get; }

        /// <summary>Everything that was typed at the far end.</summary>
        internal string Sent => Encoding.UTF8.GetString(_sent.ToArray());

        public void Resize(int columns, int rows) { }

        public void Dispose() => Incoming.Release();

        internal sealed class FromTheServer(byte[] greeting, MemoryStream sent) : Stream
        {
            private readonly SemaphoreSlim _quiet = new(0);
            private int _at;

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => true;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_at < greeting.Length)
                {
                    var taken = Math.Min(count, greeting.Length - _at);
                    greeting.AsSpan(_at, taken).CopyTo(buffer.AsSpan(offset));
                    _at += taken;
                    return taken;
                }

                // Quiet, not closed: a shell nobody is typing at.
                _quiet.Wait();
                return 0;
            }

            public override void Write(byte[] buffer, int offset, int count) => sent.Write(buffer, offset, count);

            public override void Flush() { }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            internal void Release() => _quiet.Release(10);
        }
    }

    /// <summary>
    /// What <see cref="TerminalSession.Show"/> writes before anything else,
    /// when there is a prompt on the line: back to its start, and clear it.
    /// The prompt comes back underneath at the end.
    /// </summary>
    private const string Erase = "\r\u001b[K";

    /// <summary>
    /// Reads what was asked for, or gives up.
    ///
    /// The deadline is the difference between a test that fails and a test that
    /// hangs: the stream blocks when there is nothing to read, so one that asks
    /// for a byte more than was written -- which is what a test asserting on
    /// what Show writes does when Show stops writing it -- would never come
    /// back to say so.
    /// </summary>
    private static string Read(Stream stream, int count)
    {
        var buffer = new byte[count];
        var got = 0;
        while (got < count)
        {
            var at = got;
            var read = Task.Run(() => stream.Read(buffer, at, count - at));
            if (!read.Wait(TimeSpan.FromSeconds(2)) || read.Result <= 0)
                break;
            got += read.Result;
        }

        return Encoding.UTF8.GetString(buffer, 0, got);
    }

    [Fact]
    public void WhatIsShownReachesTheReader()
    {
        using var session = new TerminalSession(new Loopback("prompt$ "));

        Read(session.Stream, "prompt$ ".Length).ShouldBe("prompt$ ");

        session.Show("-- assistant");

        Read(session.Stream, (Erase + "-- assistant").Length).ShouldBe(Erase + "-- assistant");
    }

    /// <summary>
    /// And the far end never sees it. This is the whole reason it is not simply
    /// typed into the shell: the command has already run somewhere else, and
    /// echoing it at a prompt would run it a second time.
    /// </summary>
    [Fact]
    public void WhatIsShownIsNeverSent()
    {
        var channel = new Loopback();
        using var session = new TerminalSession(channel);

        session.Show("-- assistant ran df -h /");
        session.Send("ls\r");

        channel.Sent.ShouldBe("ls\r");
    }

    /// <summary>
    /// The engine sees it too, because the engine is fed from the same stream:
    /// what the app knows is on screen stays what is on screen.
    /// </summary>
    [Fact]
    public void WhatIsShownIsOnTheScreenTheAppKnowsAbout()
    {
        using var session = new TerminalSession(new Loopback());

        session.Show("the assistant was here\r\n");
        Read(session.Stream, "the assistant was here\r\n".Length);

        session.RecentText(10).ShouldNotBeNull().ShouldContain("the assistant was here");
    }

    /// <summary>
    /// Both writers in one order. The server's bytes and the app's share a
    /// reader, and an escape sequence torn in half by an interleaving would be
    /// drawn as the characters it is made of.
    /// </summary>
    [Fact]
    public void WhatTheServerSendsStillArrivesWhole()
    {
        using var session = new TerminalSession(new Loopback("one"));

        Read(session.Stream, 3).ShouldBe("one");
        session.Show("two");
        Read(session.Stream, (Erase + "two").Length).ShouldBe(Erase + "two");
    }

    /// <summary>
    /// The prompt comes back underneath.
    ///
    /// The shell does not know any of this happened -- nothing was sent to it,
    /// so it has no reason to draw its prompt again -- and a terminal whose last
    /// line is somebody else's output looks like a terminal that has hung. That
    /// is what it looked like.
    /// </summary>
    [Fact]
    public void ThePromptIsPutBackUnderneath()
    {
        using var session = new TerminalSession(new Loopback("deploy@web-01:~$ "));

        Read(session.Stream, "deploy@web-01:~$ ".Length);
        session.Show("-- assistant\r\n");

        // What the reader gets: the narration, and the prompt again after it.
        Read(session.Stream, (Erase + "-- assistant\r\ndeploy@web-01:~$ ").Length)
            .ShouldBe(Erase + "-- assistant\r\ndeploy@web-01:~$ ");

        // Including the space after the $: the cursor was a column past the
        // last character, and that is where it has to come back to.
    }

    /// <summary>
    /// And what was half typed comes back with it. It is on the same line as
    /// the prompt, and losing it would be worse than losing the prompt.
    /// </summary>
    [Fact]
    public void SoDoesWhateverWasHalfTyped()
    {
        using var session = new TerminalSession(new Loopback("deploy@web-01:~$ systemctl sta"));

        Read(session.Stream, "deploy@web-01:~$ systemctl sta".Length);
        session.Show("-- assistant\r\n");

        Read(session.Stream, (Erase + "-- assistant\r\ndeploy@web-01:~$ systemctl sta").Length)
            .ShouldBe(Erase + "-- assistant\r\ndeploy@web-01:~$ systemctl sta");
    }

    /// <summary>
    /// Two things shown one after the other leave one prompt, at the bottom.
    ///
    /// A run narrates twice -- the command going out, and its output coming
    /// back -- and the prompt put back after the first was still on the line
    /// when the second arrived, so the output was written along the end of it:
    /// "deploy@web-01:~$ Filesystem  Size  Used". It reads as a command
    /// somebody typed. It is not one.
    /// </summary>
    [Fact]
    public void TwoThingsShownOneAfterTheOtherLeaveOnePrompt()
    {
        const string prompt = "deploy@web-01:~$ ";
        using var session = new TerminalSession(new Loopback(prompt));
        Read(session.Stream, prompt.Length);

        var starting = "\u2500\u2500 assistant \u00b7 df -h /\r\n";
        session.Show(starting);
        Read(session.Stream, Encoding.UTF8.GetByteCount(Erase + starting + prompt));

        var finished = "/dev/disk3s1s1  460Gi\r\n\u2500\u2500 exit 0\r\n";
        session.Show(finished);
        Read(session.Stream, Encoding.UTF8.GetByteCount(Erase + finished + prompt));

        var screen = session.RecentText(10).ShouldNotBeNull();
        var lines = screen.Split('\n').Where(line => line.Length > 0).ToArray();

        lines.Count(line => line.Contains("deploy@web-01:~$")).ShouldBe(1);
        lines[^1].ShouldBe(prompt.TrimEnd());
        lines.ShouldContain("/dev/disk3s1s1  460Gi");
    }

    /// <summary>A channel that has nothing more to say ends the session, as it always did.</summary>
    [Fact]
    public void AChannelThatClosesStillEndsTheSession()
    {
        var channel = new Loopback("bye");
        using var session = new TerminalSession(channel);

        var ended = false;
        session.Ended += (_, _) => ended = true;

        Read(session.Stream, 3).ShouldBe("bye");
        channel.Incoming.Release();

        // Draining past the end is what reports it, as it did when the control
        // read the channel directly.
        Read(session.Stream, 1).ShouldBe("");
        ended.ShouldBeTrue();
    }
}
