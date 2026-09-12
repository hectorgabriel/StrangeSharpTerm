using Avalonia.Controls;
using StrangeSharpTerm.App.ViewModels;
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
            connect ?? (_ => new TerminalSession(new DeadChannel())),
            (_, _) => new Border());
    }

    [Fact]
    public async Task ActivatingAHostOpensATabShowingIt()
    {
        var shell = Arrange(out var host);
        var row = shell.Inventory.Rows.Single(row => row.Id == host.Id);

        await shell.ActivateCommand.ExecuteAsync(row);

        shell.Workspace.Tabs.Count.ShouldBe(1);
        shell.Tabs.Single().Title.ShouldBe("web-01");
        shell.PaneContent.ShouldNotBeNull();
        shell.Inventory.Selection.ShouldBe(host.Id);
    }

    [Fact]
    public async Task ActivatingAFolderOpensAndShutsItRatherThanConnecting()
    {
        var shell = Arrange(out _, _ => throw new InvalidOperationException("a folder must not connect"));
        var folder = shell.Inventory.Rows.First(row => row.IsFolder);

        await shell.ActivateCommand.ExecuteAsync(folder);

        shell.Inventory.IsExpanded(folder.Id).ShouldBeFalse();
        shell.Workspace.Tabs.ShouldBeEmpty();
    }

    [Fact]
    public async Task AFailedConnectionSaysWhyInsteadOfOpeningAnEmptyTab()
    {
        var shell = Arrange(out var host, _ => throw new Renci.SshNet.Common.SshAuthenticationException("Permission denied"));
        var row = shell.Inventory.Rows.Single(r => r.Id == host.Id);

        await shell.ActivateCommand.ExecuteAsync(row);

        shell.Failure.ShouldNotBeNull().ShouldContain("Authentication failed");
        shell.Workspace.Tabs.ShouldBeEmpty();
    }

    [Fact]
    public async Task DeletingAHostClosesWhatItHadOpen()
    {
        var shell = Arrange(out var host);
        await shell.ActivateCommand.ExecuteAsync(shell.Inventory.Rows.Single(r => r.Id == host.Id));

        shell.Inventory.DeleteConnection(host.Id);

        shell.Workspace.Tabs.ShouldBeEmpty();
        shell.PaneContent.ShouldBeNull();
    }

    [Fact]
    public async Task ClosingATabLeavesTheOthersAlone()
    {
        var shell = Arrange(out var host);
        await shell.ActivateCommand.ExecuteAsync(shell.Inventory.Rows.Single(r => r.Id == host.Id));
        await shell.ActivateCommand.ExecuteAsync(shell.Inventory.Rows.Single(r => r.Id == host.Id));
        shell.Tabs.Count.ShouldBe(2);

        shell.CloseTabCommand.Execute(shell.Tabs[0]);

        shell.Tabs.Count.ShouldBe(1);
        shell.PaneContent.ShouldNotBeNull();
    }
}
