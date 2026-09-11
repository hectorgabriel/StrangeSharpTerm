namespace StrangeSharpTerm.Model.Tests;

public class SnippetPlaceholderTests
{
    private static readonly Dictionary<string, string> NoValues = [];

    [Fact]
    public void PlaceholdersAreFoundInOrderWithoutDuplicates()
    {
        SnippetTemplate.Placeholders("rsync {{source}} {{target}} && echo {{source}} done")
            .ShouldBe(new[] { "source", "target" });
    }

    [Fact]
    public void ACommandWithNoPlaceholdersNeedsNothing()
    {
        SnippetTemplate.Placeholders("systemctl restart nginx").ShouldBeEmpty();
        new Snippet { Name = "x", Command = "uptime" }.IsParameterised.ShouldBeFalse();
    }

    [Fact]
    public void ShellVariablesAreLeftAlone()
    {
        // A saved command is full of legitimate $VAR; treating those as placeholders
        // would corrupt the commands people most want to keep.
        SnippetTemplate.Placeholders("echo $HOME ${PATH} {{real}}").ShouldBe(new[] { "real" });
    }

    [Fact]
    public void ValuesAreSubstituted()
    {
        var snippet = new Snippet { Name = "tail", Command = "tail -f {{path}}" };
        snippet.Rendered(new Dictionary<string, string> { ["path"] = "/var/log/syslog" })
            .ShouldBe("tail -f /var/log/syslog");
    }

    [Fact]
    public void SpacingInsideTheBracesIsTolerated()
    {
        SnippetTemplate.Render("tail -f {{ path }}", new Dictionary<string, string> { ["path"] = "/tmp/a" })
            .ShouldBe("tail -f /tmp/a");
    }

    [Fact]
    public void AMissingValueLeavesThePlaceholderInPlace()
    {
        // Turning `rm -rf {{path}}` into `rm -rf` would be a spectacular way to lose
        // a filesystem, so an unfilled placeholder stays visible.
        SnippetTemplate.Render("rm -rf {{path}}", NoValues).ShouldBe("rm -rf {{path}}");
    }

    [Fact]
    public void CompletenessRequiresEveryPlaceholderToHaveANonEmptyValue()
    {
        const string command = "cp {{from}} {{to}}";
        SnippetTemplate.IsComplete(command, new Dictionary<string, string> { ["from"] = "a" }).ShouldBeFalse();
        SnippetTemplate.IsComplete(command, new Dictionary<string, string> { ["from"] = "a", ["to"] = "" }).ShouldBeFalse();
        SnippetTemplate.IsComplete(command, new Dictionary<string, string> { ["from"] = "a", ["to"] = "b" }).ShouldBeTrue();
    }

    [Fact]
    public void EveryOccurrenceIsReplacedNotOnlyTheFirst()
    {
        SnippetTemplate.Render("{{x}} and {{x}}", new Dictionary<string, string> { ["x"] = "1" }).ShouldBe("1 and 1");
    }

    [Fact]
    public void AnUnterminatedMarkerIsIgnoredRatherThanHalfParsed()
    {
        SnippetTemplate.Placeholders("echo {{oops").ShouldBeEmpty();
    }

    [Fact]
    public void EmptyBracesAreNotAPlaceholder()
    {
        SnippetTemplate.Placeholders("echo {{}}").ShouldBeEmpty();
    }
}
