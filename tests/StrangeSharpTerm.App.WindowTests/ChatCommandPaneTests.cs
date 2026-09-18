using StrangeSharpTerm.App.ViewModels;
using StrangeSharpTerm.Assist;

namespace StrangeSharpTerm.App.WindowTests;

/// <summary>
/// What a line starting with a slash does to a pane.
///
/// In the window suite because the rows are posted back to the UI thread, and a
/// plain test has nothing to pump that — which is the same reason the retry
/// tests live here.
/// </summary>
[Collection("window")]
public class ChatCommandPaneTests
{
    private static AssistantViewModel Pane(
        Func<string>? tools = null,
        params IReadOnlyList<AssistEvent>[] turns) =>
        new(
            new HostAgent(new Canned(turns), new Quiet("web-01"), new AssistSettings(), new StandingAnswer(true)),
            new AssistSettings(),
            _ => { },
            connectedTools: tools);

    [Fact]
    public void HelpIsAnsweredInThePaneAndNeverSent()
    {
        Headless.Run(() =>
        {
            var pane = Pane(turns: Canned.Says("This should never be reached."));

            pane.Question = "/help";
            Headless.Finish(pane.AskCommand.ExecuteAsync(null));

            pane.Question.ShouldBe("");
            pane.Rows.ShouldContain(row => row.IsNote && row.Text.Contains("/clear"));
            // Nothing was asked: no question row, and so no answer.
            pane.Rows.ShouldNotContain(row => row.IsQuestion);
        });
    }

    [Fact]
    public void ClearEmptiesWhatIsOnScreen()
    {
        Headless.Run(() =>
        {
            var pane = Pane(turns: Canned.Says("It is 98% full."));

            pane.Question = "how full is the disk?";
            Headless.Finish(pane.AskCommand.ExecuteAsync(null));
            pane.Rows.ShouldNotBeEmpty();

            pane.Question = "/clear";
            Headless.Finish(pane.AskCommand.ExecuteAsync(null));

            pane.Rows.ShouldBeEmpty();
            pane.IsEmpty.ShouldBeTrue();
            // And there is nothing left to take back, because there is no
            // longer a question in the conversation to take.
            pane.CanRetry.ShouldBeFalse();
        });
    }

    [Fact]
    public void McpReportsWhatTheAppKnowsRatherThanWhatAModelWouldSay()
    {
        Headless.Run(() =>
        {
            var pane = Pane(() => "Runbooks — 3 tools");

            pane.Question = "/mcp";
            Headless.Finish(pane.AskCommand.ExecuteAsync(null));

            pane.Rows.ShouldContain(row => row.IsNote && row.Text.Contains("Runbooks — 3 tools"));
        });
    }

    [Fact]
    public void AnUnknownCommandIsTurnedDownRatherThanAsked()
    {
        Headless.Run(() =>
        {
            var pane = Pane(turns: Canned.Says("This should never be reached."));

            pane.Question = "/mpc";
            Headless.Finish(pane.AskCommand.ExecuteAsync(null));

            pane.Rows.ShouldContain(row => row.Text.Contains("There is no /mpc"));
            pane.Rows.ShouldNotContain(row => row.IsQuestion);
        });
    }

    [Fact]
    public void AQuestionThatStartsWithAPathIsStillAQuestion()
    {
        Headless.Run(() =>
        {
            var pane = Pane(turns: Canned.Says("Because it was never installed."));

            pane.Question = "/etc/nginx is missing, why?";
            Headless.Finish(pane.AskCommand.ExecuteAsync(null));

            pane.Rows.ShouldContain(row => row.IsQuestion && row.Text.StartsWith("/etc/nginx"));
            pane.Rows.ShouldContain(row => row.IsAnswer);
        });
    }

    [Fact]
    public void ACommandIsNotSomethingTheUpArrowWalksBackTo()
    {
        Headless.Run(() =>
        {
            var pane = Pane(turns: Canned.Says("It is 98% full."));

            pane.Question = "how full is the disk?";
            Headless.Finish(pane.AskCommand.ExecuteAsync(null));
            pane.Question = "/help";
            Headless.Finish(pane.AskCommand.ExecuteAsync(null));

            // The last thing typed was /help; the last thing asked was the
            // question, and that is what walking back is for.
            pane.Recall(1).ShouldBeTrue();
            pane.Question.ShouldBe("how full is the disk?");
        });
    }
}
