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
            mayRunCommands: false,
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
            Plan(phase), mayRunCommands: true, TestContext.Current.CancellationToken);

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
            .Run(plan, mayRunCommands: false, TestContext.Current.CancellationToken);

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
            mayRunCommands: false,
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
                mayRunCommands: false,
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
            .Run(plan, mayRunCommands: false, TestContext.Current.CancellationToken);

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
                mayRunCommands: false,
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
            mayRunCommands: true,
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
            mayRunCommands: true,
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
            mayRunCommands: false,
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
        var backend = new RecordingBackend(asked, answer);
        return new HostAgent(backend, new FakeHost("host"), Fixtures.Settings, new StandingAnswer(true));
    }
}
