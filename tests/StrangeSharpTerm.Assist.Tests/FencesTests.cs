using StrangeSharpTerm.Assist;

namespace StrangeSharpTerm.Assist.Tests;

public class FencesTests
{
    [Fact]
    public void ProseAndBlocksComeBackInOrder()
    {
        var blocks = Fences.Parse("Check the filesystem first.\n\n```sh\ndf -h /\n```\n\nThen the directories.");

        blocks.Count.ShouldBe(3);
        blocks[0].ShouldBeOfType<AnswerBlock.Prose>().Text.ShouldBe("Check the filesystem first.");
        blocks[1].ShouldBeOfType<AnswerBlock.Code>().Text.ShouldBe("df -h /");
        blocks[2].ShouldBeOfType<AnswerBlock.Prose>().Text.ShouldBe("Then the directories.");
    }

    [Theory]
    [InlineData("sh")]
    [InlineData("bash")]
    [InlineData("shell")]
    [InlineData("zsh")]
    [InlineData("console")]
    [InlineData("Bash")]
    public void ABlockTaggedAsShellGetsTheButton(string tag) =>
        Fences.ShellBlocks($"```{tag}\nuptime\n```").Count.ShouldBe(1);

    [Theory]
    [InlineData("json")]
    [InlineData("yaml")]
    [InlineData("python")]
    [InlineData("")]
    public void AnythingElseRendersAsCodeWithNoWayToRunIt(string tag)
    {
        var markdown = tag.Length == 0 ? "```\nuptime\n```" : $"```{tag}\nuptime\n```";

        Fences.Parse(markdown).OfType<AnswerBlock.Code>().Single().IsShell.ShouldBeFalse();
        Fences.ShellBlocks(markdown).ShouldBeEmpty();
    }

    [Fact]
    public void AnUntaggedFenceIsStillShown()
    {
        // It has no button, but the answer is not thrown away for want of a tag.
        Fences.Parse("```\nuptime\n```").OfType<AnswerBlock.Code>().Single().Text.ShouldBe("uptime");
    }

    [Fact]
    public void APromptIsStrippedBeforeItIsTyped()
    {
        var block = Fences.ShellBlocks("```console\n$ df -h /\n$ free -m\n```").Single();

        block.Staged.ShouldBe("df -h /\nfree -m");
        // What is shown keeps the prompts, because that is what the model wrote.
        block.Text.ShouldBe("$ df -h /\n$ free -m");
    }

    [Fact]
    public void AFenceTheModelNeverClosedIsStillABlock()
    {
        var blocks = Fences.Parse("Try this:\n```sh\ndf -h");

        blocks.OfType<AnswerBlock.Code>().Single().Text.ShouldBe("df -h");
    }

    [Fact]
    public void ATagWithExtraWordsStillCounts() =>
        Fences.ShellBlocks("```bash title=\"check\"\nuptime\n```").Count.ShouldBe(1);

    [Fact]
    public void SeveralBlocksAreAllOffered()
    {
        var blocks = Fences.ShellBlocks("```sh\ndf -h /\n```\nand\n```sh\ndu -xh /var\n```");

        blocks.Select(block => block.Staged).ShouldBe(["df -h /", "du -xh /var"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NothingParsesToNothing(string? markdown) => Fences.Parse(markdown).ShouldBeEmpty();

    [Fact]
    public void PlainProseIsOneBlock() =>
        Fences.Parse("Nothing is wrong with it.").Single()
            .ShouldBeOfType<AnswerBlock.Prose>().Text.ShouldBe("Nothing is wrong with it.");

    [Fact]
    public void AnIndentedFenceCloses()
    {
        var blocks = Fences.Parse("  ```sh\n  uptime\n  ```\nafter");

        blocks.OfType<AnswerBlock.Code>().Single().IsShell.ShouldBeTrue();
        blocks.OfType<AnswerBlock.Prose>().Single().Text.ShouldBe("after");
    }
}
