using System.Text.Json;
using StrangeSharpTerm.Model;

namespace StrangeSharpTerm.Store.Tests;

internal sealed class TemporaryDirectory : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"st-store-{Guid.NewGuid():N}");

    public string PathTo(string name) => Path.Combine(Root, name);

    public void Dispose()
    {
        if (Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);
    }
}

public class InventoryStoreTests
{
    [Fact]
    public void NothingSavedYetReadsAsNullWhichIsHowFirstLaunchIsDetected()
    {
        using var directory = new TemporaryDirectory();
        new InventoryStore(directory.PathTo(InventoryStore.FileName)).Load().ShouldBeNull();
    }

    [Fact]
    public void ATreeSurvivesASaveAndLoad()
    {
        using var directory = new TemporaryDirectory();
        var folder = new Folder { Name = "Production", Settings = new ConnectionSettings { Username = "deploy" } };
        var connection = new Connection { ParentId = folder.Id, Name = "web-01", Hostname = "web-01.example.com", Tags = ["nginx"] };
        var store = new InventoryStore(directory.PathTo(InventoryStore.FileName));

        store.Save(new InventoryTree([folder], [connection]));
        var loaded = store.Load().ShouldNotBeNull();

        loaded.Folders.Count.ShouldBe(1);
        loaded.Connections.Count.ShouldBe(1);
        loaded.Connections[connection.Id].Hostname.ShouldBe("web-01.example.com");
        loaded.Folders[folder.Id].Settings.Username.ShouldBe("deploy");
    }

    [Fact]
    public void InheritanceStillResolvesAfterARoundTrip()
    {
        using var directory = new TemporaryDirectory();
        var folder = new Folder
        {
            Name = "Production",
            Settings = new ConnectionSettings { Username = "deploy", HostKeyPolicy = HostKeyPolicy.Strict },
        };
        var connection = new Connection { ParentId = folder.Id, Name = "web-01", Hostname = "web-01.example.com" };
        var store = new InventoryStore(directory.PathTo(InventoryStore.FileName));

        store.Save(new InventoryTree([folder], [connection]));
        var resolved = store.Load().ShouldNotBeNull().Resolve(connection.Id);

        resolved.Settings.Username.ShouldBe("deploy");
        resolved.Settings.HostKeyPolicy.ShouldBe(HostKeyPolicy.Strict);
    }

    [Fact]
    public void PortForwardsAndEnvironmentSurvive()
    {
        using var directory = new TemporaryDirectory();
        var forward = new PortForward
        {
            Kind = PortForwardKind.Local, Name = "pg", BindPort = 5432, DestinationHost = "localhost", DestinationPort = 5432,
        };
        var connection = new Connection
        {
            Name = "db", Hostname = "db.example.com",
            Settings = new ConnectionSettings
            {
                Environment = new Dictionary<string, string> { ["LANG"] = "en_US.UTF-8" },
                PortForwards = [forward],
            },
        };
        var store = new InventoryStore(directory.PathTo(InventoryStore.FileName));

        store.Save(new InventoryTree(connections: [connection]));
        var loaded = store.Load().ShouldNotBeNull().Connections[connection.Id];

        loaded.Settings.PortForwards.ShouldNotBeNull()[0].BindPort.ShouldBe(5432);
        loaded.Settings.Environment.ShouldNotBeNull()["LANG"].ShouldBe("en_US.UTF-8");
    }

    [Fact]
    public void SavingTwiceReplacesRatherThanAccumulating()
    {
        using var directory = new TemporaryDirectory();
        var store = new InventoryStore(directory.PathTo(InventoryStore.FileName));
        store.Save(new InventoryTree(connections: [new Connection { Name = "a", Hostname = "a" }]));
        store.Save(new InventoryTree(connections: [new Connection { Name = "b", Hostname = "b" }]));

        var loaded = store.Load().ShouldNotBeNull();
        loaded.Connections.Count.ShouldBe(1);
        loaded.Connections.Values.Single().Name.ShouldBe("b");
    }

    [Fact]
    public void AFileFromANewerVersionIsRefusedInsteadOfSilentlyTruncated()
    {
        // Decoding a newer shape with an older reader drops fields, and the next save
        // would make that loss permanent.
        using var directory = new TemporaryDirectory();
        var path = directory.PathTo(InventoryStore.FileName);
        Directory.CreateDirectory(directory.Root);
        File.WriteAllBytes(path, new InventoryDocument { Version = 99, Folders = [], Connections = [] }.ToUtf8Json());

        Should.Throw<UnsupportedInventoryVersionException>(() => new InventoryStore(path).Load()).Found.ShouldBe(99);
    }

    [Fact]
    public void SavingCreatesTheContainingDirectory()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.PathTo(InventoryStore.FileName);
        new InventoryStore(path).Save(new InventoryTree(connections: [new Connection { Name = "a", Hostname = "a" }]));

        File.Exists(path).ShouldBeTrue();
    }

    [Fact]
    public void SavingLeavesNoTemporaryFileBehind()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.PathTo(InventoryStore.FileName);
        var store = new InventoryStore(path);
        store.Save(new InventoryTree());
        store.Save(new InventoryTree());

        Directory.GetFiles(directory.Root).ShouldBe(new[] { path });
    }

    [Fact]
    public void AMissingRequiredFieldIsRefusedRatherThanDefaulted()
    {
        // Swift's decoder refuses a connection without tags; defaulting here instead
        // would quietly accept files the Swift app considers corrupt.
        var json = $$"""{"version":1,"folders":[],"connections":[{"id":{"rawValue":"{{Guid.NewGuid()}}"},"name":"web","hostname":"web","settings":{},"sortIndex":0,"importedFromSSHConfig":false}]}""";

        Should.Throw<JsonException>(() => InventoryDocument.Parse(System.Text.Encoding.UTF8.GetBytes(json)));
    }
}

public class SnippetPersistenceTests
{
    [Fact]
    public void SnippetsSurviveARoundTrip()
    {
        using var directory = new TemporaryDirectory();
        var store = new InventoryStore(directory.PathTo(InventoryStore.FileName));
        var snippet = new Snippet { Name = "tail syslog", Command = "tail -f {{path}}", Tags = ["logs"] };
        store.Save(new InventoryTree(snippets: [snippet]));

        var loaded = store.Load().ShouldNotBeNull();
        loaded.Snippets.Count.ShouldBe(1);
        loaded.Snippets[snippet.Id].Command.ShouldBe("tail -f {{path}}");
    }

    [Fact]
    public void AFileWrittenBeforeSnippetsExistedStillLoads()
    {
        // Bumping the document version instead would refuse these outright and lose
        // the user's hosts.
        using var directory = new TemporaryDirectory();
        var path = directory.PathTo(InventoryStore.FileName);
        Directory.CreateDirectory(directory.Root);
        File.WriteAllText(path, $$"""{"version":1,"folders":[],"connections":[{"id":{"rawValue":"{{Guid.NewGuid().ToString().ToUpperInvariant()}}"},"name":"web","hostname":"web.example.com","settings":{},"tags":[],"sortIndex":0,"importedFromSSHConfig":false}]}""");

        var loaded = new InventoryStore(path).Load().ShouldNotBeNull();
        loaded.Connections.Count.ShouldBe(1);
        loaded.Snippets.ShouldBeEmpty();
    }

    [Fact]
    public void FolderScopedSnippetsAreOfferedOnlyInsideThatFolder()
    {
        var production = new Folder { Name = "Production" };
        var other = new Folder { Name = "Personal" };
        var inside = new Connection { ParentId = production.Id, Name = "web", Hostname = "web" };
        var outside = new Connection { ParentId = other.Id, Name = "home", Hostname = "home" };
        var tree = new InventoryTree(
            [production, other],
            [inside, outside],
            snippets:
            [
                new Snippet { Name = "everywhere", Command = "uptime" },
                new Snippet { Name = "prod only", Command = "deploy", FolderId = production.Id },
            ]);

        tree.SnippetsFor(inside.Id).Select(s => s.Name).Order(StringComparer.Ordinal).ShouldBe(new[] { "everywhere", "prod only" });
        tree.SnippetsFor(outside.Id).Select(s => s.Name).ShouldBe(new[] { "everywhere" });
    }
}

public class SwiftAppImportTests
{
    [Fact]
    public void TheSwiftAppsInventoryIsCopiedWhenThereIsNoneYet()
    {
        using var directory = new TemporaryDirectory();
        var swift = directory.PathTo("swift.json");
        var destination = directory.PathTo(Path.Combine("new", InventoryStore.FileName));
        new InventoryStore(swift).Save(new InventoryTree(connections: [new Connection { Name = "a", Hostname = "a" }]));

        InventoryStore.ImportIfAbsent(destination, [directory.PathTo("missing.json"), swift]).ShouldBeTrue();

        File.ReadAllBytes(destination).ShouldBe(File.ReadAllBytes(swift));
        File.Exists(swift).ShouldBeTrue(); // left in place for the Swift app
    }

    [Fact]
    public void AnExistingInventoryIsNeverOverwritten()
    {
        using var directory = new TemporaryDirectory();
        var swift = directory.PathTo("swift.json");
        var destination = directory.PathTo(InventoryStore.FileName);
        new InventoryStore(swift).Save(new InventoryTree(connections: [new Connection { Name = "old", Hostname = "old" }]));
        new InventoryStore(destination).Save(new InventoryTree(connections: [new Connection { Name = "mine", Hostname = "mine" }]));

        InventoryStore.ImportIfAbsent(destination, [swift]).ShouldBeFalse();

        new InventoryStore(destination).Load().ShouldNotBeNull().Connections.Values.Single().Name.ShouldBe("mine");
    }

    [Fact]
    public void AnInventoryThisVersionCannotReadIsRefusedBeforeCopying()
    {
        using var directory = new TemporaryDirectory();
        var swift = directory.PathTo("swift.json");
        var destination = directory.PathTo(InventoryStore.FileName);
        Directory.CreateDirectory(directory.Root);
        File.WriteAllBytes(swift, new InventoryDocument { Version = 99, Folders = [], Connections = [] }.ToUtf8Json());

        Should.Throw<UnsupportedInventoryVersionException>(() => InventoryStore.ImportIfAbsent(destination, [swift]));
        File.Exists(destination).ShouldBeFalse();
    }
}
