using StrangeSharpTerm.App.ViewModels;
using StrangeSharpTerm.Model;
using StrangeSharpTerm.Store;

namespace StrangeSharpTerm.App.Tests;

public class InventoryLoaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"st-app-{Guid.NewGuid():N}");

    private string PathTo(string name) => Path.Combine(_directory, name);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void ASavedInventoryIsWhatOpens()
    {
        var path = PathTo("inventory.json");
        new InventoryStore(path).Save(new InventoryTree(connections: [new Connection { Name = "db-01", Hostname = "db-01" }]));

        var model = InventoryLoader.Load(path, sshConfigPath: null, inherited: []);

        model.Tree.Connections.Values.Single().Name.ShouldBe("db-01");
    }

    [Fact]
    public void WithNothingSavedTheUsersSshConfigIsImportedAndWrittenOut()
    {
        // Reading the hosts someone already has is what makes a first launch
        // useful rather than empty, and the file has to match the window from the
        // moment it opens.
        var config = PathTo("config");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(config, "Host web\n  HostName web.example.com\n  User deploy\n");
        var path = PathTo("inventory.json");

        var model = InventoryLoader.Load(path, config, inherited: []);

        model.Tree.Connections.Values.Single().Hostname.ShouldBe("web.example.com");
        new InventoryStore(path).Load().ShouldNotBeNull().Connections.Count.ShouldBe(1);
    }

    [Fact]
    public void WithNothingAtAllTheAppOpensEmptyRatherThanInventingHosts()
    {
        // Shipping fake hosts that cannot connect would be worse than showing nothing.
        var model = InventoryLoader.Load(PathTo("inventory.json"), PathTo("missing-config"), inherited: []);

        model.Tree.Connections.ShouldBeEmpty();
        model.Tree.Folders.ShouldBeEmpty();
    }

    [Fact]
    public void AFirstRunInheritsTheInventoryOfAnEarlierInstall()
    {
        var earlier = PathTo("swift.json");
        new InventoryStore(earlier).Save(new InventoryTree(connections: [new Connection { Name = "inherited", Hostname = "h" }]));
        var path = PathTo("inventory.json");

        var model = InventoryLoader.Load(path, sshConfigPath: null, inherited: [earlier]);

        model.Tree.Connections.Values.Single().Name.ShouldBe("inherited");
        File.Exists(earlier).ShouldBeTrue();
    }

    [Fact]
    public void AnUnreadableInventoryOpensEmptyAndIsNotOverwritten()
    {
        // Losing someone's hosts to a failed read would be the worst possible
        // response to a file this version does not understand.
        var path = PathTo("inventory.json");
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(path, new InventoryDocument { Version = 99, Folders = [], Connections = [] }.ToUtf8Json());

        var model = InventoryLoader.Load(path, sshConfigPath: null, inherited: []);
        model.Upsert(new Connection { Name = "new", Hostname = "new" });

        model.Tree.Connections.Count.ShouldBe(1);
        System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path)).ShouldContain("\"version\" : 99");
    }
}
