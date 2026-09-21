using StrangeSharpTerm.Assist;

namespace StrangeSharpTerm.Assist.Tests;

public class HostAgentTests
{
    [Fact]
    public async Task ByDefaultNothingIsEverRun()
    {
        var host = new FakeHost("web-01");
        var backend = new ScriptedBackend(ScriptedBackend.Says("Try `df -h /`."));
        var agent = new HostAgent(backend, host, Fixtures.Settings, new StandingAnswer(true));

        await agent.Ask("what is wrong?", cancellationToken: TestContext.Current.CancellationToken);

        // The tool is not offered at all, so there is nothing for a gate to
        // refuse: a suggested command arrives as a block with a button that
        // types it and stops.
        backend.Requests.Single().Tools.ShouldBeEmpty();
        backend.Requests.Single().System.ShouldBe(AssistPrompts.Host);
        host.Ran.ShouldBeEmpty();
    }

    [Fact]
    public async Task WithTheToggleOnAReadOnlyCommandRunsWithoutAsking()
    {
        var host = new FakeHost("web-01") { Answer = _ => new CommandOutcome(0, "/dev/sda1 98% /") };
        var gate = new RecordingGate();
        var agent = Agent(host, gate, ScriptedBackend.Runs("df -h /", "how full"), ScriptedBackend.Says("It is 98% full."));

        var answer = await agent.Ask("what is eating the disk?", Running, TestContext.Current.CancellationToken);

        host.Ran.ShouldBe(["df -h /"]);
        gate.Asked.ShouldBeEmpty();
        answer.CommandsRun.ShouldBe(1);
        answer.Text.ShouldBe("It is 98% full.");

        var step = agent.Entries.OfType<TranscriptEntry.Step>().Single();
        step.State.ShouldBe(StepState.Ran);
        step.RanUnattended.ShouldBeTrue();
        step.Why.ShouldBe("how full");
    }

    [Fact]
    public async Task AWritingCommandStopsAndTheQuestionNamesItsHost()
    {
        var host = new FakeHost("web-01");
        var gate = new RecordingGate();
        var agent = Agent(host, gate, ScriptedBackend.Runs("systemctl restart nginx", "restart it"), ScriptedBackend.Says("Done."));

        await agent.Ask("restart nginx", Running, TestContext.Current.CancellationToken);

        var asked = gate.Asked.Single();
        asked.Host.ShouldBe("web-01");
        asked.Command.ShouldBe("systemctl restart nginx");
        asked.Why.ShouldBe("restart it");
        asked.Reason.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task DecliningIsNotADeadEndAndTheModelIsToldPlainly()
    {
        var host = new FakeHost("web-01");
        var gate = new RecordingGate { Answer = _ => false };
        var backend = new ScriptedBackend(
            ScriptedBackend.Runs("rm -rf /var/log/old"),
            ScriptedBackend.Says("Understood."));
        var agent = new HostAgent(backend, host, Fixtures.Settings, gate);

        await agent.Ask("clean up", Running, TestContext.Current.CancellationToken);

        host.Ran.ShouldBeEmpty();
        agent.Entries.OfType<TranscriptEntry.Step>().Single().State.ShouldBe(StepState.Refused);

        // What goes back says so, which is what stops it trying the same thing
        // another way.
        var result = backend.Requests[1].Messages.Last().ToolResults.Single();
        result.Output.ShouldContain("refused");
        result.Output.ShouldContain("another way");
    }

    [Fact]
    public async Task TwelveCommandsAndThenItIsToldToSummarise()
    {
        var host = new FakeHost("web-01");
        // Thirteen turns that each ask for a command, then one that answers.
        var turns = Enumerable.Range(0, 13)
            .Select(number => ScriptedBackend.Runs("uptime", "again", $"call_{number}"))
            .Append(ScriptedBackend.Says("Here is what I found."))
            .ToArray();
        var backend = new ScriptedBackend(turns);
        var agent = new HostAgent(backend, host, Fixtures.Settings, new StandingAnswer(true));

        var answer = await agent.Ask("investigate", Running, TestContext.Current.CancellationToken);

        host.Ran.Count.ShouldBe(AssistLimits.CommandBudget);
        answer.CommandsRun.ShouldBe(AssistLimits.CommandBudget);

        // Told to summarise rather than stopped mid-investigation.
        var thirteenth = backend.Requests[13].Messages.Last().ToolResults.Single();
        thirteenth.Output.ShouldContain("summarise");
        agent.Entries.OfType<TranscriptEntry.Step>().Last().State.ShouldBe(StepState.Skipped);
    }

    [Fact]
    public async Task CommandOutputIsRedactedBeforeItGoesBack()
    {
        var host = new FakeHost("web-01") { Answer = _ => new CommandOutcome(0, "DB_PASSWORD=hunter2") };
        var backend = new ScriptedBackend(ScriptedBackend.Runs("cat /etc/app.env"), ScriptedBackend.Says("Read it."));
        var agent = new HostAgent(backend, host, Fixtures.Settings, new StandingAnswer(true));

        await agent.Ask("what is configured?", Running, TestContext.Current.CancellationToken);

        var result = backend.Requests[1].Messages.Last().ToolResults.Single();
        result.Output.ShouldNotContain("hunter2");
        result.Output.ShouldContain("DB_PASSWORD");

        // What the row shows is the same text the model saw.
        agent.Entries.OfType<TranscriptEntry.Step>().Single().Output.ShouldNotContain("hunter2");
    }

    [Fact]
    public async Task ACommandThatDidNotFinishSaysSoRatherThanPretending()
    {
        var host = new FakeHost("web-01") { Answer = _ => new CommandOutcome(0, "partial", TimedOut: true) };
        var backend = new ScriptedBackend(ScriptedBackend.Runs("du -xh /"), ScriptedBackend.Says("It was slow."));
        var agent = new HostAgent(backend, host, Fixtures.Settings, new StandingAnswer(true));

        await agent.Ask("what is big?", Running, TestContext.Current.CancellationToken);

        agent.Entries.OfType<TranscriptEntry.Step>().Single().State.ShouldBe(StepState.TimedOut);
        backend.Requests[1].Messages.Last().ToolResults.Single().Output.ShouldContain("did not finish");
    }

    [Fact]
    public async Task SixtySecondsPerCommand()
    {
        var host = new FakeHost("web-01");
        var agent = Agent(host, new RecordingGate(), ScriptedBackend.Runs("uptime"), ScriptedBackend.Says("Fine."));

        await agent.Ask("how long has it been up?", Running, TestContext.Current.CancellationToken);

        host.Timeouts.Single().ShouldBe(AssistLimits.CommandTimeout);
    }

    [Fact]
    public async Task TheQuestionCarriesTheContextBlock()
    {
        var host = new FakeHost("web-01")
        {
            Snapshot = new HostSnapshot("Linux 6.1.0 x86_64", Fixtures.Metrics, "deploy@web-01:~$ df -h"),
        };
        var backend = new ScriptedBackend(ScriptedBackend.Says("Looks full."));
        var agent = new HostAgent(backend, host, Fixtures.Settings, new StandingAnswer(true));

        await agent.Ask("is it full?", cancellationToken: TestContext.Current.CancellationToken);

        var sent = backend.Requests.Single().Messages.Single().Text!;
        sent.ShouldContain("Host: web-01");
        sent.ShouldContain("Linux 6.1.0");
        sent.ShouldContain("up 10 days");
        sent.ShouldContain("deploy@web-01:~$ df -h");
        sent.ShouldContain("is it full?");
    }

    [Fact]
    public async Task BothContextSourcesAreSwitches()
    {
        var host = new FakeHost("web-01")
        {
            Snapshot = new HostSnapshot("Linux 6.1.0", Fixtures.Metrics, "deploy@web-01:~$ secrets"),
        };
        var backend = new ScriptedBackend(ScriptedBackend.Says("."));
        var settings = Fixtures.Settings with { SendMetrics = false, SendTerminalTail = false };
        var agent = new HostAgent(backend, host, settings, new StandingAnswer(true));

        await agent.Ask("hello", cancellationToken: TestContext.Current.CancellationToken);

        var sent = backend.Requests.Single().Messages.Single().Text!;
        sent.ShouldContain("Host: web-01");
        sent.ShouldNotContain("up 10 days");
        sent.ShouldNotContain("deploy@web-01");
        host.Asked.ShouldBe((false, false, Fixtures.Settings.TerminalTailLines));
    }

    /// <summary>
    /// An agent with no terminal of its own carries none, whatever the switch
    /// says. This is what an orchestrated run relies on: it is handed no pane,
    /// so a run across eight hosts does not depend on which of them had a window
    /// open and what was last printed in it.
    /// </summary>
    [Fact]
    public async Task AnAgentWithNoTerminalOfItsOwnCarriesNoTail()
    {
        var host = new FakeHost("web-01")
        {
            Snapshot = new HostSnapshot("Linux 6.1.0", Fixtures.Metrics, TerminalTail: null),
        };
        var backend = new ScriptedBackend(ScriptedBackend.Says("."));
        // The switch is on, and there is still nothing to send.
        var agent = new HostAgent(backend, host, Fixtures.Settings, new StandingAnswer(true));

        await agent.Ask("is it full?", cancellationToken: TestContext.Current.CancellationToken);

        var sent = backend.Requests.Single().Messages.Single().Text!;
        sent.ShouldContain("Host: web-01");
        sent.ShouldContain("Linux 6.1.0");
        sent.ShouldNotContain("Terminal");
        agent.LastContext.ShouldNotBeNull().TerminalTail.ShouldBeNull();
    }

    [Fact]
    public async Task ThePreviewIsTheRequestRatherThanADescriptionOfIt()
    {
        var host = new FakeHost("web-01")
        {
            Snapshot = new HostSnapshot("Linux 6.1.0", Fixtures.Metrics, "DB_PASSWORD=hunter2"),
        };
        var backend = new ScriptedBackend(ScriptedBackend.Says("."));
        var agent = new HostAgent(backend, host, Fixtures.Settings, new StandingAnswer(true));

        var preview = await agent.Context(TestContext.Current.CancellationToken);
        await agent.Ask("hello", cancellationToken: TestContext.Current.CancellationToken);

        backend.Requests.Single().Messages.Single().Text!.ShouldContain(preview.Render());
        // The count next to the disclosure is the only evidence the redactor ran.
        preview.Redactions.ShouldBe(1);
        preview.Render().ShouldNotContain("hunter2");
    }

    [Fact]
    public async Task AHostThatWillNotAnswerAProbeCanStillBeAskedAbout()
    {
        var host = new FakeHost("web-01") { Throws = true };
        var backend = new ScriptedBackend(ScriptedBackend.Says("I have little to go on."));
        var agent = new HostAgent(backend, host, Fixtures.Settings, new StandingAnswer(true));

        var answer = await agent.Ask("what is it?", cancellationToken: TestContext.Current.CancellationToken);

        answer.Failed.ShouldBeFalse();
        backend.Requests.Single().Messages.Single().Text!.ShouldContain("Host: web-01");
    }

    [Fact]
    public async Task AProviderFailureReachesThePaneAsARow()
    {
        var backend = new ScriptedBackend { Fails = new AssistException("The key was refused.") };
        var agent = new HostAgent(backend, new FakeHost("web-01"), Fixtures.Settings, new StandingAnswer(true));

        var answer = await agent.Ask("hello", cancellationToken: TestContext.Current.CancellationToken);

        answer.Failed.ShouldBeTrue();
        agent.Entries.OfType<TranscriptEntry.Note>().Single().Text.ShouldBe("The key was refused.");
    }

    [Fact]
    public async Task AServerSideRefusalIsSaidRatherThanSwallowed()
    {
        var backend = new ScriptedBackend(
            [new AssistEvent.Say("I can't help with that."), new AssistEvent.Finished(AssistStop.Refusal)]);
        var agent = new HostAgent(backend, new FakeHost("web-01"), Fixtures.Settings, new StandingAnswer(true));

        await agent.Ask("something", cancellationToken: TestContext.Current.CancellationToken);

        agent.Entries.OfType<TranscriptEntry.Note>().Single().Text.ShouldContain("declined");
    }

    [Fact]
    public async Task TheTranscriptIsOneListWithCommandsInIt()
    {
        var host = new FakeHost("web-01") { Answer = _ => new CommandOutcome(0, "up 3 days") };
        var agent = Agent(host, new RecordingGate(), ScriptedBackend.Runs("uptime"), ScriptedBackend.Says("Three days."));

        await agent.Ask("how long?", Running, TestContext.Current.CancellationToken);

        // What it actually ran should never require looking somewhere else.
        agent.Entries.Select(entry => entry.GetType().Name)
            .ShouldBe(["Question", "Step", "Answer"]);
    }

    [Fact]
    public async Task AConversationRemembersItsEarlierTurns()
    {
        var backend = new ScriptedBackend(ScriptedBackend.Says("First."), ScriptedBackend.Says("Second."));
        var agent = new HostAgent(backend, new FakeHost("web-01"), Fixtures.Settings, new StandingAnswer(true));

        await agent.Ask("one", cancellationToken: TestContext.Current.CancellationToken);
        await agent.Ask("two", cancellationToken: TestContext.Current.CancellationToken);

        backend.Requests[1].Messages.Count.ShouldBe(3);
        backend.Requests[1].Messages[1].Text.ShouldBe("First.");
    }

    [Fact]
    public void LongOutputKeepsBothEnds()
    {
        var output = string.Concat(Enumerable.Repeat("x", 100)) + "THE ERROR";

        var truncated = HostAgent.Truncate(output, limit: 40);

        truncated.ShouldStartWith("xxxx");
        // The error is usually at the end; a truncation that cut the tail off
        // would be a truncation that lost the answer.
        truncated.ShouldEndWith("THE ERROR");
        truncated.ShouldContain("characters removed");
    }

    [Fact]
    public void ShortOutputIsUntouched() => HostAgent.Truncate("fine", limit: 40).ShouldBe("fine");

    /// <summary>
    /// The pane beside a one-host conversation needs to know a command is
    /// running on it, and when it is over -- the same thing the fleet agent
    /// says, so the window can treat both the same way.
    /// </summary>
    [Fact]
    public async Task ItSaysWhichHostItHasAndWhenItLetsGo()
    {
        var host = new FakeHost("web-01") { Answer = _ => new CommandOutcome(0, "/dev/sda1 98% /") };
        var agent = Agent(host, new RecordingGate(), ScriptedBackend.Runs("df -h /", "how full"), ScriptedBackend.Says("98% full."));

        var steps = new List<AssistStep>();
        agent.Working += (_, step) => steps.Add(step);

        await agent.Ask("what is eating the disk?", Running, TestContext.Current.CancellationToken);

        // In on the way to the command, out on the way back.
        steps.Count.ShouldBe(2);
        steps[0].ShouldSatisfyAllConditions(
            () => steps[0].Host.ShouldBe("web-01"),
            () => steps[0].Command.ShouldBe("df -h /"),
            () => steps[0].Why.ShouldBe("how full"),
            () => steps[0].Running.ShouldBeTrue());
        steps[1].ShouldSatisfyAllConditions(
            () => steps[1].Running.ShouldBeFalse(),
            () => steps[1].ExitStatus.ShouldBe(0),
            () => steps[1].Output.ShouldContain("98%"));
    }

    /// <summary>
    /// A command nobody allowed never reaches a pane.
    ///
    /// The narration is raised after the gate, not before it: a refused command
    /// that had already announced itself in the terminal would be the window
    /// saying something happened on a host when nothing did.
    /// </summary>
    [Fact]
    public async Task ARefusedCommandIsNeverNarrated()
    {
        var host = new FakeHost("web-01");
        var agent = Agent(
            host,
            new RecordingGate { Answer = _ => false },
            ScriptedBackend.Runs("systemctl restart nginx", "restart it"),
            ScriptedBackend.Says("Understood."));

        var steps = new List<AssistStep>();
        agent.Working += (_, step) => steps.Add(step);

        await agent.Ask("restart nginx", Running, TestContext.Current.CancellationToken);

        host.Ran.ShouldBeEmpty();
        steps.ShouldBeEmpty();
    }

    private static AskOptions Running { get; } = new() { MayRunCommands = true };

    private static HostAgent Agent(FakeHost host, ICommandGate gate, params IReadOnlyList<AssistEvent>[] turns) =>
        new(new ScriptedBackend(turns), host, Fixtures.Settings, gate);
}

/// <summary>
/// A tool the model was not offered is not a tool it may use.
///
/// Found while reproducing the plan bug: "Run commands" decided what the model
/// was *offered* and nothing about what was *carried*. A provider that emitted
/// a run_command call it had not been given -- models do produce calls for
/// tools that are not there -- had it run anyway, and a read-only one ran
/// without anybody being asked. The switch was a suggestion.
/// </summary>
public class UnofferedToolTests
{
    [Fact]
    public async Task ACommandIsNotRunWhenRunningCommandsIsOff()
    {
        var host = new FakeHost("web-01");
        var agent = new HostAgent(
            new ScriptedBackend(ScriptedBackend.Runs("uptime"), ScriptedBackend.Says("I could not.")),
            host,
            Fixtures.Settings,
            new StandingAnswer(true));

        await agent.Ask(
            "how long up?",
            new AskOptions { MayRunCommands = false },
            TestContext.Current.CancellationToken);

        host.Ran.ShouldBeEmpty();
    }

    [Fact]
    public async Task AWriteIsNotCarriedWhenEditingIsOff()
    {
        // The same hole in the file tools: write_file is offered only with
        // editing on, and was carried regardless.
        var workspace = new FakeWorkspace().With("app.py", "print()");
        var gate = new RecordingGate(answer: true);
        var agent = new HostAgent(
            new ScriptedBackend(
                ScriptedBackend.Calls(
                    WorkspaceTools.WriteFile,
                    new { path = "app.py", content = "print('x')", why = "change it" }),
                ScriptedBackend.Says("I could not.")),
            new FakeHost("web-01"),
            Fixtures.Settings,
            gate,
            null,
            workspace);

        await agent.Ask(
            "change it",
            new AskOptions { MayEditFiles = false },
            TestContext.Current.CancellationToken);

        gate.Asked.ShouldBeEmpty();
        workspace.Written.ShouldBeEmpty();
    }
}
