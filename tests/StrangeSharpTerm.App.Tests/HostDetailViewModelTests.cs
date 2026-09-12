using StrangeSharpTerm.App.ViewModels;
using StrangeSharpTerm.Model;

namespace StrangeSharpTerm.App.Tests;

public class HostDetailViewModelTests
{
    private static HostDetailViewModel Detail(ConnectionSettings settings, ConnectionSettings? folderSettings = null)
    {
        var folder = new Folder { Name = "Production", Settings = folderSettings ?? ConnectionSettings.Empty };
        var host = new Connection
        {
            ParentId = folder.Id, Name = "db-01", Hostname = "db-01.internal", Settings = settings, Tags = ["postgres"],
        };
        return new HostDetailViewModel(new InventoryTree([folder], [host]).Resolve(host.Id));
    }

    private static string ValueOf(HostDetailViewModel detail, string label) =>
        detail.Fields.Single(field => field.Label == label).Value;

    [Fact]
    public void TheHeadlineReadsAsSomethingYouCouldTypeAtASshPrompt()
    {
        var detail = Detail(new ConnectionSettings { Username = "postgres", Port = 2222 });

        detail.UserAtHost.ShouldBe("postgres@db-01.internal:2222");
    }

    [Fact]
    public void PartsThatAreNotSetAreLeftOutOfTheHeadline()
    {
        Detail(ConnectionSettings.Empty).UserAtHost.ShouldBe("db-01.internal");
    }

    [Fact]
    public void AnUnsetPortSaysWhatWillActuallyBeUsed()
    {
        ValueOf(Detail(ConnectionSettings.Empty), "Port").ShouldBe("22 (default)");
    }

    [Fact]
    public void AnUnsetUsernameNamesTheAccountThatWillBeUsed()
    {
        // The Swift app said "from ssh_config", which was true when it shelled out
        // to ssh. Nothing reads that file at connect time now.
        ValueOf(Detail(ConnectionSettings.Empty), "Username")
            .ShouldBe($"{Environment.UserName} (yours)");
    }

    [Fact]
    public void InheritedSettingsAreShownAsTheHostWillUseThem()
    {
        // The point of the pane: what a connection would do, not what was typed
        // into this one host.
        var detail = Detail(ConnectionSettings.Empty, new ConnectionSettings { Username = "deploy", Port = 2200 });

        detail.UserAtHost.ShouldBe("deploy@db-01.internal:2200");
        detail.InheritanceChain.Count.ShouldBe(1);
    }

    [Fact]
    public void AHostThatAcceptsAnyKeyIsMarkedAsDangerous()
    {
        var field = Detail(new ConnectionSettings { HostKeyPolicy = HostKeyPolicy.AcceptAny })
            .Fields.Single(f => f.Label == "Host keys");

        field.Value.ShouldBe("Any key, without asking");
        field.Emphasis.ShouldBe(FieldEmphasis.Danger);
    }

    [Fact]
    public void AgentForwardingIsWorthNoticingWhenItIsOn()
    {
        Detail(new ConnectionSettings { ForwardAgent = true })
            .Fields.Single(f => f.Label == "Agent forwarding").Emphasis.ShouldBe(FieldEmphasis.Warning);
        Detail(ConnectionSettings.Empty)
            .Fields.Single(f => f.Label == "Agent forwarding").Emphasis.ShouldBe(FieldEmphasis.None);
    }

    [Fact]
    public void JumpHostsReadInTheOrderTheyAreTravelled()
    {
        ValueOf(Detail(new ConnectionSettings { JumpHosts = ["edge", "bastion"] }), "Jump hosts")
            .ShouldBe("edge → bastion");
    }

    [Fact]
    public void AForwardThatCannotBindSaysSoBeforeItIsTried()
    {
        var detail = Detail(new ConnectionSettings
        {
            PortForwards =
            [
                new PortForward { Kind = PortForwardKind.Local, Name = "http", BindPort = 80, DestinationHost = "h", DestinationPort = 80 },
                new PortForward { Kind = PortForwardKind.Local, Name = "alt", BindPort = 8080, DestinationHost = "h", DestinationPort = 80 },
            ],
        });

        detail.Forwards[0].ShouldBe(new ForwardSummary("-L", "80:h:80", "http", NeedsRoot: true));
        detail.Forwards[1].NeedsRoot.ShouldBeFalse();
    }

    [Fact]
    public void EnvironmentIsSortedBecauseADictionarysOwnOrderMeansNothing()
    {
        var detail = Detail(new ConnectionSettings
        {
            Environment = new Dictionary<string, string> { ["TZ"] = "UTC", ["AWS_PROFILE"] = "prod", ["LANG"] = "C" },
        });

        detail.Environment.Select(pair => pair.Key).ShouldBe(new[] { "AWS_PROFILE", "LANG", "TZ" });
    }
}
