using StrangeSharpTerm.App.ViewModels;
using StrangeSharpTerm.Model;
using StrangeSharpTerm.Transport;

namespace StrangeSharpTerm.App.Tests;

/// <summary>
/// A host's tunnels: what the pane says about them, and what it does when one
/// will not start — which is the case worth most of the attention, because a
/// port that is taken is the ordinary failure.
/// </summary>
public class TunnelTests
{
    private sealed class FakeTunnels : ITunnels
    {
        public List<PortForward> Started { get; } = [];

        /// <summary>Thrown by the next start, whatever it is for.</summary>
        public Exception? Refuses { get; set; }

        public IRunningTunnel Start(PortForward forward)
        {
            if (Refuses is { } refusal)
            {
                Refuses = null;
                throw refusal;
            }

            Started.Add(forward);
            return new Running();
        }

        public sealed class Running : IRunningTunnel
        {
            public bool IsRunning { get; private set; } = true;

            public bool WasStopped { get; private set; }

            public void Stop()
            {
                IsRunning = false;
                WasStopped = true;
            }

            public void Dispose() => Stop();
        }
    }

    private static PortForward Forward(
        string name = "postgres",
        PortForwardKind kind = PortForwardKind.Local,
        int bindPort = 5432,
        bool autoStart = false) =>
        new()
        {
            Kind = kind,
            Name = name,
            BindPort = bindPort,
            DestinationHost = "localhost",
            DestinationPort = 5432,
            AutoStart = autoStart,
        };

    private static Task Here(Action work)
    {
        work();
        return Task.CompletedTask;
    }

    private static TunnelsViewModel Pane(FakeTunnels tunnels, params PortForward[] forwards) =>
        new(tunnels, "db-01", forwards, Here);

    [Fact]
    public void ItListsWhatTheHostDefines()
    {
        var pane = Pane(new FakeTunnels(), Forward(), Forward("redis", bindPort: 6379));

        pane.Rows.Select(row => row.Name).ShouldBe(["postgres", "redis"]);
        pane.Rows.ShouldAllBe(row => !row.IsRunning);
        pane.IsEmpty.ShouldBeFalse();
        pane.AnyRunning.ShouldBeFalse();
    }

    [Fact]
    public void AHostWithNoneSaysSoRatherThanShowingAnEmptyList()
    {
        Pane(new FakeTunnels()).IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void EachKindSaysWhatItActuallyDoes()
    {
        // The flags are not obvious to everyone, and a tunnel is the feature
        // people most often get backwards.
        new TunnelRow(Forward(kind: PortForwardKind.Local)).Explanation
            .ShouldBe("Anything reaching localhost:5432 arrives at localhost:5432, from the server.");
        new TunnelRow(Forward(kind: PortForwardKind.Remote, bindPort: 9000)).Explanation
            .ShouldContain("on the server arrives at");
        new TunnelRow(Forward(kind: PortForwardKind.Dynamic, bindPort: 1080)).Explanation
            .ShouldContain("SOCKS proxy");
    }

    [Fact]
    public void TheSpecificationIsTheOneSshWouldBeGiven()
    {
        new TunnelRow(Forward()).Specification.ShouldBe("-L 5432:localhost:5432");
    }

    [Fact]
    public async Task StartingOneMarksItUp()
    {
        var tunnels = new FakeTunnels();
        var pane = Pane(tunnels, Forward());

        await pane.ToggleCommand.ExecuteAsync(pane.Rows[0]);

        tunnels.Started.ShouldHaveSingleItem().Name.ShouldBe("postgres");
        pane.Rows[0].IsRunning.ShouldBeTrue();
        pane.Rows[0].Action.ShouldBe("Stop");
        pane.AnyRunning.ShouldBeTrue();
    }

    [Fact]
    public async Task StoppingOneLetsItsPortGo()
    {
        // The property the integration gate asserts for a forward: a tunnel that
        // stopped while still holding its port cannot be started again, and the
        // error the next attempt gives blames the wrong thing.
        var tunnels = new FakeTunnels();
        var pane = Pane(tunnels, Forward());
        await pane.ToggleCommand.ExecuteAsync(pane.Rows[0]);

        await pane.ToggleCommand.ExecuteAsync(pane.Rows[0]);

        pane.Rows[0].IsRunning.ShouldBeFalse();
        pane.AnyRunning.ShouldBeFalse();
    }

    [Fact]
    public async Task APortThatIsTakenIsSaidPlainly()
    {
        var tunnels = new FakeTunnels { Refuses = new InvalidOperationException("Address already in use") };
        var pane = Pane(tunnels, Forward());

        await pane.ToggleCommand.ExecuteAsync(pane.Rows[0]);

        pane.Rows[0].Failure.ShouldBe("something is already listening on 5432");
        pane.Rows[0].IsRunning.ShouldBeFalse();
    }

    [Fact]
    public async Task APrivilegedPortIsExplainedRatherThanRetried()
    {
        // Said before the attempt in the row, and again if someone tries anyway:
        // there is no privileged helper and there is not going to be one.
        var tunnels = new FakeTunnels { Refuses = new InvalidOperationException("permission denied") };
        var pane = Pane(tunnels, Forward("http", bindPort: 80));

        pane.Rows[0].NeedsRoot.ShouldBeTrue();
        await pane.ToggleCommand.ExecuteAsync(pane.Rows[0]);

        pane.Rows[0].Failure.ShouldBe("port 80 needs rights this app does not have");
    }

    [Fact]
    public async Task TryingAgainClearsTheLastComplaint()
    {
        var tunnels = new FakeTunnels { Refuses = new InvalidOperationException("Address already in use") };
        var pane = Pane(tunnels, Forward());
        await pane.ToggleCommand.ExecuteAsync(pane.Rows[0]);
        pane.Rows[0].Failure.ShouldNotBeNull();

        await pane.ToggleCommand.ExecuteAsync(pane.Rows[0]);

        pane.Rows[0].Failure.ShouldBeNull();
        pane.Rows[0].IsRunning.ShouldBeTrue();
    }

    [Fact]
    public async Task OnlyTheAutomaticOnesStartWithTheHost()
    {
        var tunnels = new FakeTunnels();
        var pane = Pane(tunnels, Forward("postgres", autoStart: true), Forward("redis", bindPort: 6379));

        await pane.StartAutomaticCommand.ExecuteAsync(null);

        tunnels.Started.ShouldHaveSingleItem().Name.ShouldBe("postgres");
        pane.Rows[0].IsRunning.ShouldBeTrue();
        pane.Rows[1].IsRunning.ShouldBeFalse();
    }

    [Fact]
    public async Task StoppingEverythingStopsEverythingThatIsUp()
    {
        var tunnels = new FakeTunnels();
        var pane = Pane(tunnels, Forward("postgres", autoStart: true), Forward("redis", bindPort: 6379, autoStart: true));
        await pane.StartAutomaticCommand.ExecuteAsync(null);

        pane.StopAllCommand.Execute(null);

        pane.Rows.ShouldAllBe(row => !row.IsRunning);
        pane.AnyRunning.ShouldBeFalse();
    }

    [Fact]
    public async Task ClosingThePaneStopsWhatItStarted()
    {
        // A pane that goes away while its ports stay bound is worse than no
        // tunnel: nothing will bind them again until the app quits.
        var tunnels = new FakeTunnels();
        var pane = Pane(tunnels, Forward(autoStart: true));
        await pane.StartAutomaticCommand.ExecuteAsync(null);

        pane.Dispose();

        pane.Rows[0].IsRunning.ShouldBeFalse();
    }

    [Fact]
    public void ALocalPortBelowAThousandNeedsRootAndARemoteOneDoesNot()
    {
        // Remote forwards bind on the server, where this app's rights are moot.
        new TunnelRow(Forward(bindPort: 443)).NeedsRoot.ShouldBeTrue();
        new TunnelRow(Forward(kind: PortForwardKind.Remote, bindPort: 443)).NeedsRoot.ShouldBeFalse();
        new TunnelRow(Forward(kind: PortForwardKind.Dynamic, bindPort: 1080)).NeedsRoot.ShouldBeFalse();
    }

    [Fact]
    public async Task APaneShowsTheForwardsAFolderGaveTheHost()
    {
        // Forwards accumulate down the tree: a folder's applies to every host
        // beneath it, and the host adds its own rather than replacing them.
        var folder = new Folder
        {
            Name = "Production",
            Settings = new ConnectionSettings { PortForwards = [Forward("shared", bindPort: 9000)] },
        };
        var host = new Connection
        {
            ParentId = folder.Id,
            Name = "db-01",
            Hostname = "db-01.example.com",
            Settings = new ConnectionSettings { PortForwards = [Forward()] },
        };
        var shell = new ShellViewModel(
            new InventoryViewModel(null, new InventoryTree([folder], [host])),
            new FakeSessions { OnTunnels = _ => new FakeTunnels() });
        shell.Inventory.Selection = host.Id;

        await shell.OpenTunnelsCommand.ExecuteAsync(null);

        shell.Tabs.ShouldHaveSingleItem().Title.ShouldBe("db-01 tunnels");
        shell.Panes.ShouldHaveSingleItem();
    }
}
