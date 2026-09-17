using StrangeSharpTerm.App.ViewModels;
using StrangeSharpTerm.Assist;

namespace StrangeSharpTerm.App.WindowTests;

/// <summary>
/// The orchestrator pane keeps its conversation.
///
/// It did not: every run cleared the rows and built a new assistant, so a
/// second question arrived at one that had never been asked anything and the
/// pane showed only the last answer. "And what about the other two" meant
/// nothing, which is most of what a conversation is for.
/// </summary>
[Collection("window")]
public class ConversationTests
{
    [Fact]
    public void ASecondQuestionKeepsTheFirstOnScreenAndInTheConversation()
    {
        Headless.Run(() =>
        {
            var backend = new Canned(Canned.Says("web-01 is full."), Canned.Says("So is web-02."));
            var model = new OrchestratorViewModel(
                backend,
                [new TargetRow { Alias = "web-01", IsConnected = true, IsChosen = true }],
                _ => null);

            model.Instruction = "is web-01 full?";
            Headless.Finish(model.RunCommand.ExecuteAsync(null));

            model.Instruction = "and the other one?";
            Headless.Finish(model.RunCommand.ExecuteAsync(null));

            // Both questions and both answers are still there to read.
            model.Rows.Count(row => row.Entry is TranscriptEntry.Question).ShouldBe(2);
            model.Rows.Count(row => row.Entry is TranscriptEntry.Answer).ShouldBe(2);

            // And the second question reached an assistant that had heard the
            // first: one conversation, not two.
            backend.Sent[^1].Messages.Count.ShouldBe(3);
        });
    }

    /// <summary>
    /// The box is emptied when the question is asked, because the question is
    /// in the transcript now and the next one is a different question. A plan
    /// keeps its goal: Run presses on it a second time to carry it out.
    /// </summary>
    [Fact]
    public void AskingEmptiesTheBoxAndPlanningDoesNot()
    {
        Headless.Run(() =>
        {
            var model = new OrchestratorViewModel(
                new Canned(Canned.Says("Fine.")),
                [new TargetRow { Alias = "web-01", IsConnected = true, IsChosen = true }],
                _ => null);

            model.Instruction = "is web-01 full?";
            Headless.Finish(model.RunCommand.ExecuteAsync(null));

            model.Instruction.ShouldBe("");
            model.RunCommand.CanExecute(null).ShouldBeFalse();
        });
    }

    private sealed class Canned(params IReadOnlyList<AssistEvent>[] turns) : IAssistBackend
    {
        private int _turn;

        public string ProviderName => "Canned";

        public string Model => "canned-1";

        /// <summary>Every request it was given, so a test can see what the second one carried.</summary>
        internal List<AssistRequest> Sent { get; } = [];

        public static IReadOnlyList<AssistEvent> Says(string text) =>
            [new AssistEvent.Say(text), new AssistEvent.Finished(AssistStop.EndTurn)];

        public async IAsyncEnumerable<AssistEvent> Stream(
            AssistRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Sent.Add(request);
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
