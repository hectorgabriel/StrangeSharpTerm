using System.Text.Json;
using StrangeSharpTerm.Assist;

namespace StrangeSharpTerm.Assist.Tests;

/// <summary>
/// One conversation about several hosts.
///
/// What this replaced gave every ticked host its own conversation and then made
/// a further call to collate what they each reported: nine conversations for
/// eight hosts, each told what the others found second-hand. This is one
/// assistant that chooses where to look.
/// </summary>
public class FleetAgentTests
{
    private static IReadOnlyList<AssistEvent> Runs(string host, string command, string id) =>
    [
        new AssistEvent.Call(new AssistToolCall(
            id,
            AssistTools.RunCommand,
            JsonSerializer.Serialize(new { host, command, why = "looking" }))),
        new AssistEvent.Finished(AssistStop.ToolUse),
    ];

    private static (FleetAgent Agent, Dictionary<string, FakeHost> Hosts, ScriptedBackend Backend) Fleet(
        ICommandGate gate, params IReadOnlyList<AssistEvent>[] turns)
    {
        var hosts = new[] { "web-01", "web-02", "db-primary" }
            .ToDictionary(alias => alias, alias => new FakeHost(alias));
        var backend = new ScriptedBackend(turns);
        var agent = new FleetAgent(
            backend,
            [.. hosts.Select(pair => new FleetHost(pair.Key, () => pair.Value))],
            gate);
        return (agent, hosts, backend);
    }

    [Fact]
    public async Task OneConversationReachesWhicheverHostItChooses()
    {
        var (agent, hosts, backend) = Fleet(
            new RecordingGate(),
            Runs("web-01", "df -h /", "c1"),
            Runs("db-primary", "df -h /", "c2"),
            ScriptedBackend.Says("web-01 is full; db-primary is not."));

        var answer = await agent.Ask("is the disk full anywhere?", mayRunCommands: true, TestContext.Current.CancellationToken);

        answer.CommandsRun.ShouldBe(2);
        answer.Text.ShouldBe("web-01 is full; db-primary is not.");

        // It went to the two it chose, and left the third alone.
        hosts["web-01"].Ran.ShouldHaveSingleItem().ShouldBe("df -h /");
        hosts["db-primary"].Ran.ShouldHaveSingleItem().ShouldBe("df -h /");
        hosts["web-02"].Ran.ShouldBeEmpty();

        // One conversation, not one per host: three requests for three turns.
        backend.Requests.Count.ShouldBe(3);
    }

    /// <summary>
    /// A result says which machine it came from. One conversation holds every
    /// host's output, and a result that did not say would be one the model has
    /// to guess about.
    /// </summary>
    [Fact]
    public async Task EveryResultNamesItsHost()
    {
        var (_, hosts, backend) = Fleet(
            new RecordingGate(),
            Runs("web-02", "uptime", "c1"),
            ScriptedBackend.Says("Fine."));
        hosts["web-02"].Answer = _ => new CommandOutcome(0, "up 3 days");

        var agent = new FleetAgent(
            backend,
            [.. hosts.Select(pair => new FleetHost(pair.Key, () => pair.Value))],
            new RecordingGate());
        await agent.Ask("how long up?", mayRunCommands: true, TestContext.Current.CancellationToken);

        var results = backend.Requests[^1].Messages.SelectMany(message => message.ToolResults).ToArray();
        results.ShouldHaveSingleItem().Output.ShouldStartWith("web-02:");
    }

    /// <summary>A host it invented is told about rather than run somewhere else.</summary>
    [Fact]
    public async Task AHostThatIsNotInTheRunIsRefusedRatherThanSubstituted()
    {
        var (agent, hosts, backend) = Fleet(
            new RecordingGate(),
            Runs("web-99", "rm -rf /", "c1"),
            ScriptedBackend.Says("Sorry."));

        await agent.Ask("tidy up", mayRunCommands: true, TestContext.Current.CancellationToken);

        hosts.Values.ShouldAllBe(host => host.Ran.Count == 0);
        var result = backend.Requests[^1].Messages.SelectMany(message => message.ToolResults).ShouldHaveSingleItem();
        result.Failed.ShouldBeTrue();
        result.Output.ShouldContain("no host called web-99");
        result.Output.ShouldContain("web-01");
    }

    /// <summary>
    /// The gate is the same one, and it is told which host is being asked about
    /// -- approving a restart means nothing until you know whose.
    /// </summary>
    [Fact]
    public async Task TheGateIsAskedPerCommandAndKnowsTheHost()
    {
        var gate = new RecordingGate(answer: false);
        var (agent, hosts, _) = Fleet(
            gate,
            Runs("db-primary", "systemctl restart postgres", "c1"),
            ScriptedBackend.Says("Refused, so nothing changed."));

        await agent.Ask("restart postgres", mayRunCommands: true, TestContext.Current.CancellationToken);

        gate.Asked.ShouldHaveSingleItem().Host.ShouldBe("db-primary");
        hosts["db-primary"].Ran.ShouldBeEmpty();
    }

    /// <summary>
    /// A run is bounded by the run's budget. There are no per-host conversations
    /// to give twelve commands each to any more.
    /// </summary>
    [Fact]
    public async Task ItSpendsTheRunBudgetAndThenSummarises()
    {
        var turns = Enumerable.Range(0, AssistLimits.RunBudget + 5)
            .Select(index => Runs("web-01", $"echo {index}", $"c{index}"))
            .ToList();
        turns.Add(ScriptedBackend.Says("That is all I could look at."));

        var (agent, hosts, _) = Fleet(new RecordingGate(), [.. turns]);
        var answer = await agent.Ask("look at everything", mayRunCommands: true, TestContext.Current.CancellationToken);

        answer.CommandsRun.ShouldBe(AssistLimits.RunBudget);
        hosts["web-01"].Ran.Count.ShouldBe(AssistLimits.RunBudget);
    }

    /// <summary>With the tool withheld it can still answer, and cannot reach anything.</summary>
    [Fact]
    public async Task WithoutTheToolItReachesNothing()
    {
        var (agent, hosts, backend) = Fleet(new RecordingGate(), ScriptedBackend.Says("I would need to look."));

        await agent.Ask("is the disk full?", mayRunCommands: false, TestContext.Current.CancellationToken);

        backend.Requests.ShouldHaveSingleItem().Tools.ShouldBeEmpty();
        hosts.Values.ShouldAllBe(host => host.Ran.Count == 0);
    }

    /// <summary>
    /// A pane needs to know which host is being driven, and when it is let go
    /// of again, or there is no way to see a fleet being worked on.
    /// </summary>
    [Fact]
    public async Task ItSaysWhichHostItHasAndWhenItLetsGo()
    {
        var (agent, _, _) = Fleet(
            new RecordingGate(),
            Runs("web-01", "df -h /", "c1"),
            ScriptedBackend.Says("Done."));

        var steps = new List<FleetStep>();
        agent.Working += (_, step) => steps.Add(step);

        await agent.Ask("how full?", mayRunCommands: true, TestContext.Current.CancellationToken);

        steps.Count.ShouldBe(2);
        steps[0].ShouldSatisfyAllConditions(
            () => steps[0].Host.ShouldBe("web-01"),
            () => steps[0].Command.ShouldBe("df -h /"),
            () => steps[0].Running.ShouldBeTrue());
        steps[1].Running.ShouldBeFalse();
        steps[1].ExitStatus.ShouldBe(0);
    }
}
