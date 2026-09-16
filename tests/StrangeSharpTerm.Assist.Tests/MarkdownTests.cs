using StrangeSharpTerm.Assist;

namespace StrangeSharpTerm.Assist.Tests;

/// <summary>
/// As much Markdown as an answer about a server actually uses.
///
/// The property that matters most is the last few of these: anything it does
/// not understand comes through as text. A pane full of asterisks is worse than
/// a pane with no bold in it.
/// </summary>
public class MarkdownTests
{
    private static string Plain(IEnumerable<InlineSpan> spans) => string.Concat(spans.Select(span => span.Text));

    [Fact]
    public void BoldItalicAndInlineCodeAreTheirOwnRuns()
    {
        var spans = Markdown.Inlines("The journal is **the whole of it**, and `journald.conf` sets *no* cap.");

        spans.Where(span => span.Style == InlineStyle.Strong).Single().Text.ShouldBe("the whole of it");
        spans.Where(span => span.Style == InlineStyle.Code).Single().Text.ShouldBe("journald.conf");
        spans.Where(span => span.Style == InlineStyle.Emphasis).Single().Text.ShouldBe("no");

        // Nothing is lost or duplicated on the way through.
        Plain(spans).ShouldBe("The journal is the whole of it, and journald.conf sets no cap.");
    }

    /// <summary>
    /// What is inside backticks is literal. This is the case that matters on a
    /// server: an asterisk in a command is a glob, not a mark.
    /// </summary>
    [Fact]
    public void AsterisksInsideCodeAreNotMarkup()
    {
        var spans = Markdown.Inlines("Run `rm -rf /tmp/*.log` and check.");

        spans.Single(span => span.Style == InlineStyle.Code).Text.ShouldBe("rm -rf /tmp/*.log");
        spans.ShouldNotContain(span => span.Style == InlineStyle.Emphasis || span.Style == InlineStyle.Strong);
    }

    [Fact]
    public void AMarkWithNoPartnerIsJustText()
    {
        Plain(Markdown.Inlines("2 * 3 * 4 is twelve")).ShouldBe("2 * 3 * 4 is twelve");
        Markdown.Inlines("2 * 3 * 4 is twelve").ShouldAllBe(span => span.Style == InlineStyle.Plain);

        // An opening mark and nothing after it.
        Plain(Markdown.Inlines("the size is 49G**")).ShouldBe("the size is 49G**");
        Plain(Markdown.Inlines("a lone ` backtick")).ShouldBe("a lone ` backtick");
    }

    [Fact]
    public void HeadingsAreRecognisedByTheirLevel()
    {
        var lines = Markdown.Lines("## Status\ntext under it");

        lines[0].Heading.ShouldBe(2);
        Plain(lines[0].Spans).ShouldBe("Status");
        lines[1].Heading.ShouldBe(0);
    }

    /// <summary>A hashtag is not a heading, and a rule is not a bullet.</summary>
    [Fact]
    public void AHashWithNoSpaceIsNotAHeading()
    {
        Markdown.Lines("#nofilter").ShouldHaveSingleItem().Heading.ShouldBe(0);
        Markdown.Lines("---").ShouldHaveSingleItem().Marker.ShouldBeNull();
        // Seven hashes is not a heading level, so it is text.
        Markdown.Lines("####### too deep").ShouldHaveSingleItem().Heading.ShouldBe(0);
    }

    [Fact]
    public void BulletsAndNumbersKeepTheirMarkers()
    {
        var lines = Markdown.Lines("- first\n- second\n\n3. third\n4) fourth");

        lines[0].Marker.ShouldBe("•");
        lines[1].Marker.ShouldBe("•");
        // The number the model wrote, rather than one counted here: a list that
        // starts at three usually means to.
        lines[2].Marker.ShouldBe("3.");
        lines[3].Marker.ShouldBe("4)");
        Plain(lines[3].Spans).ShouldBe("fourth");
    }

    [Fact]
    public void AnIndentedBulletIsNestedRatherThanFlattened()
    {
        var lines = Markdown.Lines("- outer\n    - inner");

        lines[0].Depth.ShouldBe(0);
        lines[1].Depth.ShouldBe(2);
    }

    /// <summary>
    /// Emphasis is not a bullet. "*no* cap" opens with the same character a
    /// list does, and the difference is the space after it.
    /// </summary>
    [Fact]
    public void EmphasisAtTheStartOfALineIsNotABullet()
    {
        var line = Markdown.Lines("*definitely* not a list").ShouldHaveSingleItem();

        line.Marker.ShouldBeNull();
        line.Spans[0].Style.ShouldBe(InlineStyle.Emphasis);
    }

    [Fact]
    public void NothingAtAllIsNoLines()
    {
        Markdown.Lines(null).ShouldBeEmpty();
        Markdown.Lines("   \n  \n").ShouldBeEmpty();
        Markdown.Inlines(null).ShouldBeEmpty();
    }
}
