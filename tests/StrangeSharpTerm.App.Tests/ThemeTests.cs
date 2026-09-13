using System.Text.RegularExpressions;
using Avalonia.Controls;
using StrangeSharpTerm.App.Theming;
using StrangeSharpTerm.App.ViewModels;
using StrangeSharpTerm.Model;
using StrangeSharpTerm.Terminal;

namespace StrangeSharpTerm.App.Tests;

/// <summary>
/// The theme, which is mostly a set of numbers and therefore mostly a set of
/// claims about where those numbers came from. These hold the measured ones to
/// what <c>build/theme/sample.py</c> reads out of the Swift app's screenshots,
/// and re-derive the rest here rather than trusting a comment: see
/// docs/adr/0004-theme-colours.md.
/// </summary>
public class ThemeTests
{
    private static (byte Red, byte Green, byte Blue) Rgb(uint colour) => TerminalPalette.Rgb(colour);

    private static double Luminance(uint colour)
    {
        var (red, green, blue) = Rgb(colour);
        return 0.2126 * red + 0.7152 * green + 0.0722 * blue;
    }

    [Fact]
    public void DraculaIsWhatTheScreenshotsShow()
    {
        // Every reference screenshot is in this theme, so every one of these is
        // a pixel someone can go and look at. sample.py names the coordinates.
        var dracula = AppPalette.Dracula;

        dracula.Rail.ShouldBe(0x191A21u);
        dracula.Sidebar.ShouldBe(0x21222Cu);
        dracula.Background.ShouldBe(0x282A36u);
        dracula.Surface.ShouldBe(0x343746u);
        dracula.Border.ShouldBe(0x424450u);
        dracula.Selection.ShouldBe(0x353147u);
        dracula.Text.ShouldBe(0xF8F8F2u);
        dracula.Muted.ShouldBe(0x6272A4u);
        dracula.Accent.ShouldBe(0xBD93F9u);
        dracula.OnAccent.ShouldBe(0x191A21u);
        dracula.Success.ShouldBe(0x50FA7Bu);
        dracula.Warning.ShouldBe(0xFFB86Cu);
        dracula.Danger.ShouldBe(0xFF5555u);
    }

    [Fact]
    public void StrangeTermDarkIsWhatItsSwatchShows()
    {
        // No screenshot is in this theme. Its row in the settings sheet draws its
        // own surfaces and accent as four squares, and that is the measurement.
        var dark = AppPalette.StrangeTermDark;

        dark.Sidebar.ShouldBe(0x16181Du);
        dark.Background.ShouldBe(0x1A1D23u);
        dark.Surface.ShouldBe(0x21252Eu);
        dark.Accent.ShouldBe(0x2ED3A0u);

        // Its text is not in any screenshot either, but it is already ported: the
        // terminal foreground the Swift app used for this theme.
        dark.Text.ShouldBe(TerminalPalette.StrangeTermDark.Foreground);
    }

    [Fact]
    public void WhatStrangeTermDarkDoesNotShowIsDerivedFromWhatDraculaDoes()
    {
        // The derivations, run rather than described. If one of these numbers is
        // ever corrected against the Swift source, this fails and says so.
        var dracula = AppPalette.Dracula;
        var dark = AppPalette.StrangeTermDark;

        Scaled(dark.Sidebar, dracula.Rail, dracula.Sidebar).ShouldBe(dark.Rail);
        Scaled(dark.Surface, dracula.Border, dracula.Surface).ShouldBe(dark.Border);

        // The selection is the accent laid over the sidebar, at the alpha that
        // reproduces Dracula's measured one.
        AppPalette.Blend(dracula.Accent, dracula.Sidebar, 0.13).ShouldBe(dracula.Selection);
        AppPalette.Blend(dark.Accent, dark.Sidebar, 0.13).ShouldBe(dark.Selection);

        // Muted text sits as far down as Dracula's comment colour does, in the
        // hue this theme's own surfaces have.
        Luminance(dark.Muted).ShouldBe(Luminance(dracula.Muted), tolerance: 1);
        Ratio(dark.Muted).ShouldBe(Ratio(dark.Background), tolerance: 0.01);

        dark.OnAccent.ShouldBe(dark.Rail);
        dracula.OnAccent.ShouldBe(dracula.Rail);

        // A colour that means something means it in both themes.
        dark.Success.ShouldBe(dracula.Success);
        dark.Warning.ShouldBe(dracula.Warning);
        dark.Danger.ShouldBe(dracula.Danger);

        static uint Scaled(uint colour, uint numerator, uint denominator)
        {
            var (r, g, b) = Rgb(colour);
            var (nr, ng, nb) = Rgb(numerator);
            var (dr, dg, db) = Rgb(denominator);
            return (Channel(r, nr, dr) << 16) | (Channel(g, ng, dg) << 8) | Channel(b, nb, db);

            static uint Channel(byte value, byte over, byte under) =>
                (uint)Math.Round(value * (double)over / under);
        }

        // Red against blue: enough to say two colours are the same hue family.
        static double Ratio(uint colour)
        {
            var (red, _, blue) = Rgb(colour);
            return (double)red / blue;
        }
    }

    [Fact]
    public void EachThemeIsOneThemeInBothHalves()
    {
        foreach (var palette in AppPalette.BuiltIn)
        {
            // Joined by id, so a terminal and the window around it cannot end up
            // in different themes.
            palette.Terminal.Id.ShouldBe(palette.Id);
            palette.Terminal.Name.ShouldBe(palette.Name);

            // And agreeing where they touch: a terminal is drawn on the content
            // surface, so its own background has to be that colour.
            palette.Terminal.Background.ShouldBe(palette.Background);
        }
    }

    [Fact]
    public void EveryTokenIsDefinedForEveryTheme()
    {
        var names = ThemeTokens.Of(AppPalette.StrangeTermDark).Keys.Order().ToArray();

        foreach (var palette in AppPalette.BuiltIn)
        {
            var tokens = ThemeTokens.Of(palette);
            tokens.Keys.Order().ShouldBe(names);
            foreach (var (name, colour) in tokens)
                colour.ShouldBeLessThanOrEqualTo(0xFFFFFFu, $"{palette.Name}'s {name} is not a 24-bit colour");
        }
    }

    [Fact]
    public void EveryColourTheMarkupNamesIsOneTheThemeDefines()
    {
        // The failure this catches is silent: a DynamicResource nobody defines
        // paints nothing at all, which in a dark window looks like a control that
        // simply is not there.
        var defined = ThemeTokens.Of(AppPalette.Dracula).Keys
            .SelectMany(name => new[] { $"{name}Brush", $"{name}Color" })
            .ToHashSet();

        foreach (var file in Directory.EnumerateFiles(AppSource(), "*.axaml", SearchOption.AllDirectories))
        {
            foreach (Match match in Regex.Matches(File.ReadAllText(file), @"\{DynamicResource ([A-Za-z0-9_]+)\}"))
            {
                var key = match.Groups[1].Value;
                defined.ShouldContain(key, $"{Path.GetFileName(file)} asks for {key}, which no theme defines");
            }
        }
    }

    [Fact]
    public void ATintIsTheColourItselfWhenItIsFullyOpaque()
    {
        AppPalette.Blend(0x123456, 0xFFFFFF, 1).ShouldBe(0x123456u);
        AppPalette.Blend(0x123456, 0xFEDCBA, 0).ShouldBe(0xFEDCBAu);

        // And halfway is halfway, rounded rather than truncated.
        AppPalette.Blend(0x000000, 0xFFFFFF, 0.5).ShouldBe(0x808080u);
    }

    [Fact]
    public void AHostThatNamesNoThemeFollowsTheApp()
    {
        var host = new Connection { Name = "web-01", Hostname = "web-01.example.com" };
        var tree = new InventoryTree(connections: [host]);
        var theme = new AppTheme();

        theme.TerminalPaletteFor(tree, host).ShouldBe(TerminalPalette.StrangeTermDark);

        theme.Use(AppPalette.Dracula);
        theme.TerminalPaletteFor(tree, host).ShouldBe(TerminalPalette.Dracula);
    }

    [Fact]
    public void AHostThatNamesOneKeepsIt()
    {
        // Someone set this deliberately, host by host, and the app theme is not
        // an instruction to undo that.
        var host = new Connection
        {
            Name = "db-01",
            Hostname = "db-01.example.com",
            Settings = new ConnectionSettings { TerminalTheme = "Dracula" },
        };
        var tree = new InventoryTree(connections: [host]);

        new AppTheme().TerminalPaletteFor(tree, host).ShouldBe(TerminalPalette.Dracula);
    }

    [Fact]
    public void AThemeSetOnAFolderReachesTheHostsInside()
    {
        var folder = new Model.Folder
        {
            Name = "Production",
            Settings = new ConnectionSettings { TerminalTheme = "Dracula" },
        };
        var host = new Connection { ParentId = folder.Id, Name = "web-01", Hostname = "web-01.example.com" };
        var tree = new InventoryTree([folder], [host]);

        new AppTheme().TerminalPaletteFor(tree, host).ShouldBe(TerminalPalette.Dracula);
    }

    [Fact]
    public void AThemeNameNothingAnswersToFallsBackToTheApp()
    {
        // An inventory written by a later version, or by hand.
        var host = new Connection
        {
            Name = "web-01",
            Hostname = "web-01.example.com",
            Settings = new ConnectionSettings { TerminalTheme = "Solarized" },
        };
        var tree = new InventoryTree(connections: [host]);
        var theme = new AppTheme(AppPalette.Dracula);

        theme.TerminalPaletteFor(tree, host).ShouldBe(TerminalPalette.Dracula);
    }

    [Fact]
    public void TheChoiceIsRemembered()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), Preferences.FileName);
        try
        {
            var theme = AppTheme.Load(path);
            theme.Palette.ShouldBe(AppPalette.StrangeTermDark);

            theme.Use(AppPalette.Dracula);

            AppTheme.Load(path).Palette.ShouldBe(AppPalette.Dracula);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void APreferencesFileNobodyCanReadCostsTheThemeAndNothingElse()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        File.WriteAllText(path, "{ this is not json");
        try
        {
            AppTheme.Load(path).Palette.ShouldBe(AppPalette.StrangeTermDark);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WritingOnePreferenceKeepsTheOthers()
    {
        // The file will hold more than a theme before long, and a version that
        // does not know about a key must not delete it.
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        File.WriteAllText(path, """{"theme":"strangeterm-dark","fontSize":15}""");
        try
        {
            new AppTheme(preferencesPath: path).Use(AppPalette.Dracula);

            File.ReadAllText(path).ShouldContain("fontSize");
            File.ReadAllText(path).ShouldContain("dracula");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A pane that records being recoloured, and would notice being replaced.</summary>
    private sealed class ThemedPane : Border, IThemedPane
    {
        public List<TerminalPalette> Applied { get; } = [];

        public void Apply(TerminalPalette palette) => Applied.Add(palette);
    }

    /// <summary>A channel that carries nothing, so a session needs no server.</summary>
    private sealed class DeadChannel : ITerminalChannel
    {
        public Stream Stream { get; } = new MemoryStream();

        public void Resize(int columns, int rows) { }

        public void Dispose() => Stream.Dispose();
    }

    [Fact]
    public async Task ChangingTheThemeRecoloursAnOpenTerminalWhereItStands()
    {
        // The pane has an ssh session inside it. Rebuilding it to repaint it
        // would drop that session, which is the whole reason Apply exists.
        var pane = new ThemedPane();
        var host = new Connection { Name = "web-01", Hostname = "web-01.example.com" };
        var theme = new AppTheme();
        var shell = new ShellViewModel(
            new InventoryViewModel(null, new InventoryTree(connections: [host])),
            new FakeSessions(),
            (_, _) => pane,
            theme: theme);
        shell.Inventory.Selection = host.Id;
        await shell.ConnectSelectedCommand.ExecuteAsync(null);

        theme.Use(AppPalette.Dracula);

        pane.Applied.ShouldHaveSingleItem().ShouldBe(TerminalPalette.Dracula);
        shell.Panes.ShouldHaveSingleItem().View.ShouldBeSameAs(pane);
    }

    [Fact]
    public async Task APaneWhoseHostNamesAThemeIsLeftAlone()
    {
        var pane = new ThemedPane();
        var host = new Connection
        {
            Name = "db-01",
            Hostname = "db-01.example.com",
            Settings = new ConnectionSettings { TerminalTheme = "StrangeTerm Dark" },
        };
        var theme = new AppTheme();
        var shell = new ShellViewModel(
            new InventoryViewModel(null, new InventoryTree(connections: [host])),
            new FakeSessions(),
            (_, _) => pane,
            theme: theme);
        shell.Inventory.Selection = host.Id;
        await shell.ConnectSelectedCommand.ExecuteAsync(null);

        theme.Use(AppPalette.Dracula);

        pane.Applied.ShouldHaveSingleItem().ShouldBe(TerminalPalette.StrangeTermDark);
    }

    [Fact]
    public void TheSelectedHostIsMarkedAsSelected()
    {
        var host = new Connection { Name = "web-01", Hostname = "web-01.example.com" };
        var inventory = new InventoryViewModel(null, new InventoryTree(connections: [host]));

        inventory.Rows.Single().IsSelected.ShouldBeFalse();

        inventory.Selection = host.Id;

        inventory.Rows.Single().IsSelected.ShouldBeTrue();
    }

    /// <summary>Walks out of the test's bin directory to the markup that is committed.</summary>
    private static string AppSource()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
            directory = directory.Parent;
        return Path.Combine(directory!.FullName, "src/StrangeSharpTerm.App");
    }
}
