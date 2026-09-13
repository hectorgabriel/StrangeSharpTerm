using System.Text.Json;
using StrangeSharpTerm.Mcp;

namespace StrangeSharpTerm.Mcp.Tests;

public class ConfigTests
{
    [Fact]
    public void TheTwoSwitchesDifferOnPurpose()
    {
        var settings = new McpSettings();

        // Connecting a server is already the deliberate act, and every call
        // still asks.
        settings.OfferInPanes.ShouldBeTrue();
        // A fan-out points the same tools at the same place from every host.
        settings.OfferInRuns.ShouldBeFalse();
    }

    [Fact]
    public void AServerSurvivesARoundTrip()
    {
        var settings = new McpSettings
        {
            OfferInRuns = true,
            Servers =
            [
                new McpServerConfig
                {
                    Name = "Runbooks",
                    Command = "npx",
                    Arguments = ["-y", "@modelcontextprotocol/server-filesystem", "~/runbooks"],
                    AlwaysAllowed = ["read_file"],
                },
                new McpServerConfig
                {
                    Name = "Grafana",
                    Transport = McpTransport.Http,
                    Url = "https://metrics.example.com/mcp",
                },
            ],
        };

        var json = JsonSerializer.Serialize(McpDocument.From(settings));
        var back = McpDocument.Read(JsonDocument.Parse(json).RootElement);

        back.OfferInRuns.ShouldBeTrue();
        back.Servers.Count.ShouldBe(2);
        back.Servers[0].Command.ShouldBe("npx");
        back.Servers[0].Arguments.Count.ShouldBe(3);
        back.Servers[0].AlwaysAllowed.ShouldBe(["read_file"]);
        back.Servers[0].Id.ShouldBe(settings.Servers[0].Id);
        back.Servers[1].Transport.ShouldBe(McpTransport.Http);
        back.Servers[1].Url.ShouldBe("https://metrics.example.com/mcp");
    }

    [Fact]
    public void NoSecretIsEverWritten()
    {
        var settings = new McpSettings
        {
            Servers = [new McpServerConfig { Name = "Grafana", Transport = McpTransport.Http, Url = "https://metrics.example.com/mcp" }],
        };

        // An HTTP token and any OAuth tokens live in the platform store, and this
        // shape has nowhere to put one.
        var json = JsonSerializer.Serialize(McpDocument.From(settings));

        json.ShouldNotContain("token", Case.Insensitive);
        json.ShouldNotContain("secret", Case.Insensitive);
        json.ShouldNotContain("password", Case.Insensitive);
    }

    [Fact]
    public void AnUnreadableSectionLoadsAsNoServers() =>
        McpDocument.Read(JsonDocument.Parse("\"nonsense\"").RootElement).Servers.ShouldBeEmpty();

    [Fact]
    public void AbsentIsNoServers() => McpDocument.Read(null).Servers.ShouldBeEmpty();

    [Fact]
    public void AFileWrittenByHandStillLoads()
    {
        var section = JsonDocument.Parse("""
        {"servers":[{"name":"Files","command":"npx","arguments":["-y","server-filesystem"]}]}
        """).RootElement;

        var settings = McpDocument.Read(section);

        var server = settings.Servers.ShouldHaveSingleItem();
        server.Name.ShouldBe("Files");
        server.Transport.ShouldBe(McpTransport.Local);
        server.IsEnabled.ShouldBeTrue();
        server.IsUsable.ShouldBeTrue();
    }

    [Theory]
    [InlineData("", "npx", "", "It needs a name.")]
    [InlineData("Files", "", "", "It needs a command to run.")]
    public void AServerSaysWhyItCannotBeConnectedBeforeItIsTried(
        string name, string command, string url, string expected) =>
        new McpServerConfig { Name = name, Command = command, Url = url }.Problem.ShouldBe(expected);

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://example.com")]
    [InlineData("")]
    public void AnHttpServerNeedsAnHttpAddress(string url) =>
        new McpServerConfig { Name = "Grafana", Transport = McpTransport.Http, Url = url }
            .Problem.ShouldBe("It needs an http or https address.");

    [Fact]
    public void AWellFormedServerHasNoProblem() =>
        new McpServerConfig { Name = "Grafana", Transport = McpTransport.Http, Url = "https://metrics.example.com/mcp" }
            .Problem.ShouldBeNull();

    [Fact]
    public void OnlyEnabledAndUsableServersAreTried()
    {
        var settings = new McpSettings
        {
            Servers =
            [
                new McpServerConfig { Name = "Good", Command = "npx" },
                new McpServerConfig { Name = "Off", Command = "npx", IsEnabled = false },
                new McpServerConfig { Name = "Broken", Command = "" },
            ],
        };

        settings.Usable.Select(server => server.Name).ShouldBe(["Good"]);
    }

    [Fact]
    public void TheRowSaysWhereTheServerIs()
    {
        new McpServerConfig { Name = "Files", Command = "npx", Arguments = ["-y", "server-filesystem"] }
            .Where.ShouldBe("npx -y server-filesystem");
        new McpServerConfig { Name = "Grafana", Transport = McpTransport.Http, Url = "https://metrics.example.com/mcp" }
            .Where.ShouldBe("https://metrics.example.com/mcp");
    }

    [Fact]
    public void ALocalServerIsNotANetworkDestination()
    {
        // Saying so is as much a part of the choice as naming the host an HTTP
        // one reaches.
        McpConnection.Destination(new McpServerConfig { Name = "Files", Command = "npx" })
            .ShouldBe("this machine");
        McpConnection.Destination(new McpServerConfig
        {
            Name = "Grafana",
            Transport = McpTransport.Http,
            Url = "https://metrics.example.com/mcp",
        }).ShouldBe("metrics.example.com");
    }

    [Fact]
    public void ArgumentsAreShownAsAPersonReadsThem()
    {
        var pretty = McpHub.Pretty("""{"query":"up","range":"1h"}""");

        pretty.ShouldContain("\n");
        pretty.ShouldContain("\"query\": \"up\"");
    }

    [Fact]
    public void UnparseableArgumentsAreShownExactlyAsTheyArrived() =>
        // Whatever is about to be sent is what a person needs to see.
        McpHub.Pretty("{not json").ShouldBe("{not json");
}
