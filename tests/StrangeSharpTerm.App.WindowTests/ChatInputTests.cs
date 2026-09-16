using Avalonia;
using Avalonia.Controls;
using StrangeSharpTerm.App.ViewModels;
using StrangeSharpTerm.Assist;
using StrangeSharpTerm.Transport;

namespace StrangeSharpTerm.App.WindowTests;

/// <summary>
/// What the chat does with what you type: sending, starting a new line,
/// walking back through what has been asked, and asking it again.
///
/// In the window suite because the agent runs off the UI thread and posts its
/// rows back, which is what a retry adds and removes.
/// </summary>
[Collection("window")]
public class ChatInputTests
{
    private static AssistantViewModel Pane(params IReadOnlyList<AssistEvent>[] turns) =>
        new(
            new HostAgent(new Canned(turns), new Quiet("web-01"), new AssistSettings(), new StandingAnswer(true)),
            new AssistSettings(),
            _ => { });

    /// <summary>
    /// The up arrow walks back through what has been asked, and down walks
    /// forward again to the empty field you started from.
    /// </summary>
    [Fact]
    public void TheUpArrowWalksBackThroughWhatHasBeenAsked()
    {
        Headless.Run(() =>
        {
            var pane = Pane(Canned.Says("First."), Canned.Says("Second."));

            pane.Question = "how full is the disk?";
            Headless.Finish(pane.AskCommand.ExecuteAsync(null));
            pane.Question = "and the journal?";
            Headless.Finish(pane.AskCommand.ExecuteAsync(null));

            // Asking clears the field, as it always did.
            pane.Question.ShouldBe("");

            pane.Recall(1).ShouldBeTrue();
            pane.Question.ShouldBe("and the journal?");

            pane.Recall(1).ShouldBeTrue();
            pane.Question.ShouldBe("how full is the disk?");

            // Past the oldest, nothing happens rather than the field blanking.
            pane.Recall(1).ShouldBeFalse();
            pane.Question.ShouldBe("how full is the disk?");

            pane.Recall(-1).ShouldBeTrue();
            pane.Question.ShouldBe("and the journal?");

            // Back to where you started: an empty field, ready for a new one.
            pane.Recall(-1).ShouldBeTrue();
            pane.Question.ShouldBe("");
            pane.Recall(-1).ShouldBeFalse();
        });
    }

    /// <summary>Asking the same thing twice is one entry to walk back to, not two.</summary>
    [Fact]
    public void TheSameQuestionTwiceIsRememberedOnce()
    {
        Headless.Run(() =>
        {
            var pane = Pane(Canned.Says("First."), Canned.Says("Again."));

            pane.Question = "what is eating the disk?";
            Headless.Finish(pane.AskCommand.ExecuteAsync(null));
            pane.Question = "what is eating the disk?";
            Headless.Finish(pane.AskCommand.ExecuteAsync(null));

            pane.Recall(1).ShouldBeTrue();
            pane.Question.ShouldBe("what is eating the disk?");
            pane.Recall(1).ShouldBeFalse();
        });
    }

    /// <summary>
    /// Retry replaces the last exchange rather than adding to it: one question
    /// on screen afterwards, with the second answer under it.
    /// </summary>
    [Fact]
    public void RetryAsksAgainAndLeavesOneExchangeBehind()
    {
        Headless.Run(() =>
        {
            var pane = Pane(Canned.Says("98% full."), Canned.Says("98% full, and it is the journal."));

            pane.Question = "how full is the disk?";
            Headless.Finish(pane.AskCommand.ExecuteAsync(null));

            var before = pane.Rows.Count;
            pane.RetryCommand.CanExecute(null).ShouldBeTrue();
            Headless.Finish(pane.RetryCommand.ExecuteAsync(null));

            pane.Rows.Count.ShouldBe(before);
            pane.Rows.Count(row => row.IsQuestion).ShouldBe(1);
            pane.Rows.Last(row => row.IsAnswer).Text.ShouldBe("98% full, and it is the journal.");
        });
    }

    /// <summary>There is nothing to retry before anything has been asked.</summary>
    [Fact]
    public void RetryIsNotOfferedOnAnEmptyConversation()
    {
        Headless.Run(() => Pane(Canned.Says("Fine.")).RetryCommand.CanExecute(null).ShouldBeFalse());
    }

    /// <summary>Copy hands out the answer as it was written, Markdown and all.</summary>
    [Fact]
    public void CopyingAnAnswerOffersTheMarkdownItWasWrittenIn()
    {
        Headless.Run(() =>
        {
            var pane = Pane(Canned.Says("The journal is **37G**."));
            var copied = new List<string>();
            pane.Copied += (_, text) => copied.Add(text);

            pane.Question = "how full?";
            Headless.Finish(pane.AskCommand.ExecuteAsync(null));

            pane.CopyAnswerCommand.Execute(pane.Rows.Last(row => row.IsAnswer));

            copied.ShouldHaveSingleItem().ShouldBe("The journal is **37G**.");
        });
    }

    private sealed class Quiet(string alias) : IHostAccess
    {
        public string Alias => alias;

        public Task<HostSnapshot> Look(bool metrics, bool tail, int lines, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HostSnapshot());

        public Task<CommandOutcome> Run(string command, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CommandOutcome(0, ""));
    }

    private sealed class Canned(params IReadOnlyList<AssistEvent>[] turns) : IAssistBackend
    {
        private int _turn;

        public string ProviderName => "Canned";

        public string Model => "canned-1";

        public static IReadOnlyList<AssistEvent> Says(string text) =>
            [new AssistEvent.Say(text), new AssistEvent.Finished(AssistStop.EndTurn)];

        public async IAsyncEnumerable<AssistEvent> Stream(
            AssistRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var turn = _turn < turns.Length ? turns[_turn] : Says("Nothing more.");
            _turn++;
            foreach (var streamed in turn)
            {
                await Task.Yield();
                yield return streamed;
            }
        }
    }
}
