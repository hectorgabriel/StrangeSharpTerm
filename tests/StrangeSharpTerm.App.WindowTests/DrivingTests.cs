using StrangeSharpTerm.App.ViewModels;
using StrangeSharpTerm.Assist;
using StrangeSharpTerm.Transport;

namespace StrangeSharpTerm.App.WindowTests;

/// <summary>
/// Which host an assistant has hold of, as the window learns it.
///
/// Either assistant says so on the way into every command and again on the way
/// out; the pane turns that into the set of tiles to mark. What the mark looks
/// like is <c>SplitLayoutTests</c>; this is that the right panes are named, and
/// that they stop being named.
///
/// Both are here together on purpose: the window takes one path for the two of
/// them, so a change that breaks the single-host side fails beside the fleet
/// one rather than somewhere else.
/// </summary>
[Collection("window")]
public class DrivingTests
{
    [Fact]
    public void ThePaneSaysWhichHostIsBeingWorkedOnAndWhenItIsDone()
    {
        Headless.Run(() =>
        {
            var model = new OrchestratorViewModel(
                new Canned(
                    Canned.FleetRuns("web-01", "df -h /", "c1"),
                    Canned.Says("98% full.")),
                [new TargetRow { Alias = "web-01", IsConnected = true, IsChosen = true }],
                _ => null,
                _ => new Answering("web-01"));

            var driving = new List<AssistStep>();
            model.Driving += (_, step) => driving.Add(step);

            model.MayRunCommands = true;
            model.Instruction = "how full is the disk?";
            Headless.Finish(model.RunCommand.ExecuteAsync(null));

            // In on the way to the command, out on the way back.
            driving.Count.ShouldBe(2);
            driving[0].ShouldSatisfyAllConditions(
                () => driving[0].Host.ShouldBe("web-01"),
                () => driving[0].Command.ShouldBe("df -h /"),
                () => driving[0].Running.ShouldBeTrue());
            driving[1].Running.ShouldBeFalse();
            driving[1].ExitStatus.ShouldBe(0);
        });
    }

    /// <summary>
    /// The one-host panel narrates what it runs, exactly as the orchestrator
    /// does -- which is the point: a pane beside a conversation should not look
    /// idle while the assistant works through twelve commands on that host.
    /// </summary>
    [Fact]
    public void TheOneHostPanelSaysWhatItIsRunningAndWhenItIsDone()
    {
        Headless.Run(() =>
        {
            var settings = new AssistSettings();
            AssistantViewModel? model = null;
            model = new AssistantViewModel(
                new HostAgent(
                    new Canned(
                        Canned.Runs("df -h /", "how full", "c1"),
                        Canned.Says("98% full.")),
                    new Answering("web-01"),
                    settings,
                    new Late(() => model!)),
                settings,
                _ => { });

            var driving = new List<AssistStep>();
            model.Driving += (_, step) => driving.Add(step);

            model.MayRunCommands = true;
            model.Question = "how full is the disk?";
            Headless.Finish(model.AskCommand.ExecuteAsync(null));

            // In on the way to the command, out on the way back -- the same
            // pair, in the same order, as the fleet run above.
            driving.Count.ShouldBe(2);
            driving[0].ShouldSatisfyAllConditions(
                () => driving[0].Host.ShouldBe("web-01"),
                () => driving[0].Command.ShouldBe("df -h /"),
                () => driving[0].Running.ShouldBeTrue());
            driving[1].Running.ShouldBeFalse();
            driving[1].ExitStatus.ShouldBe(0);
        });
    }

    /// <summary>
    /// A plan's commands are narrated to the panes too.
    ///
    /// They were not: Ask mode reaches its hosts through the fleet agent, whose
    /// steps the pane was listening to, while a plan reaches each host through
    /// an agent of its own that nobody was listening to at all. So the one run
    /// most worth watching -- commands a model wrote, going out to several
    /// machines in order -- was the one where every open pane sat looking idle.
    /// </summary>
    [Fact]
    public void ThePaneSaysWhatAPlanIsRunningOnEachHost()
    {
        Headless.Run(() =>
        {
            const string plan = """
            {"phases":[{"name":"Check the disk","hosts":["web-01"],"commands":["df -h /"]}]}
            """;
            var model = new OrchestratorViewModel(
                new Canned(Canned.Says(plan)),
                [new TargetRow { Alias = "web-01", IsConnected = true, IsChosen = true }],
                alias => new HostAgent(
                    new Canned(
                        Canned.Runs("df -h /", "how full", "c1"),
                        Canned.Says("98% full.")),
                    new Answering(alias),
                    new AssistSettings(),
                    new StandingAnswer(true)));

            var driving = new List<AssistStep>();
            model.Driving += (_, step) => driving.Add(step);

            model.Mode = OrchestratorMode.Plan;
            model.MayRunCommands = true;
            model.Instruction = "how full is the disk?";

            // Writing the plan runs nothing, so nothing is narrated yet.
            Headless.Finish(model.RunCommand.ExecuteAsync(null));
            model.Phases.ShouldHaveSingleItem();
            driving.ShouldBeEmpty();

            // And running it narrates, in and out, exactly as Ask mode does.
            Headless.Finish(model.RunCommand.ExecuteAsync(null));

            driving.Count.ShouldBe(2);
            driving[0].ShouldSatisfyAllConditions(
                () => driving[0].Host.ShouldBe("web-01"),
                () => driving[0].Command.ShouldBe("df -h /"),
                () => driving[0].Running.ShouldBeTrue());
            driving[1].Running.ShouldBeFalse();
            driving[1].ExitStatus.ShouldBe(0);
        });
    }

    /// <summary>The pane is its own gate, and cannot be built before the agent it holds.</summary>
    private sealed class Late(Func<ICommandGate> gate) : ICommandGate
    {
        public Task<bool> Allow(PendingCommand command, CancellationToken cancellationToken = default) =>
            gate().Allow(command, cancellationToken);

        public Task<ToolApproval> Allow(PendingToolCall call, CancellationToken cancellationToken = default) =>
            gate().Allow(call, cancellationToken);
    }

    private sealed class Answering(string alias) : IHostAccess
    {
        public string Alias => alias;

        public Task<HostSnapshot> Look(bool metrics, bool tail, int lines, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HostSnapshot());

        public Task<CommandOutcome> Run(string command, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CommandOutcome(0, "/dev/sda1 49G 46G 1.2G 98% /"));
    }

    private sealed class Canned(params IReadOnlyList<AssistEvent>[] turns) : IAssistBackend
    {
        private int _turn;

        public string ProviderName => "Canned";

        public string Model => "canned-1";

        public static IReadOnlyList<AssistEvent> Says(string text) =>
            [new AssistEvent.Say(text), new AssistEvent.Finished(AssistStop.EndTurn)];

        public static IReadOnlyList<AssistEvent> Runs(string command, string why, string id) =>
        [
            new AssistEvent.Call(new AssistToolCall(
                id,
                AssistTools.RunCommand,
                System.Text.Json.JsonSerializer.Serialize(new { command, why }))),
            new AssistEvent.Finished(AssistStop.ToolUse),
        ];

        public static IReadOnlyList<AssistEvent> FleetRuns(string host, string command, string id) =>
        [
            new AssistEvent.Call(new AssistToolCall(
                id,
                AssistTools.RunCommand,
                System.Text.Json.JsonSerializer.Serialize(new { host, command, why = "looking" }))),
            new AssistEvent.Finished(AssistStop.ToolUse),
        ];

        public async IAsyncEnumerable<AssistEvent> Stream(
            AssistRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
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
