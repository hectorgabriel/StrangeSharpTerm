namespace StrangeSharpTerm.Model.Tests;

/// <summary>A three-level tree: Production &gt; Databases &gt; db-01, used by most tests below.</summary>
internal sealed class Fixture
{
    public Fixture(
        ConnectionSettings? rootSettings = null,
        ConnectionSettings? midSettings = null,
        ConnectionSettings? leafSettings = null)
    {
        Tree = new InventoryTree(
            folders:
            [
                new Folder { Id = Root, Name = "Production", Settings = rootSettings ?? ConnectionSettings.Empty },
                new Folder { Id = Mid, ParentId = Root, Name = "Databases", Settings = midSettings ?? ConnectionSettings.Empty },
            ],
            connections:
            [
                new Connection
                {
                    Id = Leaf, ParentId = Mid, Name = "db-01", Hostname = "db-01.example.com",
                    Settings = leafSettings ?? ConnectionSettings.Empty,
                },
            ]);
    }

    public NodeId Root { get; } = NodeId.New();
    public NodeId Mid { get; } = NodeId.New();
    public NodeId Leaf { get; } = NodeId.New();
    public InventoryTree Tree { get; }
}

public class InheritanceTests
{
    [Fact]
    public void NearestAncestorWinsForScalarSettings()
    {
        var f = new Fixture(
            rootSettings: new ConnectionSettings { Username = "root", Port = 22 },
            midSettings: new ConnectionSettings { Username = "dba" });
        var resolved = f.Tree.Resolve(f.Leaf);

        resolved.Settings.Username.ShouldBe("dba"); // mid beats root
        resolved.Settings.Port.ShouldBe(22);        // root still supplies port
    }

    [Fact]
    public void AConnectionsOwnValueBeatsEveryFolderAboveIt()
    {
        var f = new Fixture(
            rootSettings: new ConnectionSettings { Username = "root" },
            midSettings: new ConnectionSettings { Username = "dba" },
            leafSettings: new ConnectionSettings { Username = "postgres" });

        f.Tree.Resolve(f.Leaf).Settings.Username.ShouldBe("postgres");
    }

    [Fact]
    public void EnvironmentMergesKeyWiseRatherThanReplacingWholesale()
    {
        var f = new Fixture(
            rootSettings: new ConnectionSettings
            {
                Environment = new Dictionary<string, string> { ["LANG"] = "en_US.UTF-8", ["TZ"] = "UTC" },
            },
            midSettings: new ConnectionSettings
            {
                Environment = new Dictionary<string, string> { ["TZ"] = "Europe/Madrid" },
            },
            leafSettings: new ConnectionSettings
            {
                Environment = new Dictionary<string, string> { ["PGDATABASE"] = "orders" },
            });
        var environment = f.Tree.Resolve(f.Leaf).Settings.Environment;

        environment["LANG"].ShouldBe("en_US.UTF-8");  // survives from the root
        environment["TZ"].ShouldBe("Europe/Madrid");  // mid overrides the root's key
        environment["PGDATABASE"].ShouldBe("orders"); // leaf contributes its own
        environment.Count.ShouldBe(3);
    }

    [Fact]
    public void PortForwardsAccumulateDownTheChainInsteadOfOverriding()
    {
        var bastion = new PortForward
        {
            Kind = PortForwardKind.Local, Name = "bastion", BindPort = 2222,
            DestinationHost = "10.0.0.1", DestinationPort = 22,
        };
        var postgres = new PortForward
        {
            Kind = PortForwardKind.Local, Name = "postgres", BindPort = 5432,
            DestinationHost = "localhost", DestinationPort = 5432,
        };
        var f = new Fixture(
            rootSettings: new ConnectionSettings { PortForwards = [bastion] },
            leafSettings: new ConnectionSettings { PortForwards = [postgres] });

        f.Tree.Resolve(f.Leaf).Settings.PortForwards.Select(p => p.Name)
            .ShouldBe(new[] { "bastion", "postgres" }); // ancestors first
    }

    [Fact]
    public void RefoldingAnAlreadyMergedSetDoesNotDuplicateForwards()
    {
        var forward = new PortForward
        {
            Kind = PortForwardKind.Local, Name = "one", BindPort = 9000,
            DestinationHost = "localhost", DestinationPort = 9000,
        };
        var parent = new ConnectionSettings { PortForwards = [forward] };

        parent.InheritingFrom(parent).PortForwards.ShouldNotBeNull().Count.ShouldBe(1);
    }

    [Fact]
    public void TheResolvedChainNamesTheFoldersThatContributedOutermostFirst()
    {
        var f = new Fixture();
        f.Tree.Resolve(f.Leaf).InheritanceChain.ShouldBe(new[] { f.Root, f.Mid });
    }
}

public class InventoryIntegrityTests
{
    [Fact]
    public void AParentCycleIsReportedInsteadOfHanging()
    {
        NodeId a = NodeId.New(), b = NodeId.New();
        var tree = new InventoryTree(
            folders: [new Folder { Id = a, ParentId = b, Name = "A" }, new Folder { Id = b, ParentId = a, Name = "B" }],
            connections: [new Connection { ParentId = a, Name = "x", Hostname = "x" }]);

        Should.Throw<InventoryCycleException>(() => tree.Ancestors(a));
    }

    [Fact]
    public void ResolvingAnUnknownConnectionIsAnErrorNotACrash()
    {
        var missing = NodeId.New();
        Should.Throw<UnknownConnectionException>(() => new InventoryTree().Resolve(missing)).Id.ShouldBe(missing);
    }

    [Fact]
    public void AFolderCannotBeDraggedIntoItsOwnDescendant()
    {
        var f = new Fixture();
        f.Tree.CanReparent(f.Root, f.Mid).ShouldBeFalse();  // would orphan the tree
        f.Tree.CanReparent(f.Root, f.Root).ShouldBeFalse(); // onto itself
        f.Tree.CanReparent(f.Mid, null).ShouldBeTrue();     // out to the root is fine
    }

    [Fact]
    public void ListingDescendantsOfACycleTerminates()
    {
        // The Swift version recursed forever here. In .NET that is a stack overflow,
        // which no catch block survives.
        NodeId a = NodeId.New(), b = NodeId.New();
        var tree = new InventoryTree(
            folders: [new Folder { Id = a, ParentId = b, Name = "A" }, new Folder { Id = b, ParentId = a, Name = "B" }]);

        tree.DescendantFolders(a).ShouldBe(new[] { b });
    }
}

public class ResolvedSettingsTests
{
    [Fact]
    public void UnspecifiedSettingsFallBackToProductDefaults()
    {
        var resolved = new ResolvedSettings(ConnectionSettings.Empty);

        resolved.HostKeyPolicy.ShouldBe(HostKeyPolicy.AcceptNew);
        resolved.ConnectTimeout.ShouldBe(15);
        resolved.Compression.ShouldBeFalse();
        resolved.ForwardAgent.ShouldBeFalse();
    }

    [Fact]
    public void UsernameAndPortStayUnsetSoTheirAbsenceIsVisible()
    {
        // Inventing defaults here would make "unset" indistinguishable from "set".
        var resolved = new ResolvedSettings(ConnectionSettings.Empty);

        resolved.Username.ShouldBeNull();
        resolved.Port.ShouldBeNull();
    }
}

public class PortForwardTests
{
    [Fact]
    public void LocalAndRemoteForwardsRenderTheFullFourPartSpec()
    {
        var forward = new PortForward
        {
            Kind = PortForwardKind.Local, Name = "pg", BindPort = 5432,
            DestinationHost = "10.0.0.5", DestinationPort = 5432,
        };
        forward.SshSpecification.ShouldBe("5432:10.0.0.5:5432");
        forward.SshFlag.ShouldBe("-L");
    }

    [Fact]
    public void AnExplicitBindAddressIsIncluded()
    {
        var forward = new PortForward
        {
            Kind = PortForwardKind.Local, Name = "pg", BindAddress = "0.0.0.0", BindPort = 5432,
            DestinationHost = "10.0.0.5", DestinationPort = 5432,
        };
        forward.SshSpecification.ShouldBe("0.0.0.0:5432:10.0.0.5:5432");
    }

    [Fact]
    public void ADynamicForwardCarriesOnlyAListeningPort()
    {
        var forward = new PortForward { Kind = PortForwardKind.Dynamic, Name = "socks", BindPort = 1080 };
        forward.SshSpecification.ShouldBe("1080");
        forward.SshFlag.ShouldBe("-D");
    }

    [Fact]
    public void PrivilegedBindsAreFlaggedBeforeTheUserWaitsOnADoomedConnection()
    {
        static PortForward Forward(PortForwardKind kind, int bindPort) => new()
        {
            Kind = kind, Name = "x", BindPort = bindPort, DestinationHost = "h", DestinationPort = 80,
        };

        Forward(PortForwardKind.Local, 80).RequiresPrivilegedBind.ShouldBeTrue();
        Forward(PortForwardKind.Local, 8080).RequiresPrivilegedBind.ShouldBeFalse();
        // Remote forwards bind on the server, so our local privileges are irrelevant.
        Forward(PortForwardKind.Remote, 80).RequiresPrivilegedBind.ShouldBeFalse();
    }
}
