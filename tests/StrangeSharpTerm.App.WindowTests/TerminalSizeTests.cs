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
/// The size the far end is told, against the size the pane draws.
///
/// The control only reports a size when layout changes it, and the pane is laid
/// out before its connection is attached. So the first size went nowhere: the
/// server's pty stayed at 80 columns in a pane twice as wide, and readline, which
/// redraws a wrapped line by counting rows up from the cursor, redrew a pasted
/// command one row too high and left it on screen twice.
/// </summary>
[Collection("window")]
public class TerminalSizeTests
{
    private sealed class MeasuredChannel : ITerminalChannel
    {
        private readonly Quiet _stream = new();

        public Stream Stream => _stream;

        public (int Columns, int Rows)? Told { get; private set; }

        public void Resize(int columns, int rows) => Told = (columns, rows);

        public void Dispose() => _stream.Dispose();

        /// <summary>A shell with nothing to say, which blocks rather than ending.</summary>
        private sealed class Quiet : Stream
        {
            private readonly ManualResetEventSlim _closed = new();

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
                _closed.Wait();
                return 0;
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) =>
                await Task.Run(() => Read([], 0, 0), token);

            public override void Write(byte[] buffer, int offset, int count) { }

            public override void Flush() { }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                _closed.Set();
                base.Dispose(disposing);
            }
        }
    }

    [Fact]
    public void TheServerIsToldTheSizeThePaneOpensAt()
    {
        Headless.Run(() =>
        {
            var channel = new MeasuredChannel();
            var session = new TerminalSession(channel);
            var pane = new TerminalPaneView(session);
            var view = new PaneSplitView();
            var window = new Window { Content = view, Width = 1600, Height = 600 };
            window.Show();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            // Into a layout already on screen, as the app opens one: the control
            // will not take a connection before its template exists.
            var id = NodeId.New();
            view.OpenPanes = [id];
            view.Panes = [new PaneSlot(id, pane, true)];
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            var engine = pane.GetVisualDescendants().OfType<TerminalControl>().First().Terminal;
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (channel.Told != (engine.Cols, engine.Rows) && DateTime.UtcNow < deadline)
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Thread.Sleep(10);
            }

            // Wider than the 80 a session opens at, or this proves nothing.
            engine.Cols.ShouldBeGreaterThan(80);
            channel.Told.ShouldBe((engine.Cols, engine.Rows));
            (session.Columns, session.Rows).ShouldBe((engine.Cols, engine.Rows));

            window.Close();
            pane.Dispose();
        });
    }
}
