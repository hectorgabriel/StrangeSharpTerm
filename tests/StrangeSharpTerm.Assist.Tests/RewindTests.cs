using StrangeSharpTerm.Assist;

namespace StrangeSharpTerm.Assist.Tests;

/// <summary>
/// Taking back the last question, which is what a retry does before it asks
/// again.
/// </summary>
public class RewindTests
{
    [Fact]
    public async Task AskingAgainAfterARewindIsAnotherAttemptRatherThanAFollowUp()
    {
        var backend = new ScriptedBackend(
            ScriptedBackend.Says("The disk is 98% full."),
            ScriptedBackend.Says("The disk is 98% full, and it is the journal."));
        var agent = new HostAgent(backend, new FakeHost("web-01"), Fixtures.Settings, new RecordingGate());

        await agent.Ask("what is eating the disk?", null, TestContext.Current.CancellationToken);
        agent.CanRewind.ShouldBeTrue();
        agent.Rewind().ShouldBeTrue();

        await agent.Ask("what is eating the disk?", null, TestContext.Current.CancellationToken);

        // The second request carries one question, not a question and the
        // answer it already gave: the model is trying again, not being asked to
        // improve on itself.
        var second = backend.Requests[^1].Messages;
        second.ShouldHaveSingleItem();
        second[0].Text.ShouldNotBeNull().ShouldContain("what is eating the disk?");
    }

    /// <summary>The rows go too, or the pane shows both attempts as though both had been asked.</summary>
    [Fact]
    public async Task TheRowsTheQuestionProducedAreTakenBackWithIt()
    {
        var backend = new ScriptedBackend(
            ScriptedBackend.Runs("df -h /", "how full"),
            ScriptedBackend.Says("98% full."));
        var host = new FakeHost("web-01") { Answer = _ => new CommandOutcome(0, "98%") };
        var agent = new HostAgent(backend, host, Fixtures.Settings, new RecordingGate());

        var dropped = new List<TranscriptEntry>();
        agent.Removed += (_, entry) => dropped.Add(entry);

        await agent.Ask("how full is it?", new AskOptions { MayRunCommands = true }, TestContext.Current.CancellationToken);
        var produced = agent.Entries.Count;
        produced.ShouldBeGreaterThan(1);

        agent.Rewind();

        agent.Entries.ShouldBeEmpty();
        dropped.Count.ShouldBe(produced);
    }

    /// <summary>
    /// A conversation with history keeps it. Rewinding the second question must
    /// not take the first one with it.
    /// </summary>
    [Fact]
    public async Task OnlyTheLastQuestionIsTakenBack()
    {
        var backend = new ScriptedBackend(
            ScriptedBackend.Says("First answer."),
            ScriptedBackend.Says("Second answer."),
            ScriptedBackend.Says("Second answer, again."));
        var agent = new HostAgent(backend, new FakeHost("web-01"), Fixtures.Settings, new RecordingGate());

        await agent.Ask("first?", null, TestContext.Current.CancellationToken);
        await agent.Ask("second?", null, TestContext.Current.CancellationToken);
        agent.Rewind();
        await agent.Ask("second?", null, TestContext.Current.CancellationToken);

        // Question, answer, question: the first exchange survived.
        var third = backend.Requests[^1].Messages;
        third.Count.ShouldBe(3);
        third[0].Text.ShouldNotBeNull().ShouldContain("first?");
        third[1].Text.ShouldBe("First answer.");
        third[2].Text.ShouldNotBeNull().ShouldContain("second?");
    }

    /// <summary>Nothing to take back is not an error, and twice over is not two.</summary>
    [Fact]
    public async Task ThereIsNothingToTakeBackBeforeAnythingIsAsked()
    {
        var agent = new HostAgent(
            new ScriptedBackend(ScriptedBackend.Says("Fine.")),
            new FakeHost("web-01"),
            Fixtures.Settings,
            new RecordingGate());

        agent.CanRewind.ShouldBeFalse();
        agent.Rewind().ShouldBeFalse();

        await agent.Ask("anything?", null, TestContext.Current.CancellationToken);
        agent.Rewind().ShouldBeTrue();
        agent.Rewind().ShouldBeFalse();
    }
}
