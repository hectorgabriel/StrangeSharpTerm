using StrangeSharpTerm.Mcp;

namespace StrangeSharpTerm.Mcp.Tests;

public class ToolNameTests
{
    [Fact]
    public void AToolIsNamespacedByItsServer() =>
        // Two servers may each have a search, and a provider handed a duplicate
        // name rejects the whole request rather than the tool.
        ToolNames.Qualify("Grafana", "query_range").ShouldBe("grafana__query_range");

    [Fact]
    public void TwoServersWithTheSameToolStayApart()
    {
        var one = ToolNames.Qualify("Grafana", "search");
        var other = ToolNames.Qualify("Runbooks", "search");

        one.ShouldNotBe(other);
    }

    [Theory]
    [InlineData("My Server", "my_server")]
    [InlineData("grafana.prod", "grafana_prod")]
    [InlineData("ACME/Tools", "acme_tools")]
    [InlineData("café", "caf_")]
    [InlineData("a_b_", "a_b_")]
    public void AnythingAProviderWouldRejectIsReplaced(string name, string expected) =>
        ToolNames.Sanitise(name).ShouldBe(expected);

    [Fact]
    public void PunctuationBecomesAnUnderscoreRatherThanVanishing() =>
        // Dropping it would make two different tools the same name, which is the
        // thing namespacing exists to avoid.
        ToolNames.Sanitise("a.b").ShouldNotBe(ToolNames.Sanitise("ab"));

    [Fact]
    public void AnEmptyNameStillProducesSomethingCallable() =>
        ToolNames.Sanitise("").ShouldBe("tool");

    [Fact]
    public void TrailingPunctuationIsKeptRatherThanTrimmed() =>
        // Trimming it would make "café" and "caf" one name, which is the
        // collision the namespacing exists to prevent.
        ToolNames.Sanitise("café").ShouldNotBe(ToolNames.Sanitise("caf"));

    [Fact]
    public void ALongNameIsCappedWhereAProviderCaps()
    {
        var qualified = ToolNames.Qualify(new string('s', 80), "query_range");

        qualified.Length.ShouldBeLessThanOrEqualTo(ToolNames.MaxLength);
        // The tool's own name survives whole: a truncated one is a tool the
        // model cannot ask for.
        qualified.ShouldEndWith("query_range");
    }

    [Fact]
    public void AToolNameLongerThanTheCapIsTruncatedOnItsOwn()
    {
        var qualified = ToolNames.Qualify("grafana", new string('t', 100));

        qualified.Length.ShouldBe(ToolNames.MaxLength);
    }

    [Fact]
    public void AQualifiedNameSplitsBackIntoItsHalves() =>
        ToolNames.Split("grafana__query_range").ShouldBe(("grafana", "query_range"));

    [Theory]
    [InlineData("run_command")]
    [InlineData("__leading")]
    [InlineData("trailing__")]
    [InlineData("")]
    public void SomethingThatIsNotQualifiedSaysSo(string name) =>
        // run_command is the assistant's own tool and belongs to no server.
        ToolNames.Split(name).ShouldBeNull();

    [Fact]
    public void ASplitFindsTheFirstSeparator() =>
        // A tool whose own name contains the separator still belongs to the
        // server named before the first one.
        ToolNames.Split("grafana__query__range").ShouldBe(("grafana", "query__range"));
}
