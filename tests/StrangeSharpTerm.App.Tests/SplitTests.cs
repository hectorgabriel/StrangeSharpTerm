using Avalonia.Controls;
using StrangeSharpTerm.App.ViewModels;
using StrangeSharpTerm.Model;
using StrangeSharpTerm.Terminal;

namespace StrangeSharpTerm.App.Tests;

/// <summary>
/// A tab with more than one pane in it: what the window is handed to draw, where
/// the keyboard goes, and what closing one leaves behind.
/// </summary>
public class SplitTests
{
    private sealed class DeadChannel : ITerminalChannel
    {
        public Stream Stream { get; } = new MemoryStream();

        public void Resize(int columns, int rows) { }

        public void Dispose() => Stream.Dispose();
    }

    /// <summary>A pane that can be told apart from the next one.</summary>
    private sealed class Pane : Border
    {
        public Pane(int number) => Tag = number;
    }

    private static (ShellViewModel Shell, Connection Host) Arrange()
    {
        var host = new Connection { Name = "web-01", Hostname = "web-01.example.com" };
        var opened = 0;
        var shell = new ShellViewModel(
            new InventoryViewModel(null, new InventoryTree(connections: [host])),
            new FakeSessions(),
            (_, _) => new Pane(++opened));
        return (shell, host);
    }

    /// <summary>A shell with one terminal open on its only host.</summary>
    private static async Task<(ShellViewModel Shell, Connection Host)> WithATerminal()
    {
        var (shell, host) = Arrange();
        shell.Inventory.Selection = host.Id;
        await shell.ConnectSelectedCommand.ExecuteAsync(null);
        return (shell, host);
    }

    [Fact]
    public async Task SplittingPutsASecondPaneInTheSameTab()
    {
        var (shell, _) = await WithATerminal();

        await shell.SplitRightCommand.ExecuteAsync(null);

        shell.Tabs.ShouldHaveSingleItem();
        shell.Panes.Count.ShouldBe(2);
        shell.Axis.ShouldBe(SplitAxis.Horizontal);
    }

    [Fact]
    public async Task SplittingDownStacksThemInstead()
    {
        var (shell, _) = await WithATerminal();

        await shell.SplitDownCommand.ExecuteAsync(null);

        shell.Axis.ShouldBe(SplitAxis.Vertical);
    }

    [Fact]
    public async Task ASplitIsASecondSessionOnTheSameHost()
    {
        // Not a second view of the same shell: ssh has no such thing, and a pane
        // that mirrored another would be a lie about what is running.
        var (shell, host) = await WithATerminal();

        await shell.SplitRightCommand.ExecuteAsync(null);

        shell.Workspace.Panes.Count.ShouldBe(2);
        shell.Workspace.Panes.Select(pane => pane.ConnectionId).ShouldAllBe(id => id == host.Id);
        shell.Workspace.Panes.Select(pane => pane.Id).Distinct().Count().ShouldBe(2);
    }

    [Fact]
    public async Task TheNewPaneTakesTheKeyboard()
    {
        var (shell, _) = await WithATerminal();

        await shell.SplitRightCommand.ExecuteAsync(null);

        // Exactly one, and the one just opened.
        shell.Panes.Count(pane => pane.IsActive).ShouldBe(1);
        shell.Panes[^1].IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task ClickingAPaneMovesTheKeyboardToIt()
    {
        var (shell, _) = await WithATerminal();
        await shell.SplitRightCommand.ExecuteAsync(null);
        var first = shell.Panes[0];

        shell.FocusPaneCommand.Execute(first.Id);

        shell.Panes[0].IsActive.ShouldBeTrue();
        shell.Panes[1].IsActive.ShouldBeFalse();
        shell.Workspace.ActivePaneId.ShouldBe(first.Id);
    }

    [Fact]
    public async Task TheViewsAreTheSameObjectsAfterASplit()
    {
        // Half of the property that matters. The other half — that the view is
        // never taken out of the visual tree and put back, which tears a terminal
        // control's connection down — needs a window to assert, and waits for the
        // headless driver the plan wants for M4-M5. PaneSplitView says why.
        var (shell, _) = await WithATerminal();
        var before = shell.Panes.Single().View;

        await shell.SplitRightCommand.ExecuteAsync(null);

        shell.Panes[0].View.ShouldBeSameAs(before);
    }

    [Fact]
    public async Task ClosingAPaneLeavesTheOtherOpen()
    {
        var (shell, _) = await WithATerminal();
        await shell.SplitRightCommand.ExecuteAsync(null);
        var remaining = shell.Panes[0].Id;

        shell.ClosePaneCommand.Execute(null);

        shell.Tabs.ShouldHaveSingleItem();
        shell.Panes.ShouldHaveSingleItem().Id.ShouldBe(remaining);
        shell.Panes[0].IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task ClosingTheLastPaneClosesTheTab()
    {
        var (shell, _) = await WithATerminal();

        shell.ClosePaneCommand.Execute(null);

        shell.Tabs.ShouldBeEmpty();
        shell.Panes.ShouldBeEmpty();
        shell.ShowsEmptyState.ShouldBeFalse();
        // The host is still selected, so what it would connect with is shown again.
        shell.ShowsDetail.ShouldBeTrue();
    }

    [Fact]
    public async Task ASplitTabSaysHowManyPanesItHas()
    {
        var (shell, _) = await WithATerminal();

        await shell.SplitRightCommand.ExecuteAsync(null);

        shell.Tabs.Single().Title.ShouldBe("web-01 +1");
    }

    [Fact]
    public async Task BroadcastWaitsForSomethingToBroadcastTo()
    {
        // A group of one is not a broadcast; offering the toggle before there is
        // a second pane suggests it would do something.
        var (shell, _) = await WithATerminal();
        shell.CanBroadcast.ShouldBeFalse();

        await shell.SplitRightCommand.ExecuteAsync(null);

        shell.CanBroadcast.ShouldBeTrue();
    }

    [Fact]
    public async Task TheBroadcastToggleReachesTheWorkspace()
    {
        // Which terminals a broadcast reaches is the workspace's business and is
        // tested there; this is the wire from the button to it.
        var (shell, _) = await WithATerminal();
        await shell.SplitRightCommand.ExecuteAsync(null);

        shell.IsBroadcasting = true;

        shell.Workspace.IsBroadcasting.ShouldBeTrue();
        shell.IsBroadcasting.ShouldBeTrue();
    }

    [Fact]
    public async Task SwitchingTabsShowsThatTabsPanes()
    {
        var (shell, _) = await WithATerminal();
        await shell.SplitRightCommand.ExecuteAsync(null);
        // A second tab, unsplit.
        await shell.ConnectSelectedCommand.ExecuteAsync(null);
        shell.Panes.ShouldHaveSingleItem();

        shell.FocusTabCommand.Execute(shell.Tabs[0]);

        shell.Panes.Count.ShouldBe(2);
        shell.Tabs[0].IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task SplittingWithNothingOpenDoesNothing()
    {
        var (shell, host) = Arrange();
        shell.Inventory.Selection = host.Id;

        await shell.SplitRightCommand.ExecuteAsync(null);

        shell.Tabs.ShouldBeEmpty();
        shell.Panes.ShouldBeEmpty();
    }

    [Fact]
    public async Task DeletingAHostClosesEveryPaneItHad()
    {
        var (shell, host) = await WithATerminal();
        await shell.SplitRightCommand.ExecuteAsync(null);

        shell.Inventory.DeleteConnection(host.Id);

        shell.Panes.ShouldBeEmpty();
        shell.Tabs.ShouldBeEmpty();
    }
}
