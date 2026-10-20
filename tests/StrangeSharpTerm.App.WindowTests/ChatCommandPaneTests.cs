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

            // After a question, the disclosure shows the update the next one
            // carries -- here, that nothing changed.
            pane.Preview.ShouldContain("Nothing about the host has changed");

            pane.Question = "/clear";
            Headless.Finish(pane.AskCommand.ExecuteAsync(null));

            // And after a clear, the whole account again, because that is
            // what the next question will carry.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (pane.Preview.Contains("Nothing about the host has changed") && DateTime.UtcNow < deadline)
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            pane.Preview.ShouldBe("Host: web-01");

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

    /// <summary>
    /// /export puts the conversation on the clipboard as Markdown and says so
    /// in the pane -- a clipboard gives no sign of having been written to.
    /// </summary>
    [Fact]
    public void ExportCopiesTheConversationAndSaysSo()
    {
        Headless.Run(() =>
        {
            var pane = Pane(turns: [Canned.Runs("df -h /", "how full"), Canned.Says("It is 98% full.")]);
            pane.MayRunCommands = true;
            var copied = new List<string>();
            pane.Copied += (_, text) => copied.Add(text);

            pane.Question = "how full is the disk?";
            Headless.Finish(pane.AskCommand.ExecuteAsync(null));
            pane.Question = "/export";
            Headless.Finish(pane.AskCommand.ExecuteAsync(null));

            var markdown = copied.ShouldHaveSingleItem();
            markdown.ShouldStartWith("# web-01: assistant conversation");
            markdown.ShouldContain("## how full is the disk?");
            markdown.ShouldContain("```sh\ndf -h /\n```");
            markdown.ShouldContain("It is 98% full.");
            markdown.ShouldContain("Answered by Canned · canned-1.");

            pane.Rows.ShouldContain(row => row.IsNote
                && row.Text == "Copied this conversation to the clipboard as Markdown: 1 question, 1 command.");
            // Not a question: nothing was sent for it.
            pane.Rows.Count(row => row.IsQuestion).ShouldBe(1);
        });
    }

    [Fact]
    public void ExportWithNothingAskedSaysSoAndCopiesNothing()
    {
        Headless.Run(() =>
        {
            var pane = Pane();
            var copied = 0;
            pane.Copied += (_, _) => copied++;

            pane.Question = "/export";
            Headless.Finish(pane.AskCommand.ExecuteAsync(null));

            copied.ShouldBe(0);
            pane.Rows.ShouldContain(row => row.IsNote && row.Text == ChatCommands.NothingToExport);
        });
    }

    [Fact]
    public void HelpListsExport()
    {
        ChatCommands.Listing.ShouldContain("/export");
    }

    /// <summary>The orchestrator exports its conversation too, naming the hosts it reached.</summary>
    [Fact]
    public void TheOrchestratorExportsItsConversation()
    {
        Headless.Run(() =>
        {
            var model = new OrchestratorViewModel(
                new Canned(Canned.Says("All three are fine.")),
                [new TargetRow { Alias = "web-01", IsConnected = true, IsChosen = true }],
                _ => null);
            var copied = new List<string>();
            model.Copied += (_, text) => copied.Add(text);

            model.Instruction = "how are they?";
            Headless.Finish(model.RunCommand.ExecuteAsync(null));
            model.Instruction = "/export";
            Headless.Finish(model.RunCommand.ExecuteAsync(null));

            var markdown = copied.ShouldHaveSingleItem();
            markdown.ShouldStartWith("# Orchestrator conversation");
            markdown.ShouldContain("## how are they?");
            markdown.ShouldContain("All three are fine.");
        });
    }
}
