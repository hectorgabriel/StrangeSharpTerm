using Avalonia.Controls;
using StrangeSharpTerm.App.ViewModels;
using StrangeSharpTerm.App.Views;
using StrangeSharpTerm.Model;
using StrangeSharpTerm.Terminal;

namespace StrangeSharpTerm.App.Tests;

public class ShellViewModelTests
{
    /// <summary>A channel that carries nothing, so a session needs no server.</summary>
    private sealed class DeadChannel : ITerminalChannel
    {
        public Stream Stream { get; } = new MemoryStream();

        public void Resize(int columns, int rows) { }

        public void Dispose() => Stream.Dispose();
    }

    private static ShellViewModel Arrange(out Connection host, Func<Connection, TerminalSession>? connect = null)
    {
        var folder = new Folder { Name = "Production" };
        host = new Connection { ParentId = folder.Id, Name = "web-01", Hostname = "web-01.example.com" };
        var inventory = new InventoryViewModel(null, new InventoryTree([folder], [host]));
        return new ShellViewModel(
            inventory,
            new FakeSessions { OnShell = connect },
            (_, _) => new Border());
    }

    [Fact]
    public void SelectingAHostShowsItRatherThanConnectingToIt()
    {
        // A stray click in a list of production servers should not open a shell on one.
        var shell = Arrange(out var host, _ => throw new InvalidOperationException("selecting must not connect"));
        var row = shell.Inventory.Rows.Single(row => row.Id == host.Id);

        shell.ActivateCommand.Execute(row);

        shell.Inventory.Selection.ShouldBe(host.Id);
        shell.Detail.ShouldNotBeNull().Name.ShouldBe("web-01");
        shell.ShowsDetail.ShouldBeTrue();
        shell.Workspace.Tabs.ShouldBeEmpty();
    }

    [Fact]
    public async Task ConnectingIsTheThingThatOpensATab()
    {
        var shell = Arrange(out var host);
        shell.ActivateCommand.Execute(shell.Inventory.Rows.Single(row => row.Id == host.Id));

        await shell.ConnectSelectedCommand.ExecuteAsync(null);

        shell.Tabs.Single().Title.ShouldBe("web-01");
        shell.Panes.ShouldHaveSingleItem();
        shell.ShowsDetail.ShouldBeFalse();
    }

    [Fact]
    public void ActivatingAFolderOpensAndShutsIt()
    {
        var shell = Arrange(out _, _ => throw new InvalidOperationException("a folder must not connect"));
        var folder = shell.Inventory.Rows.First(row => row.IsFolder);

        shell.ActivateCommand.Execute(folder);

        shell.Inventory.IsExpanded(folder.Id).ShouldBeFalse();
        shell.Workspace.Tabs.ShouldBeEmpty();
    }

    [Fact]
    public async Task AFailedConnectionSaysWhyInsteadOfOpeningAnEmptyTab()
    {
        var shell = Arrange(out var host, _ => throw new Renci.SshNet.Common.SshAuthenticationException("Permission denied"));
        shell.ActivateCommand.Execute(shell.Inventory.Rows.Single(r => r.Id == host.Id));

        await shell.ConnectSelectedCommand.ExecuteAsync(null);

        shell.Failure.ShouldNotBeNull().ShouldContain("Authentication failed");
        shell.Workspace.Tabs.ShouldBeEmpty();
    }

    [Fact]
    public async Task DeletingAHostClosesWhatItHadOpen()
    {
        var shell = Arrange(out var host);
        shell.ActivateCommand.Execute(shell.Inventory.Rows.Single(r => r.Id == host.Id));
        await shell.ConnectSelectedCommand.ExecuteAsync(null);

        shell.Inventory.DeleteConnection(host.Id);

        shell.Workspace.Tabs.ShouldBeEmpty();
        shell.Panes.ShouldBeEmpty();
    }

    /// <summary>
    /// The layout is handed the focused tab's panes, so it cannot tell a pane
    /// that closed from one sitting in another tab. OpenPanes is what tells it,
    /// and without it switching tabs detached the other tab's terminals, which
    /// cost them the connection they were given.
    /// </summary>
    [Fact]
    public async Task OpenPanesSpansEveryTabWhilePanesIsJustTheFocusedOne()
    {
        var shell = Arrange(out var host);
        shell.ActivateCommand.Execute(shell.Inventory.Rows.Single(r => r.Id == host.Id));
        await shell.ConnectSelectedCommand.ExecuteAsync(null);
        var firstTabsPane = shell.Panes.ShouldHaveSingleItem().Id;

        await shell.ConnectSelectedCommand.ExecuteAsync(null);

        shell.Tabs.Count.ShouldBe(2);
        shell.Panes.ShouldHaveSingleItem().Id.ShouldNotBe(firstTabsPane);
        shell.OpenPanes.Count.ShouldBe(2);
        shell.OpenPanes.ShouldContain(firstTabsPane);

        // And it shrinks when a tab really does close.
        shell.CloseTabCommand.Execute(shell.Tabs[0]);
        shell.OpenPanes.ShouldHaveSingleItem().ShouldNotBe(firstTabsPane);
    }

    [Fact]
    public async Task ClosingATabLeavesTheOthersAlone()
    {
        var shell = Arrange(out var host);
        shell.ActivateCommand.Execute(shell.Inventory.Rows.Single(r => r.Id == host.Id));
        await shell.ConnectSelectedCommand.ExecuteAsync(null);
        await shell.ConnectSelectedCommand.ExecuteAsync(null);
        shell.Tabs.Count.ShouldBe(2);

        shell.CloseTabCommand.Execute(shell.Tabs[0]);

        shell.Tabs.Count.ShouldBe(1);
        shell.Panes.ShouldHaveSingleItem();
    }
}

public class DeletingFromTheDetailPaneTests
{
    private sealed class DeadChannel : ITerminalChannel
    {
        public Stream Stream { get; } = new MemoryStream();

        public void Resize(int columns, int rows) { }

        public void Dispose() => Stream.Dispose();
    }

    private static ShellViewModel Arrange(ScriptedDialogService dialogs, out Connection host)
    {
        host = new Connection { Name = "web-01", Hostname = "web-01.example.com" };
        var inventory = new InventoryViewModel(null, new InventoryTree(connections: [host]));
        return new ShellViewModel(inventory, new FakeSessions(), (_, _) => new Border(), dialogs);
    }

    [Fact]
    public async Task DeletingAsksFirstAndSaysWhatWouldGo()
    {
        var dialogs = new ScriptedDialogService(answer: false);
        var shell = Arrange(dialogs, out var host);
        shell.Inventory.Selection = host.Id;

        await shell.DeleteSelectedCommand.ExecuteAsync(null);

        dialogs.Asked.Single().Title.ShouldBe("Delete web-01?");
        dialogs.Asked.Single().Detail.ShouldBe("Its settings and port forwards will be removed.");
    }

    [Fact]
    public async Task SayingNoKeepsTheHostAndClearsThePrompt()
    {
        var dialogs = new ScriptedDialogService(answer: false);
        var shell = Arrange(dialogs, out var host);
        shell.Inventory.Selection = host.Id;

        await shell.DeleteSelectedCommand.ExecuteAsync(null);

        shell.Inventory.Tree.Connections.Count.ShouldBe(1);
        shell.Inventory.Pending.ShouldBeNull();
    }

    [Fact]
    public async Task SayingYesDeletesIt()
    {
        var dialogs = new ScriptedDialogService(answer: true);
        var shell = Arrange(dialogs, out var host);
        shell.Inventory.Selection = host.Id;

        await shell.DeleteSelectedCommand.ExecuteAsync(null);

        shell.Inventory.Tree.Connections.ShouldBeEmpty();
        shell.Detail.ShouldBeNull();
    }

    [Fact]
    public async Task TheDetailPaneFollowsTheSelection()
    {
        var shell = Arrange(new ScriptedDialogService(), out var host);

        shell.Detail.ShouldBeNull();
        shell.Inventory.Selection = host.Id;
        shell.Detail.ShouldNotBeNull().Name.ShouldBe("web-01");
    }
}

public class WhatTheRightHandSideShowsTests
{
    private sealed class DeadChannel : ITerminalChannel
    {
        public Stream Stream { get; } = new MemoryStream();

        public void Resize(int columns, int rows) { }

        public void Dispose() => Stream.Dispose();
    }

    private static ShellViewModel Arrange(out Connection host)
    {
        host = new Connection { Name = "web-01", Hostname = "web-01.example.com" };
        var inventory = new InventoryViewModel(null, new InventoryTree(connections: [host]));
        return new ShellViewModel(inventory, new FakeSessions(), (_, _) => new Border());
    }

    [Fact]
    public void WithNothingChosenItInvitesAChoice()
    {
        var shell = Arrange(out _);

        shell.ShowsEmptyState.ShouldBeTrue();
        shell.ShowsDetail.ShouldBeFalse();
    }

    [Fact]
    public void AChosenHostReplacesTheInvitation()
    {
        var shell = Arrange(out var host);

        shell.Inventory.Selection = host.Id;

        shell.ShowsDetail.ShouldBeTrue();
        shell.ShowsEmptyState.ShouldBeFalse();
    }

    [Fact]
    public async Task AnOpenTerminalReplacesBoth()
    {
        // An empty-state line drawn over a terminal reads as part of the shell's
        // own output, which a screenshot caught and no test had.
        var shell = Arrange(out var host);
        shell.Inventory.Selection = host.Id;

        await shell.ConnectSelectedCommand.ExecuteAsync(null);

        shell.ShowsDetail.ShouldBeFalse();
        shell.ShowsEmptyState.ShouldBeFalse();
    }
}
