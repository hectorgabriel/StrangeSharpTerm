using StrangeSharpTerm.App.ViewModels;
using StrangeSharpTerm.Assist;
using StrangeSharpTerm.Transport;

namespace StrangeSharpTerm.App.WindowTests;

/// <summary>
/// Which host the fleet assistant has hold of, as the window learns it.
///
/// The agent says so on the way into every command and again on the way out;
/// the pane turns that into the set of tiles to mark. What the mark looks like
/// is <c>SplitLayoutTests</c>; this is that the right panes are named, and that
/// they stop being named.
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

            var driving = new List<FleetStep>();
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
