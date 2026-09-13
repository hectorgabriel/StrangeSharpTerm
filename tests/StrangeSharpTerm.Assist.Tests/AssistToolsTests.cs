using StrangeSharpTerm.Assist;

namespace StrangeSharpTerm.Assist.Tests;

public class AssistToolsTests
{
    [Fact]
    public void ACallIsReadIntoACommandAndAReason()
    {
        var (command, why) = AssistTools.ReadRun("""{"command": "df -h /", "why": "how full it is"}""");

        command.ShouldBe("df -h /");
        why.ShouldBe("how full it is");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("""{"why": "no command"}""")]
    public void ArgumentsThatCannotBeReadProduceNoCommand(string? arguments)
    {
        // Which the gate then refuses like anything else it cannot read, rather
        // than ending the conversation.
        var (command, _) = AssistTools.ReadRun(arguments);

        command.ShouldBeEmpty();
        CommandPolicy.Judge(command).MayRunUnattended.ShouldBeFalse();
    }

    [Fact]
    public void AFieldThatIsNotAStringIsStillRead() =>
        AssistTools.ReadRun("""{"command": "uptime", "why": 42}""").Why.ShouldBe("42");

    [Fact]
    public void ThereIsOneTool()
    {
        // One rather than several: every extra tool is another thing the gate
        // has to understand, and a command is already the general case.
        AssistTools.Runner.Name.ShouldBe("run_command");
        AssistTools.Runner.Description.ShouldContain("asks the user");
    }
}
