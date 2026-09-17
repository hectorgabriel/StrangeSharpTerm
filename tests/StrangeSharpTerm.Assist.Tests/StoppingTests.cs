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

    /// <summary>
    /// A stopped question says so, rather than coming back as one that answered
    /// with nothing -- which is what a caller renders as a failure.
    /// </summary>
    [Fact]
    public async Task AStoppedQuestionSaysItWasStopped()
    {
        var agent = new HostAgent(new Hangs(), new FakeHost("web-01"), Fixtures.Settings, new RecordingGate());
        using var stopping = new CancellationTokenSource();

        var asking = agent.Ask("check the journal", null, stopping.Token);
        await stopping.CancelAsync();
        var answer = await asking;

        answer.Stopped.ShouldBeTrue();
        answer.Failed.ShouldBeFalse();
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
