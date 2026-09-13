using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StrangeSharpTerm.App.ViewModels;
using StrangeSharpTerm.App.Views;
using StrangeSharpTerm.Assist;
using StrangeSharpTerm.Transport;

namespace StrangeSharpTerm.App.WindowTests;

/// <summary>
/// The assistant and orchestrator panes, drawn.
///
/// These are here rather than in the plain tests because the agent runs off the
/// UI thread and posts its rows back: a test that watches a row arrive needs
/// something pumping the dispatcher, and <see cref="Headless"/> is it.
/// </summary>
[Collection("window")]
public class AssistantPaneTests
{
    [Fact]
    public void ACommandIsARowInTheSameListAsTheAnswer()
    {
        Headless.Run(() =>
        {
            var host = new Quiet("web-01");
            host.Answers["df -h /"] = "/dev/sda1 49G 46G 1.2G 98% /";

            var model = Pane(
                host,
                Canned.Runs("df -h /", "how full, and which device"),
                Canned.Says("It is 98% full."));
            var window = Show(new AssistantView(model));

            model.MayRunCommands = true;
            model.Question = "what is eating the disk?";
            Headless.Finish(model.AskCommand.ExecuteAsync(null));
            Settle(window);

            // What it actually ran should never require looking somewhere else.
            var shown = In<TextBlock>(window).Select(block => block.Text).ToArray();
            shown.ShouldContain("what is eating the disk?");
            shown.ShouldContain("df -h /");
            shown.ShouldContain("how full, and which device");
            shown.ShouldContain("It is 98% full.");
            // A read-only command ran without anybody being asked.
            shown.ShouldContain("auto");
        });
    }

    [Fact]
    public void AWritingCommandStopsAndTheRowIsTheQuestion()
    {
        Headless.Run(() =>
        {
            var model = Pane(
                new Quiet("web-01"),
                Canned.Runs("systemctl restart nginx", "restart it"),
                Canned.Says("Done."));
            var window = Show(new AssistantView(model));

            model.MayRunCommands = true;
            model.Question = "restart nginx";
            var asking = model.AskCommand.ExecuteAsync(null);

            // It stops at the gate: the command, its reason, and the two answers
            // are all on screen, and nothing else happens until one is pressed.
            Pump(() => model.Waiting is not null);
            Settle(window);

            var shown = In<TextBlock>(window).Select(block => block.Text).ToArray();
            shown.ShouldContain("systemctl restart nginx");
            shown.ShouldContain("Run it");
            shown.ShouldContain("No");
            model.Waiting.ShouldNotBeNull().Gate.ShouldNotBeEmpty();

            model.RefuseCommand.Execute(null);
            Headless.Finish(asking);
            Settle(window);

            model.Rows.Last(row => row.IsStep).State.ShouldBe("refused");

            // Drawn, not only computed. The row was put on screen while the
            // command was still waiting, so what this checks is that the one
            // already there changed -- a step is a record whose state changes as
            // it runs, and a row looked up by value is lost the moment it does.
            var after = In<TextBlock>(window).Select(block => block.Text).ToArray();
            after.ShouldContain("refused");
            after.ShouldNotContain("waiting for you");
        });
    }

    [Fact]
    public void FindingsReadInTheOrderTheHostsWereTickedNotTheOrderTheyAnswerIn()
    {
        Headless.Run(() =>
        {
            // web-01 takes its time; bastion is not connected and reports at
            // once. A list that reordered itself as each host finished would
            // come out differently on a second reading.
            var model = new OrchestratorViewModel(
                new Canned(Canned.Says("Summary.")),
                [
                    new TargetRow { Alias = "web-01", IsConnected = true, IsChosen = true },
                    new TargetRow { Alias = "bastion", IsConnected = false, IsChosen = true },
                    new TargetRow { Alias = "web-02", IsConnected = true, IsChosen = true },
                ],
                alias => alias == "bastion"
                    ? null
                    : new HostAgent(
                        new Slow($"{alias} is affected.", alias == "web-01" ? 80 : 10),
                        new Quiet(alias),
                        new AssistSettings(),
                        new StandingAnswer(true)));
            var window = Show(new OrchestratorView(model));

            model.Instruction = "is the journal filling the disk?";
            Headless.Finish(model.RunCommand.ExecuteAsync(null));
            Settle(window);

            model.Findings.Select(finding => finding.Alias).ShouldBe(["web-01", "bastion", "web-02"]);
            // And the question they are answers to is above them.
            In<TextBlock>(window).Select(block => block.Text)
                .ShouldContain("is the journal filling the disk?");
        });
    }

    /// <summary>A backend that takes its time, so the hosts finish out of order.</summary>
    private sealed class Slow(string answer, int milliseconds) : IAssistBackend
    {
        public string ProviderName => "Slow";

        public string Model => "slow-1";

        public async IAsyncEnumerable<AssistEvent> Stream(
            AssistRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(milliseconds, cancellationToken);
            yield return new AssistEvent.Say(answer);
            yield return new AssistEvent.Finished(AssistStop.EndTurn);
        }
    }

    [Fact]
    public void ASuggestedCommandIsTypedAndNotRun()
    {
        Headless.Run(() =>
        {
            var typed = new List<string>();
            var model = Pane(new Quiet("web-01"), Canned.Says("Try this:\n\n```sh\ndf -h /\n```"), stage: typed.Add);
            var window = Show(new AssistantView(model));

            model.Question = "how full is it?";
            Headless.Finish(model.AskCommand.ExecuteAsync(null));
            Settle(window);

            In<TextBlock>(window).Select(block => block.Text).ShouldContain("Type it");

            model.StageCommand.Execute(model.Rows.Last().Staged.Single());

            // No newline: the person reads it and presses Return.
            typed.ShouldBe(["df -h /"]);
        });
    }

    [Fact]
    public void TheHeaderNamesWhoIsAnswering()
    {
        Headless.Run(() =>
        {
            var model = Pane(new Quiet("web-01"), Canned.Says("."));
            var window = Show(new AssistantView(model));

            var shown = In<TextBlock>(window).Select(block => block.Text).ToArray();
            shown.ShouldContain("Assistant");
            shown.ShouldContain("web-01");
            shown.ShouldContain("Canned · canned-1");
        });
    }

    [Fact]
    public void TheDisclosureShowsTheRequestItself()
    {
        Headless.Run(() =>
        {
            var host = new Quiet("web-01")
            {
                Snapshot = new HostSnapshot(
                    "Linux 6.1.0 x86_64",
                    new ServerMetrics { Uptime = "3 days" },
                    "deploy@web-01:~$ export API_TOKEN=abcdefghijkl"),
            };
            var model = Pane(host, Canned.Says("."));
            Show(new AssistantView(model));

            Headless.Finish(model.LookCommand.ExecuteAsync(null));

            model.Preview.ShouldContain("Host: web-01");
            model.Preview.ShouldContain("up 3 days");
            model.Preview.ShouldContain("API_TOKEN");
            model.Preview.ShouldNotContain("abcdefghijkl");
            model.RedactionNote.ShouldBe("1 secret removed.");
        });
    }

    [Fact]
    public void EveryHostIsListedAboveTheCollatedAnswerIncludingTheOnesNotAsked()
    {
        Headless.Run(() =>
        {
            var model = new OrchestratorViewModel(
                new Canned(Canned.Says("Two of the three are affected.")),
                [
                    new TargetRow { Alias = "web-01", IsConnected = true, IsChosen = true },
                    new TargetRow { Alias = "web-02", IsConnected = true, IsChosen = true },
                    new TargetRow { Alias = "bastion", IsConnected = false, IsChosen = true },
                ],
                alias => alias == "bastion"
                    ? null
                    : new HostAgent(
                        new Canned(Canned.Says($"{alias} is affected.")),
                        new Quiet(alias),
                        new AssistSettings(),
                        new StandingAnswer(true)));
            var window = Show(new OrchestratorView(model));

            model.Instruction = "is the journal filling the disk?";
            Headless.Finish(model.RunCommand.ExecuteAsync(null));
            Settle(window);

            var shown = In<TextBlock>(window).Select(block => block.Text).ToArray();
            shown.ShouldContain("web-01");
            shown.ShouldContain("bastion");
            shown.ShouldContain("not asked");
            shown.ShouldContain("reported");
            // A summary that read as though it covered every host is the failure
            // this layout exists to prevent.
            shown.ShouldContain("ACROSS ALL HOSTS");
            shown.ShouldContain("Two of the three are affected.");
        });
    }

    [Fact]
    public void APlanIsShownInFullAndNothingRunsUntilItIsRun()
    {
        Headless.Run(() =>
        {
            const string plan = """
            {"phases":[
              {"name":"Prepare every node","hosts":["web-01","web-02"],"task":"Install containerd."},
              {"name":"Initialise the control plane","hosts":["web-01"],"task":"Run kubeadm init.","capture":"join_command"},
              {"name":"Join the workers","hosts":["web-02"],"task":"Join with {{join_command}}"}
            ]}
            """;
            var asked = new List<string>();
            var model = new OrchestratorViewModel(
                new Canned(Canned.Says(plan)),
                [
                    new TargetRow { Alias = "web-01", IsConnected = true, IsChosen = true },
                    new TargetRow { Alias = "web-02", IsConnected = true, IsChosen = true },
                ],
                alias =>
                {
                    asked.Add(alias);
                    return null;
                });
            var window = Show(new OrchestratorView(model));

            model.Mode = OrchestratorMode.Plan;
            model.Instruction = "build a cluster";
            Headless.Finish(model.RunCommand.ExecuteAsync(null));
            Settle(window);

            // Every phase, its hosts, and the words each host will be given.
            var shown = In<TextBlock>(window).Select(block => block.Text).ToArray();
            shown.ShouldContain("Prepare every node");
            shown.ShouldContain("Install containerd.");
            shown.ShouldContain("yields join_command");
            shown.ShouldContain("uses join_command");
            shown.ShouldContain("Run the plan");

            // It wrote the plan and ran none of it.
            asked.ShouldBeEmpty();
            model.Phases.Count.ShouldBe(3);
            model.Phases.ShouldAllBe(phase => phase.IsEnabled);
        });
    }

    [Fact]
    public void APlanThatWouldNotBeSafeIsRefusedRatherThanShown()
    {
        Headless.Run(() =>
        {
            var model = new OrchestratorViewModel(
                new Canned(Canned.Says("""{"phases":[{"name":"Do it","hosts":["db-primary"],"task":"x"}]}""")),
                [new TargetRow { Alias = "web-01", IsConnected = true, IsChosen = true }],
                _ => null);
            var window = Show(new OrchestratorView(model));

            model.Mode = OrchestratorMode.Plan;
            model.Instruction = "build a cluster";
            Headless.Finish(model.RunCommand.ExecuteAsync(null));
            Settle(window);

            model.Phases.ShouldBeEmpty();
            // The refusal says which phase and why, where a plan would have been.
            model.Refusal.ShouldNotBeNull().ShouldContain("db-primary");
            In<TextBlock>(window).Select(block => block.Text).ShouldContain(model.Refusal);
        });
    }

    private static AssistantViewModel Pane(
        IHostAccess host,
        IReadOnlyList<AssistEvent> first,
        IReadOnlyList<AssistEvent>? second = null,
        Action<string>? stage = null)
    {
        var settings = new AssistSettings();
        var backend = second is null ? new Canned(first) : new Canned(first, second);
        AssistantViewModel? model = null;
        model = new AssistantViewModel(
            new HostAgent(backend, host, settings, new Late(() => model!)),
            settings,
            stage ?? (_ => { }));
        return model;
    }

    private static Window Show(Control pane)
    {
        var window = new Window { Content = pane, Width = 720, Height = 700 };
        window.Show();
        Settle(window);
        return window;
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    /// <summary>Pumps until something becomes true, or fails rather than hanging.</summary>
    private static void Pump(Func<bool> until, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!until())
        {
            Dispatcher.UIThread.RunJobs();
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"it did not happen within {seconds}s");
            Thread.Sleep(1);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static IEnumerable<T> In<T>(Visual root) where T : Visual => root.GetVisualDescendants().OfType<T>();

    private sealed class Late(Func<ICommandGate> gate) : ICommandGate
    {
        public Task<bool> Allow(PendingCommand command, CancellationToken cancellationToken = default) =>
            gate().Allow(command, cancellationToken);
    }

    private sealed class Quiet(string alias) : IHostAccess
    {
        public string Alias => alias;

        internal HostSnapshot Snapshot { get; set; } = new();

        internal Dictionary<string, string> Answers { get; } = [];

        public Task<HostSnapshot> Look(bool metrics, bool tail, int lines, CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);

        public Task<CommandOutcome> Run(string command, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CommandOutcome(0, Answers.GetValueOrDefault(command, "")));
    }

    private sealed class Canned(params IReadOnlyList<AssistEvent>[] turns) : IAssistBackend
    {
        private int _turn;

        public string ProviderName => "Canned";

        public string Model => "canned-1";

        internal static IReadOnlyList<AssistEvent> Says(string text) =>
            [new AssistEvent.Say(text), new AssistEvent.Finished(AssistStop.EndTurn)];

        internal static IReadOnlyList<AssistEvent> Runs(string command, string why) =>
        [
            new AssistEvent.Call(new AssistToolCall(
                "c1",
                AssistTools.RunCommand,
                System.Text.Json.JsonSerializer.Serialize(new { command, why }))),
            new AssistEvent.Finished(AssistStop.ToolUse),
        ];

        public async IAsyncEnumerable<AssistEvent> Stream(
            AssistRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
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
