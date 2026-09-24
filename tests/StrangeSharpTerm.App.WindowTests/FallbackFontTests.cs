using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Iciclecreek.Terminal;
using StrangeSharpTerm.App.Terminal;
using StrangeSharpTerm.App.ViewModels;
using StrangeSharpTerm.App.Views;
using StrangeSharpTerm.Model;
using StrangeSharpTerm.Terminal;

namespace StrangeSharpTerm.App.WindowTests;

/// <summary>
/// The fonts for characters the terminal font lacks are found before a frame
/// needs them, not while it draws.
///
/// The renderer looked each one up on the UI thread the first time it drew it,
/// and NemoClaw's installer output held that thread for seconds: every
/// keystroke typed meanwhile waited behind the font server. These run headless
/// with no Skia drawing at all, so anything in the renderer's fallback cache
/// was put there ahead of drawing -- which is the whole claim.
///
/// The cache is Iciclecreek's and private, reached here the way the app reaches
/// it. If an upgrade moves it, these fail, and that is their second job: the
/// app would quietly go back to stalling, and nothing else would say so.
/// </summary>
[Collection("window")]
public class FallbackFontTests
{
    /// <summary>A lobster: in no monospace font on any platform CI runs on.</summary>
    private const int Lobster = 0x1F99E;

    [Fact]
    public void CharactersTheTerminalFontLacksAreLookedUpBeforeTheyAreDrawn()
    {
        var cache = RunOutput(Encoding.UTF8.GetBytes("✓ done \U0001F99E 丁 ⠀ plain ascii\r\n"));

        cache.ShouldContain(Lobster);
        // Nothing the terminal font draws itself is asked about.
        cache.ShouldNotContain('d');
        cache.ShouldNotContain(' ');
    }

    [Fact]
    public void ACharacterSplitAcrossReadsIsStillFound()
    {
        // The four bytes of one emoji, arriving as two reads: how a busy
        // channel cuts a UTF-8 sequence, and what a per-chunk decode would drop.
        var bytes = Encoding.UTF8.GetBytes("\U0001F99E\r\n");
        var cache = RunOutput(bytes[..2], bytes[2..]);

        cache.ShouldContain(Lobster);
    }

    /// <summary>
    /// Opens a pane, feeds it these reads one at a time, waits until its own
    /// engine has them, and returns what the renderer's fallback cache holds.
    /// </summary>
    private static int[] RunOutput(params byte[][] reads)
    {
        int[] held = [];
        Headless.Run(() =>
        {
            var channel = new ScriptedChannel(reads);
            var session = new TerminalSession(channel);
            var pane = new TerminalPaneView(session);
            var view = new PaneSplitView();
            var window = new Window { Content = view, Width = 900, Height = 500 };
            window.Show();

            var id = NodeId.New();
            view.OpenPanes = [id];
            view.Panes = [new PaneSlot(id, pane, true)];
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            // Past the observer and into the control's engine: every read has
            // been through the lookups by the time the engine has its bytes.
            var control = pane.GetVisualDescendants().OfType<TerminalControl>().First();
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (control.Terminal.Buffer.Y == 0 && DateTime.UtcNow < deadline)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }
            control.Terminal.Buffer.Y.ShouldBeGreaterThan(0, "the output never reached the pane");

            var terminalView = pane.GetVisualDescendants().OfType<TerminalView>().First();
            held = FallbackCache(terminalView);

            window.Close();
            pane.Dispose();
        });
        return held;
    }

    private static int[] FallbackCache(TerminalView view)
    {
        var cache = typeof(TerminalView)
            .GetField("_skiaFonts", BindingFlags.Instance | BindingFlags.NonPublic)
            .ShouldNotBeNull("Iciclecreek no longer keeps its font cache where the app looks for it")
            .GetValue(view)
            .ShouldNotBeNull();
        var fallback = cache.GetType()
            .GetField("_fallback", BindingFlags.Instance | BindingFlags.NonPublic)
            .ShouldNotBeNull("Iciclecreek no longer keeps its fallback fonts where the app looks for them")
            .GetValue(cache)
            .ShouldBeAssignableTo<IDictionary>();
        return [.. fallback!.Keys.Cast<int>()];
    }

    /// <summary>A shell that says these things, one read each, then goes quiet without ending.</summary>
    private sealed class ScriptedChannel(byte[][] reads) : ITerminalChannel
    {
        private readonly Scripted _stream = new(reads);

        public Stream Stream => _stream;

        public void Resize(int columns, int rows) { }

        public void Dispose() => _stream.Dispose();

        private sealed class Scripted(byte[][] reads) : Stream
        {
            private readonly BlockingCollection<byte[]> _pending = new(new ConcurrentQueue<byte[]>(reads));
            private readonly CancellationTokenSource _closed = new();

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
                try
                {
                    var next = _pending.Take(_closed.Token);
                    next.CopyTo(buffer, offset);
                    return next.Length;
                }
                catch (OperationCanceledException)
                {
                    return 0;
                }
            }

            public override void Write(byte[] buffer, int offset, int count) { }

            public override void Flush() { }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                _closed.Cancel();
                base.Dispose(disposing);
            }
        }
    }
}
