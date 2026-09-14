using System.Runtime.CompilerServices;
using StrangeSharpTerm.App.Assistant;
using StrangeSharpTerm.App.ViewModels;
using StrangeSharpTerm.Assist;
using StrangeSharpTerm.Model;
using StrangeSharpTerm.Transport;

namespace StrangeSharpTerm.App.Tests;

/// <summary>
/// The two assistant panes, as far as they can be checked without a dispatcher.
///
/// Anything that watches a row arrive is in the window tests instead: the agent
/// runs off the UI thread and posts its rows back, and there is nothing in a
/// plain test to pump that.
/// </summary>
public class AssistantTests
{
    [Fact]
    public async Task AnAssistantOpensBesideTheSessionItIsAbout()
    {
        // The reason it is a pane and not a window: a conversation about what
        // the terminal is showing, beside the terminal.
        var fixture = new Fixture();
        fixture.Shell.Inventory.Selection = fixture.Host.Id;
        await fixture.Shell.ConnectSelectedCommand.ExecuteAsync(null);

        fixture.Shell.OpenAssistantCommand.Execute(null);

        fixture.Shell.Tabs.ShouldHaveSingleItem();
        fixture.Shell.Panes.Count.ShouldBe(2);
        fixture.Shell.Panes[^1].IsActive.ShouldBeTrue();
    }

    [Fact]
    public void WithNoKeyItSaysSoRatherThanOpeningAPaneThatCannotAnswer()
    {
        var fixture = new Fixture(hasKey: false);
        fixture.Shell.Inventory.Selection = fixture.Host.Id;

        fixture.Shell.OpenAssistantCommand.Execute(null);

        fixture.Shell.Panes.ShouldBeEmpty();
        fixture.Shell.Failure.ShouldNotBeNull().ShouldContain("Settings");
    }

    [Fact]
    public void TheAssistantNeedsAHostChosen()
    {
        var fixture = new Fixture();

        fixture.Shell.OpenAssistantCommand.CanExecute(null).ShouldBeFalse();

        fixture.Shell.Inventory.Selection = fixture.Host.Id;
        fixture.Shell.OpenAssistantCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public void TheOrchestratorBelongsToNoHost()
    {
        // Disconnecting a host closes the panes belonging to it, and a fan-out
        // across eight servers should not vanish because one was disconnected.
        var fixture = new Fixture();

        fixture.Shell.AskSeveralHostsCommand.Execute(null);

        fixture.Shell.Workspace.Panes.ShouldHaveSingleItem().ConnectionId.ShouldBeNull();
    }

    /// <summary>
    /// The host detail shares its row with the panes and is drawn after them, so
    /// whenever it is showing it covers whatever is open. That is deliberate for
    /// a terminal belonging to a different host from the selected one. The
    /// orchestrator belongs to no host, so "a different host" does not apply to
    /// it, and treating null as different put the detail panel on top of it.
    /// </summary>
    [Fact]
    public void TheHostDetailStandsAsideForTheOrchestrator()
    {
        var fixture = new Fixture();
        fixture.Shell.Inventory.Selection = fixture.Host.Id;

        // With nothing open, the selected host explains itself.
        fixture.Shell.ShowsDetail.ShouldBeTrue();

        fixture.Shell.AskSeveralHostsCommand.Execute(null);

        fixture.Shell.ShowsDetail.ShouldBeFalse();
    }

    /// <summary>
    /// A fresh agent was built for every phase and every run, so on a given host
    /// phase three had never heard of phase one -- the only thing that crossed
    /// between them was the single captured value. One agent per host, held for
    /// the life of the pane, is what makes a plan a sequence rather than a set of
    /// separate errands.
    /// </summary>
    [Fact]
    public async Task EachHostGetsOneAgentAndKeepsIt()
    {
        var built = new List<string>();
        var model = new OrchestratorViewModel(
            new Canned(Canned.Says("Summary."), Canned.Says("Summary again.")),
            [new TargetRow { Alias = "web-01", IsConnected = true, IsChosen = true }],
            alias =>
            {
                built.Add(alias);
                return new HostAgent(
                    new Canned(Canned.Says($"{alias} says so."), Canned.Says($"{alias} still says so.")),
                    new Silent(alias),
                    new AssistSettings(),
                    new StandingAnswer(true));
            });

        model.Instruction = "is the journal filling the disk?";
        await model.RunCommand.ExecuteAsync(null);
        model.Instruction = "and is it still?";
        await model.RunCommand.ExecuteAsync(null);

        // Asked twice, built once: the second question reached the same
        // conversation as the first.
        built.ShouldBe(["web-01"]);
    }

    [Fact]
    public void TheOrchestratorOffersEveryHostAndSaysWhichAreConnected()
    {
        var fixture = new Fixture();
        fixture.Sessions.Open.Add("web-01");

        var model = fixture.Orchestrator();

        model.Targets.Select(target => target.Alias).ShouldBe(["db-primary", "web-01"]);
        model.Targets.Single(target => target.Alias == "web-01").IsConnected.ShouldBeTrue();
        model.Targets.Single(target => target.Alias == "db-primary").IsConnected.ShouldBeFalse();
    }

    [Fact]
    public async Task ThePreviewIsFilledInBeforeAnythingIsAsked()
    {
        var host = new Silent("web-01")
        {
            Snapshot = new HostSnapshot("Linux 6.1.0", new ServerMetrics { Uptime = "3 days" }, "DB_PASSWORD=hunter2"),
        };
        var model = new AssistantViewModel(
            new HostAgent(new Canned(Canned.Says(".")), host, new AssistSettings(), new StandingAnswer(true)),
            new AssistSettings(),
            _ => { });

        await model.LookCommand.ExecuteAsync(null);

        model.Preview.ShouldContain("Host: web-01");
        model.Preview.ShouldNotContain("hunter2");
        model.RedactionNote.ShouldBe("1 secret removed.");
    }

    [Fact]
    public void ThePaneHeaderNamesTheProviderAndModel()
    {
        var model = new AssistantViewModel(
            new HostAgent(new Canned(), new Silent("web-01"), new AssistSettings(), new StandingAnswer(true)),
            new AssistSettings(),
            _ => { });

        // Which backend is in use decides where this conversation's terminal
        // output is being sent, and that should be readable without opening
        // Settings.
        model.Answering.ShouldBe("Canned · canned-1");
    }

    [Fact]
    public void RunningCommandsIsOffUnlessTheSettingsSayOtherwise()
    {
        var agent = new HostAgent(new Canned(), new Silent("web-01"), new AssistSettings(), new StandingAnswer(true));

        new AssistantViewModel(agent, new AssistSettings(), _ => { }).MayRunCommands.ShouldBeFalse();
        new AssistantViewModel(agent, new AssistSettings { AllowCommandsByDefault = true }, _ => { })
            .MayRunCommands.ShouldBeTrue();
    }

    [Fact]
    public async Task ThePaneIsItsOwnGateAndTheWaitingRowIsTheQuestion()
    {
        var model = new AssistantViewModel(
            new HostAgent(new Canned(), new Silent("web-01"), new AssistSettings(), new StandingAnswer(true)),
            new AssistSettings(),
            _ => { });

        var answered = model.Allow(
            new PendingCommand("web-01", "rm -rf /tmp", "clean up", "rm deletes files", true),
            TestContext.Current.CancellationToken);

        // Nothing happens until a person answers.
        answered.IsCompleted.ShouldBeFalse();
        model.RefuseCommand.Execute(null);
        (await answered).ShouldBeFalse();
    }

    /// <summary>A shell, an assistant, and an orchestrator, none of which needs a server.</summary>
    private sealed class Fixture
    {
        internal Fixture(IAssistBackend? backend = null, bool hasKey = true)
        {
            Host = new Connection { Name = "web-01", Hostname = "web-01.example.com" };
            var other = new Connection { Name = "db-primary", Hostname = "db-01.example.com" };
            Sessions = new FakeSessions
            {
                OnCommands = _ => new Answering(),
                OnHealth = _ => new Quiet(),
            };

            var answer = backend ?? new Canned(Canned.Says("It is 98% full."));
            Shell = new ShellViewModel(
                new InventoryViewModel(null, new InventoryTree(connections: [Host, other])),
                Sessions,
                (_, _) => new Avalonia.Controls.Border(),
                // No key is what a fresh install looks like, and the shell has
                // to say so rather than open a pane that cannot answer.
                backends: _ => hasKey ? answer : null,
                // A stand-in for the orchestrator control, for the same reason the
                // terminal gets one: building the real one runs its XAML, and
                // these tests have no application around it to run it in. Done
                // against the real control it raced Avalonia's weak-event
                // bookkeeping and failed on Windows with a NullReferenceException
                // in WeakHashList, on whichever test happened to build it first.
                orchestratorView: model => new Avalonia.Controls.Border { DataContext = model });
        }

        internal Connection Host { get; }

        internal FakeSessions Sessions { get; }

        internal ShellViewModel Shell { get; }

        internal AssistantViewModel Assistant() =>
            Shell.Panes.Select(pane => pane.View)
                .OfType<Views.AssistantView>()
                .Last()
                .DataContext as AssistantViewModel
            ?? throw new InvalidOperationException("no assistant pane is open");

        internal OrchestratorViewModel Orchestrator()
        {
            Shell.AskSeveralHostsCommand.Execute(null);
            // By what the pane holds rather than by the control's type, so the
            // stand-in above answers as the real control would.
            return Shell.Panes.Select(pane => pane.View.DataContext)
                .OfType<OrchestratorViewModel>()
                .LastOrDefault()
                ?? throw new InvalidOperationException("no orchestrator pane is open");
        }
    }

    private sealed class Answering : IRemoteCommands
    {
        public CommandResult Run(string command, TimeSpan timeout) => new(0, "Linux 6.1.0 x86_64", "");
    }

    private sealed class Quiet : IServerHealth
    {
        public ServerMetrics Collect() => new() { Uptime = "3 days" };
    }

    private sealed class Silent(string alias) : IHostAccess
    {
        public string Alias => alias;

        internal HostSnapshot Snapshot { get; set; } = new();

        public Task<HostSnapshot> Look(bool metrics, bool tail, int lines, CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);

        public Task<CommandOutcome> Run(string command, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CommandOutcome(0, ""));
    }

    private sealed class Canned(params IReadOnlyList<AssistEvent>[] turns) : IAssistBackend
    {
        private int _turn;

        public string ProviderName => "Canned";

        public string Model => "canned-1";

        internal static IReadOnlyList<AssistEvent> Says(string text) =>
            [new AssistEvent.Say(text), new AssistEvent.Finished(AssistStop.EndTurn)];

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
