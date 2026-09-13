using StrangeSharpTerm.Assist;

namespace StrangeSharpTerm.Assist.Tests;

/// <summary>
/// The scanner underneath the policy. It is not a shell and does not try to be;
/// what it has to get right is where one stage ends and the next begins.
/// </summary>
public class ShellWordsTests
{
    [Theory]
    [InlineData("df -h", 1)]
    [InlineData("ps aux | grep nginx", 2)]
    [InlineData("df -h; uptime", 2)]
    [InlineData("df -h && uptime", 2)]
    [InlineData("df -h || uptime", 2)]
    [InlineData("df -h\nuptime", 2)]
    [InlineData("a | b | c | d", 4)]
    public void EveryStageIsSeen(string command, int stages) =>
        ShellWords.Stages(command).Count.ShouldBe(stages);

    [Fact]
    public void AQuotedSeparatorIsNotASeparator() =>
        ShellWords.Stages("grep 'a | b' file").Count.ShouldBe(1);

    [Fact]
    public void QuotesComeOffAndAreRemembered()
    {
        var words = ShellWords.Stages("echo 'A=B'").Single().ToArray();

        words[1].Text.ShouldBe("A=B");
        words[1].WasQuoted.ShouldBeTrue();
    }

    [Fact]
    public void ARedirectionIsItsOwnToken()
    {
        var tokens = ShellWords.Stages("df -h > /tmp/x").Single().ToArray();

        tokens.ShouldContain(token => token.Kind == ShellTokenKind.Redirection && token.Text == ">");
        tokens[^1].Text.ShouldBe("/tmp/x");
    }

    [Fact]
    public void AFileDescriptorInFrontOfOneFallsOutAsAWord()
    {
        var tokens = ShellWords.Stages("df -h 2>/dev/null").Single().ToArray();

        tokens.Select(token => token.Text).ShouldBe(["df", "-h", "2", ">", "/dev/null"]);
    }

    [Theory]
    [InlineData("ls $(reboot)")]
    [InlineData("ls `reboot`")]
    [InlineData("diff <(ls a) <(ls b)")]
    public void SubstitutionsAreSpotted(string command) =>
        ShellWords.HasSubstitution(command).ShouldBeTrue();

    [Theory]
    [InlineData("df -h")]
    [InlineData("echo $HOME")]
    [InlineData("echo ${HOME}")]
    public void OrdinaryExpansionIsNotASubstitution(string command) =>
        ShellWords.HasSubstitution(command).ShouldBeFalse();

    [Fact]
    public void AnUnterminatedQuoteTakesTheRestOfTheLine()
    {
        // Nothing here can parse it, so it becomes one word and the policy stops
        // on a command it does not recognise.
        var words = ShellWords.Stages("echo 'unfinished").Single().ToArray();

        words.Length.ShouldBe(2);
        words[1].Text.ShouldBe("unfinished");
    }

    [Fact]
    public void AnEscapedSpaceKeepsOneWord() =>
        ShellWords.Stages(@"cat /var/log/my\ file").Single().Last().Text.ShouldBe("/var/log/my file");
}
