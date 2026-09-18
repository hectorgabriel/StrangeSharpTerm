using StrangeSharpTerm.App.Assistant;
using StrangeSharpTerm.App.ViewModels;
using StrangeSharpTerm.Mcp;

namespace StrangeSharpTerm.App.Tests;

/// <summary>
/// The few things you can type that are not questions.
///
/// What matters most here is the line between the two. A question that happens
/// to start with a path is a question, and sending it as one is what the person
/// meant; a command is answered here, costs nothing, and never reaches a
/// provider.
/// </summary>
public class ChatCommandTests
{
    [Theory]
    [InlineData("/clear")]
    [InlineData("/mcp")]
    [InlineData("/help")]
    [InlineData("  /help  ")]
    [InlineData("/Clear")]
    public void TheseAreCommands(string text) => ChatCommands.Looks(text).ShouldBeTrue();

    [Theory]
    [InlineData("/etc/nginx is missing, why?")]
    [InlineData("why is /var/log full?")]
    [InlineData("/")]
    [InlineData("/2fa")]
    [InlineData("")]
    public void AndTheseAreQuestions(string text) => ChatCommands.Looks(text).ShouldBeFalse();

    [Fact]
    public void TheNameIsTheWordAfterTheSlash()
    {
        ChatCommands.Name("/mcp").ShouldBe("mcp");
        ChatCommands.Name("  /HELP me ").ShouldBe("help");
    }

    [Fact]
    public void TheListingNamesEveryOneThereIs()
    {
        // The only list of these, so a command that is not in it is one nobody
        // can find.
        ChatCommands.Listing.ShouldContain("/clear");
        ChatCommands.Listing.ShouldContain("/mcp");
        ChatCommands.Listing.ShouldContain("/help");
    }

    [Fact]
    public void WithNoServersItSaysWhereToAttachThem() =>
        ConnectedToolsReport.Of(null).ShouldContain("Settings");

    [Fact]
    public void AnAttachedServerIsListedWhetherOrNotItConnected()
    {
        // Read from the configuration rather than from what connected: a status
        // exists only once a server has been tried, so reporting statuses alone
        // answers "what is attached?" with silence in the very case somebody
        // asked the question.
        var hub = new McpHub(new McpSettings
        {
            Servers = [new McpServerConfig { Name = "Grafana", Transport = McpTransport.Http, Url = "https://x/mcp" }],
        });

        ConnectedToolsReport.Of(hub).ShouldContain("Grafana");
    }

    [Fact]
    public void AServerThatFailedToStartIsInTheReport()
    {
        // The case somebody types /mcp to find out about, and the one a model
        // cannot see: it was never handed the tools of a server that is not
        // running.
        var hub = new McpHub(new McpSettings
        {
            Servers =
            [
                new McpServerConfig { Name = "Runbooks", Command = "npx", Arguments = ["-y", "server"] },
            ],
        });

        var report = ConnectedToolsReport.Of(hub);

        report.ShouldContain("Runbooks");
        report.ShouldContain("not connected");
        report.ShouldContain("npx -y server");
    }
}
