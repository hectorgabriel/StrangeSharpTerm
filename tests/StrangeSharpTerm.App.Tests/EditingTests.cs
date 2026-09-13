using Avalonia.Controls;
using StrangeSharpTerm.App.ViewModels;
using StrangeSharpTerm.App.Views;
using StrangeSharpTerm.Model;
using StrangeSharpTerm.Terminal;

namespace StrangeSharpTerm.App.Tests;

/// <summary>
/// Adding, editing and removing hosts and folders through the window: which
/// dialog opens, what reaches the inventory, and what a cancelled dialog leaves
/// behind — which is nothing.
/// </summary>
public class EditingTests
{
    private sealed class DeadChannel : ITerminalChannel
    {
        public Stream Stream { get; } = new MemoryStream();

        public void Resize(int columns, int rows) { }

        public void Dispose() => Stream.Dispose();
    }

    /// <summary>Counts what was written, so a cancelled edit can be shown not to have been.</summary>
    private sealed class CountingStore : StrangeSharpTerm.Store.IInventoryPersistence
    {
        public int Saves { get; private set; }

        public InventoryTree? Load() => null;

        public void Save(InventoryTree tree) => Saves++;
    }

    private static ShellViewModel Shell(
        IDialogService dialogs,
        InventoryTree tree,
        StrangeSharpTerm.Store.IInventoryPersistence? store = null) =>
        new(
            new InventoryViewModel(store, tree),
            _ => new TerminalSession(new DeadChannel()),
            (_, _) => new Border(),
            dialogs);

    [Fact]
    public async Task ANewHostIsWrittenWhenTheDialogSaysItWasSaved()
    {
        var dialogs = new ScriptedDialogService
        {
            EditHost = draft =>
            {
                draft.Name = "web-01";
                draft.Hostname = "web-01.example.com";
                return true;
            },
        };
        var shell = Shell(dialogs, new InventoryTree());

        await shell.NewHostCommand.ExecuteAsync(null);

        var host = shell.Inventory.Tree.Connections.Values.ShouldHaveSingleItem();
        host.Name.ShouldBe("web-01");
        host.Hostname.ShouldBe("web-01.example.com");
        // Selected, because the thing you just made is the thing you are looking at.
        shell.Inventory.Selection.ShouldBe(host.Id);
    }

    [Fact]
    public async Task ACancelledDialogWritesNothingAtAll()
    {
        var store = new CountingStore();
        var dialogs = new ScriptedDialogService
        {
            EditHost = draft =>
            {
                draft.Name = "web-01";
                draft.Hostname = "web-01.example.com";
                return false;
            },
        };
        var shell = Shell(dialogs, new InventoryTree(), store);

        await shell.NewHostCommand.ExecuteAsync(null);

        shell.Inventory.Tree.Connections.ShouldBeEmpty();
        store.Saves.ShouldBe(0);
    }

    [Fact]
    public async Task ANewHostLandsInTheFolderYouAreLookingAt()
    {
        var folder = new Folder { Name = "Production" };
        var dialogs = new ScriptedDialogService
        {
            EditHost = draft =>
            {
                draft.Name = "web-01";
                draft.Hostname = "web-01.example.com";
                return true;
            },
        };
        var shell = Shell(dialogs, new InventoryTree([folder]));
        shell.Inventory.Selection = folder.Id;

        await shell.NewHostCommand.ExecuteAsync(null);

        shell.Inventory.Tree.Connections.Values.Single().ParentId.ShouldBe(folder.Id);
    }

    [Fact]
    public async Task ANewHostBesideASelectedHostLandsInTheSameFolder()
    {
        // Selecting a host means "here"; the folder that host is in is here.
        var folder = new Folder { Name = "Production" };
        var sibling = new Connection { ParentId = folder.Id, Name = "web-01", Hostname = "web-01.example.com" };
        var dialogs = new ScriptedDialogService
        {
            EditHost = draft =>
            {
                draft.Name = "web-02";
                draft.Hostname = "web-02.example.com";
                return true;
            },
        };
        var shell = Shell(dialogs, new InventoryTree([folder], [sibling]));
        shell.Inventory.Selection = sibling.Id;

        await shell.NewHostCommand.ExecuteAsync(null);

        var added = shell.Inventory.Tree.Connections.Values.Single(host => host.Name == "web-02");
        added.ParentId.ShouldBe(folder.Id);
        // And after it, not on top of it.
        added.SortIndex.ShouldBeGreaterThan(sibling.SortIndex);
    }

    [Fact]
    public async Task EditingAHostOpensItWithItsOwnValues()
    {
        var host = new Connection
        {
            Name = "web-01",
            Hostname = "web-01.example.com",
            Settings = new ConnectionSettings { Username = "ops", Port = 2222 },
        };
        HostDraft? opened = null;
        var dialogs = new ScriptedDialogService
        {
            EditHost = draft =>
            {
                opened = draft;
                draft.Hostname = "web-01.internal";
                return true;
            },
        };
        var shell = Shell(dialogs, new InventoryTree(connections: [host]));
        shell.Inventory.Selection = host.Id;

        await shell.EditSelectedCommand.ExecuteAsync(null);

        opened.ShouldNotBeNull();
        opened.IsNew.ShouldBeFalse();
        opened.Name.ShouldBe("web-01");
        opened.Settings.Username.ShouldBe("ops");
        opened.Settings.Port.ShouldBe("2222");

        var saved = shell.Inventory.Tree.Connections[host.Id];
        saved.Hostname.ShouldBe("web-01.internal");
        saved.Settings.Username.ShouldBe("ops");
    }

    [Fact]
    public async Task EditingARowEditsWhateverTheRowIs()
    {
        var folder = new Folder { Name = "Producton" };
        var host = new Connection { ParentId = folder.Id, Name = "web-01", Hostname = "web-01.example.com" };
        var dialogs = new ScriptedDialogService
        {
            EditFolder = draft =>
            {
                draft.Name = "Production";
                return true;
            },
            EditHost = _ => throw new InvalidOperationException("a folder row must not open the host editor"),
        };
        var shell = Shell(dialogs, new InventoryTree([folder], [host]));
        var row = shell.Inventory.Rows.Single(row => row.IsFolder);

        await shell.EditRowCommand.ExecuteAsync(row);

        shell.Inventory.Tree.Folders[folder.Id].Name.ShouldBe("Production");
        // Renaming a folder does not disturb what is inside it.
        shell.Inventory.Tree.Connections[host.Id].ParentId.ShouldBe(folder.Id);
    }

    [Fact]
    public async Task ANewFolderIsWrittenAndOpened()
    {
        var dialogs = new ScriptedDialogService
        {
            EditFolder = draft =>
            {
                draft.Name = "Production";
                return true;
            },
        };
        var shell = Shell(dialogs, new InventoryTree());

        await shell.NewFolderCommand.ExecuteAsync(null);

        var folder = shell.Inventory.Tree.Folders.Values.ShouldHaveSingleItem();
        folder.Name.ShouldBe("Production");
        // Expanded, so the host you are about to put in it has somewhere visible to go.
        shell.Inventory.IsExpanded(folder.Id).ShouldBeTrue();
    }

    [Fact]
    public async Task DeletingAFolderFromItsRowSaysWhatGoesWithIt()
    {
        var folder = new Folder { Name = "Production" };
        var host = new Connection { ParentId = folder.Id, Name = "web-01", Hostname = "web-01.example.com" };
        var dialogs = new ScriptedDialogService(answer: true);
        var shell = Shell(dialogs, new InventoryTree([folder], [host]));
        var row = shell.Inventory.Rows.Single(row => row.IsFolder);

        await shell.DeleteRowCommand.ExecuteAsync(row);

        dialogs.Asked.Single().Title.ShouldBe("Delete Production?");
        dialogs.Asked.Single().Detail.ShouldBe("1 host inside will be deleted too.");
        shell.Inventory.Tree.Folders.ShouldBeEmpty();
        shell.Inventory.Tree.Connections.ShouldBeEmpty();
    }

    [Fact]
    public async Task DeletingAHostFromItsRowAsksTheSameQuestionAsThePane()
    {
        var host = new Connection { Name = "web-01", Hostname = "web-01.example.com" };
        var dialogs = new ScriptedDialogService(answer: false);
        var shell = Shell(dialogs, new InventoryTree(connections: [host]));
        var row = shell.Inventory.Rows.Single();

        await shell.DeleteRowCommand.ExecuteAsync(row);

        dialogs.Asked.Single().Title.ShouldBe("Delete web-01?");
        shell.Inventory.Tree.Connections.Count.ShouldBe(1);
        shell.Inventory.Pending.ShouldBeNull();
    }

    [Fact]
    public async Task NothingHappensWhenTheRowIsGone()
    {
        // A menu can outlive the row it was opened on: the host was deleted in
        // another way while the flyout was up.
        var dialogs = new ScriptedDialogService(answer: true);
        var shell = Shell(dialogs, new InventoryTree());
        var row = new SidebarRow(NodeId.New(), "web-01", 0, IsFolder: false);

        await shell.DeleteRowCommand.ExecuteAsync(row);
        await shell.EditRowCommand.ExecuteAsync(row);

        dialogs.Asked.ShouldBeEmpty();
        dialogs.Edited.ShouldBeEmpty();
        shell.Inventory.Pending.ShouldBeNull();
    }
}
