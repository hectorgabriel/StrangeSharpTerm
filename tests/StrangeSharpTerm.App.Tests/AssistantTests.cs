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
        // Beside rather than among: the dock is a column of its own, so asking
        // about a host does not take a tile away from the sessions. What it is
        // about is still the session with the keyboard.
        var fixture = new Fixture();
        fixture.Shell.Inventory.Selection = fixture.Host.Id;
        await fixture.Shell.ConnectSelectedCommand.ExecuteAsync(null);

        fixture.Shell.OpenAssistantCommand.Execute(null);

        fixture.Shell.IsDockOpen.ShouldBeTrue();
        fixture.Shell.DockShows.ShouldBe(DockView.Assistant);
        fixture.Shell.DockTitle.ShouldBe("web-01");
        // The session is still the only pane: the conversation cost it nothing.
        fixture.Shell.Panes.ShouldHaveSingleItem();
    }

    [Fact]
    public void WithNoKeyItSaysSoRatherThanOpeningAPaneThatCannotAnswer()
    {
        var fixture = new Fixture(hasKey: false);
        fixture.Shell.Inventory.Selection = fixture.Host.Id;

        fixture.Shell.OpenAssistantCommand.Execute(null);

        fixture.Shell.IsDockOpen.ShouldBeFalse();
        fixture.Shell.Dock.ShouldBeNull();
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
    public async Task TheOrchestratorBelongsToNoHost()
    {
        // Disconnecting a host closes what belongs to it, and a fan-out across
        // eight servers should not vanish because one was disconnected. In the
        // dock it belongs to nothing that can be closed out from under it.
        var fixture = new Fixture();
        fixture.Shell.Inventory.Selection = fixture.Host.Id;
        await fixture.Shell.ConnectSelectedCommand.ExecuteAsync(null);

        var orchestrator = fixture.Orchestrator();
        fixture.Shell.Workspace.ClosePane(fixture.Shell.Workspace.Panes[0].Id);

        fixture.Shell.IsDockOpen.ShouldBeTrue();
        fixture.Shell.Dock?.DataContext.ShouldBe(orchestrator);
    }

    /// <summary>
    /// The host detail shares its row with the panes, so whatever is showing
    /// there covers what is open. The orchestrator used to be a pane, and being
    /// about no host it was the one thing the detail should never have covered.
    /// In a column of its own the question no longer arises: asking across every
    /// host leaves the middle of the window exactly as it was.
    /// </summary>
    [Fact]
    public void TheOrchestratorTakesNothingFromTheSessions()
    {
        var fixture = new Fixture();
        fixture.Shell.Inventory.Selection = fixture.Host.Id;

        // With nothing open, the selected host explains itself.
        fixture.Shell.ShowsDetail.ShouldBeTrue();

        fixture.Shell.AskSeveralHostsCommand.Execute(null);

        fixture.Shell.ShowsDetail.ShouldBeTrue();
        fixture.Shell.Panes.ShouldBeEmpty();
        fixture.Shell.IsDockOpen.ShouldBeTrue();
        fixture.Shell.DockShows.ShouldBe(DockView.Orchestrator);
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

    /// <summary>
    /// Ticking a host has to reach everything that reads the ticks.
    ///
    /// Only the list of them was announced, so the count beside the Hosts
    /// header still read "none selected" with two ticked, and Run stayed
    /// disabled until the instruction was touched again. Typing the question
    /// and then choosing the hosts -- the obvious order -- left a button that
    /// did nothing.
    /// </summary>
    [Fact]
    public void ChoosingAHostAfterTypingStillArmsTheRunButton()
    {
        var model = new OrchestratorViewModel(
            new Canned(Canned.Says("Summary.")),
            [
                new TargetRow { Alias = "web-01", IsConnected = true },
                new TargetRow { Alias = "web-02", IsConnected = true },
            ],
            _ => null);

        // The instruction first, as anyone would.
        model.Instruction = "is the journal filling the disk?";
        model.RunCommand.CanExecute(null).ShouldBeFalse();

        // What the window is told, rather than what the properties return:
        // both recompute on read, so a screen that is never told to read again
        // keeps showing the answer from before.
        var announced = new List<string>();
        var rearmed = 0;
        model.PropertyChanged += (_, e) => announced.Add(e.PropertyName ?? "");
        model.RunCommand.CanExecuteChanged += (_, _) => rearmed++;

        model.Targets[0].IsChosen = true;

        announced.ShouldContain(nameof(OrchestratorViewModel.ChosenNote));
        rearmed.ShouldBeGreaterThan(0);

        model.ChosenNote.ShouldBe("1 host");
        model.RunCommand.CanExecute(null).ShouldBeTrue();

        model.Targets[1].IsChosen = true;
        model.ChosenNote.ShouldBe("2 hosts");
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

        /// <summary>
        /// The conversation the dock is showing. Both of them live there now
        /// rather than in a pane, so both are found the same way.
        /// </summary>
        internal AssistantViewModel Assistant() =>
            (Shell.Dock as Views.AssistantView)?.DataContext as AssistantViewModel
            ?? throw new InvalidOperationException("the dock is not showing an assistant");

        internal OrchestratorViewModel Orchestrator()
        {
            Shell.AskSeveralHostsCommand.Execute(null);
            // By what the control holds rather than by its type, so the stand-in
            // above answers as the real control would.
            return Shell.Dock?.DataContext as OrchestratorViewModel
                ?? throw new InvalidOperationException("the dock is not showing the orchestrator");
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

        /// <summary>Every request it was handed, in order.</summary>
        internal List<AssistRequest> Requests { get; } = [];

        internal static IReadOnlyList<AssistEvent> Says(string text) =>
            [new AssistEvent.Say(text), new AssistEvent.Finished(AssistStop.EndTurn)];

        public async IAsyncEnumerable<AssistEvent> Stream(
            AssistRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
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
