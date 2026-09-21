using System.Text;
using Avalonia.Controls;
using Avalonia.Threading;
using StrangeSharpTerm.App.ViewModels;
using StrangeSharpTerm.Assist;
using StrangeSharpTerm.Model;
using StrangeSharpTerm.Terminal;
using StrangeSharpTerm.Transport;

namespace StrangeSharpTerm.App.WindowTests;

/// <summary>
/// What the one-host panel puts on the screen of the pane beside it.
///
/// <see cref="DrivingTests"/> covers the event; this is the rest of the path and
/// the part a person actually sees -- the shell turning that event into text in
/// the terminal for that host, through the real <see cref="TerminalSession"/>
/// rather than a double. A stub here would only assert that the test calls the
/// method it already knows about; the mechanism being tested is that the
/// narration arrives on the same stream the server's own output arrives on.
/// </summary>
[Collection("window")]
public class NarrationTests
{
    [Fact]
    public void TheCommandTheAssistantRanIsShownInThatHostsPane()
    {
        Headless.Run(() =>
        {
            using var shell = new Fixture(
                Canned.Runs("df -h /", "how full", "c1"),
                Canned.Says("98% full."));

            // Before anything is asked, the pane is the shell's own prompt.
            shell.Screen().ShouldContain("deploy@web-01:~$");
            shell.Screen().ShouldNotContain("assistant");

            shell.Ask("how full is the disk?");

            // Both halves, because each alone is true in the middle of the
            // sequence. Show takes the prompt off the line, writes, and puts
            // the prompt back -- once when the command starts and again when it
            // finishes. So "exit 0" alone can arrive before the prompt is back
            // under it, and a prompt at the end alone is already true after the
            // starting line. Waiting for "exit 0" was what this did, and what
            // it caught on a Windows runner was not a slow machine: it was
            // TerminalSession.Show reading the screen while its own bytes were
            // still queued. See ShowTests.AndWithNobodyReadingInBetween.
            shell.Until(
                () => shell.Screen().Contains("exit 0")
                    && shell.Screen().TrimEnd().EndsWith("deploy@web-01:~$"),
                "the finished narration, with the prompt back underneath");
            var screen = shell.Screen();
            screen.ShouldSatisfyAllConditions(
                // The command, marked as the assistant's rather than the shell's.
                () => screen.ShouldContain("assistant"),
                () => screen.ShouldContain("df -h /"),
                // What came back, and how it ended.
                () => screen.ShouldContain("98%"),
                () => screen.ShouldContain("exit 0"),
                // The prompt is put back underneath, or the pane would read as
                // a terminal that had hung.
                () => screen.TrimEnd().ShouldEndWith("deploy@web-01:~$"));

            // And not one byte of it was typed at the shell. The command has
            // already run over the exec channel; echoing it at a prompt would
            // run it a second time.
            shell.Sent.ShouldBeEmpty();
        });
    }

    /// <summary>
    /// A command nobody allowed leaves the pane alone.
    ///
    /// The narration is raised after the gate, so the window cannot say
    /// something happened on a host when nothing did.
    /// </summary>
    [Fact]
    public void ARefusedCommandIsNeverShown()
    {
        Headless.Run(() =>
        {
            using var shell = new Fixture(
                // Something the policy stops, so the pane becomes its own gate.
                Canned.Runs("systemctl restart nginx", "restart it", "c1"),
                Canned.Says("Understood."));

            var asking = shell.Asking("restart nginx");
            shell.Until(() => shell.Assistant.Waiting is not null, "the gate to open");
            shell.Assistant.RefuseCommand.Execute(null);
            Headless.Finish(asking);

            var screen = shell.Screen();
            screen.ShouldNotContain("systemctl");
            screen.ShouldNotContain("assistant");
            shell.Sent.ShouldBeEmpty();
        });
    }

    /// <summary>
    /// A window with one host open, its assistant docked, and a real terminal
    /// underneath being driven the way a control drives one.
    /// </summary>
    private sealed class Fixture : IDisposable
    {
        private static readonly Connection Web01 = new() { Name = "web-01", Hostname = "web-01.example.com" };

        private readonly Quiet _channel = new("deploy@web-01:~$ ");
        private readonly TerminalSession _session;
        private readonly CancellationTokenSource _reading = new();
        private readonly Task _pumping;

        internal Fixture(params IReadOnlyList<AssistEvent>[] turns)
        {
            _session = new TerminalSession(_channel);

            // Nothing reaches the screen until something reads the stream. A
            // control does that in the app; this is the headless equivalent,
            // and it runs off the UI thread because the test body owns that.
            _pumping = Task.Run(() => _session.RunAsync(_reading.Token));

            AssistantViewModel? assistant = null;
            Shell = new ShellViewModel(
                new InventoryViewModel(null, new InventoryTree(connections: [Web01])),
                new StrangeSharpTerm.App.Tests.FakeSessions
                {
                    OnShell = _ => _session,
                    OnCommands = _ => new Answering(),
                },
                // The real terminal control would need its XAML run; what this
                // test is about is underneath it.
                view: (_, _) => new Border(),
                // No tail, so the assistant's own narration cannot come back to
                // it as context, and no probe, so there is no host to ask.
                assist: new AssistSettings { SendMetrics = false, SendTerminalTail = false },
                backends: _ => new Canned(turns),
                assistantView: model =>
                {
                    assistant = model;
                    return new Border();
                });

            Headless.Finish(Shell.OpenTerminal(Web01));
            Until(() => Screen().Contains("deploy@web-01:~$"), "the prompt");

            // Selecting it is what gives the shell the Detail the assistant
            // command works from, as clicking the sidebar does in the window.
            Shell.Inventory.Selection = Web01.Id;
            Dispatcher.UIThread.RunJobs();

            Shell.OpenAssistantCommand.Execute(null);
            Assistant = assistant ?? throw new InvalidOperationException(
                $"the dock did not build an assistant. {Shell.Failure}");
            Assistant.MayRunCommands = true;
        }

        internal ShellViewModel Shell { get; }

        internal AssistantViewModel Assistant { get; }

        /// <summary>Everything typed at the far end. The narration must never be in it.</summary>
        internal string Sent => _channel.Sent;

        internal string Screen() => _session.RecentText(40) ?? "";

        internal void Ask(string question) => Headless.Finish(Asking(question));

        internal Task Asking(string question)
        {
            Assistant.Question = question;
            return Assistant.AskCommand.ExecuteAsync(null);
        }

        /// <summary>The screen, once it says this, or a failed test.</summary>
        internal string Until(string text)
        {
            Until(() => Screen().Contains(text), $"'{text}'");
            return Screen();
        }

        /// <summary>
        /// Pumps the dispatcher until something is true.
        ///
        /// The engine is fed on another thread and the view models post to this
        /// one, so a test that only slept would be racing both.
        /// </summary>
        internal void Until(Func<bool> done, string what, int seconds = 10)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (!done())
            {
                Dispatcher.UIThread.RunJobs();
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException($"waited {seconds}s for {what}. The pane showed:\n{Screen()}");
                Thread.Sleep(1);
            }
        }

        public void Dispose()
        {
            _reading.Cancel();
            _session.Dispose();
            _pumping.Wait(TimeSpan.FromSeconds(2));
            _reading.Dispose();
        }
    }

    private sealed class Answering : IRemoteCommands
    {
        public CommandResult Run(string command, TimeSpan timeout) =>
            new(0, "/dev/sda1        49G   46G  1.2G  98% /", "");

        public CommandResult RunFeeding(string command, TimeSpan timeout, string input) => Run(command, timeout);
    }

    private sealed class Canned(params IReadOnlyList<AssistEvent>[] turns) : IAssistBackend
    {
        private int _turn;

        public string ProviderName => "Canned";

        public string Model => "canned-1";

        internal static IReadOnlyList<AssistEvent> Says(string text) =>
            [new AssistEvent.Say(text), new AssistEvent.Finished(AssistStop.EndTurn)];

        internal static IReadOnlyList<AssistEvent> Runs(string command, string why, string id) =>
        [
            new AssistEvent.Call(new AssistToolCall(
                id,
                AssistTools.RunCommand,
                System.Text.Json.JsonSerializer.Serialize(new { command, why }))),
            new AssistEvent.Finished(AssistStop.ToolUse),
        ];

        public async IAsyncEnumerable<AssistEvent> Stream(
            AssistRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var turn = _turn < turns.Length ? turns[_turn] : Says("That is all.");
            _turn++;
            foreach (var streamed in turn)
            {
                await Task.Yield();
                yield return streamed;
            }
        }
    }

    /// <summary>
    /// A shell that says its prompt and then stays quiet rather than closing.
    ///
    /// Blocking rather than returning zero: zero is the end of a session, and a
    /// pane whose session ended is not one the assistant would narrate into.
    /// </summary>
    private sealed class Quiet(string greeting) : ITerminalChannel
    {
        private readonly Talking _stream = new(greeting);

        public Stream Stream => _stream;

        internal string Sent => _stream.Typed;

        public void Resize(int columns, int rows) { }

        public void Dispose() => _stream.Dispose();

        private sealed class Talking(string greeting) : Stream
        {
            private readonly ManualResetEventSlim _closed = new();
            private readonly MemoryStream _typed = new();
            private ReadOnlyMemory<byte> _left = Encoding.UTF8.GetBytes(greeting);

            internal string Typed => Encoding.UTF8.GetString(_typed.ToArray());

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
                if (_left.IsEmpty)
                {
                    _closed.Wait();
                    return 0;
                }

                var taken = Math.Min(buffer.Length, _left.Length);
                _left.Span[..taken].CopyTo(buffer);
                _left = _left[taken..];
                return taken;
            }

            public override void Write(byte[] buffer, int offset, int count) =>
                _typed.Write(buffer, offset, count);

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
}
