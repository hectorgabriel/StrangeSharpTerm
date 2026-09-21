using Avalonia.Controls;
using StrangeSharpTerm.App.ViewModels;
using StrangeSharpTerm.Assist;
using StrangeSharpTerm.Model;
using StrangeSharpTerm.Terminal;
using StrangeSharpTerm.Transport;

namespace StrangeSharpTerm.App.Tests;

/// <summary>
/// The three-pane window: the hosts, every session at once, and the dock.
///
/// What these are about is the arithmetic and the bookkeeping — which panes are
/// on screen, where a broadcast goes, what the dock is talking about. What a
/// tile looks like is <see cref="Views.PaneSplitView"/>'s, and the rule it must
/// not break is that a pane is moved between cells rather than rebuilt.
/// </summary>
public class TiledLayoutTests
{
    private static readonly Connection Web01 = new() { Name = "web-01", Hostname = "web-01.example.com" };
    private static readonly Connection Web02 = new() { Name = "web-02", Hostname = "web-02.example.com" };
    private static readonly Connection Db = new() { Name = "db-primary", Hostname = "db-01.example.com" };

    /// <summary>
    /// Tabs show one tab's panes; tiles show every session there is.
    ///
    /// Three sessions opened the ordinary way are three tabs, so tiled they are
    /// three tiles and untiled they are one.
    /// </summary>
    [Fact]
    public async Task TilingShowsEverySessionAndTabsShowOne()
    {
        var shell = Shell();
        foreach (var host in new[] { Web01, Web02, Db })
            await shell.OpenTerminal(host);

        shell.Panes.ShouldHaveSingleItem();

        shell.ToggleTilesCommand.Execute(null);

        shell.IsTiled.ShouldBeTrue();
        shell.Panes.Select(pane => pane.Title).ShouldBe(["web-01", "web-02", "db-primary"]);

        shell.ToggleTilesCommand.Execute(null);

        shell.IsTiled.ShouldBeFalse();
        shell.Panes.ShouldHaveSingleItem();
    }

    /// <summary>
    /// Tiling arranges what is open; it opens and closes nothing.
    ///
    /// The same views, in different cells — which is the whole reason a terminal
    /// survives it. A tile that was rebuilt would be a shell that was hung up on.
    /// </summary>
    [Fact]
    public async Task TilingMovesThePanesItAlreadyHas()
    {
        var shell = Shell();
        await shell.OpenTerminal(Web01);
        await shell.OpenTerminal(Web02);
        var before = shell.Workspace.Panes.Select(pane => pane.Id).ToArray();

        shell.ToggleTilesCommand.Execute(null);

        shell.Workspace.Panes.Select(pane => pane.Id).ShouldBe(before);
        shell.OpenPanes.ShouldBe(before, ignoreOrder: true);
    }

    /// <summary>
    /// A broadcast goes where the keystrokes can be seen to land.
    ///
    /// Two sessions in two tabs cannot be broadcast to: only one of them is on
    /// screen. Tiled, both are, and both should hear it.
    /// </summary>
    [Fact]
    public async Task ABroadcastFollowsWhatIsOnScreen()
    {
        var shell = Shell();
        await shell.OpenTerminal(Web01);
        await shell.OpenTerminal(Web02);

        shell.CanBroadcast.ShouldBeFalse();

        shell.ToggleTilesCommand.Execute(null);

        shell.CanBroadcast.ShouldBeTrue();
    }

    /// <summary>
    /// Tiled, the host detail no longer covers the sessions for a host that has
    /// one: hiding every server on screen to describe one of them is the reverse
    /// of what tiling is for. A host with nothing open still explains itself,
    /// because that panel is where a session is started from.
    /// </summary>
    [Fact]
    public async Task TheDetailDoesNotCoverTilesForAHostThatIsAlreadyOpen()
    {
        var shell = Shell();
        await shell.OpenTerminal(Web01);
        await shell.OpenTerminal(Web02);
        shell.ToggleTilesCommand.Execute(null);

        shell.Inventory.Selection = Web01.Id;
        shell.ShowsDetail.ShouldBeFalse();
        shell.ShowsPanes.ShouldBeTrue();

        // Nothing open for this one, so the panel that opens a session shows.
        shell.Inventory.Selection = Db.Id;
        shell.ShowsDetail.ShouldBeTrue();
    }

    /// <summary>
    /// The dock follows the focus: clicking a tile points the assistant at that
    /// host, and clicking back finds the conversation it already had.
    /// </summary>
    [Fact]
    public async Task TheDockFollowsWhicheverSessionHasTheKeyboard()
    {
        var shell = Shell();
        await shell.OpenTerminal(Web01);
        await shell.OpenTerminal(Web02);
        shell.ToggleTilesCommand.Execute(null);

        shell.Inventory.Selection = Web01.Id;
        shell.OpenAssistantCommand.Execute(null);
        shell.DockTitle.ShouldBe("web-01");
        var first = shell.Dock;

        shell.FocusPaneCommand.Execute(shell.Panes[1].Id);

        shell.DockTitle.ShouldBe("web-02");
        shell.Dock.ShouldNotBe(first);

        // Back again, and it is the same conversation rather than a new one.
        shell.FocusPaneCommand.Execute(shell.Panes[0].Id);

        shell.DockTitle.ShouldBe("web-01");
        shell.Dock.ShouldBe(first);
    }

    /// <summary>
    /// The orchestrator is about several hosts, so it does not follow one.
    /// Moving between tiles while it is up must not quietly swap it for a
    /// conversation about whichever server was clicked last.
    /// </summary>
    [Fact]
    public async Task TheOrchestratorInTheDockDoesNotFollowTheFocus()
    {
        var shell = Shell();
        await shell.OpenTerminal(Web01);
        await shell.OpenTerminal(Web02);
        shell.ToggleTilesCommand.Execute(null);

        shell.AskSeveralHostsCommand.Execute(null);
        var orchestrator = shell.Dock;

        shell.FocusPaneCommand.Execute(shell.Panes[1].Id);

        shell.DockShows.ShouldBe(DockView.Orchestrator);
        shell.Dock.ShouldBe(orchestrator);
    }

    /// <summary>The dock is a column, and a column can be put away.</summary>
    [Fact]
    public void TheDockAndTheSidebarBothClose()
    {
        var shell = Shell();

        shell.IsSidebarOpen.ShouldBeTrue();
        shell.ToggleSidebarCommand.Execute(null);
        shell.IsSidebarOpen.ShouldBeFalse();

        shell.Inventory.Selection = Web01.Id;
        shell.OpenAssistantCommand.Execute(null);
        shell.IsDockOpen.ShouldBeTrue();

        shell.CloseDockCommand.Execute(null);
        shell.IsDockOpen.ShouldBeFalse();
    }

    private static ShellViewModel Shell() =>
        new(
            new InventoryViewModel(null, new InventoryTree(connections: [Web01, Web02, Db])),
            new FakeSessions { OnCommands = _ => new Silent(), OnHealth = _ => new Silent() },
            (_, _) => new Border(),
            backends: _ => new Nothing(),
            assistantView: model => new Border { DataContext = model },
            orchestratorView: model => new Border { DataContext = model });

    /// <summary>A provider that is never actually asked anything by these tests.</summary>
    private sealed class Nothing : IAssistBackend
    {
        public string ProviderName => "Nothing";

        public string Model => "nothing-1";

        public async IAsyncEnumerable<AssistEvent> Stream(
            AssistRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new AssistEvent.Finished(AssistStop.EndTurn);
        }
    }

    private sealed class Silent : IRemoteCommands, IServerHealth
    {
        public CommandResult Run(string command, TimeSpan timeout) => new(0, "", "");

        public CommandResult RunFeeding(string command, TimeSpan timeout, string input) => Run(command, timeout);

        public ServerMetrics Collect() => new() { Uptime = "3 days" };
    }
}
