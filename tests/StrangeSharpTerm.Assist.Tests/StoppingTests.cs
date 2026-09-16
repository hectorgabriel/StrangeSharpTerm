using System.Runtime.CompilerServices;
using StrangeSharpTerm.Assist;

namespace StrangeSharpTerm.Assist.Tests;

/// <summary>
/// What a run says about itself when it is interrupted, or when it runs out of
/// turns.
///
/// Both used to come out as "failed — It returned nothing", which is the same
/// sentence a host that genuinely broke produces. Pressing Stop on a run across
/// ten hosts then accused all ten of failing, and a question that spent its
/// whole budget threw away everything it had found on the way.
/// </summary>
public class StoppingTests
{
    /// <summary>A provider that never answers, like one mid-stream when Stop is pressed.</summary>
    private sealed class Hangs : IAssistBackend
    {
        public string ProviderName => "Hangs";

        public string Model => "hangs-1";

        public async IAsyncEnumerable<AssistEvent> Stream(
            AssistRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            yield break;
        }
    }

    [Fact]
    public async Task AStoppedHostSaysItWasStoppedRatherThanThatItFailed()
    {
        var agent = new HostAgent(new Hangs(), new FakeHost("web-01"), Fixtures.Settings, new RecordingGate());
        using var stopping = new CancellationTokenSource();
        var orchestrator = new Orchestrator(new ScriptedBackend(ScriptedBackend.Says("Summary.")));

        var running = orchestrator.Ask(
            "check the journal",
            [new OrchestratorTarget("web-01", () => agent)],
            mayRunCommands: false,
            stopping.Token);

        await stopping.CancelAsync();
        var run = await running;

        var finding = run.Findings.ShouldHaveSingleItem();
        finding.Outcome.ShouldBe(HostOutcome.Stopped);
        finding.Label.ShouldBe("stopped");
        finding.Text.ShouldBe("Stopped.");
    }

    /// <summary>
    /// Stopping a run must still produce the run.
    ///
    /// A host cancelled while it was queued behind the concurrency limit used to
    /// throw out of the fan-out entirely, taking the whole result with it: the
    /// hosts that had already answered were on screen but nothing was collated,
    /// and the planner was never told any of it had happened.
    /// </summary>
    [Fact]
    public async Task StoppingAQueuedHostStillReturnsWhatTheOthersFound()
    {
        var answered = new HostAgent(
            new ScriptedBackend(ScriptedBackend.Says("Affected.")),
            new FakeHost("web-01"),
            Fixtures.Settings,
            new RecordingGate());

        using var stopping = new CancellationTokenSource();
        var hanging = Enumerable.Range(0, AssistLimits.Concurrency + 2)
            .Select(index => new OrchestratorTarget(
                $"slow-{index}",
                () => new HostAgent(new Hangs(), new FakeHost($"slow-{index}"), Fixtures.Settings, new RecordingGate())))
            .ToList();

        var orchestrator = new Orchestrator(new ScriptedBackend(ScriptedBackend.Says("One host answered.")));
        var running = orchestrator.Ask(
            "check the journal",
            [new OrchestratorTarget("web-01", () => answered), .. hanging],
            mayRunCommands: false,
            stopping.Token);

        await stopping.CancelAsync();

        // It comes back at all, which is the point: no exception escapes.
        var run = await running;

        run.Findings.Count.ShouldBe(hanging.Count + 1);
        run.Findings.Single(finding => finding.Alias == "web-01").Outcome.ShouldBe(HostOutcome.Reported);
        run.Findings
            .Where(finding => finding.Alias != "web-01")
            .ShouldAllBe(finding => finding.Outcome == HostOutcome.Stopped);
    }

    /// <summary>
    /// A model that ends its last turn thinking rather than answering.
    ///
    /// Observed against a local Qwen through Ollama, and not rare there:
    /// <c>finish_reason: stop</c>, no tool calls, no content, and the conclusion
    /// sitting in the reasoning. Read back as an empty answer it became "It
    /// returned nothing" — a host that had worked the problem out, reported as
    /// one that broke.
    /// </summary>
    [Fact]
    public async Task AnAnswerLeftInTheThinkingIsStillAnAnswer()
    {
        var backend = new ScriptedBackend(
            [
                new AssistEvent.Reasoning("I should look at the disk first."),
                new AssistEvent.Call(new AssistToolCall(
                    "call_1",
                    AssistTools.RunCommand,
                    System.Text.Json.JsonSerializer.Serialize(new { command = "df -h /", why = "how full" }))),
                new AssistEvent.Finished(AssistStop.ToolUse),
            ],
            [
                new AssistEvent.Reasoning("The command returned 12, so the host has 12 cores."),
                new AssistEvent.Finished(AssistStop.EndTurn),
            ]);

        var host = new FakeHost("web-01") { Answer = _ => new CommandOutcome(0, "12") };
        var agent = new HostAgent(backend, host, Fixtures.Settings, new RecordingGate());

        var answer = await agent.Ask(
            "how many cores?",
            new AskOptions { MayRunCommands = true },
            TestContext.Current.CancellationToken);

        answer.Failed.ShouldBeFalse();
        answer.Text.ShouldBe("The command returned 12, so the host has 12 cores.");
    }

    /// <summary>
    /// And a host that ends that way reports as one that reported, which is the
    /// whole point: the fan-out used to call it failed.
    /// </summary>
    [Fact]
    public async Task AHostThatOnlyThoughtStillCountsAsReporting()
    {
        var backend = new ScriptedBackend(
            [
                new AssistEvent.Reasoning("Nothing is wrong with this one."),
                new AssistEvent.Finished(AssistStop.EndTurn),
            ]);
        var agent = new HostAgent(backend, new FakeHost("web-01"), Fixtures.Settings, new RecordingGate());

        var run = await new Orchestrator(new ScriptedBackend(ScriptedBackend.Says("All fine."))).Ask(
            "anything wrong?",
            [new OrchestratorTarget("web-01", () => agent)],
            mayRunCommands: false,
            TestContext.Current.CancellationToken);

        var finding = run.Findings.ShouldHaveSingleItem();
        finding.Outcome.ShouldBe(HostOutcome.Reported);
        finding.Text.ShouldBe("Nothing is wrong with this one.");
    }

    /// <summary>
    /// Real words still win. A turn that answered and then a turn that only
    /// thought must not replace the answer with the deliberation after it.
    /// </summary>
    [Fact]
    public async Task ContentBeatsThinkingWithinTheSameTurn()
    {
        var backend = new ScriptedBackend(
            [
                new AssistEvent.Reasoning("Working it out."),
                new AssistEvent.Say("The disk is 98% full."),
                new AssistEvent.Finished(AssistStop.EndTurn),
            ]);
        var agent = new HostAgent(backend, new FakeHost("web-01"), Fixtures.Settings, new RecordingGate());

        var answer = await agent.Ask(
            "how full is the disk?",
            new AskOptions(),
            TestContext.Current.CancellationToken);

        answer.Text.ShouldBe("The disk is 98% full.");
    }

    /// <summary>
    /// A question that runs out of turns keeps what it found.
    ///
    /// The ceiling is reached after twelve commands of real investigation, and
    /// returning nothing turns all of that into "It returned nothing" — the
    /// worst possible summary of the most work a host ever does.
    /// </summary>
    [Fact]
    public async Task RunningOutOfTurnsKeepsWhatWasFound()
    {
        // Says something, then asks for a command, over and over: it never
        // finishes of its own accord, so the ceiling is what stops it.
        var turns = Enumerable.Range(0, 40)
            .Select(index => (IReadOnlyList<AssistEvent>)
            [
                new AssistEvent.Say($"Looking at step {index}."),
                new AssistEvent.Call(new AssistToolCall(
                    $"call_{index}",
                    AssistTools.RunCommand,
                    System.Text.Json.JsonSerializer.Serialize(new { command = "ls /", why = "looking" }))),
                new AssistEvent.Finished(AssistStop.ToolUse),
            ])
            .ToArray();
        var backend = new ScriptedBackend(turns);

        var host = new FakeHost("web-01") { Answer = _ => new CommandOutcome(0, "bin etc var") };
        var agent = new HostAgent(backend, host, Fixtures.Settings, new RecordingGate());

        var answer = await agent.Ask(
            "what is on this disk?",
            new AskOptions { MayRunCommands = true },
            TestContext.Current.CancellationToken);

        answer.CommandsRun.ShouldBe(AssistLimits.CommandBudget);
        // Whatever it last said, rather than nothing at all.
        answer.Text.ShouldNotBeEmpty();
        answer.Text.ShouldContain("Looking at step");
    }
}
