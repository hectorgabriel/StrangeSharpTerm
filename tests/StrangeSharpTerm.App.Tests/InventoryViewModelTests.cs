using StrangeSharpTerm.App.ViewModels;
using StrangeSharpTerm.Model;
using StrangeSharpTerm.Store;

namespace StrangeSharpTerm.App.Tests;

internal sealed class RecordingStore : IInventoryPersistence
{
    public int Saves { get; private set; }
    public InventoryTree? Last { get; private set; }
    public Exception? FailWith { get; set; }

    public InventoryTree? Load() => Last;

    public void Save(InventoryTree tree)
    {
        if (FailWith is not null)
            throw FailWith;
        Saves++;
        Last = tree;
    }
}

public class InventoryViewModelTests
{
    private static (InventoryViewModel Model, RecordingStore Store, Folder Production, Connection Web) Arrange()
    {
        var production = new Folder { Name = "Production" };
        var web = new Connection { ParentId = production.Id, Name = "web-01", Hostname = "web-01.example.com", Tags = ["nginx"] };
        var store = new RecordingStore();
        var model = new InventoryViewModel(store, new InventoryTree([production], [web]));
        return (model, store, production, web);
    }

    [Fact]
    public void EverythingStartsExpandedSoTheFirstLaunchShowsWhatIsThere()
    {
        // A collapsed sidebar on first launch hides the very thing the user came to see.
        var f = Arrange();

        f.Model.IsExpanded(f.Production.Id).ShouldBeTrue();
    }

    [Fact]
    public void FoldersToggleOpenAndShut()
    {
        var f = Arrange();

        f.Model.Toggle(f.Production.Id);
        f.Model.IsExpanded(f.Production.Id).ShouldBeFalse();
        f.Model.Toggle(f.Production.Id);
        f.Model.IsExpanded(f.Production.Id).ShouldBeTrue();
    }

    [Fact]
    public void SearchMatchesNameHostnameOrTag()
    {
        // Typing "prod" should find both a host named prod-web and one tagged production.
        var f = Arrange();

        f.Model.Search = "WEB-01";
        f.Model.ConnectionsIn(f.Production.Id).Count.ShouldBe(1);

        f.Model.Search = "example.com";
        f.Model.ConnectionsIn(f.Production.Id).Count.ShouldBe(1);

        f.Model.Search = "nginx";
        f.Model.ConnectionsIn(f.Production.Id).Count.ShouldBe(1);

        f.Model.Search = "database";
        f.Model.ConnectionsIn(f.Production.Id).ShouldBeEmpty();
    }

    [Fact]
    public void AFolderHidesItselfWhenNothingInsideMatches()
    {
        var f = Arrange();

        f.Model.Search = "nginx";
        f.Model.FolderIsVisible(f.Production.Id).ShouldBeTrue();

        f.Model.Search = "database";
        f.Model.FolderIsVisible(f.Production.Id).ShouldBeFalse();
    }

    [Fact]
    public void AFolderStaysVisibleForAMatchBuriedInsideIt()
    {
        var outer = new Folder { Name = "All" };
        var inner = new Folder { ParentId = outer.Id, Name = "Databases" };
        var host = new Connection { ParentId = inner.Id, Name = "db-01", Hostname = "db-01" };
        var model = new InventoryViewModel(null, new InventoryTree([outer, inner], [host])) { Search = "db-01" };

        model.FolderIsVisible(outer.Id).ShouldBeTrue();
    }

    [Fact]
    public void EveryChangeIsSaved()
    {
        var f = Arrange();

        f.Model.Upsert(new Connection { Name = "db-01", Hostname = "db-01" });
        f.Model.Upsert(new Folder { Name = "Staging" });
        f.Model.Upsert(new Credential { Name = "Ops key" });
        f.Model.Upsert(new Snippet { Name = "uptime", Command = "uptime" });

        f.Store.Saves.ShouldBe(4);
    }

    [Fact]
    public void AFailedSaveIsReportedRatherThanThrownAtAKeystroke()
    {
        var f = Arrange();
        f.Store.FailWith = new IOException("the disk is full");

        f.Model.Upsert(new Connection { Name = "db-01", Hostname = "db-01" });

        f.Model.StoreError.ShouldBe("the disk is full");
    }

    [Fact]
    public void AddingAHostSelectsIt()
    {
        var f = Arrange();
        var db = new Connection { Name = "db-01", Hostname = "db-01" };

        f.Model.Upsert(db);

        f.Model.Selection.ShouldBe(db.Id);
        f.Model.SelectedConnection!.Name.ShouldBe("db-01");
    }

    [Fact]
    public void DeletingAHostTellsWhateverHasItOpenFirst()
    {
        var f = Arrange();
        var closed = new List<NodeId>();
        f.Model.ConnectionRemoving += (_, id) => closed.Add(id);

        f.Model.DeleteConnection(f.Web.Id);

        closed.ShouldBe(new[] { f.Web.Id });
        f.Model.Tree.Connections.ShouldBeEmpty();
    }

    [Fact]
    public void DeletingAFolderTakesEverythingInsideWithIt()
    {
        var outer = new Folder { Name = "All" };
        var inner = new Folder { ParentId = outer.Id, Name = "Databases" };
        var host = new Connection { ParentId = inner.Id, Name = "db-01", Hostname = "db-01" };
        var model = new InventoryViewModel(null, new InventoryTree([outer, inner], [host]));

        model.DeleteFolder(outer.Id);

        model.Tree.Folders.ShouldBeEmpty();
        model.Tree.Connections.ShouldBeEmpty();
    }

    [Fact]
    public void ANewItemGoesToTheEndOfItsFolder()
    {
        var folder = new Folder { Name = "Production", SortIndex = 0 };
        var first = new Connection { ParentId = folder.Id, Name = "a", Hostname = "a", SortIndex = 3 };
        var model = new InventoryViewModel(null, new InventoryTree([folder], [first]));

        model.NextSortIndex(folder.Id).ShouldBe(4);
        model.NextSortIndex(null).ShouldBe(1);
    }

    [Fact]
    public void FoldersFlattenForAPickerWithTheDepthToIndentBy()
    {
        var outer = new Folder { Name = "All" };
        var inner = new Folder { ParentId = outer.Id, Name = "Databases" };
        var model = new InventoryViewModel(null, new InventoryTree([outer, inner]));

        model.FolderChoices.Select(choice => (choice.Folder.Name, choice.Depth))
            .ShouldBe(new[] { ("All", 0), ("Databases", 1) });
    }

    [Fact]
    public void SnippetsNarrowToTheFocusedHost()
    {
        var f = Arrange();
        f.Model.Upsert(new Snippet { Name = "everywhere", Command = "uptime" });
        f.Model.Upsert(new Snippet { Name = "prod only", Command = "deploy", FolderId = f.Production.Id });

        f.Model.Selection = null;
        f.Model.VisibleSnippets.Count.ShouldBe(2);

        f.Model.Selection = f.Web.Id;
        f.Model.VisibleSnippets.Select(s => s.Name).Order(StringComparer.Ordinal)
            .ShouldBe(new[] { "everywhere", "prod only" });
    }
}

public class PendingDeletionTests
{
    [Fact]
    public void DeletingAHostSaysWhatGoesWithIt()
    {
        var host = new Connection { Name = "web-01", Hostname = "web-01" };
        var model = new InventoryViewModel(null, new InventoryTree(connections: [host]));

        model.ConfirmDelete(new PendingDeletion.Connection(host.Id));

        model.PendingDeletionMessage.ShouldNotBeNull().Title.ShouldBe("Delete web-01?");
        model.PendingDeletionMessage!.Value.Detail.ShouldBe("Its settings and port forwards will be removed.");
    }

    [Fact]
    public void DeletingAFolderCountsTheHostsItWouldTake()
    {
        // The confirmation has to name the consequence, not just ask twice.
        var folder = new Folder { Name = "Production" };
        var a = new Connection { ParentId = folder.Id, Name = "a", Hostname = "a" };
        var b = new Connection { ParentId = folder.Id, Name = "b", Hostname = "b" };
        var model = new InventoryViewModel(null, new InventoryTree([folder], [a, b]));

        model.ConfirmDelete(new PendingDeletion.Folder(folder.Id));

        model.PendingDeletionMessage!.Value.Detail.ShouldBe("2 hosts inside will be deleted too.");
    }

    [Fact]
    public void AnEmptyFolderSaysSo()
    {
        var folder = new Folder { Name = "Empty" };
        var model = new InventoryViewModel(null, new InventoryTree([folder]));

        model.ConfirmDelete(new PendingDeletion.Folder(folder.Id));

        model.PendingDeletionMessage!.Value.Detail.ShouldBe("The folder is empty.");
    }

    [Fact]
    public void DeletingACredentialCountsWhatWouldBeLeftWithout()
    {
        var credential = new Credential { Name = "Ops key" };
        var folder = new Folder { Name = "Production", Settings = new ConnectionSettings { CredentialId = credential.Id } };
        var model = new InventoryViewModel(null, new InventoryTree([folder], credentials: [credential]));

        model.ConfirmDelete(new PendingDeletion.Credential(credential.Id));

        model.PendingDeletionMessage!.Value.Detail
            .ShouldBe("1 host or folder using it will be left without a credential.");
    }

    [Fact]
    public void ConfirmingCarriesItOutAndClearsThePrompt()
    {
        var host = new Connection { Name = "web-01", Hostname = "web-01" };
        var model = new InventoryViewModel(null, new InventoryTree(connections: [host]));
        model.ConfirmDelete(new PendingDeletion.Connection(host.Id));

        model.PerformPendingDeletion();

        model.Tree.Connections.ShouldBeEmpty();
        model.Pending.ShouldBeNull();
        model.PendingDeletionMessage.ShouldBeNull();
    }
}
