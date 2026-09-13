using StrangeSharpTerm.Mcp;

namespace StrangeSharpTerm.Mcp.Tests;

public class ToolGrantTests
{
    private static McpServerConfig Server(params string[] granted) =>
        new() { Name = "Grafana", Transport = McpTransport.Http, Url = "https://metrics.example.com/mcp", AlwaysAllowed = granted };

    private static McpTool Tool(string name, bool readOnly = false, bool destructive = false) =>
        new("Grafana", name, $"grafana__{name}", "", "{}", readOnly, destructive)
        {
            Destination = "metrics.example.com",
        };

    [Fact]
    public void EveryCallAsksUntilAPersonSaysOtherwise() =>
        ToolGrants.MayRunUnattended(Server(), Tool("query_range")).ShouldBeFalse();

    [Fact]
    public void AGrantedToolRunsWithoutAsking() =>
        ToolGrants.MayRunUnattended(Server("query_range"), Tool("query_range")).ShouldBeTrue();

    [Fact]
    public void AGrantIsPerToolAndNotPerServer() =>
        ToolGrants.MayRunUnattended(Server("query_range"), Tool("delete_dashboard")).ShouldBeFalse();

    /// <summary>
    /// readOnlyHint is a claim by the party being trusted. It is shown and never
    /// acted on — the same role the command denylist plays beside the allowlist.
    /// </summary>
    [Fact]
    public void AServerCallingItsOwnToolReadOnlyGrantsItNothing()
    {
        ToolGrants.MayRunUnattended(Server(), Tool("search", readOnly: true)).ShouldBeFalse();

        // And it does not become granted by being claimed read-only either: the
        // only way onto the list is a person at the gate.
        var after = ToolGrants.Grant(Server(), Tool("search", readOnly: true));
        after.AlwaysAllowed.ShouldBe(["search"]);
    }

    /// <summary>
    /// destructiveHint is the one claim that <em>is</em> believed, because
    /// believing it only ever narrows what can happen.
    /// </summary>
    [Fact]
    public void AToolTheServerCallsDestructiveCannotBeGivenAStandingPass()
    {
        var tool = Tool("delete_dashboard", destructive: true);

        ToolGrants.MayBeGranted(tool).ShouldBeFalse();
        ToolGrants.Grant(Server(), tool).AlwaysAllowed.ShouldBeEmpty();
    }

    [Fact]
    public void ADestructiveToolDoesNotRunUnattendedEvenIfItIsSomehowOnTheList() =>
        ToolGrants.MayRunUnattended(Server("delete_dashboard"), Tool("delete_dashboard", destructive: true))
            .ShouldBeFalse();

    [Fact]
    public void GrantingTwiceGrantsOnce() =>
        ToolGrants.Grant(ToolGrants.Grant(Server(), Tool("search")), Tool("search"))
            .AlwaysAllowed.ShouldBe(["search"]);

    [Fact]
    public void AGrantCanBeTakenBack() =>
        ToolGrants.Revoke(Server("search", "query_range"), "search").AlwaysAllowed.ShouldBe(["query_range"]);

    [Fact]
    public void RemovingAServerTakesItsGrantsWithIt()
    {
        var server = Server("search");
        var settings = new McpSettings().Upsert(server);

        var after = settings.Remove(server).Upsert(server with { Id = Model.NodeId.New(), AlwaysAllowed = [] });

        // Re-adding one by the same name must not inherit what was granted to
        // whatever was there before.
        after.Servers.Single().AlwaysAllowed.ShouldBeEmpty();
    }
}
