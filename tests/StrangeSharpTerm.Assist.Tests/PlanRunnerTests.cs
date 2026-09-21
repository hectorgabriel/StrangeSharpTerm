using StrangeSharpTerm.Assist;

namespace StrangeSharpTerm.Assist.Tests;

public class PlanRunnerTests
{
    [Fact]
    public async Task PhasesGoInOrderAndAValueIsCarriedBetweenThem()
    {
        var asked = new List<string>();
        var runner = Runner(asked, "Initialised.\nCAPTURED: kubeadm join 10.0.0.1 --token abc", "Joined.");

        var result = await runner.Run(
            Plan(
                Phase("Initialise", ["web-01"], "kubeadm init", capture: "join_command"),
                Phase("Join", ["web-02"], "{{join_command}}")),
            TestContext.Current.CancellationToken);

        result.Stopped.ShouldBeFalse();
        result.Phases.Select(phase => phase.Outcome).ShouldBe([PhaseOutcome.Completed, PhaseOutcome.Completed]);

        // The captured value is shown verbatim where it was produced.
        result.Phases[0].Captured.ShouldBe("kubeadm join 10.0.0.1 --token abc");
        result.Phases[0].CapturedName.ShouldBe("join_command");

        // The captured value reaches the next phase inside the command itself.
        asked[1].ShouldContain("kubeadm join 10.0.0.1 --token abc");
        asked[1].ShouldNotContain("{{join_command}}");
    }

    /// <summary>
    /// The commands are handed over as written rather than described back to the
    /// model. Paraphrasing them would invite it to write its own, and the plan on
    /// screen would stop being the plan that runs.
    /// </summary>
    [Fact]
    public async Task TheHostIsGivenTheCommandsAsTheyWereWritten()
    {
        var asked = new List<string>();
        var phase = new PlanPhase
        {
            Name = "Install OpenClaw",
            Hosts = ["web-01"],
            Why = "It has the most memory.",
            Commands = ["apt-get update", "apt-get install -y openclaw"],
        };

        await Runner(asked, "Done.").Run(
            Plan(phase), TestContext.Current.CancellationToken);

        var instruction = asked.ShouldHaveSingleItem();
        instruction.ShouldContain("apt-get update");
        instruction.ShouldContain("apt-get install -y openclaw");
        // In order, and told to run them rather than improve on them.
        instruction.IndexOf("apt-get update", StringComparison.Ordinal)
            .ShouldBeLessThan(instruction.IndexOf("apt-get install -y openclaw", StringComparison.Ordinal));
        instruction.ShouldContain("as they are written");
    }

    [Fact]
    public async Task APhaseThatIsSwitchedOffDoesNotRun()
    {
        var asked = new List<string>();
        var plan = Plan(
            Phase("One", ["web-01"], "echo one"),
            Phase("Two", ["web-01"], "echo two"));
        plan.Phases[0].IsEnabled = false;

        var result = await Runner(asked, "Done.", "Done.")
            .Run(plan, TestContext.Current.CancellationToken);

        result.Phases[0].Outcome.ShouldBe(PhaseOutcome.Disabled);
        // One host asked, once, and about the phase that was left on.
        asked.ShouldHaveSingleItem().ShouldContain("echo two");
        asked.Single().ShouldNotContain("echo one");
    }

    [Fact]
    public async Task TheRunStopsIfAPhaseGetsNowhere()
    {
        // web-01 is not connected, so the phase that needs it gets nowhere.
        var runner = new PlanRunner(alias => alias == "web-01" ? null : Agent([], "Done."));

        var result = await runner.Run(
            Plan(
                Phase("Initialise", ["web-01"], "kubeadm init"),
                Phase("Join", ["web-02"], "kubeadm join 10.0.0.1")),
            TestContext.Current.CancellationToken);

        result.Stopped.ShouldBeTrue();
        result.StoppedBecause.ShouldNotBeNull().ShouldContain("Initialise");
        result.Phases[0].Outcome.ShouldBe(PhaseOutcome.Stalled);
        // What was already done stays on screen, and the rest says it was not reached.
        result.Phases[1].Outcome.ShouldBe(PhaseOutcome.NotReached);
    }

    [Fact]
    public async Task APhaseThatProducedNoValueEndsTheRun()
    {
        var asked = new List<string>();
        var result = await Runner(asked, "I ran it, but I will not say what it printed.")
            .Run(
                Plan(
                    Phase("Initialise", ["web-01"], "kubeadm init", capture: "join_command"),
                    Phase("Join", ["web-02"], "Join with {{join_command}}")),
                TestContext.Current.CancellationToken);

        result.Stopped.ShouldBeTrue();
        result.Phases[0].Outcome.ShouldBe(PhaseOutcome.NothingCaptured);
        result.StoppedBecause.ShouldNotBeNull().ShouldContain("join_command");
        // Joining nodes to a control plane that never came up is worse than stopping.
        result.Phases[1].Outcome.ShouldBe(PhaseOutcome.NotReached);
    }

    [Fact]
    public async Task APhaseIsSkippedRatherThanRunWithAPlaceholderStillInIt()
    {
        var asked = new List<string>();
        var plan = Plan(
            Phase("Initialise", ["web-01"], "kubeadm init", capture: "join_command"),
            Phase("Join", ["web-02"], "Join with {{join_command}}"));
        // The first phase is switched off, so its value is never produced.
        plan.Phases[0].IsEnabled = false;

        var result = await Runner(asked, "Joined.")
            .Run(plan, TestContext.Current.CancellationToken);

        result.Phases[1].Outcome.ShouldBe(PhaseOutcome.Unfilled);
        result.Phases[1].Note.ShouldNotBeNull().ShouldContain("join_command");
        // Sending {{join_command}} to a server is the mistake this prevents.
        asked.ShouldBeEmpty();
    }

    [Fact]
    public async Task ACapturingPhaseIsToldToReportItsValue()
    {
        var asked = new List<string>();
        await Runner(asked, "Done.\nCAPTURED: value")
            .Run(
                Plan(Phase("Initialise", ["web-01"], "kubeadm init", capture: "join_command")),
                TestContext.Current.CancellationToken);

        asked.Single().ShouldContain("CAPTURED:");
        asked.Single().ShouldContain("join_command");
    }

    [Fact]
    public async Task CommandsGetTenMinutesRatherThanOne()
    {
        var host = new FakeHost("web-01") { Answer = _ => new CommandOutcome(0, "ok") };
        var runner = new PlanRunner(_ => new HostAgent(
            new ScriptedBackend(ScriptedBackend.Runs("uptime"), ScriptedBackend.Says("Done.")),
            host,
            Fixtures.Settings,
            new StandingAnswer(true)));

        await runner.Run(
            Plan(Phase("Check", ["web-01"], "uptime")),
            TestContext.Current.CancellationToken);

        // apt install and kubeadm init legitimately take that long.
        host.Timeouts.Single().ShouldBe(AssistLimits.PlanCommandTimeout);
    }

    [Fact]
    public async Task TheGateIsUnchangedAndStillNamesItsHost()
    {
        var gate = new RecordingGate();
        var runner = new PlanRunner(alias => new HostAgent(
            new ScriptedBackend(ScriptedBackend.Runs("systemctl restart kubelet"), ScriptedBackend.Says("Done.")),
            new FakeHost(alias),
            Fixtures.Settings,
            gate));

        await runner.Run(
            Plan(Phase("Restart", ["web-02"], "systemctl restart kubelet")),
            TestContext.Current.CancellationToken);

        gate.Asked.Single().Host.ShouldBe("web-02");
    }

    [Fact]
    public async Task EachPhaseIsReportedAsItFinishes()
    {
        var seen = new List<string>();
        var runner = Runner([], "One.", "Two.");
        runner.Finished += (_, phase) => seen.Add(phase.Phase.Name);

        await runner.Run(
            Plan(Phase("One", ["web-01"], "echo one"), Phase("Two", ["web-01"], "echo two")),
            TestContext.Current.CancellationToken);

        seen.ShouldBe(["One", "Two"]);
    }

    [Theory]
    [InlineData("Done.\nCAPTURED: kubeadm join 10.0.0.1", "kubeadm join 10.0.0.1")]
    [InlineData("CAPTURED: `kubeadm join 10.0.0.1`", "kubeadm join 10.0.0.1")]
    [InlineData("I will report it.\nCAPTURED: first\nRan it.\nCAPTURED: second", "second")]
    [InlineData("Nothing to report.", null)]
    public void TheValueIsTakenFromTheLineThatNamesIt(string answer, string? expected) =>
        PlanRunner.Captured(answer).ShouldBe(expected);

    private static RunPlan Plan(params PlanPhase[] phases) => new(phases);

    /// <summary>One phase, one command -- enough for everything about order and carrying here.</summary>
    private static PlanPhase Phase(string name, string[] hosts, string command, string? capture = null) =>
        new() { Name = name, Hosts = hosts, Commands = [command], Capture = capture };

    /// <summary>Every host is reachable and answers with the next line of the script.</summary>
    private static PlanRunner Runner(List<string> asked, params string[] answers)
    {
        var script = new Queue<string>(answers);
        return new PlanRunner(_ => Agent(asked, script.Count > 0 ? script.Dequeue() : "Done."));
    }

    private static HostAgent Agent(List<string> asked, string answer)
    {
        var backend = new Working(asked, answer);
        return new HostAgent(backend, new FakeHost("host"), Fixtures.Settings, new StandingAnswer(true));
    }

    /// <summary>
    /// A worker that does what a real one does: runs something, then reports.
    ///
    /// These tests are about order and carrying a value, and used to drive the
    /// workers with a backend that only ever answered in prose. That stopped
    /// being a phase once a host that ran nothing stopped counting as having
    /// done it -- which is the rule that catches a plan sending nothing to its
    /// hosts. So the worker here runs one harmless command before it answers.
    /// </summary>
    private sealed class Working(List<string> asked, string answer) : IAssistBackend
    {
        private readonly RecordingBackend _recording = new(asked, answer);
        private int _turn;

        public string ProviderName => "Working";

        public string Model => "working-1";

        public async IAsyncEnumerable<AssistEvent> Stream(
            AssistRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (_turn++ > 0)
            {
                // The report, once the command has come back. Not through the
                // recording backend: that would record a second "instruction"
                // for the same phase, which is the tool result and not one.
                await Task.Yield();
                yield return new AssistEvent.Say(answer);
                yield return new AssistEvent.Finished(AssistStop.EndTurn);
                yield break;
            }

            // The instruction is recorded on the way in, as it always was, so
            // what each host was told is still what these tests check.
            await foreach (var _ in _recording.Stream(request, cancellationToken))
            {
            }
            yield return new AssistEvent.Call(new AssistToolCall(
                "run",
                AssistTools.RunCommand,
                System.Text.Json.JsonSerializer.Serialize(new { command = "uptime", why = "the phase" })));
            yield return new AssistEvent.Finished(AssistStop.ToolUse);
        }
    }
}

/// <summary>
/// Whether running a plan actually runs its commands.
///
/// The tests above are about order and carrying a value between phases, and
/// they drive the workers with a backend that only ever answers in prose -- so
/// none of them could notice a plan that ran nothing. This one drives a real
/// worker with a model that asks to run the command, against a host that
/// records what reached it.
/// </summary>
public class PlanRunsItsCommandsTests
{
    private static PlanPhase Phase(string command) =>
        new() { Name = "Install", Hosts = ["web-01"], Commands = [command] };

    [Fact]
    public async Task APlanWithRunCommandsOffStillOffersTheHostSomethingToRunThemWith()
    {
        // The report: Plan mode sent nothing to the hosts. Each worker was told
        // "run these commands, in order" and -- with the switch off -- offered
        // no tool to run them with, so a real model could only answer in prose.
        //
        // Asserted on what is offered rather than on what ran, because the
        // scripted backend here would call run_command whether offered it or
        // not, and a real provider will not. That difference is how this bug
        // passed every test the plan runner had.
        //
        // Pressing "Run the plan" after reading every command in it is the
        // consent; a switch left off must not quietly empty it.
        var backend = new ScriptedBackend(ScriptedBackend.Says("Done."));
        var runner = new PlanRunner(_ => new HostAgent(
            backend, new FakeHost("web-01"), Fixtures.Settings, new StandingAnswer(true)));

        await runner.Run(
            new RunPlan([Phase("uptime")]),
            TestContext.Current.CancellationToken);

        backend.Requests[0].Tools.Select(tool => tool.Name).ShouldContain(AssistTools.RunCommand);
    }

    [Fact]
    public async Task EveryCommandInAPlanStillMeetsTheGate()
    {
        // Running the plan is consent to *the plan*, not a standing pass: a
        // command that writes still stops for a person, exactly as it would in
        // the pane about one host.
        var gate = new RecordingGate(answer: false);
        var host = new FakeHost("web-01");
        var runner = new PlanRunner(_ => new HostAgent(
            new ScriptedBackend(ScriptedBackend.Runs("apt-get install -y nginx"), ScriptedBackend.Says("Refused.")),
            host,
            Fixtures.Settings,
            gate));

        await runner.Run(
            new RunPlan([Phase("apt-get install -y nginx")]),
            TestContext.Current.CancellationToken);

        gate.Asked.ShouldHaveSingleItem().Command.ShouldBe("apt-get install -y nginx");
        host.Ran.ShouldBeEmpty();
    }

    [Fact]
    public async Task APhaseWhoseHostRanNothingIsNotCalledDone()
    {
        // The other half of the report: the plan looked like it had worked.
        // A host that answered in prose and ran none of its phase's commands
        // has not completed the phase, however confidently it says so.
        var host = new FakeHost("web-01");
        var runner = new PlanRunner(_ => new HostAgent(
            new ScriptedBackend(ScriptedBackend.Says("I have installed nginx.")),
            host,
            Fixtures.Settings,
            new StandingAnswer(true)));

        var result = await runner.Run(
            new RunPlan([Phase("apt-get install -y nginx")]),
            TestContext.Current.CancellationToken);

        host.Ran.ShouldBeEmpty();
        result.Phases.Single().Outcome.ShouldNotBe(PhaseOutcome.Completed);
    }
}
