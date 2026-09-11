namespace StrangeSharpTerm.Terminal.Tests;

public class TerminalSessionTests
{
    private static async Task<(TerminalSession Session, FakeChannel Channel)> Run(
        string incoming, TerminalSessionOptions? options = null)
    {
        var channel = new FakeChannel(incoming);
        var session = new TerminalSession(channel, options);
        await session.RunAsync(TestContext.Current.CancellationToken);
        return (session, channel);
    }

    [Fact]
    public async Task WhatTheServerSendsIsWhatTheTerminalShows()
    {
        var (session, _) = await Run("hello\r\nworld\r\n");

        session.VisibleText.ShouldStartWith("hello\nworld");
    }

    [Fact]
    public async Task EscapeSequencesAreInterpretedRatherThanPrinted()
    {
        // Red text, then a reset: the codes must not reach the screen as characters.
        var (session, _) = await Run("\u001b[31mdanger\u001b[0m\r\n");

        session.VisibleText.ShouldStartWith("danger");
        session.VisibleText.ShouldNotContain("[31m");
    }

    [Fact]
    public async Task TheFarEndCanSetTheTitle()
    {
        var channel = new FakeChannel("\u001b]0;db-01 postgres\u0007ready\r\n");
        var session = new TerminalSession(channel);
        var announced = new List<string>();
        session.TitleChanged += (_, title) => announced.Add(title);

        await session.RunAsync(TestContext.Current.CancellationToken);

        session.Title.ShouldBe("db-01 postgres");
        announced.ShouldBe(new[] { "db-01 postgres" });
    }

    [Fact]
    public async Task AClosedChannelEndsTheSessionExactlyOnce()
    {
        var channel = new FakeChannel("bye\r\n");
        var session = new TerminalSession(channel);
        var ended = 0;
        session.Ended += (_, _) => ended++;

        await session.RunAsync(TestContext.Current.CancellationToken);
        await session.RunAsync(TestContext.Current.CancellationToken);

        ended.ShouldBe(1);
    }

    [Fact]
    public void KeystrokesReachTheChannel()
    {
        var channel = new FakeChannel();
        using var session = new TerminalSession(channel);

        session.Send("uptime\n");

        channel.Written.ShouldBe("uptime\n");
    }

    [Fact]
    public void ResizingTellsTheServerAsWellAsTheEngine()
    {
        // The pty lives on the server: without the window-change request the far
        // end keeps wrapping at the old width.
        var channel = new FakeChannel();
        using var session = new TerminalSession(channel);

        session.Resize(120, 40);

        channel.Resizes.ShouldBe(new[] { (120, 40) });
        session.Engine.Cols.ShouldBe(120);
        session.Engine.Rows.ShouldBe(40);
    }

    [Fact]
    public void AResizeToTheSameSizeSaysNothing()
    {
        var channel = new FakeChannel();
        using var session = new TerminalSession(channel);

        session.Resize(100, 30);
        session.Resize(100, 30);
        session.Resize(0, 0);

        channel.Resizes.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ScrollbackIsReadableAfterItHasScrolledOffTheScreen()
    {
        // The command that caused an error has usually scrolled away by the time
        // anyone asks about it, which is the whole reason this exists.
        var lines = string.Concat(Enumerable.Range(1, 60).Select(n => $"line {n}\r\n"));
        var (session, _) = await Run(lines, new TerminalSessionOptions { Columns = 80, Rows = 24 });

        session.VisibleText.ShouldNotContain("line 1\n");
        var recent = session.RecentText().ShouldNotBeNull();
        recent.ShouldContain("line 1");
        recent.ShouldEndWith("line 60");
    }

    [Fact]
    public async Task AWrappedLineIsRejoinedIntoTheOneTheUserTyped()
    {
        // An 80-column chop of every long path would be worse than useless to an
        // assistant reading this.
        var path = "/var/log/" + new string('a', 100) + ".log";
        var (session, _) = await Run(path + "\r\n", new TerminalSessionOptions { Columns = 80, Rows = 24 });

        session.RecentText().ShouldNotBeNull().ShouldContain(path);
    }

    [Fact]
    public async Task AnEmptyTerminalHasNoRecentText()
    {
        var (session, _) = await Run("");

        session.RecentText().ShouldBeNull();
    }
}

public class TerminalPaletteTests
{
    [Fact]
    public void TheThemesKeepTheColoursTheSwiftAppShowed()
    {
        // A theme that looked like Dracula there has to look like Dracula here.
        TerminalPalette.Dracula.Background.ShouldBe(0x282A36u);
        TerminalPalette.Dracula.Ansi.ShouldNotBeNull().Count.ShouldBe(16);
        TerminalPalette.Dracula.Ansi![1].ShouldBe(0xFF5555u);
    }

    [Fact]
    public void TheOriginalThemeLeavesTheAnsiColoursToTheEngine()
    {
        TerminalPalette.StrangeTermDark.Ansi.ShouldBeNull();
    }

    [Fact]
    public void AnUnknownThemeFallsBackToTheDefaultRatherThanFailing()
    {
        TerminalPalette.ByName("Solarized Amber").ShouldBe(TerminalPalette.StrangeTermDark);
        TerminalPalette.ByName(null).ShouldBe(TerminalPalette.StrangeTermDark);
        TerminalPalette.ById("dracula").ShouldBe(TerminalPalette.Dracula);
    }

    [Fact]
    public void ColoursSplitIntoTheChannelsAToolkitWants()
    {
        TerminalPalette.Rgb(0x2ED3A0).ShouldBe(((byte)0x2E, (byte)0xD3, (byte)0xA0));
    }
}
