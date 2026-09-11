using StrangeSharpTerm.Model;

namespace StrangeSharpTerm.Store.Tests;

public class SshConfigLexerTests
{
    [Fact]
    public void AllThreeSeparatorFormsAreAccepted()
    {
        SshConfigParser.Tokenize("Port 22")?.Arguments.ShouldBe(new[] { "22" });
        SshConfigParser.Tokenize("Port=22")?.Arguments.ShouldBe(new[] { "22" });
        SshConfigParser.Tokenize("Port = 22")?.Arguments.ShouldBe(new[] { "22" });
        SshConfigParser.Tokenize("  Port\t22")?.Arguments.ShouldBe(new[] { "22" });
    }

    [Fact]
    public void KeywordCasingIsPreserved()
    {
        SshConfigParser.Tokenize("HostName example.com")?.Keyword.ShouldBe("HostName");
    }

    [Fact]
    public void BlankAndCommentLinesProduceNoToken()
    {
        SshConfigParser.Tokenize("").ShouldBeNull();
        SshConfigParser.Tokenize("   ").ShouldBeNull();
        SshConfigParser.Tokenize("# a comment").ShouldBeNull();
        SshConfigParser.Tokenize("   # indented comment").ShouldBeNull();
    }

    [Fact]
    public void ATrailingCommentIsStrippedFromTheValue()
    {
        SshConfigParser.Tokenize("Port 2222 # the bastion")?.Arguments.ShouldBe(new[] { "2222" });
    }

    [Fact]
    public void QuotedArgumentsMayContainSpacesAndAHash()
    {
        SshConfigParser.Tokenize("IdentityFile \"/Users/me/My Keys/id_ed25519\"")?.Arguments
            .ShouldBe(new[] { "/Users/me/My Keys/id_ed25519" });
        // A '#' inside quotes is data, not the start of a comment.
        SshConfigParser.Tokenize("SetEnv \"TAG=a#b\"")?.Arguments.ShouldBe(new[] { "TAG=a#b" });
    }
}

public class SshConfigParseTests
{
    private const string Sample = """
        # Personal config
        Host *
            ServerAliveInterval 60
            Compression yes

        Host bastion
            HostName bastion.example.com
            User ops
            Port 2222
            IdentityFile ~/.ssh/id_ed25519

        Host db-01 db-02
            HostName shared-db.internal
            User postgres
            ProxyJump bastion
        """;

    [Fact]
    public void BlocksAreSplitOnHostHeaders()
    {
        var file = SshConfigParser.Parse(Sample);
        file.Blocks.Count.ShouldBe(3);
        file.Blocks[0].Header.ShouldBeOfType<SshConfigHeader.Host>().Patterns.ShouldBe(new[] { "*" });
    }

    [Fact]
    public void SeveralAliasesOnOneHostLineAreAllCaptured()
    {
        SshConfigParser.Parse(Sample).Blocks[2].Header.ShouldBeOfType<SshConfigHeader.Host>().Patterns
            .ShouldBe(new[] { "db-01", "db-02" });
    }

    [Fact]
    public void EntriesBeforeTheFirstHostLandInAnImplicitGlobalBlock()
    {
        var file = SshConfigParser.Parse("Compression yes\n\nHost a\n  User x\n");
        file.Blocks.Count.ShouldBe(2);
        file.Blocks[0].Header.ShouldBeOfType<SshConfigHeader.Global>();
        file.Blocks[0].ArgumentsFor("compression").ShouldBe(new[] { "yes" });
    }

    [Fact]
    public void AFileWithNoDirectivesYieldsNoBlocks()
    {
        SshConfigParser.Parse("# only a comment\n\n").Blocks.ShouldBeEmpty();
    }

    [Fact]
    public void RepeatedKeywordsAreAllRetained()
    {
        var file = SshConfigParser.Parse("Host a\n  IdentityFile ~/.ssh/one\n  IdentityFile ~/.ssh/two");
        file.Blocks[0].AllArgumentsFor("IdentityFile").Count.ShouldBe(2);
    }

    [Fact]
    public void IncludePathsAreSurfacedForALoaderToResolve()
    {
        SshConfigParser.Parse("Include ~/.ssh/work/*.conf\nHost a\n  User x\n").IncludePaths
            .ShouldBe(new[] { "~/.ssh/work/*.conf" });
    }

    [Fact]
    public void WindowsLineEndingsAreOneBreakNotTwo()
    {
        // The Swift parser split CRLF into two breaks, so every line number after
        // the first came out wrong for a file saved on Windows.
        var entries = SshConfigParser.Parse("Host a\r\n  User x\r\n  Port 22\r\n").Blocks.Single().Entries;

        entries.Select(e => e.LineNumber).ShouldBe(new[] { 2, 3 });
        entries.Select(e => e.RawLine).ShouldBe(new[] { "  User x", "  Port 22" });
    }
}

public class SshConfigImportTests
{
    private static SshConfigImportResult Import(string text) => SshConfigImporter.Import(SshConfigParser.Parse(text));

    [Fact]
    public void WildcardOnlyHostBlocksBecomeDefaultsNotServers()
    {
        var result = Import("Host *\n  User ops\n  Compression yes\n\nHost real\n  HostName real.example.com");

        result.Connections.Select(c => c.Name).ShouldBe(new[] { "real" });
        result.Defaults.Username.ShouldBe("ops");
        result.Defaults.Compression.ShouldBe(true);
    }

    [Fact]
    public void EachConcreteAliasBecomesItsOwnConnectionSharingTheHostName()
    {
        var result = Import("Host db-01 db-02\n  HostName shared.internal\n  User postgres");

        result.Connections.Select(c => c.Name).ShouldBe(new[] { "db-01", "db-02" });
        result.Connections.ShouldAllBe(c => c.Hostname == "shared.internal");
        result.Connections.ShouldAllBe(c => c.Settings.Username == "postgres");
    }

    [Fact]
    public void AnAliasWithNoHostNameConnectsToTheAliasItself()
    {
        Import("Host web\n  User deploy").Connections[0].Hostname.ShouldBe("web");
    }

    [Fact]
    public void APartiallyWildcardedHostLineKeepsOnlyItsConcreteAliases()
    {
        Import("Host prod-* bastion\n  User ops").Connections.Select(c => c.Name).ShouldBe(new[] { "bastion" });
    }

    [Fact]
    public void AProxyJumpChainIsSplitOnCommas()
    {
        Import("Host deep\n  ProxyJump edge,bastion").Connections[0].Settings.JumpHosts
            .ShouldBe(new[] { "edge", "bastion" });
    }

    [Fact]
    public void StrictHostKeyCheckingMapsOntoPolicyValues()
    {
        static HostKeyPolicy? Policy(string value) =>
            Import($"Host h\n  StrictHostKeyChecking {value}").Connections[0].Settings.HostKeyPolicy;

        Policy("yes").ShouldBe(HostKeyPolicy.Strict);
        Policy("no").ShouldBe(HostKeyPolicy.AcceptAny);
        Policy("accept-new").ShouldBe(HostKeyPolicy.AcceptNew);
        Policy("ask").ShouldBe(HostKeyPolicy.AcceptNew);
    }

    [Fact]
    public void SetEnvPairsBecomeEnvironmentEntries()
    {
        var environment = Import("Host h\n  SetEnv LANG=en_US.UTF-8 TZ=UTC").Connections[0].Settings.Environment.ShouldNotBeNull();

        environment.Count.ShouldBe(2);
        environment["LANG"].ShouldBe("en_US.UTF-8");
        environment["TZ"].ShouldBe("UTC");
    }

    [Fact]
    public void MatchBlocksAreSkippedWithAWarningRatherThanMisinterpreted()
    {
        var result = Import("Match host bastion exec \"test -f /tmp/x\"\n  User ops");

        result.Connections.ShouldBeEmpty();
        result.Warnings.Select(w => w.Reason).OfType<SshConfigWarningReason.MatchBlockSkipped>().ShouldHaveSingleItem();
    }

    [Fact]
    public void AnUnrecognisedKeywordIsReportedInsteadOfDroppedSilently()
    {
        Import("Host h\n  FrobnicateWidgets yes").Warnings.Select(w => w.Reason)
            .OfType<SshConfigWarningReason.UnsupportedKeyword>().ShouldHaveSingleItem()
            .Keyword.ShouldBe("FrobnicateWidgets");
    }

    [Fact]
    public void CommonKeywordsDeliberatelyNotModelledStayQuiet()
    {
        Import("Host h\n  ControlMaster auto\n  IdentitiesOnly yes").Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void ImportProducesAFolderCarryingTheDefaultsParentingEveryHost()
    {
        var (tree, _) = SshConfigImporter.Inventory(SshConfigParser.Parse("Host *\n  User ops\nHost web\n  HostName web.example.com"));

        var connection = tree.Connections.Values.First();
        // The host itself sets no User; it must inherit one from the defaults folder.
        connection.Settings.Username.ShouldBeNull();
        tree.Resolve(connection.Id).Settings.Username.ShouldBe("ops");
    }
}

public class ForwardParsingTests
{
    [Fact]
    public void TheTwoArgumentFormIsParsed()
    {
        var forward = SshConfigImporter.ParseForward(["8080", "localhost:80"], PortForwardKind.Local).ShouldNotBeNull();

        forward.BindPort.ShouldBe(8080);
        forward.DestinationHost.ShouldBe("localhost");
        forward.DestinationPort.ShouldBe(80);
        forward.BindAddress.ShouldBe("");
    }

    [Fact]
    public void TheColonJoinedFormIsParsed()
    {
        var forward = SshConfigImporter.ParseForward(["8080:localhost:80"], PortForwardKind.Local).ShouldNotBeNull();

        forward.BindPort.ShouldBe(8080);
        forward.DestinationHost.ShouldBe("localhost");
        forward.DestinationPort.ShouldBe(80);
    }

    [Fact]
    public void ABindAddressSurvivesTheColonJoinedForm()
    {
        var forward = SshConfigImporter.ParseForward(["127.0.0.1:8080:localhost:80"], PortForwardKind.Local).ShouldNotBeNull();

        forward.BindAddress.ShouldBe("127.0.0.1");
        forward.BindPort.ShouldBe(8080);
    }

    [Fact]
    public void ABracketedIpv6LiteralIsUnwrapped()
    {
        var endpoint = SshConfigImporter.ParseEndpoint("[::1]:8080").ShouldNotBeNull();

        endpoint.Host.ShouldBe("::1");
        endpoint.Port.ShouldBe(8080);
    }

    [Fact]
    public void MalformedForwardsAreRejectedRatherThanHalfParsed()
    {
        SshConfigImporter.ParseForward(["8080"], PortForwardKind.Local).ShouldBeNull();
        SshConfigImporter.ParseForward([], PortForwardKind.Local).ShouldBeNull();
        SshConfigImporter.ParseEndpoint("localhost").ShouldBeNull();
        SshConfigImporter.ParseEndpoint("localhost:notaport").ShouldBeNull();
    }

    [Fact]
    public void DynamicForwardYieldsASocksProxyDefinition()
    {
        var forwards = SshConfigImporter.Import(SshConfigParser.Parse("Host h\n  DynamicForward 1080"))
            .Connections[0].Settings.PortForwards ?? [];

        forwards.Count.ShouldBe(1);
        forwards[0].Kind.ShouldBe(PortForwardKind.Dynamic);
        forwards[0].BindPort.ShouldBe(1080);
    }
}

public class KnownHostsImportTests
{
    [Fact]
    public void UserKnownHostsFileMapsOntoTheConnectionsTrustStore()
    {
        var result = SshConfigImporter.Import(SshConfigParser.Parse("Host h\n  UserKnownHostsFile /tmp/run/known_hosts"));

        result.Connections[0].Settings.KnownHostsFile.ShouldBe("/tmp/run/known_hosts");
        result.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void ATildeInThePathIsExpanded()
    {
        var path = SshConfigImporter.Import(SshConfigParser.Parse("Host h\n  UserKnownHostsFile ~/.ssh/work_hosts"))
            .Connections[0].Settings.KnownHostsFile.ShouldNotBeNull();

        path.ShouldNotStartWith("~");
        path.ShouldEndWith("/.ssh/work_hosts");
    }
}
