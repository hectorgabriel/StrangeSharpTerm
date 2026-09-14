using System.Text;
using Avalonia;
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
/// What a second tab does to the first one's panes.
///
/// The split bug taught that a terminal control taken out of the visual tree
/// tears its connection down. The same thing happened one level up and went
/// unnoticed: the layout is handed the focused tab's panes, so every pane of the
/// tab being left looked closed, and its shell died with "Process exited with
/// code: 0" while the tab was still sitting there.
/// </summary>
[Collection("window")]
public class TabLifetimeTests
{
    /// <summary>
    /// A shell that is quiet until the test makes it speak.
    ///
    /// Not a MemoryStream: a read of one returns zero at once, which the engine
    /// reads as end-of-stream, so a test built on it cannot tell a torn-down pane
    /// from an empty one. This blocks like a real channel, and <see cref="Say"/>
    /// is how the far end says something.
    /// </summary>
    private sealed class SpeakingChannel : ITerminalChannel
    {
        private readonly Blocking _stream = new();

        public Stream Stream => _stream;

        public void Say(string text) => _stream.Push(Encoding.UTF8.GetBytes(text));

        public void Resize(int columns, int rows) { }

        public void Dispose() => _stream.Dispose();

        private sealed class Blocking : Stream
        {
            private readonly Queue<byte> _pending = new();
            private readonly SemaphoreSlim _ready = new(0);
            private readonly Lock _gate = new();
            private volatile bool _closed;

            public void Push(byte[] bytes)
            {
                lock (_gate)
                    foreach (var b in bytes)
                        _pending.Enqueue(b);
                _ready.Release();
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

            public override int Read(Span<byte> buffer)
            {
                while (true)
                {
                    if (_closed)
                        return 0;
                    lock (_gate)
                    {
                        if (_pending.Count > 0)
                        {
                            var taken = 0;
                            while (taken < buffer.Length && _pending.Count > 0)
                                buffer[taken++] = _pending.Dequeue();
                            return taken;
                        }
                    }
                    _ready.Wait(50);
                }
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
            {
                var owned = buffer;
                return await Task.Run(() => Read(owned.Span), token);
            }

            public override void Write(byte[] buffer, int offset, int count) { }

            public override void Write(ReadOnlySpan<byte> buffer) { }

            public override void Flush() { }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                _closed = true;
                _ready.Release();
                base.Dispose(disposing);
            }
        }
    }

    private static PaneSlot Slot(NodeId id, Control view, bool active = false) => new(id, view, active);

    private static bool InTheTree(Visual control) =>
        control.GetSelfAndVisualAncestors().OfType<Window>().Any();

    /// <summary>
    /// Layout first: TerminalPaneView attaches its connection on Loaded, and the
    /// control refuses one before its template is applied. The app gets that
    /// ordering from the real layout pass; here it has to be asked for.
    /// </summary>
    /// <summary>Runs the loop until the far end's bytes have made it to the screen.</summary>
    private static void Pump(Window window, Func<bool> until, int seconds = 5)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!until() && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Thread.Sleep(10);
        }
    }

    private static void Settle(Window window)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    /// <summary>What the control itself is drawing, as opposed to what the session parsed.</summary>
    private static string Screen(Visual pane) =>
        pane.GetVisualDescendants().OfType<TerminalControl>().FirstOrDefault()?.Terminal is { } engine
            ? string.Join("\n", engine.GetVisibleLines().Select(line => line.TrimEnd())).Trim()
            : "";

    /// <summary>
    /// The bug itself, and the reason it is worse than the message it shows.
    ///
    /// A tab switch took the pane out of the visual tree; the control tore its
    /// connection down and, when the tab came back, launched its own default
    /// process instead. What looked like the remote host was a local shell on
    /// this machine -- with "Process exited with code: 0" above it, the old
    /// connection being reaped, our Pid of -1 and ExitCode of 0.
    ///
    /// So the assertion is not that the session object survived: it is that the
    /// control is still reading the channel it was given. The far end says
    /// something, and the screen has to show it.
    /// </summary>
    [Fact]
    public void ATerminalInAnotherTabIsStillReadingItsOwnChannel()
    {
        Headless.Run(() =>
        {
            var channel = new SpeakingChannel();
            var session = new TerminalSession(channel);

            var view = new PaneSplitView();
            var window = new Window { Content = view, Width = 800, Height = 600 };
            window.Show();

            var (first, second) = (NodeId.New(), NodeId.New());
            var terminal = new TerminalPaneView(session);
            view.OpenPanes = [first];
            view.Panes = [Slot(first, terminal, active: true)];
            Settle(window);

            channel.Say("before-the-switch\r\n");
            Pump(window, () => Screen(terminal).Contains("before-the-switch"));
            Screen(terminal).ShouldContain("before-the-switch");

            // A second tab opens and takes the screen, then hands it back.
            view.OpenPanes = [first, second];
            view.Panes = [Slot(second, new Border(), active: true)];
            Settle(window);
            Thread.Sleep(300);
            Dispatcher.UIThread.RunJobs();
            view.Panes = [Slot(first, terminal, active: true)];
            Settle(window);

            channel.Say("after-the-switch\r\n");
            Pump(window, () => Screen(terminal).Contains("after-the-switch"));

            var screen = Screen(terminal);
            // Still the same terminal, still fed by the same far end.
            screen.ShouldContain("before-the-switch");
            screen.ShouldContain("after-the-switch");
            // And never a shell of its own: this machine's login banner is what
            // the relaunch printed, and "exited" is how it announced the old one.
            screen.ShouldNotContain("Process exited");
        });
    }

    [Fact]
    public void APaneInAnotherTabStaysInTheTree()
    {
        Headless.Run(() =>
        {
            var view = new PaneSplitView();
            var window = new Window { Content = view, Width = 800, Height = 600 };
            window.Show();

            var (first, second) = (NodeId.New(), NodeId.New());
            var firstView = new Border();
            view.OpenPanes = [first];
            view.Panes = [Slot(first, firstView, active: true)];
            Settle(window);
            var frame = firstView.Parent;

            // A second tab opens: both panes are open, but only the new tab's is
            // shown.
            view.OpenPanes = [first, second];
            view.Panes = [Slot(second, new Border(), active: true)];
            Settle(window);

            InTheTree(firstView).ShouldBeTrue();
            firstView.Parent.ShouldBeSameAs(frame);
            ((Border)firstView.Parent!).IsVisible.ShouldBeFalse();
        });
    }

    [Fact]
    public void AndComesBackWhenItsTabIsFocusedAgain()
    {
        Headless.Run(() =>
        {
            var view = new PaneSplitView();
            var window = new Window { Content = view, Width = 800, Height = 600 };
            window.Show();

            var (first, second) = (NodeId.New(), NodeId.New());
            var firstView = new Border();
            view.OpenPanes = [first, second];
            view.Panes = [Slot(first, firstView, active: true)];
            Settle(window);
            var frame = (Border)firstView.Parent!;

            view.Panes = [Slot(second, new Border(), active: true)];
            Settle(window);
            frame.IsVisible.ShouldBeFalse();

            view.Panes = [Slot(first, firstView, active: true)];
            Settle(window);

            frame.IsVisible.ShouldBeTrue();
            firstView.Parent.ShouldBeSameAs(frame);
            frame.Bounds.Width.ShouldBeGreaterThan(0);
        });
    }

    [Fact]
    public void APaneThatClosesStillLetsGoOfItsView()
    {
        Headless.Run(() =>
        {
            var view = new PaneSplitView();
            var window = new Window { Content = view, Width = 800, Height = 600 };
            window.Show();

            var (staying, closing) = (NodeId.New(), NodeId.New());
            var closingView = new Border();
            view.OpenPanes = [staying, closing];
            view.Panes = [Slot(staying, new Border(), active: true), Slot(closing, closingView)];
            Settle(window);

            // Gone from every tab, not merely from this one.
            view.OpenPanes = [staying];
            view.Panes = [Slot(staying, new Border(), active: true)];
            Settle(window);

            closingView.Parent.ShouldBeNull();
            InTheTree(closingView).ShouldBeFalse();
        });
    }
}
